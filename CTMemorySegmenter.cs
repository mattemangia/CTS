using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using CTSegmenter;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

/// <summary>
/// A unified CTMemorySegmenter that supports both older 9/10-argument constructors
/// and the new 2-argument constructor for SAM 2.1 (encoder + decoder).
/// </summary>
public class CTMemorySegmenter : IDisposable
{
    // ------------------------------------------------------------------------
    // Fields from older code
    // ------------------------------------------------------------------------
    private string _imageEncoderPath;
    private string _promptEncoderPath;  // older SAM only
    private string _maskDecoderPath;
    private string _memoryEncoderPath;  // older SAM only
    private string _memoryAttentionPath;// older SAM only
    private string _mlpPath;            // older SAM only
    private int _imageInputSize;
    private bool _canUseTextPrompts;
    private bool _enableMlp;
    private bool _useCpuExecutionProvider;

    // The ONNX runtime sessions
    private InferenceSession _encoderSession;
    private InferenceSession _decoderSession;

    // Option flags
    public bool StorePreviousEmbeddings { get; set; } = true;   // older code references
    public bool UseSelectiveHoleFilling { get; set; } = false;  // older code references

    /// <summary>
    /// Mask binarization threshold in [0..1]. Default 0.5 => logit=0 => foreground.
    /// </summary>
    public float MaskBinarizationThreshold { get; set; } = 0.5f;

    private static void Log(string msg) => Debug.WriteLine(msg);

    // ------------------------------------------------------------------------
    // Constructors
    // ------------------------------------------------------------------------

    /// <summary>
    /// Minimal new constructor for SAM 2.1 usage: just pass encoder+decoder ONNX paths.
    /// </summary>
    public CTMemorySegmenter(string encoderOnnxPath, string decoderOnnxPath)
    {
        _imageEncoderPath = encoderOnnxPath;
        _maskDecoderPath = decoderOnnxPath;

        var options = new SessionOptions();
        options.AppendExecutionProvider_CPU();  // or GPU if desired
        _encoderSession = new InferenceSession(_imageEncoderPath, options);
        _decoderSession = new InferenceSession(_maskDecoderPath, options);

        // Default input size 1024 for SAM 2.1
        _imageInputSize = 1024;

        Log("[CTMemorySegmenter] Created with 2-arg constructor (SAM 2.1).");
    }

    /// <summary>
    /// Old 9-argument constructor signature, chaining to the 10-argument version with CPU=true.
    /// </summary>
    public CTMemorySegmenter(
        string imageEncoderPath,
        string promptEncoderPath,
        string maskDecoderPath,
        string memoryEncoderPath,
        string memoryAttentionPath,
        string mlpPath,
        int imageInputSize,
        bool canUseTextPrompts,
        bool enableMlp)
        : this(imageEncoderPath, promptEncoderPath, maskDecoderPath, memoryEncoderPath,
               memoryAttentionPath, mlpPath, imageInputSize, canUseTextPrompts, enableMlp, true)
    {
        // no body needed
    }

    /// <summary>
    /// Old 10-argument constructor: originally for older SAM.
    /// </summary>
    public CTMemorySegmenter(
        string imageEncoderPath,
        string promptEncoderPath,
        string maskDecoderPath,
        string memoryEncoderPath,
        string memoryAttentionPath,
        string mlpPath,
        int imageInputSize,
        bool canUseTextPrompts,
        bool enableMlp,
        bool useCpuExecutionProvider)
    {
        // Store them in fields
        _imageEncoderPath = imageEncoderPath;
        _promptEncoderPath = promptEncoderPath;
        _maskDecoderPath = maskDecoderPath;
        _memoryEncoderPath = memoryEncoderPath;
        _memoryAttentionPath = memoryAttentionPath;
        _mlpPath = mlpPath;
        _imageInputSize = imageInputSize;
        _canUseTextPrompts = canUseTextPrompts;
        _enableMlp = enableMlp;
        _useCpuExecutionProvider = useCpuExecutionProvider;

        Log("[CTMemorySegmenter] Old 10-arg constructor invoked.");

        // Create runtime sessions for the main encoder/decoder ONNX:
        SessionOptions options = new SessionOptions();
        if (!useCpuExecutionProvider)
        {
            try
            {
                // If you have GPU or CUDA, place code here like: options.AppendExecutionProvider_CUDA();
                Log("[CTMemorySegmenter] Attempting GPU (dummy).");
            }
            catch (Exception ex)
            {
                Log("[CTMemorySegmenter] GPU not available; fallback to CPU: " + ex.Message);
                options = new SessionOptions();
                options.AppendExecutionProvider_CPU();
            }
        }
        else
        {
            options.AppendExecutionProvider_CPU();
        }

        if (File.Exists(_imageEncoderPath) && File.Exists(_maskDecoderPath))
        {
            _encoderSession = new InferenceSession(_imageEncoderPath, options);
            _decoderSession = new InferenceSession(_maskDecoderPath, options);
            Log("[CTMemorySegmenter] Sessions loaded (possibly SAM 2.1 or older).");
        }
        else
        {
            Log("[CTMemorySegmenter] Could not find specified encoder/decoder ONNX. Not loading sessions.");
        }
    }

    // ------------------------------------------------------------------------
    // Dispose
    // ------------------------------------------------------------------------
    public void Dispose()
    {
        _encoderSession?.Dispose();
        _decoderSession?.Dispose();
    }

