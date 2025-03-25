using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Linq;
using CTSegmenter;

public class CTMemorySegmenter : IDisposable
{
    private InferenceSession _encoderSession;
    private InferenceSession _decoderSession;
    private readonly int _imageSize = 1024;  // SAM image size (long side)
    public float MaskBinarizationThreshold { get; set; } = 0.5f;
    public bool DebugSaveRawMasks { get; set; } = false;
    public string DebugOutputDir { get; set; } = ".";
    public Action<string> LogAction { get; set; }  // optional external logger

    // Constructor: loads encoder + decoder onnx
    public CTMemorySegmenter(string encoderOnnxPath, string decoderOnnxPath)
    {
        // Try GPU first, fallback to CPU if unavailable
        var options = new SessionOptions();
        try
        {
            options.AppendExecutionProvider_CUDA();
            _encoderSession = new InferenceSession(encoderOnnxPath, options);
            _decoderSession = new InferenceSession(decoderOnnxPath, options);
            Log("[CTMemorySegmenter] SAM 2.1 model sessions loaded (GPU).");
        }
        catch
        {
            options.Dispose();
            var cpuOptions = new SessionOptions();
            _encoderSession?.Dispose();
            _decoderSession?.Dispose();
            _encoderSession = new InferenceSession(encoderOnnxPath, cpuOptions);
            _decoderSession = new InferenceSession(decoderOnnxPath, cpuOptions);
            Log("[CTMemorySegmenter] SAM 2.1 model sessions loaded (CPU).");
        }
    }

    public void Dispose()
    {
        _encoderSession?.Dispose();
        _decoderSession?.Dispose();
    }

    //---------------------------
    // XY Slices
    //---------------------------

    /// <summary>
    /// SINGLE best mask for an XY slice.
    /// Returns a Bitmap (white=FG) and also a bool[,] out param for the mask bits.
    /// </summary>
    public Bitmap ProcessXYSlice(
        int sliceIndex, Bitmap baseXY,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName,
        out bool[,] maskBits)
    {
        maskBits = null;
        if (baseXY == null || slicePoints == null || slicePoints.Count == 0)
            return null;

        Log($"[ProcessXYSlice] Segmenting '{targetMaterialName}' in XY slice Z={sliceIndex}.");

        // 1) Pre-process image
        Bitmap rescaled = RescaleCT(baseXY);
        Bitmap colorImage = ApplyJetColormap(rescaled);

        // 2) Encoder forward pass
        float[] imageTensorData = BitmapToFloatTensor(colorImage, _imageSize, _imageSize);
        var imageInput = new DenseTensor<float>(imageTensorData, new[] { 1, 3, _imageSize, _imageSize });
        Tensor<float> imageEmbed, highRes0, highRes1;
        using (var encOut = _encoderSession.Run(new[] {
            NamedOnnxValue.CreateFromTensor("image", imageInput)
        }))
        {
            imageEmbed = encOut.First(x => x.Name == "image_embeddings").AsTensor<float>();
            highRes0 = encOut.First(x => x.Name == "high_res_feats_0").AsTensor<float>();
            highRes1 = encOut.First(x => x.Name == "high_res_feats_1").AsTensor<float>();
        }

        // 3) Build prompt
        (DenseTensor<float> coordsTensor, DenseTensor<float> labelsTensor) =
            BuildSinglePromptTensors(slicePoints, baseXY.Width, baseXY.Height, targetMaterialName);
        var origSizeTensor = new DenseTensor<int>(new[] { baseXY.Height, baseXY.Width }, new[] { 2 });

        int batchSize = coordsTensor.Dimensions[0];  // typically 1
        // no prior mask
        var maskInputTensor = new DenseTensor<float>(new float[batchSize * 1 * 256 * 256], new[] { batchSize, 1, 256, 256 });
        var hasMaskInputTensor = new DenseTensor<float>(new float[batchSize], new[] { batchSize });

        // 4) Decoder => up to 3 masks + IoUs
        Tensor<float> masksTensor, iouTensor;
        using (var decOut = _decoderSession.Run(new List<NamedOnnxValue> {
            NamedOnnxValue.CreateFromTensor("image_embed", imageEmbed),
            NamedOnnxValue.CreateFromTensor("high_res_feats_0", highRes0),
            NamedOnnxValue.CreateFromTensor("high_res_feats_1", highRes1),
            NamedOnnxValue.CreateFromTensor("point_coords", coordsTensor),
            NamedOnnxValue.CreateFromTensor("point_labels", labelsTensor),
            NamedOnnxValue.CreateFromTensor("mask_input", maskInputTensor),
            NamedOnnxValue.CreateFromTensor("has_mask_input", hasMaskInputTensor),
            NamedOnnxValue.CreateFromTensor("orig_im_size", origSizeTensor)
        }))
        {
            masksTensor = decOut.First(x => x.Name == "masks").AsTensor<float>();
            iouTensor = decOut.First(x => x.Name == "iou_predictions").AsTensor<float>();
        }

        int numMasks = masksTensor.Dimensions[1];  // typically 3
        if (numMasks == 0)
        {
            Log("[ProcessXYSlice] Decoder returned zero masks.");
            return null;
        }
        int maskH = masksTensor.Dimensions[2];
        int maskW = masksTensor.Dimensions[3];

        // pick best IoU
        float bestIoU = -9999f;
        int bestIndex = 0;
        for (int i = 0; i < numMasks; i++)
        {
            float iou = iouTensor[0, i];
            if (iou > bestIoU) { bestIoU = iou; bestIndex = i; }
        }
        Log($"[ProcessXYSlice] bestIndex={bestIndex}, IoU={bestIoU:F3}");

        // convert best mask
        Bitmap bestMask = BuildMaskOutput(
            masksTensor, bestIndex, maskW, maskH,
            baseXY.Width, baseXY.Height,
            out maskBits, sliceIndex, "XY");

        return bestMask;
    }

