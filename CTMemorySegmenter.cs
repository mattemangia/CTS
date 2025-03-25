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
/// A unified CTMemorySegmenter that supports both:
///  - The old constructor signatures with 9 or 10 arguments
///  - The new 2-argument constructor
///  - The StorePreviousEmbeddings and UseSelectiveHoleFilling properties
///  - XY/XZ/YZ slice methods returning binarized masks from float logits
/// </summary>
public class CTMemorySegmenter : IDisposable
{
    // Old fields from your legacy constructor
    private string _imageEncoderPath;
    private string _promptEncoderPath;
    private string _maskDecoderPath;
    private string _memoryEncoderPath;
    private string _memoryAttentionPath;
    private string _mlpPath;
    private int _imageInputSize;
    private bool _canUseTextPrompts;
    private bool _enableMlp;
    private bool _useCpuExecutionProvider;

    // For the new minimal constructor:
    private InferenceSession _encoderSession;
    private InferenceSession _decoderSession;

    // In older code, these were part of the class and used by some logic.
    // We'll keep them as public auto-properties to fix "does not contain a definition" errors.
    public bool StorePreviousEmbeddings { get; set; } = true;
    public bool UseSelectiveHoleFilling { get; set; } = false;

    /// <summary>
    /// Mask binarization threshold in [0..1]. Default 0.5 => logit=0.
    /// </summary>
    public float MaskBinarizationThreshold { get; set; } = 0.5f;

    private static void Log(string msg) => Debug.WriteLine(msg);

    #region Constructors

    /// <summary>
    /// New minimal constructor (2 arguments). 
    /// If your code calls only the old 9- or 10-argument versions, you can ignore this.
    /// </summary>
    public CTMemorySegmenter(string encoderOnnxPath, string decoderOnnxPath)
    {
        // Just store these two. If you want to actually load them now, do so:
        _imageEncoderPath = encoderOnnxPath;
        _maskDecoderPath = decoderOnnxPath;

        // Load them in a minimal way
        SessionOptions options = new SessionOptions();
        options.AppendExecutionProvider_CPU();  // or GPU if you like
        _encoderSession = new InferenceSession(encoderOnnxPath, options);
        _decoderSession = new InferenceSession(decoderOnnxPath, options);

        Log("[CTMemorySegmenter] Minimal 2-arg constructor loaded the sessions.");
    }

    /// <summary>
    /// Old 9-argument constructor signature from older code.
    /// Typically:
    /// (string imageEncoderPath,
    ///  string promptEncoderPath,
    ///  string maskDecoderPath,
    ///  string memoryEncoderPath,
    ///  string memoryAttentionPath,
    ///  string mlpPath,
    ///  int imageInputSize,
    ///  bool canUseTextPrompts,
    ///  bool enableMlp)
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
        : this(imageEncoderPath, promptEncoderPath, maskDecoderPath,
               memoryEncoderPath, memoryAttentionPath, mlpPath,
               imageInputSize, canUseTextPrompts, enableMlp, true) // default useCpu=true
    {
        // The chained call handles everything
    }

    /// <summary>
    /// Old 10-argument constructor signature from older code.
    /// Typically:
    /// (string imageEncoderPath,
    ///  string promptEncoderPath,
    ///  string maskDecoderPath,
    ///  string memoryEncoderPath,
    ///  string memoryAttentionPath,
    ///  string mlpPath,
    ///  int imageInputSize,
    ///  bool canUseTextPrompts,
    ///  bool enableMlp,
    ///  bool useCpuExecutionProvider)
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
        // Store them in fields so code referencing them won't break
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