    // ------------------------------------------------------------------------
    // XY methods
    // ------------------------------------------------------------------------
    /// <summary>
    /// Process a single XY slice (returns the single best mask as Bitmap).
    /// Overload also returns a bool[,] array of mask bits.
    /// </summary>
    public Bitmap ProcessXYSlice(
        int sliceIndex,
        Bitmap baseXY,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName,
        out bool[,] maskBits)
    {
        maskBits = null;
        if (_encoderSession == null || _decoderSession == null)
        {
            Log("[ProcessXYSlice] No loaded sessions. Returning null.");
            return null;
        }
        if (baseXY == null || slicePoints == null || slicePoints.Count == 0)
        {
            Log("[ProcessXYSlice] Invalid input data. Returning null.");
            return null;
        }
        Log($"[ProcessXYSlice] Segmenting '{targetMaterialName}' in XY slice Z={sliceIndex}.");

        // 1) Convert baseXY to float tensor of shape (1,3,1024,1024)
        float[] imageTensor = BitmapToFloatTensor(baseXY, _imageInputSize, _imageInputSize);
        var imageInput = new DenseTensor<float>(imageTensor, new[] { 1, 3, _imageInputSize, _imageInputSize });

        // 2) Run encoder => image_embed, high_res_feats_0, high_res_feats_1
        Tensor<float> imageEmbeddings, highRes0, highRes1;
        using (var encOut = _encoderSession.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("image", imageInput)
        }))
        {
            // For SAM 2.1, outputs typically named "image_embed", "high_res_feats_0", "high_res_feats_1".
            imageEmbeddings = GetFirstTensorOrNull<float>(encOut, "image_embed");
            highRes0 = GetFirstTensorOrNull<float>(encOut, "high_res_feats_0");
            highRes1 = GetFirstTensorOrNull<float>(encOut, "high_res_feats_1");
        }
        if (imageEmbeddings == null || highRes0 == null || highRes1 == null)
        {
            Log("[ProcessXYSlice] Encoder output was missing. Returning null.");
            return null;
        }

        // 3) Build prompt: points for this material => label=1, all other => label=0
        int origW = baseXY.Width;
        int origH = baseXY.Height;
        var (coordTensor, labelTensor) = BuildSinglePromptTensors(slicePoints, origW, origH, targetMaterialName);

        // 4) Setup decoder inputs
        var origSizeTensor = new DenseTensor<int>(new[] { origH, origW }, new[] { 2 });
        int batchSize = coordTensor.Dimensions[0]; // typically 1
        var maskInputTensor = new DenseTensor<float>(new float[batchSize * 1 * 256 * 256], new[] { batchSize, 1, 256, 256 });
        var hasMaskInputTensor = new DenseTensor<float>(new float[batchSize], new[] { batchSize });

        // 5) Run decoder => output masks + IoU
        Tensor<float> masksTensor, iouTensor;
        using (var decOut = _decoderSession.Run(new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("image_embed",      imageEmbeddings),
            NamedOnnxValue.CreateFromTensor("high_res_feats_0", highRes0),
            NamedOnnxValue.CreateFromTensor("high_res_feats_1", highRes1),
            NamedOnnxValue.CreateFromTensor("point_coords",     coordTensor),
            NamedOnnxValue.CreateFromTensor("point_labels",     labelTensor),
            NamedOnnxValue.CreateFromTensor("mask_input",       maskInputTensor),
            NamedOnnxValue.CreateFromTensor("has_mask_input",   hasMaskInputTensor),
            NamedOnnxValue.CreateFromTensor("orig_im_size",     origSizeTensor)
        }))
        {
            masksTensor = GetFirstTensorOrNull<float>(decOut, "masks");  // shape [1,3,256,256]
            iouTensor = GetFirstTensorOrNull<float>(decOut, "iou_predictions"); // shape [1,3]
        }

        if (masksTensor == null || iouTensor == null)
        {
            Log("[ProcessXYSlice] Decoder output is missing or invalid. Returning null.");
            return null;
        }

        int outC = masksTensor.Dimensions[1]; // typically 3
        if (outC == 0)
        {
            Log("[ProcessXYSlice] Decoder returned 0 channels. Returning null.");
            return null;
        }

        // Pick best IoU
        float bestIoU = float.MinValue;
        int bestIndex = 0;
        for (int c = 0; c < outC; c++)
        {
            float iouVal = iouTensor[0, c];
            if (iouVal > bestIoU)
            {
                bestIoU = iouVal;
                bestIndex = c;
            }
        }
        Log($"[ProcessXYSlice] Best channel={bestIndex}, IoU={bestIoU:0.000}");

        // Convert that channel’s logits => binarized mask
        int maskH = masksTensor.Dimensions[2]; // 256
        int maskW = masksTensor.Dimensions[3]; // 256
        var finalMask = BuildMaskFromDecoder(masksTensor, bestIndex, maskW, maskH, origW, origH, out maskBits);
        return finalMask;
    }

    // Overload returning just the mask as a Bitmap
    public Bitmap ProcessXYSlice(
        int sliceIndex,
        Bitmap baseXY,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName)
    {
        bool[,] ignore;
        return ProcessXYSlice(sliceIndex, baseXY, slicePoints, targetMaterialName, out ignore);
    }

    // ------------------------------------------------------------------------
    // XZ methods
    // ------------------------------------------------------------------------
    /// <summary>
    /// Single best mask for XZ slice. 
    /// </summary>
    public Bitmap ProcessXZSlice(
        int fixedY,
        Bitmap baseXZ,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName,
        out bool[,] maskBits)
    {
        maskBits = null;
        if (_encoderSession == null || _decoderSession == null)
        {
            Log("[ProcessXZSlice] No loaded sessions. Returning null.");
            return null;
        }
        if (baseXZ == null || slicePoints == null || slicePoints.Count == 0)
        {
            Log("[ProcessXZSlice] Invalid input data. Returning null.");
            return null;
        }
        Log($"[ProcessXZSlice] Segmenting '{targetMaterialName}' in XZ slice Y={fixedY}.");

        float[] imageTensor = BitmapToFloatTensor(baseXZ, _imageInputSize, _imageInputSize);
        var imageInput = new DenseTensor<float>(imageTensor, new[] { 1, 3, _imageInputSize, _imageInputSize });

        // encode
        Tensor<float> imageEmbeddings, highRes0, highRes1;
        using (var encOut = _encoderSession.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("image", imageInput)
        }))
        {
            imageEmbeddings = GetFirstTensorOrNull<float>(encOut, "image_embed");
            highRes0 = GetFirstTensorOrNull<float>(encOut, "high_res_feats_0");
            highRes1 = GetFirstTensorOrNull<float>(encOut, "high_res_feats_1");
        }
        if (imageEmbeddings == null || highRes0 == null || highRes1 == null)
        {
            Log("[ProcessXZSlice] Encoder outputs missing. Returning null.");
            return null;
        }

        int origW = baseXZ.Width;
        int origH = baseXZ.Height;
        var (coordTensor, labelTensor) = BuildSinglePromptTensors(slicePoints, origW, origH, targetMaterialName);

        var origSizeTensor = new DenseTensor<int>(new[] { origH, origW }, new[] { 2 });
        int batchSize = coordTensor.Dimensions[0];
        var maskInputTensor = new DenseTensor<float>(new float[batchSize * 1 * 256 * 256], new[] { batchSize, 1, 256, 256 });
        var hasMaskInputTensor = new DenseTensor<float>(new float[batchSize], new[] { batchSize });

        Tensor<float> masksTensor, iouTensor;
        using (var decOut = _decoderSession.Run(new List<NamedOnnxValue> {
            NamedOnnxValue.CreateFromTensor("image_embed",      imageEmbeddings),
            NamedOnnxValue.CreateFromTensor("high_res_feats_0", highRes0),
            NamedOnnxValue.CreateFromTensor("high_res_feats_1", highRes1),
            NamedOnnxValue.CreateFromTensor("point_coords",     coordTensor),
            NamedOnnxValue.CreateFromTensor("point_labels",     labelTensor),
            NamedOnnxValue.CreateFromTensor("mask_input",       maskInputTensor),
            NamedOnnxValue.CreateFromTensor("has_mask_input",   hasMaskInputTensor),
            NamedOnnxValue.CreateFromTensor("orig_im_size",     origSizeTensor)
        }))
        {
            masksTensor = GetFirstTensorOrNull<float>(decOut, "masks");
            iouTensor = GetFirstTensorOrNull<float>(decOut, "iou_predictions");
        }
        if (masksTensor == null || iouTensor == null)
            return null;

        int outC = masksTensor.Dimensions[1]; // typically 3
        if (outC == 0) return null;

        // pick best
        float bestIoU = float.MinValue;
        int bestIndex = 0;
        for (int c = 0; c < outC; c++)
        {
            float iou = iouTensor[0, c];
            if (iou > bestIoU)
            {
                bestIoU = iou;
                bestIndex = c;
            }
        }
        Log($"[ProcessXZSlice] bestIndex={bestIndex}, IoU={bestIoU:F3}");

        int maskH = masksTensor.Dimensions[2];
        int maskW = masksTensor.Dimensions[3];
        var finalMask = BuildMaskFromDecoder(masksTensor, bestIndex, maskW, maskH, origW, origH, out maskBits);
        return finalMask;
    }

    public Bitmap ProcessXZSlice(
        int fixedY,
        Bitmap baseXZ,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName)
    {
        bool[,] dummy;
        return ProcessXZSlice(fixedY, baseXZ, slicePoints, targetMaterialName, out dummy);
    }

    // ------------------------------------------------------------------------
    // YZ methods
    // ------------------------------------------------------------------------
    /// <summary>
    /// Single best mask for YZ slice.
    /// </summary>
    public Bitmap ProcessYZSlice(
        int fixedX,
        Bitmap baseYZ,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName,
        out bool[,] maskBits)
    {
        maskBits = null;
        if (_encoderSession == null || _decoderSession == null)
        {
            Log("[ProcessYZSlice] No loaded sessions. Returning null.");
            return null;
        }
        if (baseYZ == null || slicePoints == null || slicePoints.Count == 0)
        {
            Log("[ProcessYZSlice] Invalid input data. Returning null.");
            return null;
        }
        Log($"[ProcessYZSlice] Segmenting '{targetMaterialName}' in YZ slice X={fixedX}.");

        float[] imageTensor = BitmapToFloatTensor(baseYZ, _imageInputSize, _imageInputSize);
        var imageInput = new DenseTensor<float>(imageTensor, new[] { 1, 3, _imageInputSize, _imageInputSize });

        Tensor<float> imageEmbeddings, highRes0, highRes1;
        using (var encOut = _encoderSession.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("image", imageInput)
        }))
        {
            imageEmbeddings = GetFirstTensorOrNull<float>(encOut, "image_embed");
            highRes0 = GetFirstTensorOrNull<float>(encOut, "high_res_feats_0");
            highRes1 = GetFirstTensorOrNull<float>(encOut, "high_res_feats_1");
        }
        if (imageEmbeddings == null || highRes0 == null || highRes1 == null)
            return null;

        int origW = baseYZ.Width;
        int origH = baseYZ.Height;
        var (coordTensor, labelTensor) = BuildSinglePromptTensors(slicePoints, origW, origH, targetMaterialName);

        var origSizeTensor = new DenseTensor<int>(new[] { origH, origW }, new[] { 2 });
        int batchSize = coordTensor.Dimensions[0];
        var maskInputTensor = new DenseTensor<float>(new float[batchSize * 1 * 256 * 256], new[] { batchSize, 1, 256, 256 });
        var hasMaskInputTensor = new DenseTensor<float>(new float[batchSize], new[] { batchSize });

        Tensor<float> masksTensor, iouTensor;
        using (var decOut = _decoderSession.Run(new List<NamedOnnxValue> {
            NamedOnnxValue.CreateFromTensor("image_embed",      imageEmbeddings),
            NamedOnnxValue.CreateFromTensor("high_res_feats_0", highRes0),
            NamedOnnxValue.CreateFromTensor("high_res_feats_1", highRes1),
            NamedOnnxValue.CreateFromTensor("point_coords",     coordTensor),
            NamedOnnxValue.CreateFromTensor("point_labels",     labelTensor),
            NamedOnnxValue.CreateFromTensor("mask_input",       maskInputTensor),
            NamedOnnxValue.CreateFromTensor("has_mask_input",   hasMaskInputTensor),
            NamedOnnxValue.CreateFromTensor("orig_im_size",     origSizeTensor)
        }))
        {
            masksTensor = GetFirstTensorOrNull<float>(decOut, "masks");
            iouTensor = GetFirstTensorOrNull<float>(decOut, "iou_predictions");
        }
        if (masksTensor == null || iouTensor == null)
            return null;

        int outC = masksTensor.Dimensions[1];
        if (outC == 0) return null;

        float bestIoU = float.MinValue;
        int bestIndex = 0;
        for (int c = 0; c < outC; c++)
        {
            float iou = iouTensor[0, c];
            if (iou > bestIoU)
            {
                bestIoU = iou;
                bestIndex = c;
            }
        }

        int maskH = masksTensor.Dimensions[2];
        int maskW = masksTensor.Dimensions[3];
        var finalMask = BuildMaskFromDecoder(masksTensor, bestIndex, maskW, maskH, origW, origH, out maskBits);
        return finalMask;
    }

    public Bitmap ProcessYZSlice(
        int fixedX,
        Bitmap baseYZ,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName)
    {
        bool[,] ignore;
        return ProcessYZSlice(fixedX, baseYZ, slicePoints, targetMaterialName, out ignore);
    }

    // ------------------------------------------------------------------------
    // "GetAllMasks" multi-candidate versions (Optional)
    // ------------------------------------------------------------------------
    // The multi-mask code is not strictly required for basic propagation. It’s shown
    // here just to illustrate how you can retrieve all candidate masks from SAM.
    // You can remove or keep these as you prefer.

    public List<Bitmap> ProcessXYSlice_GetAllMasks(
        int sliceIndex,
        Bitmap baseXY,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName)
    {
        var results = new List<Bitmap>();
        if (_encoderSession == null || _decoderSession == null) return results;
        if (baseXY == null || slicePoints == null || slicePoints.Count == 0) return results;

        // Encode
        float[] imageTensor = BitmapToFloatTensor(baseXY, _imageInputSize, _imageInputSize);
        var imageInput = new DenseTensor<float>(imageTensor, new[] { 1, 3, _imageInputSize, _imageInputSize });
        Tensor<float> imageEmbeddings, highRes0, highRes1;
        using (var encOut = _encoderSession.Run(new[] { NamedOnnxValue.CreateFromTensor("image", imageInput) }))
        {
            imageEmbeddings = GetFirstTensorOrNull<float>(encOut, "image_embed");
            highRes0 = GetFirstTensorOrNull<float>(encOut, "high_res_feats_0");
            highRes1 = GetFirstTensorOrNull<float>(encOut, "high_res_feats_1");
        }
        if (imageEmbeddings == null || highRes0 == null || highRes1 == null) return results;

        // Prompt
        int origW = baseXY.Width;
        int origH = baseXY.Height;
        var (coordTensor, labelTensor) = BuildSinglePromptTensors(slicePoints, origW, origH, targetMaterialName);

        var origSizeTensor = new DenseTensor<int>(new[] { origH, origW }, new[] { 2 });
        int batchSize = coordTensor.Dimensions[0];
        var maskInputTensor = new DenseTensor<float>(new float[batchSize * 1 * 256 * 256], new[] { batchSize, 1, 256, 256 });
        var hasMaskInputTensor = new DenseTensor<float>(new float[batchSize], new[] { batchSize });

        // Decode
        Tensor<float> masksTensor, iouTensor;
        using (var decOut = _decoderSession.Run(new List<NamedOnnxValue> {
            NamedOnnxValue.CreateFromTensor("image_embed",      imageEmbeddings),
            NamedOnnxValue.CreateFromTensor("high_res_feats_0", highRes0),
            NamedOnnxValue.CreateFromTensor("high_res_feats_1", highRes1),
            NamedOnnxValue.CreateFromTensor("point_coords",     coordTensor),
            NamedOnnxValue.CreateFromTensor("point_labels",     labelTensor),
            NamedOnnxValue.CreateFromTensor("mask_input",       maskInputTensor),
            NamedOnnxValue.CreateFromTensor("has_mask_input",   hasMaskInputTensor),
            NamedOnnxValue.CreateFromTensor("orig_im_size",     origSizeTensor)
        }))
        {
            masksTensor = GetFirstTensorOrNull<float>(decOut, "masks");
            iouTensor = GetFirstTensorOrNull<float>(decOut, "iou_predictions");
        }
        if (masksTensor == null || iouTensor == null) return results;
        int outC = masksTensor.Dimensions[1];
        if (outC == 0) return results;

        // Sort channels by IoU
        var iouList = new List<(float iouVal, int channel)>();
        for (int c = 0; c < outC; c++)
            iouList.Add((iouTensor[0, c], c));
        iouList.Sort((a, b) => b.iouVal.CompareTo(a.iouVal));

        int maskH = masksTensor.Dimensions[2];
        int maskW = masksTensor.Dimensions[3];
        foreach (var entry in iouList)
        {
            bool[,] dummy;
            Bitmap candidate = BuildMaskFromDecoder(masksTensor, entry.channel, maskW, maskH, origW, origH, out dummy);
            results.Add(candidate);
        }
        return results;
    }

    /// <summary>
    /// Multi-mask version for XZ. Returns all candidate masks as a List, sorted by IoU descending.
    /// </summary>
    public List<Bitmap> ProcessXZSlice_GetAllMasks(
        int fixedY,
        Bitmap baseXZ,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName)
    {
        var results = new List<Bitmap>();
        if (_encoderSession == null || _decoderSession == null) return results;
        if (baseXZ == null || slicePoints == null || slicePoints.Count == 0) return results;

        // 1) Encode XZ slice
        float[] imageTensor = BitmapToFloatTensor(baseXZ, _imageInputSize, _imageInputSize);
        var imageInput = new DenseTensor<float>(imageTensor, new[] { 1, 3, _imageInputSize, _imageInputSize });

        Tensor<float> imageEmbeddings, highRes0, highRes1;
        using (var encOut = _encoderSession.Run(new[]
        {
        NamedOnnxValue.CreateFromTensor("image", imageInput)
    }))
        {
            imageEmbeddings = GetFirstTensorOrNull<float>(encOut, "image_embed");
            highRes0 = GetFirstTensorOrNull<float>(encOut, "high_res_feats_0");
            highRes1 = GetFirstTensorOrNull<float>(encOut, "high_res_feats_1");
        }
        if (imageEmbeddings == null || highRes0 == null || highRes1 == null)
            return results;

        // 2) Build prompt
        int origW = baseXZ.Width;
        int origH = baseXZ.Height;
        var (coordTensor, labelTensor) = BuildSinglePromptTensors(slicePoints, origW, origH, targetMaterialName);

        // 3) Prepare other decoder inputs
        var origSizeTensor = new DenseTensor<int>(new[] { origH, origW }, new[] { 2 });
        var maskInputTensor = new DenseTensor<float>(new float[1 * 1 * 256 * 256], new[] { 1, 1, 256, 256 });
        var hasMaskInputTensor = new DenseTensor<float>(new float[1], new[] { 1 });

        // 4) Run decoder
        Tensor<float> masksTensor, iouTensor;
        using (var decOut = _decoderSession.Run(new List<NamedOnnxValue>
    {
        NamedOnnxValue.CreateFromTensor("image_embed",      imageEmbeddings),
        NamedOnnxValue.CreateFromTensor("high_res_feats_0", highRes0),
        NamedOnnxValue.CreateFromTensor("high_res_feats_1", highRes1),
        NamedOnnxValue.CreateFromTensor("point_coords",     coordTensor),
        NamedOnnxValue.CreateFromTensor("point_labels",     labelTensor),
        NamedOnnxValue.CreateFromTensor("mask_input",       maskInputTensor),
        NamedOnnxValue.CreateFromTensor("has_mask_input",   hasMaskInputTensor),
        NamedOnnxValue.CreateFromTensor("orig_im_size",     origSizeTensor)
    }))
        {
            masksTensor = GetFirstTensorOrNull<float>(decOut, "masks");         // shape [1,3,256,256]
            iouTensor = GetFirstTensorOrNull<float>(decOut, "iou_predictions"); // shape [1,3]
        }
        if (masksTensor == null || iouTensor == null) return results;

        // 5) Sort the decoder’s channels by IoU
        int outC = masksTensor.Dimensions[1];
        var iouList = new List<(float iouVal, int channel)>();
        for (int c = 0; c < outC; c++)
            iouList.Add((iouTensor[0, c], c));
        iouList.Sort((a, b) => b.iouVal.CompareTo(a.iouVal)); // descending

        // 6) Build mask bitmaps for each channel
        int maskH = masksTensor.Dimensions[2]; // 256
        int maskW = masksTensor.Dimensions[3]; // 256
        foreach (var entry in iouList)
        {
            bool[,] dummy;
            Bitmap candidate = BuildMaskFromDecoder(masksTensor, entry.channel, maskW, maskH, origW, origH, out dummy);
            results.Add(candidate);
        }

        return results;
    }

    /// <summary>
    /// Multi-mask version for YZ. Returns all candidate masks as a List, sorted by IoU descending.
    /// </summary>
    public List<Bitmap> ProcessYZSlice_GetAllMasks(
        int fixedX,
        Bitmap baseYZ,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName)
    {
        var results = new List<Bitmap>();
        if (_encoderSession == null || _decoderSession == null) return results;
        if (baseYZ == null || slicePoints == null || slicePoints.Count == 0) return results;

        // 1) Encode YZ slice
        float[] imageTensor = BitmapToFloatTensor(baseYZ, _imageInputSize, _imageInputSize);
        var imageInput = new DenseTensor<float>(imageTensor, new[] { 1, 3, _imageInputSize, _imageInputSize });

        Tensor<float> imageEmbeddings, highRes0, highRes1;
        using (var encOut = _encoderSession.Run(new[]
        {
        NamedOnnxValue.CreateFromTensor("image", imageInput)
    }))
        {
            imageEmbeddings = GetFirstTensorOrNull<float>(encOut, "image_embed");
            highRes0 = GetFirstTensorOrNull<float>(encOut, "high_res_feats_0");
            highRes1 = GetFirstTensorOrNull<float>(encOut, "high_res_feats_1");
        }
        if (imageEmbeddings == null || highRes0 == null || highRes1 == null)
            return results;

        // 2) Build prompt
        int origW = baseYZ.Width;
        int origH = baseYZ.Height;
        var (coordTensor, labelTensor) = BuildSinglePromptTensors(slicePoints, origW, origH, targetMaterialName);

        // 3) Prepare other decoder inputs
        var origSizeTensor = new DenseTensor<int>(new[] { origH, origW }, new[] { 2 });
        var maskInputTensor = new DenseTensor<float>(new float[1 * 1 * 256 * 256], new[] { 1, 1, 256, 256 });
        var hasMaskInputTensor = new DenseTensor<float>(new float[1], new[] { 1 });

        // 4) Run decoder
        Tensor<float> masksTensor, iouTensor;
        using (var decOut = _decoderSession.Run(new List<NamedOnnxValue>
    {
        NamedOnnxValue.CreateFromTensor("image_embed",      imageEmbeddings),
        NamedOnnxValue.CreateFromTensor("high_res_feats_0", highRes0),
        NamedOnnxValue.CreateFromTensor("high_res_feats_1", highRes1),
        NamedOnnxValue.CreateFromTensor("point_coords",     coordTensor),
        NamedOnnxValue.CreateFromTensor("point_labels",     labelTensor),
        NamedOnnxValue.CreateFromTensor("mask_input",       maskInputTensor),
        NamedOnnxValue.CreateFromTensor("has_mask_input",   hasMaskInputTensor),
        NamedOnnxValue.CreateFromTensor("orig_im_size",     origSizeTensor)
    }))
        {
            masksTensor = GetFirstTensorOrNull<float>(decOut, "masks");         // shape [1,3,256,256]
            iouTensor = GetFirstTensorOrNull<float>(decOut, "iou_predictions"); // shape [1,3]
        }
        if (masksTensor == null || iouTensor == null) return results;

        // 5) Sort by IoU
        int outC = masksTensor.Dimensions[1];
        var iouList = new List<(float iouVal, int channel)>();
        for (int c = 0; c < outC; c++)
            iouList.Add((iouTensor[0, c], c));
        iouList.Sort((a, b) => b.iouVal.CompareTo(a.iouVal));

        // 6) Build mask bitmaps for each channel
        int maskH = masksTensor.Dimensions[2]; // 256
        int maskW = masksTensor.Dimensions[3]; // 256
        foreach (var entry in iouList)
        {
            bool[,] dummy;
            Bitmap candidate = BuildMaskFromDecoder(masksTensor, entry.channel, maskW, maskH, origW, origH, out dummy);
            results.Add(candidate);
        }

        return results;
    }


    // ------------------------------------------------------------------------
    // Private helper: Build prompt from points => label=1 if matches targetMaterial, else 0
    // ------------------------------------------------------------------------
    private (DenseTensor<float>, DenseTensor<float>) BuildSinglePromptTensors(
        List<AnnotationPoint> prompts,
        int origW, int origH,
        string targetMaterialName)
    {
        // Scale XY coords to [0..(imageInputSize-1)]
        float xScale = (_imageInputSize - 1f) / Math.Max(1, origW - 1);
        float yScale = (_imageInputSize - 1f) / Math.Max(1, origH - 1);

        var coordsList = new List<float>();
        var labelsList = new List<float>();

        foreach (var p in prompts)
        {
            float cx = Math.Max(0, Math.Min(p.X, origW - 1));
            float cy = Math.Max(0, Math.Min(p.Y, origH - 1));

            float row = cy * yScale;
            float col = cx * xScale;
            coordsList.Add(row);
            coordsList.Add(col);

            // label = 1 if p.Label == targetMaterialName, else 0
            bool isForeground = p.Label.Equals(targetMaterialName, StringComparison.OrdinalIgnoreCase);
            labelsList.Add(isForeground ? 1f : 0f);
        }

        int numPoints = prompts.Count;
        if (numPoints == 0)
        {
            // Provide a dummy point if absolutely none exist
            coordsList.Add(0f); coordsList.Add(0f);
            labelsList.Add(0f);
            numPoints = 1;
        }

        var coordTensor = new DenseTensor<float>(coordsList.ToArray(), new[] { 1, numPoints, 2 });
        var labelTensor = new DenseTensor<float>(labelsList.ToArray(), new[] { 1, numPoints });
        return (coordTensor, labelTensor);
    }

    // ------------------------------------------------------------------------
    // Private helper: Convert raw logits => binarized mask => upscaled to original size
    // ------------------------------------------------------------------------
    private Bitmap BuildMaskFromDecoder(
        Tensor<float> masksTensor,
        int maskIndex,
        int maskW, int maskH,
        int outW, int outH,
        out bool[,] maskBits)
    {
        float threshold = MaskBinarizationThreshold;
        maskBits = new bool[maskH, maskW];

        // Build a 256x256 raw mask
        Bitmap rawMask = new Bitmap(maskW, maskH, PixelFormat.Format24bppRgb);

        // decode channel c
        for (int yy = 0; yy < maskH; yy++)
        {
            for (int xx = 0; xx < maskW; xx++)
            {
                float logit = masksTensor[0, maskIndex, yy, xx];
                float prob = 1f / (1f + (float)Math.Exp(-logit)); // sigmoid
                bool isFg = (prob >= threshold);

                if (isFg)
                {
                    rawMask.SetPixel(xx, yy, Color.White);
                    maskBits[yy, xx] = true;
                }
                else
                {
                    rawMask.SetPixel(xx, yy, Color.Black);
                    maskBits[yy, xx] = false;
                }
            }
        }

        // upscale to outW,outH with nearest neighbor
        Bitmap finalMask = new Bitmap(outW, outH, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(finalMask))
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImage(rawMask, 0, 0, outW, outH);
        }
        rawMask.Dispose();

        return finalMask;
    }

    // ------------------------------------------------------------------------
    // Private helper: Convert a Bitmap => float[] => (C,H,W) with SAM normalization
    // ------------------------------------------------------------------------
    private float[] BitmapToFloatTensor(Bitmap bmp, int targetWidth, int targetHeight)
    {
        // SAM's typical pixel mean/std
        float[] pixelMean = { 123.675f, 116.28f, 103.53f };
        float[] pixelStd = { 58.395f, 57.12f, 57.375f };

        Bitmap resized = new Bitmap(targetWidth, targetHeight);
        using (Graphics g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(bmp, 0, 0, targetWidth, targetHeight);
        }

        // Flatten in CHW order
        float[] data = new float[3 * targetWidth * targetHeight];
        int idx = 0;
        for (int y = 0; y < targetHeight; y++)
        {
            for (int x = 0; x < targetWidth; x++)
            {
                Color c = resized.GetPixel(x, y);
                float r = c.R, gVal = c.G, b = c.B;

                data[idx++] = (r - pixelMean[0]) / pixelStd[0];
                data[idx++] = (gVal - pixelMean[1]) / pixelStd[1];
                data[idx++] = (b - pixelMean[2]) / pixelStd[2];
            }
        }
        resized.Dispose();
        return data;
    }

    // ------------------------------------------------------------------------
    // Private helper: retrieving NamedOnnxValue by name
    // ------------------------------------------------------------------------
    private Tensor<T> GetFirstTensorOrNull<T>(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        string name)
    {
        foreach (var o in outputs)
        {
            if (o.Name == name)
                return o.AsTensor<T>();
        }
        return null; // not found
    }

    // ------------------------------------------------------------------------
    // Region: "with mask input" methods (used in the new SAM2.1 propagation)
    // ------------------------------------------------------------------------
    #region PropagationHelpers

    /// <summary>
    /// Process XY slice with optional 'prevMaskLogits' as mask_input. Returns a binarized mask bool[,] (true=FG).
    /// </summary>
    public bool[,] ProcessXYSlice_WithMaskInput(
        Bitmap baseXY,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName,
        float[] prevMaskLogits,
        int origW,
        int origH)
    {
        if (_encoderSession == null || _decoderSession == null)
        {
            Log("[ProcessXYSlice_WithMaskInput] No loaded sessions. Returning null.");
            return null;
        }
        if (baseXY == null)
        {
            Log("[ProcessXYSlice_WithMaskInput] baseXY is null. Returning null.");
            return null;
        }

        // (1) Encode image
        float[] imageTensor = BitmapToFloatTensor(baseXY, _imageInputSize, _imageInputSize);
        var imageInput = new DenseTensor<float>(imageTensor, new[] { 1, 3, _imageInputSize, _imageInputSize });
        Tensor<float> imgEmbed, hr0, hr1;
        using (var encOut = _encoderSession.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("image", imageInput)
        }))
        {
            imgEmbed = GetFirstTensorOrNull<float>(encOut, "image_embed");
            hr0 = GetFirstTensorOrNull<float>(encOut, "high_res_feats_0");
            hr1 = GetFirstTensorOrNull<float>(encOut, "high_res_feats_1");
        }
        if (imgEmbed == null || hr0 == null || hr1 == null)
        {
            Log("[ProcessXYSlice_WithMaskInput] Encoder outputs missing. Returning null.");
            return null;
        }

        // (2) Build point prompts => (1, numPoints, 2) coords and (1, numPoints) labels
        var (coordTensor, labelTensor) = BuildSinglePromptTensors(slicePoints, origW, origH, targetMaterialName);

        // (3) Build mask_input from prevMaskLogits or zeros
        DenseTensor<float> maskTensor;
        DenseTensor<float> hasMaskTensor;
        if (prevMaskLogits != null && prevMaskLogits.Length == 1 * 1 * 256 * 256)
        {
            maskTensor = new DenseTensor<float>(prevMaskLogits, new[] { 1, 1, 256, 256 });
            hasMaskTensor = new DenseTensor<float>(new float[] { 1 }, new[] { 1 });
        }
        else
        {
            maskTensor = new DenseTensor<float>(new float[1 * 1 * 256 * 256], new[] { 1, 1, 256, 256 });
            hasMaskTensor = new DenseTensor<float>(new float[] { 0 }, new[] { 1 });
        }

        // (4) orig_im_size
        var origSizeTensor = new DenseTensor<int>(new[] { origH, origW }, new[] { 2 });

        // (5) Decoder => masks + iou
        Tensor<float> masksTensor, iouTensor;
        using (var decOut = _decoderSession.Run(new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("image_embed",      imgEmbed),
            NamedOnnxValue.CreateFromTensor("high_res_feats_0", hr0),
            NamedOnnxValue.CreateFromTensor("high_res_feats_1", hr1),
            NamedOnnxValue.CreateFromTensor("point_coords",     coordTensor),
            NamedOnnxValue.CreateFromTensor("point_labels",     labelTensor),
            NamedOnnxValue.CreateFromTensor("mask_input",       maskTensor),
            NamedOnnxValue.CreateFromTensor("has_mask_input",   hasMaskTensor),
            NamedOnnxValue.CreateFromTensor("orig_im_size",     origSizeTensor)
        }))
        {
            masksTensor = GetFirstTensorOrNull<float>(decOut, "masks");
            iouTensor = GetFirstTensorOrNull<float>(decOut, "iou_predictions");
        }
        if (masksTensor == null || iouTensor == null)
        {
            Log("[ProcessXYSlice_WithMaskInput] Decoder output missing. Returning null.");
            return null;
        }

        // (6) Pick best channel by IoU
        int outC = masksTensor.Dimensions[1]; // typically 3
        float bestIoU = float.MinValue;
        int bestIndex = 0;
        for (int c = 0; c < outC; c++)
        {
            float iouVal = iouTensor[0, c];
            if (iouVal > bestIoU)
            {
                bestIoU = iouVal;
                bestIndex = c;
            }
        }
        Log($"[ProcessXYSlice_WithMaskInput] Best channel={bestIndex}, IoU={bestIoU:F3}");

        // (7) Convert logits => bool[,] 
        bool[,] binMask = BuildMaskArrayFromLogits(masksTensor, bestIndex, origW, origH);
        return binMask;
    }

    /// <summary>
    /// Process XZ slice with optional 'prevMaskLogits' as mask_input.
    /// Returns a binarized bool[,] mask.
    /// </summary>
    public bool[,] ProcessXZSlice_WithMaskInput(
        Bitmap baseXZ,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName,
        float[] prevMaskLogits,
        int origW,
        int origH)
    {
        if (_encoderSession == null || _decoderSession == null)
        {
            Log("[ProcessXZSlice_WithMaskInput] No loaded sessions. Returning null.");
            return null;
        }
        if (baseXZ == null)
        {
            Log("[ProcessXZSlice_WithMaskInput] baseXZ is null. Returning null.");
            return null;
        }

        // (1) Encode
        float[] imageTensor = BitmapToFloatTensor(baseXZ, _imageInputSize, _imageInputSize);
        var imageInput = new DenseTensor<float>(imageTensor, new[] { 1, 3, _imageInputSize, _imageInputSize });
        Tensor<float> imgEmbed, hr0, hr1;
        using (var encOut = _encoderSession.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("image", imageInput)
        }))
        {
            imgEmbed = GetFirstTensorOrNull<float>(encOut, "image_embed");
            hr0 = GetFirstTensorOrNull<float>(encOut, "high_res_feats_0");
            hr1 = GetFirstTensorOrNull<float>(encOut, "high_res_feats_1");
        }
        if (imgEmbed == null || hr0 == null || hr1 == null)
        {
            Log("[ProcessXZSlice_WithMaskInput] Encoder outputs missing. Returning null.");
            return null;
        }

        // (2) Prompts
        var (coordTensor, labelTensor) = BuildSinglePromptTensors(slicePoints, origW, origH, targetMaterialName);

        // (3) Mask input
        DenseTensor<float> maskTensor;
        DenseTensor<float> hasMaskTensor;
        if (prevMaskLogits != null && prevMaskLogits.Length == 1 * 1 * 256 * 256)
        {
            maskTensor = new DenseTensor<float>(prevMaskLogits, new[] { 1, 1, 256, 256 });
            hasMaskTensor = new DenseTensor<float>(new float[] { 1 }, new[] { 1 });
        }
        else
        {
            maskTensor = new DenseTensor<float>(new float[1 * 1 * 256 * 256], new[] { 1, 1, 256, 256 });
            hasMaskTensor = new DenseTensor<float>(new float[] { 0 }, new[] { 1 });
        }

        // (4) orig_im_size => [origH, origW]
        var origSizeTensor = new DenseTensor<int>(new[] { origH, origW }, new[] { 2 });

        // (5) Decode
        Tensor<float> masksTensor, iouTensor;
        using (var decOut = _decoderSession.Run(new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("image_embed",      imgEmbed),
            NamedOnnxValue.CreateFromTensor("high_res_feats_0", hr0),
            NamedOnnxValue.CreateFromTensor("high_res_feats_1", hr1),
            NamedOnnxValue.CreateFromTensor("point_coords",     coordTensor),
            NamedOnnxValue.CreateFromTensor("point_labels",     labelTensor),
            NamedOnnxValue.CreateFromTensor("mask_input",       maskTensor),
            NamedOnnxValue.CreateFromTensor("has_mask_input",   hasMaskTensor),
            NamedOnnxValue.CreateFromTensor("orig_im_size",     origSizeTensor)
        }))
        {
            masksTensor = GetFirstTensorOrNull<float>(decOut, "masks");
            iouTensor = GetFirstTensorOrNull<float>(decOut, "iou_predictions");
        }
        if (masksTensor == null || iouTensor == null)
        {
            Log("[ProcessXZSlice_WithMaskInput] Decoder output missing. Returning null.");
            return null;
        }

        // (6) Pick best by IoU
        int outC = masksTensor.Dimensions[1];
        float bestIoU = float.MinValue;
        int bestIndex = 0;
        for (int c = 0; c < outC; c++)
        {
            float iouVal = iouTensor[0, c];
            if (iouVal > bestIoU)
            {
                bestIoU = iouVal;
                bestIndex = c;
            }
        }
        Log($"[ProcessXZSlice_WithMaskInput] Best channel={bestIndex}, IoU={bestIoU:F3}");

        // (7) Build binarized mask
        bool[,] binMask = BuildMaskArrayFromLogits(masksTensor, bestIndex, origW, origH);
        return binMask;
    }

    /// <summary>
    /// Process YZ slice with optional 'prevMaskLogits'. Returns a binarized bool[,] mask.
    /// </summary>
    public bool[,] ProcessYZSlice_WithMaskInput(
        Bitmap baseYZ,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName,
        float[] prevMaskLogits,
        int origW,
        int origH)
    {
        if (_encoderSession == null || _decoderSession == null)
        {
            Log("[ProcessYZSlice_WithMaskInput] No loaded sessions. Returning null.");
            return null;
        }
        if (baseYZ == null)
        {
            Log("[ProcessYZSlice_WithMaskInput] baseYZ is null. Returning null.");
            return null;
        }

        // (1) Encode
        float[] imageTensor = BitmapToFloatTensor(baseYZ, _imageInputSize, _imageInputSize);
        var imageInput = new DenseTensor<float>(imageTensor, new[] { 1, 3, _imageInputSize, _imageInputSize });
        Tensor<float> imgEmbed, hr0, hr1;
        using (var encOut = _encoderSession.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("image", imageInput)
        }))
        {
            imgEmbed = GetFirstTensorOrNull<float>(encOut, "image_embed");
            hr0 = GetFirstTensorOrNull<float>(encOut, "high_res_feats_0");
            hr1 = GetFirstTensorOrNull<float>(encOut, "high_res_feats_1");
        }
        if (imgEmbed == null || hr0 == null || hr1 == null)
        {
            Log("[ProcessYZSlice_WithMaskInput] Encoder outputs missing. Returning null.");
            return null;
        }

        // (2) Prompts
        var (coordTensor, labelTensor) = BuildSinglePromptTensors(slicePoints, origW, origH, targetMaterialName);

        // (3) Mask input
        DenseTensor<float> maskTensor;
        DenseTensor<float> hasMaskTensor;
        if (prevMaskLogits != null && prevMaskLogits.Length == 1 * 1 * 256 * 256)
        {
            maskTensor = new DenseTensor<float>(prevMaskLogits, new[] { 1, 1, 256, 256 });
            hasMaskTensor = new DenseTensor<float>(new float[] { 1 }, new[] { 1 });
        }
        else
        {
            maskTensor = new DenseTensor<float>(new float[1 * 1 * 256 * 256], new[] { 1, 1, 256, 256 });
            hasMaskTensor = new DenseTensor<float>(new float[] { 0 }, new[] { 1 });
        }

        // (4) orig_im_size => [origH, origW]
        var origSizeTensor = new DenseTensor<int>(new[] { origH, origW }, new[] { 2 });

        // (5) Decode
        Tensor<float> masksTensor, iouTensor;
        using (var decOut = _decoderSession.Run(new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("image_embed",      imgEmbed),
            NamedOnnxValue.CreateFromTensor("high_res_feats_0", hr0),
            NamedOnnxValue.CreateFromTensor("high_res_feats_1", hr1),
            NamedOnnxValue.CreateFromTensor("point_coords",     coordTensor),
            NamedOnnxValue.CreateFromTensor("point_labels",     labelTensor),
            NamedOnnxValue.CreateFromTensor("mask_input",       maskTensor),
            NamedOnnxValue.CreateFromTensor("has_mask_input",   hasMaskTensor),
            NamedOnnxValue.CreateFromTensor("orig_im_size",     origSizeTensor)
        }))
        {
            masksTensor = GetFirstTensorOrNull<float>(decOut, "masks");
            iouTensor = GetFirstTensorOrNull<float>(decOut, "iou_predictions");
        }
        if (masksTensor == null || iouTensor == null)
        {
            Log("[ProcessYZSlice_WithMaskInput] Decoder output missing. Returning null.");
            return null;
        }

        // (6) Pick best
        int outC = masksTensor.Dimensions[1];
        float bestIoU = float.MinValue;
        int bestIndex = 0;
        for (int c = 0; c < outC; c++)
        {
            float iouVal = iouTensor[0, c];
            if (iouVal > bestIoU)
            {
                bestIoU = iouVal;
                bestIndex = c;
            }
        }
        Log($"[ProcessYZSlice_WithMaskInput] Best channel={bestIndex}, IoU={bestIoU:F3}");

        // (7) Convert to bool array
        bool[,] binMask = BuildMaskArrayFromLogits(masksTensor, bestIndex, origW, origH);
        return binMask;
    }

    /// <summary>
    /// Converts the selected channel’s logits (256x256) to a bool[,] at the original slice size,
    /// using nearest-neighbor upsampling and the MaskBinarizationThreshold property.
    /// </summary>
    private bool[,] BuildMaskArrayFromLogits(Tensor<float> masksTensor, int channelIndex,
                                             int origW, int origH)
    {
        int maskH = masksTensor.Dimensions[2]; // 256
        int maskW = masksTensor.Dimensions[3]; // 256
        bool[,] binMask = new bool[origH, origW];

        for (int y = 0; y < origH; y++)
        {
            int yy256 = (int)Math.Round((y / (float)(origH - 1)) * (maskH - 1));

            for (int x = 0; x < origW; x++)
            {
                int xx256 = (int)Math.Round((x / (float)(origW - 1)) * (maskW - 1));
                float logit = masksTensor[0, channelIndex, yy256, xx256];
                float prob = 1f / (1f + (float)Math.Exp(-logit));
                binMask[y, x] = (prob >= MaskBinarizationThreshold);
            }
        }
        return binMask;
    }

    #endregion
}