    // Overload that returns only the Bitmap
    public Bitmap ProcessXYSlice(
        int sliceIndex, Bitmap baseXY,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName)
    {
        bool[,] dummy;
        return ProcessXYSlice(sliceIndex, baseXY, slicePoints, targetMaterialName, out dummy);
    }

    // Multi-candidate in XY
    public List<Bitmap> ProcessXYSlice_GetAllMasks(
        int sliceIndex, Bitmap baseXY,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName,
        out List<bool[,]> maskBitsList)
    {
        maskBitsList = new List<bool[,]>();
        var resultBitmaps = new List<Bitmap>();
        if (baseXY == null || slicePoints == null || slicePoints.Count == 0)
            return resultBitmaps;

        Log($"[ProcessXYSlice_GetAllMasks] Gathering all masks for '{targetMaterialName}' in XY slice Z={sliceIndex}.");

        Bitmap rescaled = RescaleCT(baseXY);
        Bitmap colorImage = ApplyJetColormap(rescaled);
        float[] imageTensorData = BitmapToFloatTensor(colorImage, _imageSize, _imageSize);
        var imageInput = new DenseTensor<float>(imageTensorData, new[] { 1, 3, _imageSize, _imageSize });
        Tensor<float> imageEmbed, highRes0, highRes1;
        using (var encOut = _encoderSession.Run(new[] {
            NamedOnnxValue.CreateFromTensor("image", imageInput)
        }))
        {
            imageEmbed = encOut.First(x => x.Name == "image_embeddings").AsTensor<float>();
            highRes0 = encOut.First(x => x.Name == "high_res_feats_0").AsTensor<float>();
            highRes1 = encOut.First(x => x.Name == "high_res_feats_1").AsTensor<float>();
        }

        (DenseTensor<float> coordsTensor, DenseTensor<float> labelsTensor) =
            BuildSinglePromptTensors(slicePoints, baseXY.Width, baseXY.Height, targetMaterialName);
        var origSizeTensor = new DenseTensor<int>(new[] { baseXY.Height, baseXY.Width }, new[] { 2 });
        int batchSize = coordsTensor.Dimensions[0];
        var maskInputTensor = new DenseTensor<float>(new float[batchSize * 1 * 256 * 256], new[] { batchSize, 1, 256, 256 });
        var hasMaskInputTensor = new DenseTensor<float>(new float[batchSize], new[] { batchSize });

        Tensor<float> masksTensor;
        using (var decOut = _decoderSession.Run(new List<NamedOnnxValue> {
            NamedOnnxValue.CreateFromTensor("image_embed", imageEmbed),
            NamedOnnxValue.CreateFromTensor("high_res_feats_0", highRes0),
            NamedOnnxValue.CreateFromTensor("high_res_feats_1", highRes1),
            NamedOnnxValue.CreateFromTensor("point_coords", coordsTensor),
            NamedOnnxValue.CreateFromTensor("point_labels", labelsTensor),
            NamedOnnxValue.CreateFromTensor("mask_input", maskInputTensor),
            NamedOnnxValue.CreateFromTensor("has_mask_input", hasMaskInputTensor),
            NamedOnnxValue.CreateFromTensor("orig_im_size", origSizeTensor)
        }))
        {
            masksTensor = decOut.First(x => x.Name == "masks").AsTensor<float>();
        }

        int outCount = masksTensor.Dimensions[1];
        if (outCount == 0) return resultBitmaps;

        int maskH = masksTensor.Dimensions[2];
        int maskW = masksTensor.Dimensions[3];
        for (int i = 0; i < outCount; i++)
        {
            Bitmap bmp = BuildMaskOutput(
                masksTensor, i, maskW, maskH,
                baseXY.Width, baseXY.Height,
                out bool[,] bits, sliceIndex, "XY");

            resultBitmaps.Add(bmp);
            maskBitsList.Add(bits);
        }
        if (resultBitmaps.Count == 3)
        {
            resultBitmaps.Add(new Bitmap(resultBitmaps[2]));
            maskBitsList.Add((bool[,])maskBitsList[2].Clone());
        }
        return resultBitmaps;
    }