        // For SAM 2.1 usage, we typically only need the encoder + decoder ONNX.
        // But let's do a minimal load so your code won't crash:
        SessionOptions options = new SessionOptions();
        if (!useCpuExecutionProvider)
        {
            try
            {
                // Attempt GPU or DML or CUDA, etc.
                // For example:
                // options.AppendExecutionProvider_CUDA();
                Log("[CTMemorySegmenter] Attempting GPU provider (dummy).");
            }
            catch (Exception ex)
            {
                Log("[CTMemorySegmenter] GPU not available, fallback to CPU: " + ex.Message);
                options = new SessionOptions();
                options.AppendExecutionProvider_CPU();
            }
        }
        else
        {
            options.AppendExecutionProvider_CPU();
        }

        // Actually load the two main models. If you are using the new SAM 2.1:
        //   imageEncoderPath => "sam2.1_large.encoder.onnx"
        //   maskDecoderPath  => "sam2.1_large.decoder.onnx"
        // We'll do a minimal check:
        if (File.Exists(_imageEncoderPath) && File.Exists(_maskDecoderPath))
        {
            _encoderSession = new InferenceSession(_imageEncoderPath, options);
            _decoderSession = new InferenceSession(_maskDecoderPath, options);
            Log("[CTMemorySegmenter] Sessions loaded for SAM 2.1 encoder/decoder.");
        }
        else
        {
            // If you have older model references, you could load them here too.
            Log("[CTMemorySegmenter] Could not find the specified encoder/decoder ONNX. Not loading sessions.");
        }
    }

    #endregion

    /// <summary>Dispose everything</summary>
    public void Dispose()
    {
        _encoderSession?.Dispose();
        _decoderSession?.Dispose();
    }

    // ------------------------------------------------------------------------
    // The XY / XZ / YZ methods as in your new code:
    // ------------------------------------------------------------------------

    /// <summary>
    /// Single best mask for XY slice. 
    /// Overload returns bool[,] mask bits as well.
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

        // 1) Preprocess (just assume baseXY is the correct grayscale or colored image).
        // If you want to do a grayscale rescale + Jet colormap, do so:
        //   baseXY = RescaleCT(baseXY);
        //   baseXY = ApplyJetColormap(baseXY);
        // We'll just skip that for brevity here.

        // 2) Encode
        float[] imageTensor = BitmapToFloatTensor(baseXY, _imageInputSize, _imageInputSize);
        var imageInput = new DenseTensor<float>(imageTensor, new[] { 1, 3, _imageInputSize, _imageInputSize });
        Tensor<float> imageEmbeddings, highRes0, highRes1;
        using (var encOut = _encoderSession.Run(new[] {
            NamedOnnxValue.CreateFromTensor("image", imageInput)
        }))
        {
            imageEmbeddings = GetFirstTensor<float>(encOut, "image_embeddings");
            highRes0 = GetFirstTensor<float>(encOut, "high_res_feats_0");
            highRes1 = GetFirstTensor<float>(encOut, "high_res_feats_1");
        }

        // 3) Build prompt
        int origW = baseXY.Width;
        int origH = baseXY.Height;
        var (coordTensor, labelTensor) = BuildSinglePromptTensors(slicePoints, origW, origH, targetMaterialName);
        var origSizeTensor = new DenseTensor<int>(new[] { origH, origW }, new[] { 2 });

        // For new SAM 2.1 style: we have mask_input and has_mask_input
        int batchSize = coordTensor.Dimensions[0]; // usually 1
        var maskInputTensor = new DenseTensor<float>(new float[batchSize * 1 * 256 * 256], new[] { batchSize, 1, 256, 256 });
        var hasMaskInputTensor = new DenseTensor<float>(new float[batchSize], new[] { batchSize });

        // 4) Decode
        Tensor<float> masksTensor, iouTensor;
        using (var decOut = _decoderSession.Run(new List<NamedOnnxValue> {
            NamedOnnxValue.CreateFromTensor("image_embed", imageEmbeddings),
            NamedOnnxValue.CreateFromTensor("high_res_feats_0", highRes0),
            NamedOnnxValue.CreateFromTensor("high_res_feats_1", highRes1),
            NamedOnnxValue.CreateFromTensor("point_coords", coordTensor),
            NamedOnnxValue.CreateFromTensor("point_labels", labelTensor),
            NamedOnnxValue.CreateFromTensor("mask_input", maskInputTensor),
            NamedOnnxValue.CreateFromTensor("has_mask_input", hasMaskInputTensor),
            NamedOnnxValue.CreateFromTensor("orig_im_size", origSizeTensor)
        }))
        {
            masksTensor = GetFirstTensor<float>(decOut, "masks");  // shape [1,3,H,W]
            iouTensor = GetFirstTensor<float>(decOut, "iou_predictions"); // shape [1,3]
        }

        int outC = masksTensor.Dimensions[1]; // typically 3
        int maskH = masksTensor.Dimensions[2];
        int maskW = masksTensor.Dimensions[3];
        if (outC == 0)
        {
            Log("[ProcessXYSlice] Decoder returned 0 candidates. Null.");
            return null;
        }

        // 5) Pick best IoU
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
        Log($"[ProcessXYSlice] Best index={bestIndex}, IoU={bestIoU:F3}");

        // 6) Convert best mask (logits) -> binarized
        Bitmap finalMask = BuildMaskFromDecoder(masksTensor, bestIndex, maskW, maskH, origW, origH, out maskBits);

        return finalMask;
    }

    // Overload returning only the Bitmap
    public Bitmap ProcessXYSlice(
        int sliceIndex,
        Bitmap baseXY,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName)
    {
        bool[,] dummy;
        return ProcessXYSlice(sliceIndex, baseXY, slicePoints, targetMaterialName, out dummy);
    }

    // Similarly for XZ, YZ single + multi...
    // (Truncated for brevity – you can copy your XY logic and rename dimension references.)

    #region Helper Methods

    private static Tensor<T> GetFirstTensor<T>(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs, string name)
    {
        foreach (var o in outputs)
        {
            if (o.Name == name)
                return o.AsTensor<T>();
        }
        throw new Exception($"Output '{name}' not found.");
    }

    private (DenseTensor<float>, DenseTensor<float>) BuildSinglePromptTensors(
        List<AnnotationPoint> prompts, int origW, int origH, string targetMaterialName)
    {
        float xScale = (_imageInputSize - 1f) / Math.Max(1, origW - 1);
        float yScale = (_imageInputSize - 1f) / Math.Max(1, origH - 1);

        float[] coords = new float[prompts.Count * 2];
        float[] labels = new float[prompts.Count];

        for (int i = 0; i < prompts.Count; i++)
        {
            var p = prompts[i];
            float cx = Math.Max(0, Math.Min(p.X, origW - 1));
            float cy = Math.Max(0, Math.Min(p.Y, origH - 1));

            coords[i * 2 + 0] = cy * yScale;  // row
            coords[i * 2 + 1] = cx * xScale;  // col

            labels[i] = p.Label.Equals(targetMaterialName, StringComparison.OrdinalIgnoreCase) ? 1f : 0f;
        }

        var coordTensor = new DenseTensor<float>(coords, new[] { 1, prompts.Count, 2 });
        var labelTensor = new DenseTensor<float>(labels, new[] { 1, prompts.Count });
        return (coordTensor, labelTensor);
    }

    /// <summary>
    /// Takes the float32 mask logits from the decoder, applies sigmoid, thresholds at MaskBinarizationThreshold,
    /// then scales up to (outW,outH). Also returns a bool[,] array of the mask bits.
    /// </summary>
    private Bitmap BuildMaskFromDecoder(
        Tensor<float> masksTensor,
        int maskIndex,
        int maskW, int maskH,
        int outW, int outH,
        out bool[,] maskBits)
    {
        float threshold = MaskBinarizationThreshold; // e.g. 0.5
        maskBits = new bool[maskH, maskW];

        // Create a small rawMask
        Bitmap rawMask = new Bitmap(maskW, maskH, PixelFormat.Format24bppRgb);

        // Convert each pixel
        for (int yy = 0; yy < maskH; yy++)
        {
            for (int xx = 0; xx < maskW; xx++)
            {
                float logit = masksTensor[0, maskIndex, yy, xx];
                float prob = 1f / (1f + (float)Math.Exp(-logit)); // sigmoid
                bool isForeground = (prob >= threshold);

                if (isForeground)
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

        // Now scale rawMask up to outW,outH
        Bitmap finalMask = new Bitmap(outW, outH, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(finalMask))
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImage(rawMask, 0, 0, outW, outH);
        }
        rawMask.Dispose();
        return finalMask;
    }

    /// <summary>
    /// Converts a Bitmap to float[3*width*height], applying SAM’s pixel mean/std normalization.
    /// </summary>
    private float[] BitmapToFloatTensor(Bitmap bmp, int targetWidth, int targetHeight)
    {
        float[] pixelMean = { 123.675f, 116.28f, 103.53f };
        float[] pixelStd = { 58.395f, 57.12f, 57.375f };

        Bitmap resized = new Bitmap(targetWidth, targetHeight);
        using (Graphics g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(bmp, 0, 0, targetWidth, targetHeight);
        }

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

    #endregion
    #region Slice Overloads for XZ and YZ

    /// <summary>
    /// Processes an XZ slice and returns a single candidate mask.
    /// Currently, it reuses the XY processing logic; adjust as needed for XZ-specific behavior.
    /// </summary>
    public Bitmap ProcessXZSlice(int fixedY, Bitmap baseXZ, List<AnnotationPoint> slicePoints, string targetMaterialName)
    {
        // Reuse XY slice processing for now (you may modify this for proper XZ processing)
        return ProcessXYSlice(fixedY, baseXZ, slicePoints, targetMaterialName);
    }

    /// <summary>
    /// Processes an XZ slice and returns all candidate masks as a list.
    /// For now, it returns a single mask in a list.
    /// </summary>
    public List<Bitmap> ProcessXZSlice_GetAllMasks(int fixedY, Bitmap baseXZ, List<AnnotationPoint> slicePoints, string targetMaterialName)
    {
        Bitmap mask = ProcessXZSlice(fixedY, baseXZ, slicePoints, targetMaterialName);
        return new List<Bitmap> { mask };
    }

    /// <summary>
    /// Processes a YZ slice and returns a single candidate mask.
    /// Currently, it reuses the XY processing logic; adjust as needed for YZ-specific behavior.
    /// </summary>
    public Bitmap ProcessYZSlice(int fixedX, Bitmap baseYZ, List<AnnotationPoint> slicePoints, string targetMaterialName)
    {
        // Reuse XY slice processing for now (you may modify this for proper YZ processing)
        return ProcessXYSlice(fixedX, baseYZ, slicePoints, targetMaterialName);
    }

    /// <summary>
    /// Processes a YZ slice and returns all candidate masks as a list.
    /// For now, it returns a single mask in a list.
    /// </summary>
    public List<Bitmap> ProcessYZSlice_GetAllMasks(int fixedX, Bitmap baseYZ, List<AnnotationPoint> slicePoints, string targetMaterialName)
    {
        Bitmap mask = ProcessYZSlice(fixedX, baseYZ, slicePoints, targetMaterialName);
        return new List<Bitmap> { mask };
    }

    /// <summary>
    /// Processes an XY slice and returns all candidate masks as a list.
    /// For now, it returns a single mask in a list.
    /// </summary>
    public List<Bitmap> ProcessXYSlice_GetAllMasks(int sliceIndex, Bitmap baseXY, List<AnnotationPoint> slicePoints, string targetMaterialName)
    {
        Bitmap mask = ProcessXYSlice(sliceIndex, baseXY, slicePoints, targetMaterialName);
        return new List<Bitmap> { mask };
    }

    #endregion

}