    // Overload returning only bitmaps
    public List<Bitmap> ProcessXYSlice_GetAllMasks(
        int sliceIndex, Bitmap baseXY,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName)
    {
        List<bool[,]> dummy;
        return ProcessXYSlice_GetAllMasks(sliceIndex, baseXY, slicePoints, targetMaterialName, out dummy);
    }

    //---------------------------
    // XZ Slices
    //---------------------------

    /// <summary>
    /// SINGLE best mask for XZ slice (width=X, height=Z).
    /// </summary>
    public Bitmap ProcessXZSlice(
        int sliceIndex, Bitmap baseXZ,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName,
        out bool[,] maskBits)
    {
        maskBits = null;
        if (baseXZ == null || slicePoints == null || slicePoints.Count == 0)
            return null;

        Log($"[ProcessXZSlice] Segmenting '{targetMaterialName}' in XZ slice index={sliceIndex}.");

        // 1) Pre-process
        Bitmap rescaled = RescaleCT(baseXZ);
        Bitmap colorImage = ApplyJetColormap(rescaled);

        // 2) Encoder
        float[] imageTensorData = BitmapToFloatTensor(colorImage, _imageSize, _imageSize);
        var imageInput = new DenseTensor<float>(imageTensorData, new[] { 1, 3, _imageSize, _imageSize });
        Tensor<float> imageEmbed, highRes0, highRes1;
        using (var encOut = _encoderSession.Run(new[] {
            NamedOnnxValue.CreateFromTensor("image", imageInput)
        }))
        {
            imageEmbed = encOut.First(x => x.Name == "image_embeddings").AsTensor<float>();
            highRes0 = encOut.First(x => x.Name == "high_res_feats_0").AsTensor<float>();
            highRes1 = encOut.First(x => x.Name == "high_res_feats_1").AsTensor<float>();
        }

        // 3) Prompt
        int origW = baseXZ.Width;
        int origH = baseXZ.Height;
        (DenseTensor<float> coordsTensor, DenseTensor<float> labelsTensor) =
            BuildSinglePromptTensors(slicePoints, origW, origH, targetMaterialName);
        var origSizeTensor = new DenseTensor<int>(new[] { origH, origW }, new[] { 2 });

        int batchSize = coordsTensor.Dimensions[0];
        var maskInputTensor = new DenseTensor<float>(new float[batchSize * 1 * 256 * 256], new[] { batchSize, 1, 256, 256 });
        var hasMaskInputTensor = new DenseTensor<float>(new float[batchSize], new[] { batchSize });

        // 4) Decoder => masks, ious
        Tensor<float> masksTensor, iouTensor;
        using (var decOut = _decoderSession.Run(new List<NamedOnnxValue> {
            NamedOnnxValue.CreateFromTensor("image_embed", imageEmbed),
            NamedOnnxValue.CreateFromTensor("high_res_feats_0", highRes0),
            NamedOnnxValue.CreateFromTensor("high_res_feats_1", highRes1),
            NamedOnnxValue.CreateFromTensor("point_coords", coordsTensor),
            NamedOnnxValue.CreateFromTensor("point_labels", labelsTensor),
            NamedOnnxValue.CreateFromTensor("mask_input", maskInputTensor),
            NamedOnnxValue.CreateFromTensor("has_mask_input", hasMaskInputTensor),
            NamedOnnxValue.CreateFromTensor("orig_im_size", origSizeTensor)
        }))
        {
            masksTensor = decOut.First(x => x.Name == "masks").AsTensor<float>();
            iouTensor = decOut.First(x => x.Name == "iou_predictions").AsTensor<float>();
        }

        int numMasks = masksTensor.Dimensions[1];
        if (numMasks == 0) return null;
        int maskH = masksTensor.Dimensions[2];
        int maskW = masksTensor.Dimensions[3];

        // pick best IoU
        float bestIoU = -9999f;
        int bestIndex = 0;
        for (int i = 0; i < numMasks; i++)
        {
            float iou = iouTensor[0, i];
            if (iou > bestIoU) { bestIoU = iou; bestIndex = i; }
        }
        Log($"[ProcessXZSlice] bestIndex={bestIndex}, IoU={bestIoU:F3}");

        // 5) Build mask
        Bitmap bestMask = BuildMaskOutput(
            masksTensor, bestIndex, maskW, maskH,
            origW, origH, out maskBits, sliceIndex, "XZ");

        return bestMask;
    }

    // Overload returning only the Bitmap
    public Bitmap ProcessXZSlice(
        int sliceIndex, Bitmap baseXZ,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName)
    {
        bool[,] dummy;
        return ProcessXZSlice(sliceIndex, baseXZ, slicePoints, targetMaterialName, out dummy);
    }

    // ALL candidate masks in XZ
    public List<Bitmap> ProcessXZSlice_GetAllMasks(
        int sliceIndex, Bitmap baseXZ,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName,
        out List<bool[,]> maskBitsList)
    {
        maskBitsList = new List<bool[,]>();
        var resultBitmaps = new List<Bitmap>();
        if (baseXZ == null || slicePoints == null || slicePoints.Count == 0)
            return resultBitmaps;

        Log($"[ProcessXZSlice_GetAllMasks] Gathering all masks for '{targetMaterialName}', index={sliceIndex}.");

        Bitmap rescaled = RescaleCT(baseXZ);
        Bitmap colorImage = ApplyJetColormap(rescaled);
        float[] imageTensorData = BitmapToFloatTensor(colorImage, _imageSize, _imageSize);
        var imageInput = new DenseTensor<float>(imageTensorData, new[] { 1, 3, _imageSize, _imageSize });
        Tensor<float> imageEmbed, highRes0, highRes1;
        using (var encOut = _encoderSession.Run(new[] {
            NamedOnnxValue.CreateFromTensor("image", imageInput)
        }))
        {
            imageEmbed = encOut.First(x => x.Name == "image_embeddings").AsTensor<float>();
            highRes0 = encOut.First(x => x.Name == "high_res_feats_0").AsTensor<float>();
            highRes1 = encOut.First(x => x.Name == "high_res_feats_1").AsTensor<float>();
        }

        int origW = baseXZ.Width, origH = baseXZ.Height;
        (DenseTensor<float> coordsTensor, DenseTensor<float> labelsTensor) =
            BuildSinglePromptTensors(slicePoints, origW, origH, targetMaterialName);
        var origSizeTensor = new DenseTensor<int>(new[] { origH, origW }, new[] { 2 });

        int batchSize = coordsTensor.Dimensions[0];
        var maskInputTensor = new DenseTensor<float>(new float[batchSize * 1 * 256 * 256], new[] { batchSize, 1, 256, 256 });
        var hasMaskInputTensor = new DenseTensor<float>(new float[batchSize], new[] { batchSize });

        Tensor<float> masksTensor;
        using (var decOut = _decoderSession.Run(new List<NamedOnnxValue> {
            NamedOnnxValue.CreateFromTensor("image_embed", imageEmbed),
            NamedOnnxValue.CreateFromTensor("high_res_feats_0", highRes0),
            NamedOnnxValue.CreateFromTensor("high_res_feats_1", highRes1),
            NamedOnnxValue.CreateFromTensor("point_coords", coordsTensor),
            NamedOnnxValue.CreateFromTensor("point_labels", labelsTensor),
            NamedOnnxValue.CreateFromTensor("mask_input", maskInputTensor),
            NamedOnnxValue.CreateFromTensor("has_mask_input", hasMaskInputTensor),
            NamedOnnxValue.CreateFromTensor("orig_im_size", origSizeTensor)
        }))
        {
            masksTensor = decOut.First(x => x.Name == "masks").AsTensor<float>();
        }

        int outCount = masksTensor.Dimensions[1];
        if (outCount == 0) return resultBitmaps;
        int maskH = masksTensor.Dimensions[2], maskW = masksTensor.Dimensions[3];

        for (int i = 0; i < outCount; i++)
        {
            Bitmap bmp = BuildMaskOutput(
                masksTensor, i, maskW, maskH,
                origW, origH, out bool[,] bits, sliceIndex, "XZ");
            resultBitmaps.Add(bmp);
            maskBitsList.Add(bits);
        }
        if (resultBitmaps.Count == 3)
        {
            resultBitmaps.Add(new Bitmap(resultBitmaps[2]));
            maskBitsList.Add((bool[,])maskBitsList[2].Clone());
        }
        return resultBitmaps;
    }

    public List<Bitmap> ProcessXZSlice_GetAllMasks(
        int sliceIndex, Bitmap baseXZ,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName)
    {
        List<bool[,]> dummy;
        return ProcessXZSlice_GetAllMasks(sliceIndex, baseXZ, slicePoints, targetMaterialName, out dummy);
    }

    //---------------------------
    // YZ Slices
    //---------------------------

    public Bitmap ProcessYZSlice(
        int sliceIndex, Bitmap baseYZ,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName,
        out bool[,] maskBits)
    {
        maskBits = null;
        if (baseYZ == null || slicePoints == null || slicePoints.Count == 0)
            return null;

        Log($"[ProcessYZSlice] Segmenting '{targetMaterialName}' in YZ slice index={sliceIndex}.");

        Bitmap rescaled = RescaleCT(baseYZ);
        Bitmap colorImage = ApplyJetColormap(rescaled);
        float[] imageTensorData = BitmapToFloatTensor(colorImage, _imageSize, _imageSize);
        var imageInput = new DenseTensor<float>(imageTensorData, new[] { 1, 3, _imageSize, _imageSize });
        Tensor<float> imageEmbed, highRes0, highRes1;
        using (var encOut = _encoderSession.Run(new[] {
            NamedOnnxValue.CreateFromTensor("image", imageInput)
        }))
        {
            imageEmbed = encOut.First(x => x.Name == "image_embeddings").AsTensor<float>();
            highRes0 = encOut.First(x => x.Name == "high_res_feats_0").AsTensor<float>();
            highRes1 = encOut.First(x => x.Name == "high_res_feats_1").AsTensor<float>();
        }

        int origW = baseYZ.Width;   // Z dimension
        int origH = baseYZ.Height;  // Y dimension
        (DenseTensor<float> coordsTensor, DenseTensor<float> labelsTensor) =
            BuildSinglePromptTensors(slicePoints, origW, origH, targetMaterialName);
        var origSizeTensor = new DenseTensor<int>(new[] { origH, origW }, new[] { 2 });

        int batchSize = coordsTensor.Dimensions[0];
        var maskInputTensor = new DenseTensor<float>(new float[batchSize * 1 * 256 * 256], new[] { batchSize, 1, 256, 256 });
        var hasMaskInputTensor = new DenseTensor<float>(new float[batchSize], new[] { batchSize });

        Tensor<float> masksTensor, iouTensor;
        using (var decOut = _decoderSession.Run(new List<NamedOnnxValue> {
            NamedOnnxValue.CreateFromTensor("image_embed", imageEmbed),
            NamedOnnxValue.CreateFromTensor("high_res_feats_0", highRes0),
            NamedOnnxValue.CreateFromTensor("high_res_feats_1", highRes1),
            NamedOnnxValue.CreateFromTensor("point_coords", coordsTensor),
            NamedOnnxValue.CreateFromTensor("point_labels", labelsTensor),
            NamedOnnxValue.CreateFromTensor("mask_input", maskInputTensor),
            NamedOnnxValue.CreateFromTensor("has_mask_input", hasMaskInputTensor),
            NamedOnnxValue.CreateFromTensor("orig_im_size", origSizeTensor)
        }))
        {
            masksTensor = decOut.First(x => x.Name == "masks").AsTensor<float>();
            iouTensor = decOut.First(x => x.Name == "iou_predictions").AsTensor<float>();
        }

        int outCount = masksTensor.Dimensions[1];
        if (outCount == 0) return null;
        int maskH = masksTensor.Dimensions[2], maskW = masksTensor.Dimensions[3];

        float bestIoU = -9999f;
        int bestIndex = 0;
        for (int i = 0; i < outCount; i++)
        {
            float iou = iouTensor[0, i];
            if (iou > bestIoU) { bestIoU = iou; bestIndex = i; }
        }
        Log($"[ProcessYZSlice] bestIndex={bestIndex}, IoU={bestIoU:F3}");

        Bitmap bestMask = BuildMaskOutput(
            masksTensor, bestIndex, maskW, maskH,
            origW, origH, out maskBits, sliceIndex, "YZ");
        return bestMask;
    }

    public Bitmap ProcessYZSlice(
        int sliceIndex, Bitmap baseYZ,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName)
    {
        bool[,] dummy;
        return ProcessYZSlice(sliceIndex, baseYZ, slicePoints, targetMaterialName, out dummy);
    }

    public List<Bitmap> ProcessYZSlice_GetAllMasks(
        int sliceIndex, Bitmap baseYZ,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName,
        out List<bool[,]> maskBitsList)
    {
        maskBitsList = new List<bool[,]>();
        var resultBitmaps = new List<Bitmap>();
        if (baseYZ == null || slicePoints == null || slicePoints.Count == 0)
            return resultBitmaps;

        Log($"[ProcessYZSlice_GetAllMasks] Gathering all masks for '{targetMaterialName}', index={sliceIndex}.");

        Bitmap rescaled = RescaleCT(baseYZ);
        Bitmap colorImage = ApplyJetColormap(rescaled);
        float[] imageTensorData = BitmapToFloatTensor(colorImage, _imageSize, _imageSize);
        var imageInput = new DenseTensor<float>(imageTensorData, new[] { 1, 3, _imageSize, _imageSize });
        Tensor<float> imageEmbed, highRes0, highRes1;
        using (var encOut = _encoderSession.Run(new[] {
            NamedOnnxValue.CreateFromTensor("image", imageInput)
        }))
        {
            imageEmbed = encOut.First(x => x.Name == "image_embeddings").AsTensor<float>();
            highRes0 = encOut.First(x => x.Name == "high_res_feats_0").AsTensor<float>();
            highRes1 = encOut.First(x => x.Name == "high_res_feats_1").AsTensor<float>();
        }

        int origW = baseYZ.Width, origH = baseYZ.Height;
        (DenseTensor<float> coordsTensor, DenseTensor<float> labelsTensor) =
            BuildSinglePromptTensors(slicePoints, origW, origH, targetMaterialName);
        var origSizeTensor = new DenseTensor<int>(new[] { origH, origW }, new[] { 2 });

        int batchSize = coordsTensor.Dimensions[0];
        var maskInputTensor = new DenseTensor<float>(new float[batchSize * 1 * 256 * 256], new[] { batchSize, 1, 256, 256 });
        var hasMaskInputTensor = new DenseTensor<float>(new float[batchSize], new[] { batchSize });

        Tensor<float> masksTensor;
        using (var decOut = _decoderSession.Run(new List<NamedOnnxValue> {
            NamedOnnxValue.CreateFromTensor("image_embed", imageEmbed),
            NamedOnnxValue.CreateFromTensor("high_res_feats_0", highRes0),
            NamedOnnxValue.CreateFromTensor("high_res_feats_1", highRes1),
            NamedOnnxValue.CreateFromTensor("point_coords", coordsTensor),
            NamedOnnxValue.CreateFromTensor("point_labels", labelsTensor),
            NamedOnnxValue.CreateFromTensor("mask_input", maskInputTensor),
            NamedOnnxValue.CreateFromTensor("has_mask_input", hasMaskInputTensor),
            NamedOnnxValue.CreateFromTensor("orig_im_size", origSizeTensor)
        }))
        {
            masksTensor = decOut.First(x => x.Name == "masks").AsTensor<float>();
        }

        int outCount = masksTensor.Dimensions[1];
        if (outCount == 0) return resultBitmaps;
        int maskH = masksTensor.Dimensions[2], maskW = masksTensor.Dimensions[3];

        for (int i = 0; i < outCount; i++)
        {
            Bitmap bmp = BuildMaskOutput(
                masksTensor, i, maskW, maskH,
                origW, origH, out bool[,] bits,
                sliceIndex, "YZ");
            resultBitmaps.Add(bmp);
            maskBitsList.Add(bits);
        }
        if (resultBitmaps.Count == 3)
        {
            resultBitmaps.Add(new Bitmap(resultBitmaps[2]));
            maskBitsList.Add((bool[,])maskBitsList[2].Clone());
        }
        return resultBitmaps;
    }

    public List<Bitmap> ProcessYZSlice_GetAllMasks(
        int sliceIndex, Bitmap baseYZ,
        List<AnnotationPoint> slicePoints,
        string targetMaterialName)
    {
        List<bool[,]> dummy;
        return ProcessYZSlice_GetAllMasks(sliceIndex, baseYZ, slicePoints, targetMaterialName, out dummy);
    }

    //---------------------------
    // BuildMaskOutput + Helper
    //---------------------------

    private Bitmap BuildMaskOutput(
        Tensor<float> masksTensor, int maskIndex,
        int maskW, int maskH,
        int outW, int outH,
        out bool[,] maskBits,
        int sliceIndex = -1,
        string orientation = "")
    {
        // find min + max
        float minVal = float.MaxValue, maxVal = float.MinValue;
        for (int yy = 0; yy < maskH; yy++)
        {
            for (int xx = 0; xx < maskW; xx++)
            {
                float v = masksTensor[0, maskIndex, yy, xx];
                if (v < minVal) minVal = v;
                if (v > maxVal) maxVal = v;
            }
        }
        Log($"[DEBUG] {orientation} slice={sliceIndex}, mask={maskIndex}, minLogit={minVal:F4}, maxLogit={maxVal:F4}");

        // build binary mask from logits
        maskBits = new bool[maskH, maskW];
        Bitmap rawMask = new Bitmap(maskW, maskH, PixelFormat.Format24bppRgb);
        for (int yy = 0; yy < maskH; yy++)
        {
            for (int xx = 0; xx < maskW; xx++)
            {
                float logit = masksTensor[0, maskIndex, yy, xx];
                float prob = 1f / (1f + (float)Math.Exp(-logit));  // sigmoid
                if (prob > MaskBinarizationThreshold)
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

        // debug save?
        if (DebugSaveRawMasks)
        {
            try
            {
                System.IO.Directory.CreateDirectory(DebugOutputDir);
                string prefix = orientation != "" ? orientation + "_" : "";
                string fileName = $"{prefix}slice{sliceIndex}_mask{maskIndex}_raw.txt";
                string path = System.IO.Path.Combine(DebugOutputDir, fileName);
                using (var writer = new StreamWriter(path))
                {
                    for (int y = 0; y < maskH; y++)
                    {
                        for (int x = 0; x < maskW; x++)
                        {
                            float val = masksTensor[0, maskIndex, y, x];
                            writer.Write(val.ToString("G6"));
                            if (x < maskW - 1) writer.Write(',');
                        }
                        writer.WriteLine();
                    }
                }
                Log($"[DEBUG] Wrote raw mask logits => {path}");
            }
            catch (Exception ex)
            {
                Log($"[WARN] Could not write raw mask data => {ex.Message}");
            }
        }

        // scale to original size
        Bitmap finalMask = new Bitmap(outW, outH, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(finalMask))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.DrawImage(rawMask, 0, 0, outW, outH);
        }
        rawMask.Dispose();
        return finalMask;
    }

    //---------------------------
    // Single-Prompt Helper
    //---------------------------

    private (DenseTensor<float>, DenseTensor<float>) BuildSinglePromptTensors(
        List<AnnotationPoint> prompts,
        int origW, int origH,
        string targetMaterialName)
    {
        float xScale = (origW <= 1) ? 0f : (float)(_imageSize - 1) / (origW - 1);
        float yScale = (origH <= 1) ? 0f : (float)(_imageSize - 1) / (origH - 1);

        float[] coords = new float[prompts.Count * 2];
        float[] labels = new float[prompts.Count];

        for (int i = 0; i < prompts.Count; i++)
        {
            var p = prompts[i];
            float px = MyClamp(p.X, 0, origW - 1);
            float py = MyClamp(p.Y, 0, origH - 1);

            coords[i * 2 + 0] = py * yScale;
            coords[i * 2 + 1] = px * xScale;
            labels[i] = (p.Label != null && p.Label.Equals(targetMaterialName, StringComparison.OrdinalIgnoreCase))
                        ? 1f : 0f;
        }

        var coordTensor = new DenseTensor<float>(coords, new[] { 1, prompts.Count, 2 });
        var labelTensor = new DenseTensor<float>(labels, new[] { 1, prompts.Count });
        return (coordTensor, labelTensor);
    }

    //---------------------------
    // Utility Functions
    //---------------------------

    private Bitmap RescaleCT(Bitmap rawCtSlice)
    {
        if (rawCtSlice == null) return null;
        byte minVal = 255, maxVal = 0;
        for (int y = 0; y < rawCtSlice.Height; y++)
        {
            for (int x = 0; x < rawCtSlice.Width; x++)
            {
                byte g = rawCtSlice.GetPixel(x, y).R;
                if (g < minVal) minVal = g;
                if (g > maxVal) maxVal = g;
            }
        }
        if (minVal >= maxVal) return rawCtSlice; // no range

        Bitmap scaled = new Bitmap(rawCtSlice.Width, rawCtSlice.Height);
        double range = maxVal - minVal;
        for (int y = 0; y < rawCtSlice.Height; y++)
        {
            for (int x = 0; x < rawCtSlice.Width; x++)
            {
                byte g = rawCtSlice.GetPixel(x, y).R;
                int newg = (int)(((g - minVal) / range) * 255.0);
                if (newg < 0) newg = 0;
                if (newg > 255) newg = 255;
                scaled.SetPixel(x, y, Color.FromArgb(newg, newg, newg));
            }
        }
        return scaled;
    }

    private Bitmap ApplyJetColormap(Bitmap graySlice)
    {
        if (graySlice == null) return null;
        Color[] jetTable = BuildJetLookupTable();
        Bitmap colored = new Bitmap(graySlice.Width, graySlice.Height);
        for (int y = 0; y < graySlice.Height; y++)
        {
            for (int x = 0; x < graySlice.Width; x++)
            {
                byte g = graySlice.GetPixel(x, y).R;
                colored.SetPixel(x, y, jetTable[g]);
            }
        }
        return colored;
    }

    private Color[] BuildJetLookupTable()
    {
        Color[] table = new Color[256];
        for (int i = 0; i < 256; i++)
        {
            float x = i / 255f;
            float r = 0f, g = 0f, b = 0f;
            if (x <= 0.35f)
            {
                float t = x / 0.35f;
                r = 0f; g = t; b = 1f;
            }
            else if (x <= 0.65f)
            {
                float t = (x - 0.35f) / 0.30f;
                r = t; g = 1f; b = 1f - t;
            }
            else
            {
                float t = (x - 0.65f) / 0.35f;
                r = 1f; g = 1f - t; b = 0f;
            }
            byte rr = (byte)(255 * r);
            byte gg = (byte)(255 * g);
            byte bb = (byte)(255 * b);
            table[i] = Color.FromArgb(rr, gg, bb);
        }
        return table;
    }

    private float[] BitmapToFloatTensor(Bitmap bmp, int targetWidth, int targetHeight)
    {
        float[] pixelMean = { 123.675f, 116.28f, 103.53f };
        float[] pixelStd = { 58.395f, 57.12f, 57.375f };

        Bitmap resized = new Bitmap(targetWidth, targetHeight);
        using (Graphics g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
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

    private void Log(string msg)
    {
        if (LogAction != null) LogAction(msg);
        else Debug.WriteLine(msg);
    }

    /// <summary>Helper clamp since older .NET doesn't have Math.Clamp.</summary>
    private float MyClamp(float value, float minVal, float maxVal)
    {
        if (value < minVal) return minVal;
        if (value > maxVal) return maxVal;
        return value;
    }
}

// Minimal annotation point class
