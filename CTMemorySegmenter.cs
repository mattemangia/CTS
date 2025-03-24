using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Drawing.Drawing2D;
using System.Numerics;
using System.IO;
using System.Windows.Forms;

namespace CTSegmenter
{
    public class CTMemorySegmenter : IDisposable
    {
        private readonly int _imageSize;
        private readonly Dictionary<int, SliceMemory> _sliceMem;

        public bool usingSam2 { get; private set; }

        public bool usingSAM2;
        // SAM 2.1 models
        private InferenceSession _encoderSession;
        private InferenceSession _decoderSession;
        
        private bool _usingSam2Model;
        private string _encoderModelPath;
        private string _decoderModelPath;

        // Add these SAM 2.1 related fields
        private DenseTensor<float> _cachedImageEmbeddings;
        private DenseTensor<float> _cachedHighResFeatures0;
        private DenseTensor<float> _cachedHighResFeatures1;
        private bool _hasCachedEmbeddings = false;
        public bool StorePreviousEmbeddings { get; set; } = true;

        // Add these original SAM model session fields
        private InferenceSession _imageEncoderSession;
        private InferenceSession _promptEncoderSession;
        private InferenceSession _maskDecoderSession;
        private InferenceSession _memoryEncoderSession;
        private InferenceSession _memoryAttentionSession;
        private InferenceSession _mlpSession;

        

        public bool UseSelectiveHoleFilling { get; set; } = false;
        public int MaskThreshold { get; set; } = 128; // Default threshold value
        
        private static void Log(string message) => Logger.Log(message);

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
    bool useCpuExecutionProvider = true)
        {
            Log("[CTMemorySegmenter] Constructor start");
            _imageSize = imageInputSize;
            _sliceMem = new Dictionary<int, SliceMemory>();

            Log($"[CTMemorySegmenter] Debug - imageEncoderPath: {imageEncoderPath}");
            Log($"[CTMemorySegmenter] Debug - maskDecoderPath: {maskDecoderPath}");

            // Check if we're using SAM 2.1 models
            usingSam2 = !string.IsNullOrEmpty(imageEncoderPath) && Path.GetFileName(imageEncoderPath).Contains("sam2");
            _usingSam2Model = usingSam2;

            if (usingSam2)
            {
                // Use SAM 2.1 encoder/decoder paths
                string encoderPath = Path.Combine(Path.GetDirectoryName(imageEncoderPath), "sam2.1_large.encoder.onnx");
                string decoderPath = Path.Combine(Path.GetDirectoryName(maskDecoderPath), "sam2.1_large.decoder.onnx");

                // Store these paths for later validation
                _encoderModelPath = encoderPath;
                _decoderModelPath = decoderPath;

                Log($"[CTMemorySegmenter] Using SAM 2.1 models: {encoderPath}, {decoderPath}");
                Log($"[CTMemorySegmenter] Using CPU Execution Provider: {useCpuExecutionProvider}");

                // Verify file existence immediately
                if (!File.Exists(encoderPath))
                {
                    Log($"[CTMemorySegmenter] ERROR: Encoder file does not exist: {encoderPath}");
                }

                if (!File.Exists(decoderPath))
                {
                    Log($"[CTMemorySegmenter] ERROR: Decoder file does not exist: {decoderPath}");
                }

                SessionOptions options = new SessionOptions();

                // Only try to use DirectML if CPU execution is not forced
                if (!useCpuExecutionProvider)
                {
                    try
                    {
                        options.AppendExecutionProvider_DML();
                        Log("[CTMemorySegmenter] Using DirectML Execution Provider");
                    }
                    catch (Exception ex)
                    {
                        Log("[CTMemorySegmenter] DML not available, falling back to CPU: " + ex.Message);
                        options = new SessionOptions();
                        options.AppendExecutionProvider_CPU();
                    }
                }
                else
                {
                    // Explicitly use CPU provider
                    options.AppendExecutionProvider_CPU();
                    Log("[CTMemorySegmenter] Using CPU Execution Provider by user choice");
                }

                try
                {
                    _encoderSession = new InferenceSession(encoderPath, options);
                    _decoderSession = new InferenceSession(decoderPath, options);

                    Log("[CTMemorySegmenter] All SAM 2.1 sessions loaded successfully.");
                    Logger.Log($"[CTMemorySegmenter] Model requires input size: {_imageSize}");
                }
                catch (Exception ex)
                {
                    Log("[CTMemorySegmenter] Exception during initialization: " + ex.Message);

                    if (!useCpuExecutionProvider)
                    {
                        // Try falling back to CPU
                        Log("[CTMemorySegmenter] Falling back to CPU Execution Provider.");
                        var cpuOptions = new SessionOptions();
                        cpuOptions.AppendExecutionProvider_CPU();

                        try
                        {
                            _encoderSession = new InferenceSession(encoderPath, cpuOptions);
                            _decoderSession = new InferenceSession(decoderPath, cpuOptions);
                            Log("[CTMemorySegmenter] CPU fallback successful.");
                        }
                        catch (Exception fallbackEx)
                        {
                            Log("[CTMemorySegmenter] CPU fallback also failed: " + fallbackEx.Message);
                            throw; // Rethrow the fallback exception
                        }
                    }
                    else
                    {
                        // Already using CPU, just rethrow
                        throw;
                    }
                }
            }
            EnsureModelPaths();
            Log("[CTMemorySegmenter] Constructor end");
            LogAllModelMetadata();
        }


        private Tensor<float> _lastImageEmbeddings;
        private Tensor<float> _lastHighResFeatures0;
        private Tensor<float> _lastHighResFeatures1;


        // This needs to be implemented in places where propagation happens
        private void SaveEmbeddingsForPropagation(Tensor<float> imageEmbeddings,
                                 Tensor<float> highResFeatures0,
                                 Tensor<float> highResFeatures1)
        {
            try
            {
                if (!StorePreviousEmbeddings)
                    return;

                _usingSam2Model = usingSam2;

                // Create new tensor instances to avoid memory issues with sharing references
                _cachedImageEmbeddings = new DenseTensor<float>(imageEmbeddings.Dimensions);
                _cachedHighResFeatures0 = new DenseTensor<float>(highResFeatures0.Dimensions);
                _cachedHighResFeatures1 = new DenseTensor<float>(highResFeatures1.Dimensions);

                // Copy tensor data
                for (int i = 0; i < imageEmbeddings.Length; i++)
                    _cachedImageEmbeddings.SetValue(i, imageEmbeddings.GetValue(i));

                for (int i = 0; i < highResFeatures0.Length; i++)
                    _cachedHighResFeatures0.SetValue(i, highResFeatures0.GetValue(i));

                for (int i = 0; i < highResFeatures1.Length; i++)
                    _cachedHighResFeatures1.SetValue(i, highResFeatures1.GetValue(i));

                _hasCachedEmbeddings = true;
                Log("[SaveEmbeddingsForPropagation] Cached embeddings for propagation");
            }
            catch (Exception ex)
            {
                Log($"[SaveEmbeddingsForPropagation] Error: {ex.Message}");
                _hasCachedEmbeddings = false;
            }
        }

        private void LogAllModelMetadata()
        {
            Log("[CTMemorySegmenter] Logging model metadata...");

            if (_encoderSession != null)
            {
                Log("Encoder:");
                LogSessionMetadata(_encoderSession);
            }

            if (_decoderSession != null)
            {
                Log("Decoder:");
                LogSessionMetadata(_decoderSession);
            }

            Log("[CTMemorySegmenter] Model metadata logged.");
        }

        private void LogSessionMetadata(InferenceSession session)
        {
            foreach (var input in session.InputMetadata)
            {
                string dims = string.Join("x", input.Value.Dimensions.Select(d => d.ToString()));
                Log($"Input Name: {input.Key} | Shape: {dims} | Type: {input.Value.ElementType}");
            }
            foreach (var output in session.OutputMetadata)
            {
                string dims = string.Join("x", output.Value.Dimensions.Select(d => d.ToString()));
                Log($"Output Name: {output.Key} | Shape: {dims} | Type: {output.Value.ElementType}");
            }
        }

        /// <summary>
        /// Processes a CT slice in the XY view using a list of AnnotationPoints.
        /// </summary>
        public Bitmap ProcessXYSlice(
            int sliceIndex,
            Bitmap baseXY,
            List<AnnotationPoint> promptPoints,
            object additionalParam1,
            object additionalParam2)
        {
            try
            {
                Logger.Log($"[ProcessXYSlice] Start slice={sliceIndex}, #points={(promptPoints?.Count ?? 0)}");

                // Validation
                if (baseXY == null)
                    throw new ArgumentNullException(nameof(baseXY), "Input bitmap cannot be null");
                if (promptPoints == null || promptPoints.Count == 0)
                    throw new ArgumentException("No prompt points provided", nameof(promptPoints));
                if (baseXY.Width <= 0 || baseXY.Height <= 0)
                    throw new ArgumentException("Invalid baseXY dimensions");

                // Convert image to tensor for SAM 2.1
                float[] imageTensorData = BitmapToFloatTensor(baseXY, _imageSize, _imageSize);
                var imageInput = new DenseTensor<float>(imageTensorData, new[] { 1, 3, _imageSize, _imageSize });

                // Run the encoder
                Tensor<float> imageEmbeddings;
                Tensor<float> highResFeatures0;
                Tensor<float> highResFeatures1;

                using (var encoderOutputs = _encoderSession.Run(
                    new[] { NamedOnnxValue.CreateFromTensor("image", imageInput) }))
                {
                    imageEmbeddings = GetFirstTensor<float>(encoderOutputs, "image_embeddings");
                    highResFeatures0 = GetFirstTensor<float>(encoderOutputs, "high_res_feats_0");
                    highResFeatures1 = GetFirstTensor<float>(encoderOutputs, "high_res_feats_1");
                }

                // Prepare point prompts for SAM 2.1
                int pointCount = promptPoints.Count;
                List<AnnotationPoint> positivePoints = new List<AnnotationPoint>();
                List<AnnotationPoint> negativePoints = new List<AnnotationPoint>();

                // Separate points into positive and negative
                foreach (var point in promptPoints)
                {
                    bool isNegative = point.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase);
                    if (isNegative)
                        negativePoints.Add(point);
                    else
                        positivePoints.Add(point);
                }

                // Prepare point inputs and labels for SAM 2.1
                float[] pointInputs = new float[positivePoints.Count * 2 + negativePoints.Count * 2];
                float[] pointLabels = new float[positivePoints.Count + negativePoints.Count];

                // Process positive points
                for (int i = 0; i < positivePoints.Count; i++)
                {
                    // Scale coordinates to model input size
                    float xClamped = Math.Max(0, Math.Min(positivePoints[i].X, baseXY.Width - 1));
                    float yClamped = Math.Max(0, Math.Min(positivePoints[i].Y, baseXY.Height - 1));
                    float xScale = (_imageSize - 1f) / Math.Max(1, baseXY.Width - 1f);
                    float yScale = (_imageSize - 1f) / Math.Max(1, baseXY.Height - 1f);

                    pointInputs[i * 2] = xClamped * xScale;
                    pointInputs[i * 2 + 1] = yClamped * yScale;
                    pointLabels[i] = 1.0f; // Positive point
                }

                // Process negative points
                for (int i = 0; i < negativePoints.Count; i++)
                {
                    float xClamped = Math.Max(0, Math.Min(negativePoints[i].X, baseXY.Width - 1));
                    float yClamped = Math.Max(0, Math.Min(negativePoints[i].Y, baseXY.Height - 1));
                    float xScale = (_imageSize - 1f) / Math.Max(1, baseXY.Width - 1f);
                    float yScale = (_imageSize - 1f) / Math.Max(1, baseXY.Height - 1f);

                    pointInputs[(positivePoints.Count + i) * 2] = xClamped * xScale;
                    pointInputs[(positivePoints.Count + i) * 2 + 1] = yClamped * yScale;
                    pointLabels[positivePoints.Count + i] = 0.0f; // Negative point
                }

                // Format for SAM 2.1: [num_labels,num_points,2] and [num_labels,num_points]
                var pointInputsTensor = new DenseTensor<float>(pointInputs, new[] { 1, pointCount, 2 });
                var pointLabelsTensor = new DenseTensor<float>(pointLabels, new[] { 1, pointCount });

                // Original image size tensor
                var origSizeTensor = new DenseTensor<int>(new[] { baseXY.Height, baseXY.Width }, new[] { 2 });

                // Run the decoder
                Tensor<byte> masksTensor;
                Tensor<float> iousTensor;

                using (var decoderOutputs = _decoderSession.Run(new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("image_embeddings", imageEmbeddings),
                    NamedOnnxValue.CreateFromTensor("high_res_feats_0", highResFeatures0),
                    NamedOnnxValue.CreateFromTensor("high_res_feats_1", highResFeatures1),
                    NamedOnnxValue.CreateFromTensor("original_image_size", origSizeTensor),
                    NamedOnnxValue.CreateFromTensor("point_inputs", pointInputsTensor),
                    NamedOnnxValue.CreateFromTensor("point_labels", pointLabelsTensor)
                }))
                {
                    masksTensor = GetFirstTensor<byte>(decoderOutputs, "masks");
                    iousTensor = GetFirstTensor<float>(decoderOutputs, "ious");
                }

                // Process masks
                int outChannels = masksTensor.Dimensions[1]; // number of masks
                int maskH = masksTensor.Dimensions[2];
                int maskW = masksTensor.Dimensions[3];

                // Find the mask with highest IoU
                float bestIoU = -1f;
                int bestMaskIdx = 0;

                for (int i = 0; i < outChannels; i++)
                {
                    float iou = iousTensor[0, i];
                    if (iou > bestIoU)
                    {
                        bestIoU = iou;
                        bestMaskIdx = i;
                    }
                }

                // Create binary mask from the best mask
                Bitmap bestMask = new Bitmap(maskW, maskH);
                for (int y = 0; y < maskH; y++)
                {
                    for (int x = 0; x < maskW; x++)
                    {
                        byte maskValue = masksTensor[0, bestMaskIdx, y, x];
                        if (maskValue > (MaskThreshold / 255.0f * 255)) // Apply threshold
                        {
                            bestMask.SetPixel(x, y, Color.White);
                        }
                        else
                        {
                            bestMask.SetPixel(x, y, Color.Black);
                        }
                    }
                }

                // Optionally perform dilation if coverage is too small
                float coverage = CalculateCoverage(bestMask);
                if (coverage > 0 && coverage < 15f)
                {
                    Bitmap dilated = Dilate(bestMask, 3);
                    bestMask.Dispose();
                    bestMask = dilated;
                }

                // Create output mask at original image size
                Bitmap finalMask = new Bitmap(baseXY.Width, baseXY.Height);
                using (Graphics g = Graphics.FromImage(finalMask))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bestMask, new Rectangle(0, 0, baseXY.Width, baseXY.Height));
                }
                bestMask.Dispose();

                Logger.Log($"[ProcessXYSlice] Best mask IoU={bestIoU:F3}");
                return finalMask;
            }
            catch (Exception ex)
            {
                Logger.Log($"[ProcessXYSlice] Error: {ex.Message}\n{ex.StackTrace}");
                // Return empty mask on error
                Bitmap errorMask = new Bitmap(baseXY.Width, baseXY.Height);
                using (Graphics g = Graphics.FromImage(errorMask))
                {
                    g.Clear(Color.Black);
                }
                return errorMask;
            }
        }

        private Bitmap ProcessXYSliceWithSam2(
    int sliceIndex,
    Bitmap baseXY,
    List<AnnotationPoint> promptPoints)
        {
            // Convert to model's input size
            float[] imageTensorData = BitmapToFloatTensor(baseXY, _imageSize, _imageSize);
            var imageInput = new DenseTensor<float>(imageTensorData, new[] { 1, 3, _imageSize, _imageSize });

            // Run encoder
            Tensor<float> imageEmbeddings;
            Tensor<float> highResFeatures0;
            Tensor<float> highResFeatures1;

            using (var encoderOutputs = _encoderSession.Run(
                new[] { NamedOnnxValue.CreateFromTensor("image", imageInput) }))
            {
                imageEmbeddings = GetFirstTensor<float>(encoderOutputs, "image_embeddings");
                highResFeatures0 = GetFirstTensor<float>(encoderOutputs, "high_res_feats_0");
                highResFeatures1 = GetFirstTensor<float>(encoderOutputs, "high_res_feats_1");

                // Store embeddings for potential propagation
                if (StorePreviousEmbeddings)
                {
                    SaveEmbeddingsForPropagation(imageEmbeddings, highResFeatures0, highResFeatures1);
                }
            }

            // Prepare points for SAM 2.1
            int pointCount = promptPoints.Count;
            float[] pointInputs = new float[pointCount * 2];
            float[] pointLabels = new float[pointCount];

            for (int i = 0; i < pointCount; i++)
            {
                var point = promptPoints[i];

                // Scale points to model input size
                float xClamped = Math.Max(0, Math.Min(point.X, baseXY.Width - 1));
                float yClamped = Math.Max(0, Math.Min(point.Y, baseXY.Height - 1));
                float xScale = (_imageSize - 1f) / Math.Max(1, baseXY.Width - 1f);
                float yScale = (_imageSize - 1f) / Math.Max(1, baseXY.Height - 1f);

                pointInputs[i * 2] = xClamped * xScale;
                pointInputs[i * 2 + 1] = yClamped * yScale;

                // SAM 2.1 uses float points: 1.0 for positive, 0.0 for negative
                pointLabels[i] = point.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase) ? 0.0f : 1.0f;
            }

            var pointInputsTensor = new DenseTensor<float>(pointInputs, new[] { 1, pointCount, 2 });
            var pointLabelsTensor = new DenseTensor<float>(pointLabels, new[] { 1, pointCount });

            // SAM 2.1 requires original image size
            var origSizeTensor = new DenseTensor<int>(new[] { baseXY.Height, baseXY.Width }, new[] { 2 });

            // Run decoder
            Tensor<byte> masksTensor;
            Tensor<float> iousTensor;

            using (var decoderOutputs = _decoderSession.Run(new List<NamedOnnxValue>
    {
        NamedOnnxValue.CreateFromTensor("image_embeddings", imageEmbeddings),
        NamedOnnxValue.CreateFromTensor("high_res_feats_0", highResFeatures0),
        NamedOnnxValue.CreateFromTensor("high_res_feats_1", highResFeatures1),
        NamedOnnxValue.CreateFromTensor("original_image_size", origSizeTensor),
        NamedOnnxValue.CreateFromTensor("point_inputs", pointInputsTensor),
        NamedOnnxValue.CreateFromTensor("point_labels", pointLabelsTensor)
    }))
            {
                masksTensor = GetFirstTensor<byte>(decoderOutputs, "masks");
                iousTensor = GetFirstTensor<float>(decoderOutputs, "ious");
            }

            // Find best mask based on IoU
            int maskCount = masksTensor.Dimensions[1];
            int maskHeight = masksTensor.Dimensions[2];
            int maskWidth = masksTensor.Dimensions[3];

            float bestIoU = -1f;
            int bestMaskIdx = 0;

            for (int i = 0; i < maskCount; i++)
            {
                float iou = iousTensor[0, i];
                if (iou > bestIoU)
                {
                    bestIoU = iou;
                    bestMaskIdx = i;
                }
            }

            // Create mask from best mask
            Bitmap bestMask = new Bitmap(maskWidth, maskHeight);

            // SAM 2.1 returns binary masks (uint8)
            byte thresholdValue = (byte)(MaskThreshold * 255 / 255);

            for (int y = 0; y < maskHeight; y++)
            {
                for (int x = 0; x < maskWidth; x++)
                {
                    byte maskValue = masksTensor[0, bestMaskIdx, y, x];
                    bestMask.SetPixel(x, y, maskValue > thresholdValue ? Color.White : Color.Black);
                }
            }

            // Optional dilation for small masks
            float coverage = CalculateCoverage(bestMask);
            if (coverage > 0 && coverage < 15f)
            {
                Bitmap dilated = Dilate(bestMask, 3);
                bestMask.Dispose();
                bestMask = dilated;
            }

            // Scale to original image size
            Bitmap finalMask = new Bitmap(baseXY.Width, baseXY.Height);
            using (Graphics g = Graphics.FromImage(finalMask))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(bestMask, new Rectangle(0, 0, baseXY.Width, baseXY.Height));
            }
            bestMask.Dispose();

            Logger.Log($"[ProcessXYSliceWithSam2] Best mask IoU={bestIoU:F3}");
            return finalMask;
        }

        /// <summary>
        /// Returns multiple candidate masks for an XY slice.
        /// </summary>
        public List<Bitmap> ProcessXYSlice_GetAllMasks(
    int sliceIndex,
    Bitmap sliceBmp,
    List<AnnotationPoint> promptPoints,
    object additionalParam1,
    object additionalParam2)
        {
            List<Bitmap> candidateMasks = new List<Bitmap>();

            try
            {
                Logger.Log($"[ProcessXYSlice_GetAllMasks] Starting slice={sliceIndex}, points={(promptPoints?.Count ?? 0)}");

                // Validate inputs
                if (sliceBmp == null)
                {
                    Logger.Log("[ProcessXYSlice_GetAllMasks] ERROR: sliceBmp is null");
                    return CreateFallbackMasks(100, 100, promptPoints);
                }

                if (promptPoints == null || promptPoints.Count == 0)
                {
                    Logger.Log("[ProcessXYSlice_GetAllMasks] ERROR: No prompt points provided");
                    return CreateFallbackMasks(sliceBmp.Width, sliceBmp.Height, new List<AnnotationPoint>());
                }

                // Analyze what the user is trying to segment
                var positivePoints = promptPoints.Where(p => !p.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase)).ToList();
                var negativePoints = promptPoints.Where(p => p.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase)).ToList();

                if (positivePoints.Count == 0)
                {
                    Logger.Log("[ProcessXYSlice_GetAllMasks] ERROR: No positive points found, cannot segment");
                    return CreateFallbackMasks(sliceBmp.Width, sliceBmp.Height, promptPoints);
                }

                // Analyze point locations
                double avgPosBrightness = 0;
                double avgNegBrightness = 0;

                foreach (var point in positivePoints)
                {
                    int x = Math.Min(Math.Max(0, (int)point.X), sliceBmp.Width - 1);
                    int y = Math.Min(Math.Max(0, (int)point.Y), sliceBmp.Height - 1);
                    Color pixel = sliceBmp.GetPixel(x, y);
                    avgPosBrightness += (pixel.R + pixel.G + pixel.B) / 3.0;
                }
                avgPosBrightness /= positivePoints.Count;

                if (negativePoints.Count > 0)
                {
                    foreach (var point in negativePoints)
                    {
                        int x = Math.Min(Math.Max(0, (int)point.X), sliceBmp.Width - 1);
                        int y = Math.Min(Math.Max(0, (int)point.Y), sliceBmp.Height - 1);
                        Color pixel = sliceBmp.GetPixel(x, y);
                        avgNegBrightness += (pixel.R + pixel.G + pixel.B) / 3.0;
                    }
                    avgNegBrightness /= negativePoints.Count;
                }

                Logger.Log($"[ProcessXYSlice_GetAllMasks] Analysis: Positive points brightness={avgPosBrightness:F1}, " +
                           $"Negative points brightness={avgNegBrightness:F1}");

                // Calculate global image statistics
                double avgImageBrightness = 0;
                double stdDevBrightness = 0;
                int totalPixels = 0;

                // Sample the image to calculate average brightness
                for (int y = 0; y < sliceBmp.Height; y += 4)
                {
                    for (int x = 0; x < sliceBmp.Width; x += 4)
                    {
                        Color pixel = sliceBmp.GetPixel(x, y);
                        avgImageBrightness += (pixel.R + pixel.G + pixel.B) / 3.0;
                        totalPixels++;
                    }
                }
                avgImageBrightness /= totalPixels;

                // Calculate standard deviation
                for (int y = 0; y < sliceBmp.Height; y += 4)
                {
                    for (int x = 0; x < sliceBmp.Width; x += 4)
                    {
                        Color pixel = sliceBmp.GetPixel(x, y);
                        double pixelBrightness = (pixel.R + pixel.G + pixel.B) / 3.0;
                        stdDevBrightness += Math.Pow(pixelBrightness - avgImageBrightness, 2);
                    }
                }
                stdDevBrightness = Math.Sqrt(stdDevBrightness / totalPixels);

                Logger.Log($"[ProcessXYSlice_GetAllMasks] Image stats: Avg={avgImageBrightness:F1}, StdDev={stdDevBrightness:F1}");

                // STEP 1: Process with original image
                List<Bitmap> originalImageMasks = new List<Bitmap>();
                try
                {
                    Logger.Log("[ProcessXYSlice_GetAllMasks] Processing original image...");
                    originalImageMasks = GenerateMasksFromImageSam2(sliceBmp, promptPoints, "Original");

                    if (originalImageMasks != null && originalImageMasks.Count > 0)
                    {
                        candidateMasks.AddRange(originalImageMasks);
                        Logger.Log($"[ProcessXYSlice_GetAllMasks] Generated {originalImageMasks.Count} masks from original image");
                    }
                    else
                    {
                        Logger.Log("[ProcessXYSlice_GetAllMasks] No masks generated from original image");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ProcessXYSlice_GetAllMasks] Error processing original image: {ex.Message}");
                }

                // If we already have enough masks, skip further processing
                if (candidateMasks.Count >= 4)
                {
                    // Ensure we have exactly 4 masks
                    while (candidateMasks.Count > 4)
                    {
                        Bitmap last = candidateMasks[candidateMasks.Count - 1];
                        candidateMasks.RemoveAt(candidateMasks.Count - 1);
                        last.Dispose();
                    }

                    Logger.Log($"[ProcessXYSlice_GetAllMasks] Returning {candidateMasks.Count} masks from first pass");
                    return candidateMasks;
                }

                // STEP 2: Create additional masks if needed
                if (candidateMasks.Count < 4)
                {
                    try
                    {
                        // Create specialized enhancement based on target features
                        bool targetingDarkFeatures = avgPosBrightness < avgImageBrightness - stdDevBrightness / 2;
                        bool targetingBrightFeatures = avgPosBrightness > avgImageBrightness + stdDevBrightness / 2;

                        // Create enhanced version optimized for feature type
                        Bitmap enhancedImage = new Bitmap(sliceBmp.Width, sliceBmp.Height);

                        if (targetingDarkFeatures)
                        {
                            Logger.Log("[ProcessXYSlice_GetAllMasks] Creating dark-feature enhanced image");
                            // Enhance dark features with local contrast enhancement
                            for (int y = 0; y < sliceBmp.Height; y++)
                            {
                                for (int x = 0; x < sliceBmp.Width; x++)
                                {
                                    Color pixel = sliceBmp.GetPixel(x, y);
                                    int intensity = (pixel.R + pixel.G + pixel.B) / 3;

                                    // Enhance contrast in darker regions
                                    double factor = 1.0;
                                    if (intensity < avgImageBrightness)
                                    {
                                        // Amplify differences in dark regions
                                        factor = 0.7 + 0.6 * (1.0 - (intensity / avgImageBrightness));
                                    }

                                    int newIntensity = (int)Math.Min(255, Math.Max(0, intensity * factor));
                                    enhancedImage.SetPixel(x, y, Color.FromArgb(newIntensity, newIntensity, newIntensity));
                                }
                            }
                        }
                        else if (targetingBrightFeatures)
                        {
                            Logger.Log("[ProcessXYSlice_GetAllMasks] Creating bright-feature enhanced image");
                            // Enhance bright features
                            for (int y = 0; y < sliceBmp.Height; y++)
                            {
                                for (int x = 0; x < sliceBmp.Width; x++)
                                {
                                    Color pixel = sliceBmp.GetPixel(x, y);
                                    int intensity = (pixel.R + pixel.G + pixel.B) / 3;

                                    // Enhance contrast in brighter regions
                                    double factor = 1.0;
                                    if (intensity > avgImageBrightness)
                                    {
                                        // Amplify differences in bright regions
                                        factor = 1.0 + 0.8 * ((intensity - avgImageBrightness) / (255 - avgImageBrightness));
                                    }

                                    int newIntensity = (int)Math.Min(255, Math.Max(0, intensity * factor));
                                    enhancedImage.SetPixel(x, y, Color.FromArgb(newIntensity, newIntensity, newIntensity));
                                }
                            }
                        }
                        else
                        {
                            Logger.Log("[ProcessXYSlice_GetAllMasks] Creating general contrast enhanced image");
                            // General contrast enhancement
                            int[] histogram = new int[256];

                            for (int y = 0; y < sliceBmp.Height; y++)
                            {
                                for (int x = 0; x < sliceBmp.Width; x++)
                                {
                                    Color pixel = sliceBmp.GetPixel(x, y);
                                    int intensity = (pixel.R + pixel.G + pixel.B) / 3;
                                    histogram[intensity]++;
                                }
                            }

                            // Find 5th and 95th percentiles
                            int totalPixels0 = sliceBmp.Width * sliceBmp.Height;
                            int lowerBound = 0;
                            int sum = 0;
                            while (sum < totalPixels0 * 0.05 && lowerBound < 255)
                            {
                                sum += histogram[lowerBound];
                                lowerBound++;
                            }

                            int upperBound = 255;
                            sum = 0;
                            while (sum < totalPixels0 * 0.05 && upperBound > 0)
                            {
                                sum += histogram[upperBound];
                                upperBound--;
                            }

                            // Apply contrast stretching
                            for (int y = 0; y < sliceBmp.Height; y++)
                            {
                                for (int x = 0; x < sliceBmp.Width; x++)
                                {
                                    Color pixel = sliceBmp.GetPixel(x, y);
                                    int r = AdjustPixelValue(pixel.R, lowerBound, upperBound);
                                    int g = AdjustPixelValue(pixel.G, lowerBound, upperBound);
                                    int b = AdjustPixelValue(pixel.B, lowerBound, upperBound);
                                    enhancedImage.SetPixel(x, y, Color.FromArgb(r, g, b));
                                }
                            }
                        }

                        // Process enhanced image
                        Logger.Log("[ProcessXYSlice_GetAllMasks] Processing enhanced image");
                        List<Bitmap> enhancedImageMasks = GenerateMasksFromImageSam2(enhancedImage, promptPoints, "Enhanced");
                        enhancedImage.Dispose();

                        if (enhancedImageMasks != null && enhancedImageMasks.Count > 0)
                        {
                            // Add each mask that doesn't exceed our limit
                            foreach (var mask in enhancedImageMasks)
                            {
                                if (candidateMasks.Count < 4)
                                {
                                    candidateMasks.Add(mask);
                                }
                                else
                                {
                                    mask.Dispose();
                                }
                            }
                            Logger.Log($"[ProcessXYSlice_GetAllMasks] Added masks from enhanced image, total={candidateMasks.Count}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ProcessXYSlice_GetAllMasks] Error processing enhanced image: {ex.Message}");
                    }
                }

                // STEP 3: Fill with fallbacks if still needed
                while (candidateMasks.Count < 4)
                {
                    int index = candidateMasks.Count;
                    Bitmap fallback = CreateSingleFallbackMask(sliceBmp.Width, sliceBmp.Height, promptPoints, index);
                    candidateMasks.Add(fallback);
                    Logger.Log($"[ProcessXYSlice_GetAllMasks] Added fallback mask {index}");
                }

                // Ensure we have exactly 4 masks
                while (candidateMasks.Count > 4)
                {
                    Bitmap last = candidateMasks[candidateMasks.Count - 1];
                    candidateMasks.RemoveAt(candidateMasks.Count - 1);
                    last.Dispose();
                }

                Logger.Log($"[ProcessXYSlice_GetAllMasks] Successfully generated {candidateMasks.Count} candidate masks");
                return candidateMasks;
            }
            catch (Exception ex)
            {
                Logger.Log($"[ProcessXYSlice_GetAllMasks] Critical error: {ex.Message}");
                Logger.Log($"[ProcessXYSlice_GetAllMasks] Stack trace: {ex.StackTrace}");

                // Clean up any masks created before the error
                foreach (var mask in candidateMasks)
                {
                    try { mask.Dispose(); } catch { }
                }
                candidateMasks.Clear();

                // Return fallback masks
                return CreateFallbackMasks(sliceBmp?.Width ?? 100, sliceBmp?.Height ?? 100, promptPoints);
            }
        }

        private List<Bitmap> DirectSegmentFeatures(Bitmap image, List<AnnotationPoint> promptPoints, string sourceType)
        {
            List<Bitmap> masks = new List<Bitmap>();

            try
            {
                Log($"[DirectSegmentFeatures] Processing {sourceType} image: {image.Width}x{image.Height}, {promptPoints.Count} points");

                // 1. ANALYZE WHAT USER IS TARGETING
                var positivePoints = promptPoints.Where(p => !p.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase)).ToList();
                var negativePoints = promptPoints.Where(p => p.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase)).ToList();

                // Calculate brightness at annotation points
                double posAvgBrightness = 0;
                double posStdDevBrightness = 0;
                List<int> posPointValues = new List<int>();

                foreach (var point in positivePoints)
                {
                    int x = Math.Min(Math.Max(0, (int)point.X), image.Width - 1);
                    int y = Math.Min(Math.Max(0, (int)point.Y), image.Height - 1);
                    Color c = image.GetPixel(x, y);
                    int val = (c.R + c.G + c.B) / 3;
                    posPointValues.Add(val);
                    posAvgBrightness += val;
                }
                posAvgBrightness /= positivePoints.Count;

                // Calculate standard deviation of positive points
                foreach (int val in posPointValues)
                {
                    posStdDevBrightness += Math.Pow(val - posAvgBrightness, 2);
                }
                posStdDevBrightness = Math.Sqrt(posStdDevBrightness / positivePoints.Count);

                // 2. DETERMINE SEGMENTATION STRATEGY
                bool isTargetingDarkFeatures = posAvgBrightness < 128;
                Log($"[DirectSegmentFeatures] Positive points avg brightness: {posAvgBrightness:F1}, std dev: {posStdDevBrightness:F1}");
                Log($"[DirectSegmentFeatures] Target appears to be {(isTargetingDarkFeatures ? "DARK" : "BRIGHT")} features");

                // 3. CREATE SPECIALIZED INPUTS BASED ON TARGET TYPE
                List<Bitmap> inputVersions = new List<Bitmap>();
                inputVersions.Add(new Bitmap(image)); // Original

                // For dark features, create specialized enhancement
                if (isTargetingDarkFeatures)
                {
                    // Create a brightness-inverted version to help segment dark features
                    Bitmap invertedImage = new Bitmap(image.Width, image.Height);
                    using (Graphics g = Graphics.FromImage(invertedImage))
                    {
                        // Set up for high quality
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                        // Draw inverted image
                        using (ImageAttributes attrs = new ImageAttributes())
                        {
                            // Create a color matrix that inverts the image
                            ColorMatrix colorMatrix = new ColorMatrix(
                                new float[][] {
                            new float[] {-1, 0, 0, 0, 0},
                            new float[] {0, -1, 0, 0, 0},
                            new float[] {0, 0, -1, 0, 0},
                            new float[] {0, 0, 0, 1, 0},
                            new float[] {1, 1, 1, 0, 1}
                                });
                            attrs.SetColorMatrix(colorMatrix);

                            g.DrawImage(image,
                                new Rectangle(0, 0, image.Width, image.Height),
                                0, 0, image.Width, image.Height,
                                GraphicsUnit.Pixel, attrs);
                        }
                    }
                    inputVersions.Add(invertedImage);
                    Log("[DirectSegmentFeatures] Added inverted image for dark feature detection");

                    // Create an enhanced version with local contrast for dark features
                    Bitmap enhancedDarkImage = new Bitmap(image.Width, image.Height);
                    for (int y = 0; y < image.Height; y++)
                    {
                        for (int x = 0; x < image.Width; x++)
                        {
                            Color pixel = image.GetPixel(x, y);
                            int val = (pixel.R + pixel.G + pixel.B) / 3;

                            // Apply aggressive non-linear curve to enhance dark features
                            double enhancedVal;
                            if (val < posAvgBrightness + 20)
                            {
                                // For pixels in the dark target range, amplify differences
                                double relativeVal = val / (double)posAvgBrightness;
                                enhancedVal = Math.Pow(relativeVal, 0.5) * 200; // Power < 1 expands dark range
                            }
                            else
                            {
                                // For brighter areas, suppress them
                                enhancedVal = val * 0.5 + 128;
                            }

                            int newVal = (int)Math.Min(255, Math.Max(0, enhancedVal));
                            enhancedDarkImage.SetPixel(x, y, Color.FromArgb(newVal, newVal, newVal));
                        }
                    }
                    inputVersions.Add(enhancedDarkImage);
                    Log("[DirectSegmentFeatures] Added dark-feature enhanced image");
                }
                else
                {
                    // For bright features, create brightness-enhanced version
                    Bitmap enhancedBrightImage = new Bitmap(image.Width, image.Height);
                    for (int y = 0; y < image.Height; y++)
                    {
                        for (int x = 0; x < image.Width; x++)
                        {
                            Color pixel = image.GetPixel(x, y);
                            int val = (pixel.R + pixel.G + pixel.B) / 3;

                            // Apply non-linear curve to enhance bright features
                            double enhancedVal;
                            if (val > posAvgBrightness - 20)
                            {
                                // For pixels in the bright target range, amplify differences
                                double normalizedVal = (val - posAvgBrightness) / (255.0 - posAvgBrightness);
                                enhancedVal = posAvgBrightness + normalizedVal * 255;
                            }
                            else
                            {
                                // For darker areas, suppress them further
                                enhancedVal = val * 0.7;
                            }

                            int newVal = (int)Math.Min(255, Math.Max(0, enhancedVal));
                            enhancedBrightImage.SetPixel(x, y, Color.FromArgb(newVal, newVal, newVal));
                        }
                    }
                    inputVersions.Add(enhancedBrightImage);
                }

                // 4. PROCESS EACH INPUT VERSION
                for (int versionIdx = 0; versionIdx < inputVersions.Count; versionIdx++)
                {
                    Bitmap inputImage = inputVersions[versionIdx];
                    string versionName = versionIdx == 0 ? "Original" :
                                        (versionIdx == 1 && isTargetingDarkFeatures ? "Inverted" : "Enhanced");

                    // Prepare model input tensor 
                    float[] imageTensorData = new float[3 * _imageSize * _imageSize];

                    // Create high-quality resized image
                    Bitmap resizedImage = new Bitmap(_imageSize, _imageSize);
                    using (Graphics g = Graphics.FromImage(resizedImage))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(inputImage, 0, 0, _imageSize, _imageSize);
                    }

                    // Use proper normalization for SAM model
                    float[] mean = new float[] { 0.485f, 0.456f, 0.406f };
                    float[] std = new float[] { 0.229f, 0.224f, 0.225f };

                    for (int y = 0; y < _imageSize; y++)
                    {
                        for (int x = 0; x < _imageSize; x++)
                        {
                            Color pixel = resizedImage.GetPixel(x, y);

                            float r = (pixel.R / 255f - mean[0]) / std[0];
                            float g = (pixel.G / 255f - mean[1]) / std[1];
                            float b = (pixel.B / 255f - mean[2]) / std[2];

                            // NCHW format required by ONNX
                            imageTensorData[0 * _imageSize * _imageSize + y * _imageSize + x] = r;
                            imageTensorData[1 * _imageSize * _imageSize + y * _imageSize + x] = g;
                            imageTensorData[2 * _imageSize * _imageSize + y * _imageSize + x] = b;
                        }
                    }
                    resizedImage.Dispose();

                    // Create image tensor and run the encoder
                    var imageInput = new DenseTensor<float>(imageTensorData, new[] { 1, 3, _imageSize, _imageSize });
                    Tensor<float> imageEmbeddings = null;
                    Tensor<float> highResFeatures0 = null;
                    Tensor<float> highResFeatures1 = null;

                    try
                    {
                        using (var encoderOutputs = _encoderSession.Run(new[] { NamedOnnxValue.CreateFromTensor("image", imageInput) }))
                        {
                            imageEmbeddings = encoderOutputs.FirstOrDefault(o => o.Name == "image_embeddings")?.AsTensor<float>();
                            highResFeatures0 = encoderOutputs.FirstOrDefault(o => o.Name == "high_res_feats_0")?.AsTensor<float>();
                            highResFeatures1 = encoderOutputs.FirstOrDefault(o => o.Name == "high_res_feats_1")?.AsTensor<float>();

                            if (imageEmbeddings == null)
                            {
                                Log("[DirectSegmentFeatures] ERROR: No image embeddings produced by encoder");
                                continue;
                            }
                        }
                        Log($"[DirectSegmentFeatures] Encoder successful for {versionName}");
                    }
                    catch (Exception ex)
                    {
                        Log($"[DirectSegmentFeatures] Encoder error for {versionName}: {ex.Message}");
                        continue;
                    }

                    // Prepare point prompts for SAM
                    // If targeting dark features, we need to adapt the prompt points for inverted image
                    List<AnnotationPoint> versionPrompts = new List<AnnotationPoint>(promptPoints);
                    if (versionIdx == 1 && isTargetingDarkFeatures)
                    {
                        // For inverted image, we need to swap positive and negative points
                        versionPrompts = promptPoints.Select(p => new AnnotationPoint
                        {
                            X = p.X,
                            Y = p.Y,
                            Z = p.Z,
                            Label = p.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase) ?
                                "Foreground" : "Exterior"
                        }).ToList();
                    }

                    // Process all points for this version
                    int pointCount = versionPrompts.Count;
                    float[] pointInputs = new float[pointCount * 2];
                    float[] pointLabels = new float[pointCount];

                    for (int i = 0; i < pointCount; i++)
                    {
                        var point = versionPrompts[i];

                        // Scale to model coordinates
                        float xScale = (_imageSize - 1f) / Math.Max(1, image.Width - 1f);
                        float yScale = (_imageSize - 1f) / Math.Max(1, image.Height - 1f);

                        pointInputs[i * 2] = Math.Max(0, Math.Min(point.X, image.Width - 1)) * xScale;
                        pointInputs[i * 2 + 1] = Math.Max(0, Math.Min(point.Y, image.Height - 1)) * yScale;
                        pointLabels[i] = point.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase) ? 0.0f : 1.0f;
                    }

                    // Create input tensors for decoder
                    var pointInputsTensor = new DenseTensor<float>(pointInputs, new[] { 1, pointCount, 2 });
                    var pointLabelsTensor = new DenseTensor<float>(pointLabels, new[] { 1, pointCount });
                    var origSizeTensor = new DenseTensor<int>(new[] { image.Height, image.Width }, new[] { 2 });

                    // Run decoder
                    Tensor<byte> masksTensor = null;
                    Tensor<float> iousTensor = null;

                    try
                    {
                        using (var decoderOutputs = _decoderSession.Run(new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("image_embeddings", imageEmbeddings),
                    NamedOnnxValue.CreateFromTensor("high_res_feats_0", highResFeatures0),
                    NamedOnnxValue.CreateFromTensor("high_res_feats_1", highResFeatures1),
                    NamedOnnxValue.CreateFromTensor("original_image_size", origSizeTensor),
                    NamedOnnxValue.CreateFromTensor("point_inputs", pointInputsTensor),
                    NamedOnnxValue.CreateFromTensor("point_labels", pointLabelsTensor)
                }))
                        {
                            masksTensor = decoderOutputs.FirstOrDefault(o => o.Name == "masks")?.AsTensor<byte>();
                            iousTensor = decoderOutputs.FirstOrDefault(o => o.Name == "ious")?.AsTensor<float>();

                            if (masksTensor == null || iousTensor == null)
                            {
                                Log($"[DirectSegmentFeatures] ERROR: Missing output tensor for {versionName}");
                                continue;
                            }
                        }
                        Log($"[DirectSegmentFeatures] Decoder successful for {versionName}");
                    }
                    catch (Exception ex)
                    {
                        Log($"[DirectSegmentFeatures] Decoder error for {versionName}: {ex.Message}");
                        continue;
                    }

                    // Process mask outputs
                    int maskCount = masksTensor.Dimensions[1];
                    int maskHeight = masksTensor.Dimensions[2];
                    int maskWidth = masksTensor.Dimensions[3];

                    Log($"[DirectSegmentFeatures] {versionName} produced {maskCount} masks of size {maskWidth}x{maskHeight}");

                    // Log IoU scores and find mask values
                    byte[] maxValues = new byte[maskCount];

                    for (int maskIdx = 0; maskIdx < maskCount; maskIdx++)
                    {
                        // Find max value in the mask
                        byte maxVal = 0;
                        for (int y = 0; y < maskHeight; y++)
                        {
                            for (int x = 0; x < maskWidth; x++)
                            {
                                maxVal = Math.Max(maxVal, masksTensor[0, maskIdx, y, x]);
                            }
                        }
                        maxValues[maskIdx] = maxVal;

                        Log($"[DirectSegmentFeatures] {versionName} Mask {maskIdx}: IoU={iousTensor[0, maskIdx]:F4}, MaxVal={maxVal}");
                    }

                    // Only proceed with mask creation if we have valid signals
                    if (maxValues.Max() == 0)
                    {
                        Log($"[DirectSegmentFeatures] {versionName} has no valid mask signal. Skipping.");
                        continue;
                    }

                    // Process each mask with adaptive thresholds
                    for (int maskIdx = 0; maskIdx < maskCount; maskIdx++)
                    {
                        if (masks.Count >= 4) break; // Stop if we already have 4 masks

                        // Skip masks with no signal
                        if (maxValues[maskIdx] == 0) continue;

                        // Use carefully chosen thresholds based on max value
                        byte maxVal = maxValues[maskIdx];
                        List<byte> thresholds = new List<byte>();

                        if (isTargetingDarkFeatures)
                        {
                            // For dark features we need more aggressive thresholds
                            thresholds.Add(1); // Ultra-low threshold to catch any signal
                            thresholds.Add((byte)Math.Max(1, maxVal * 0.1f)); // 10% of max
                            thresholds.Add((byte)Math.Max(1, maxVal * 0.3f)); // 30% of max
                        }
                        else
                        {
                            // For bright features
                            thresholds.Add((byte)Math.Max(1, maxVal * 0.05f)); // 5% of max
                            thresholds.Add((byte)Math.Max(1, maxVal * 0.2f));  // 20% of max
                            thresholds.Add((byte)Math.Max(1, maxVal * 0.4f));  // 40% of max
                        }

                        foreach (byte threshold in thresholds)
                        {
                            if (masks.Count >= 4) break; // Stop if we already have 4 masks

                            // Create high quality mask at original mask resolution
                            Bitmap maskBitmap = new Bitmap(maskWidth, maskHeight, PixelFormat.Format32bppArgb);
                            BitmapData bmpData = maskBitmap.LockBits(
                                new Rectangle(0, 0, maskWidth, maskHeight),
                                ImageLockMode.WriteOnly,
                                PixelFormat.Format32bppArgb);

                            int whitePixels = 0;

                            unsafe
                            {
                                byte* ptr = (byte*)bmpData.Scan0;
                                int stride = bmpData.Stride;

                                for (int y = 0; y < maskHeight; y++)
                                {
                                    for (int x = 0; x < maskWidth; x++)
                                    {
                                        byte value = masksTensor[0, maskIdx, y, x];
                                        bool isOn = value >= threshold;

                                        if (isOn) whitePixels++;

                                        int offset = y * stride + x * 4;
                                        byte pixelVal = isOn ? (byte)255 : (byte)0;

                                        // BGRA format
                                        ptr[offset] = pixelVal;     // B
                                        ptr[offset + 1] = pixelVal; // G
                                        ptr[offset + 2] = pixelVal; // R
                                        ptr[offset + 3] = 255;      // A (fully opaque)
                                    }
                                }
                            }

                            maskBitmap.UnlockBits(bmpData);

                            float coverage = (float)whitePixels / (maskWidth * maskHeight);
                            Log($"[DirectSegmentFeatures] {versionName} Mask {maskIdx}, Threshold={threshold}: Coverage={coverage:P3}");

                            // Skip empty or full masks
                            if (coverage <= 0.0001f || coverage >= 0.9999f)
                            {
                                maskBitmap.Dispose();
                                continue;
                            }

                            // Now scale to original image dimensions with high quality
                            Bitmap finalMask = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
                            using (Graphics g = Graphics.FromImage(finalMask))
                            {
                                // Use high quality settings for mask scaling to avoid pixelation
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.SmoothingMode = SmoothingMode.HighQuality;
                                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                                g.CompositingQuality = CompositingQuality.HighQuality;

                                // Draw the mask at full image size
                                g.DrawImage(maskBitmap, 0, 0, image.Width, image.Height);
                            }

                            // For inverted image masks, we need to invert the mask if targeting dark features
                            if (versionIdx == 1 && isTargetingDarkFeatures)
                            {
                                Bitmap invertedMask = new Bitmap(finalMask.Width, finalMask.Height);
                                using (Graphics g = Graphics.FromImage(invertedMask))
                                {
                                    // Set background to black
                                    g.Clear(Color.Black);

                                    // Set up to draw the inverted mask (white becomes black, black becomes white)
                                    using (ImageAttributes attrs = new ImageAttributes())
                                    {
                                        ColorMatrix inverter = new ColorMatrix(
                                            new float[][] {
                                        new float[] {-1, 0, 0, 0, 0},
                                        new float[] {0, -1, 0, 0, 0},
                                        new float[] {0, 0, -1, 0, 0},
                                        new float[] {0, 0, 0, 1, 0},
                                        new float[] {1, 1, 1, 0, 1}
                                            });
                                        attrs.SetColorMatrix(inverter);

                                        g.DrawImage(finalMask,
                                            new Rectangle(0, 0, finalMask.Width, finalMask.Height),
                                            0, 0, finalMask.Width, finalMask.Height,
                                            GraphicsUnit.Pixel, attrs);
                                    }
                                }
                                finalMask.Dispose();
                                finalMask = invertedMask;
                            }

                            // Add mask to the collection
                            masks.Add(finalMask);
                            Log($"[DirectSegmentFeatures] Added {versionName} mask {maskIdx}, threshold={threshold}");

                            // Clean up temporary bitmap
                            maskBitmap.Dispose();
                        }
                    }

                    // Clean up version image if not the original
                    if (versionIdx > 0)
                    {
                        inputImage.Dispose();
                    }

                    // If we have enough masks, stop processing more versions
                    if (masks.Count >= 4)
                    {
                        Log($"[DirectSegmentFeatures] Got enough masks ({masks.Count}), stopping further processing");
                        break;
                    }
                }

                // If we still don't have any masks, fall back to raw visualization
                if (masks.Count == 0)
                {
                    Log("[DirectSegmentFeatures] Failed to create any valid masks, creating fallback");
                    Bitmap fallback = CreateFallbackVisualization(image, promptPoints, isTargetingDarkFeatures);
                    masks.Add(fallback);
                }

                return masks;
            }
            catch (Exception ex)
            {
                Log($"[DirectSegmentFeatures] Error: {ex.Message}");
                Log($"[DirectSegmentFeatures] Stack trace: {ex.StackTrace}");

                // Clean up any masks
                foreach (var mask in masks)
                {
                    try { mask.Dispose(); } catch { }
                }
                masks.Clear();

                // Create simple error indicator
                Bitmap errorMask = new Bitmap(image.Width, image.Height);
                using (Graphics g = Graphics.FromImage(errorMask))
                {
                    g.Clear(Color.Black);
                    using (Font font = new Font("Arial", 12))
                    using (Brush brush = new SolidBrush(Color.Red))
                    {
                        g.DrawString($"Error: {ex.Message}", font, brush, 10, 10);
                    }
                }
                masks.Add(errorMask);

                return masks;
            }
        }

        // Helper for creating a fallback visualization
        private Bitmap CreateFallbackVisualization(Bitmap image, List<AnnotationPoint> promptPoints, bool isDarkTarget)
        {
            Bitmap visualization = new Bitmap(image.Width, image.Height);
            using (Graphics g = Graphics.FromImage(visualization))
            {
                // Draw a grayscale version of the image with reduced opacity
                g.Clear(Color.Black);

                // Extract positive and negative points
                var positivePoints = promptPoints.Where(p => !p.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase)).ToList();
                var negativePoints = promptPoints.Where(p => p.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase)).ToList();

                // Draw a region around positive points
                if (positivePoints.Count > 0)
                {
                    // Calculate centroid
                    float centerX = positivePoints.Average(p => p.X);
                    float centerY = positivePoints.Average(p => p.Y);

                    // Calculate spread
                    float radius = 30; // Default
                    if (positivePoints.Count > 1)
                    {
                        float maxDist = positivePoints.Max(p =>
                            (float)Math.Sqrt(Math.Pow(p.X - centerX, 2) + Math.Pow(p.Y - centerY, 2)));
                        radius = Math.Max(30, maxDist * 1.5f);
                    }

                    // Draw area
                    using (Brush brush = new SolidBrush(isDarkTarget ? Color.Blue : Color.Red))
                    {
                        g.FillEllipse(brush, centerX - radius, centerY - radius, radius * 2, radius * 2);
                    }
                }

                // Overlay the point markers
                using (Brush posBrush = new SolidBrush(Color.Lime))
                using (Brush negBrush = new SolidBrush(Color.Red))
                {
                    foreach (var point in promptPoints)
                    {
                        bool isPositive = !point.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase);
                        g.FillEllipse(isPositive ? posBrush : negBrush,
                                     point.X - 5, point.Y - 5, 10, 10);
                    }
                }

                // Add text about the fallback
                using (Font font = new Font("Arial", 12))
                using (Brush textBrush = new SolidBrush(Color.White))
                using (StringFormat sf = new StringFormat() { Alignment = StringAlignment.Center })
                {
                    string message = "Fallback visualization\nTarget: " + (isDarkTarget ? "Dark features" : "Bright features");
                    g.DrawString(message, font, textBrush, new PointF(image.Width / 2, 20), sf);
                }
            }

            return visualization;
        }

        // Helper method to create fallback masks
        private List<Bitmap> CreateFallbackMasks(int width, int height, List<AnnotationPoint> promptPoints)
        {
            List<Bitmap> fallbacks = new List<Bitmap>();

            for (int i = 0; i < 4; i++)
            {
                fallbacks.Add(CreateSingleFallbackMask(width, height, promptPoints, i));
            }

            return fallbacks;
        }

        // Helper method for a single fallback mask
        private Bitmap CreateSingleFallbackMask(int width, int height, List<AnnotationPoint> promptPoints, int style)
        {
            // Extract positive points for visualization
            var positivePoints = promptPoints?.Where(p => !p.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase))?.ToList()
                                ?? new List<AnnotationPoint>();

            Bitmap fallback = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(fallback))
            {
                g.Clear(Color.Black);

                switch (style % 4)
                {
                    case 0: // Simple dots
                        using (Brush brush = new SolidBrush(Color.Red))
                        {
                            foreach (var point in positivePoints)
                            {
                                g.FillEllipse(brush, point.X - 10, point.Y - 10, 20, 20);
                            }
                        }
                        break;

                    case 1: // Large circle at centroid
                        if (positivePoints.Count > 0)
                        {
                            // Calculate centroid
                            float centerX = positivePoints.Average(p => p.X);
                            float centerY = positivePoints.Average(p => p.Y);

                            using (Brush brush = new SolidBrush(Color.Red))
                            {
                                float radius = 30;
                                g.FillEllipse(brush, centerX - radius, centerY - radius, radius * 2, radius * 2);
                            }
                        }
                        break;

                    case 2: // Connect points
                        if (positivePoints.Count >= 2)
                        {
                            using (Pen pen = new Pen(Color.Red, 2))
                            {
                                for (int i = 0; i < positivePoints.Count - 1; i++)
                                {
                                    g.DrawLine(pen,
                                        positivePoints[i].X, positivePoints[i].Y,
                                        positivePoints[i + 1].X, positivePoints[i + 1].Y);
                                }

                                // Connect last to first
                                g.DrawLine(pen,
                                    positivePoints[positivePoints.Count - 1].X, positivePoints[positivePoints.Count - 1].Y,
                                    positivePoints[0].X, positivePoints[0].Y);
                            }
                        }
                        break;

                    case 3: // Box/outline
                        if (positivePoints.Count >= 2)
                        {
                            // Find bounding box
                            float minX = positivePoints.Min(p => p.X);
                            float minY = positivePoints.Min(p => p.Y);
                            float maxX = positivePoints.Max(p => p.X);
                            float maxY = positivePoints.Max(p => p.Y);

                            // Add some padding
                            minX = Math.Max(0, minX - 10);
                            minY = Math.Max(0, minY - 10);
                            maxX = Math.Min(width - 1, maxX + 10);
                            maxY = Math.Min(height - 1, maxY + 10);

                            using (Pen pen = new Pen(Color.Green, 2))
                            {
                                g.DrawRectangle(pen, minX, minY, maxX - minX, maxY - minY);
                            }
                        }
                        break;
                }

                // Add text indicating this is a fallback
                using (Font font = new Font("Arial", 8))
                using (Brush textBrush = new SolidBrush(Color.White))
                {
                    g.DrawString("FallbackMask", font, textBrush, 5, height - 15);
                }
            }

            return fallback;
        }

        private List<Bitmap> GenerateMasksFromImageSam2(Bitmap image, List<AnnotationPoint> promptPoints, string sourceType)
        {
            List<Bitmap> masks = new List<Bitmap>();

            try
            {
                // Log basic information about the segmentation task
                Log($"[GenerateMasksFromImageSam2] Processing {sourceType} image: {image.Width}x{image.Height}, points={promptPoints.Count}");

                // First analyze what the user is trying to segment based on point placement
                var positivePoints = promptPoints.Where(p => !p.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase)).ToList();
                var negativePoints = promptPoints.Where(p => p.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase)).ToList();

                // Calculate average brightness under positive and negative points
                double avgPositiveBrightness = 0;
                double avgNegativeBrightness = 0;

                if (positivePoints.Count > 0)
                {
                    foreach (var point in positivePoints)
                    {
                        int x = Math.Min(Math.Max(0, (int)point.X), image.Width - 1);
                        int y = Math.Min(Math.Max(0, (int)point.Y), image.Height - 1);
                        Color pixel = image.GetPixel(x, y);
                        avgPositiveBrightness += (pixel.R + pixel.G + pixel.B) / 3.0;
                    }
                    avgPositiveBrightness /= positivePoints.Count;
                }

                if (negativePoints.Count > 0)
                {
                    foreach (var point in negativePoints)
                    {
                        int x = Math.Min(Math.Max(0, (int)point.X), image.Width - 1);
                        int y = Math.Min(Math.Max(0, (int)point.Y), image.Height - 1);
                        Color pixel = image.GetPixel(x, y);
                        avgNegativeBrightness += (pixel.R + pixel.G + pixel.B) / 3.0;
                    }
                    avgNegativeBrightness /= negativePoints.Count;
                }

                Log($"[GenerateMasksFromImageSam2] Analysis: Positive points avg brightness={avgPositiveBrightness:F1}, " +
                    $"Negative points avg brightness={avgNegativeBrightness:F1}");

                // Strategy depends on what's being segmented
                bool needsInversion = false;
                bool isMicrostructureDark = false;

                // If positive points are on darker areas than negative points, we're likely targeting pores
                if (avgPositiveBrightness < avgNegativeBrightness && positivePoints.Count > 0 && negativePoints.Count > 0)
                {
                    Log("[GenerateMasksFromImageSam2] User appears to be selecting darker structures (pores/cracks)");
                    isMicrostructureDark = true;
                }
                else if (avgPositiveBrightness > avgNegativeBrightness && positivePoints.Count > 0 && negativePoints.Count > 0)
                {
                    Log("[GenerateMasksFromImageSam2] User appears to be selecting brighter structures");
                }
                else if (positivePoints.Count > 0)
                {
                    // Just look at absolute brightness if no negative points
                    isMicrostructureDark = avgPositiveBrightness < 128;
                    Log($"[GenerateMasksFromImageSam2] No negative points, positive points are " +
                        (isMicrostructureDark ? "dark" : "bright"));
                }

                // Create multiple versions of inputs for SAM
                List<Bitmap> inputVersions = new List<Bitmap>();
                List<string> versionDescriptions = new List<string>();

                // Always process original image
                inputVersions.Add(new Bitmap(image));
                versionDescriptions.Add("Original");

                // Process an inverted image version only for darker structures
                if (isMicrostructureDark)
                {
                    // Create inverted image to make pores easier to detect
                    Bitmap invertedImage = new Bitmap(image.Width, image.Height);
                    for (int y = 0; y < image.Height; y++)
                    {
                        for (int x = 0; x < image.Width; x++)
                        {
                            Color pixel = image.GetPixel(x, y);
                            invertedImage.SetPixel(x, y, Color.FromArgb(255 - pixel.R, 255 - pixel.G, 255 - pixel.B));
                        }
                    }
                    inputVersions.Add(invertedImage);
                    versionDescriptions.Add("Inverted");
                    Log("[GenerateMasksFromImageSam2] Added inverted image to help with dark structure detection");
                }

                // Create an enhanced contrast version
                Bitmap enhancedImage = new Bitmap(image.Width, image.Height);
                float[] histogram = new float[256];
                int pixelCount = 0;

                // Calculate histogram for contrast enhancement
                for (int y = 0; y < image.Height; y++)
                {
                    for (int x = 0; x < image.Width; x++)
                    {
                        Color pixel = image.GetPixel(x, y);
                        int intensity = (pixel.R + pixel.G + pixel.B) / 3;
                        histogram[intensity]++;
                        pixelCount++;
                    }
                }

                // Find 5th and 95th percentiles for contrast stretching
                int lowerBound = 0;
                int sumLower = 0;
                while (sumLower < pixelCount * 0.05 && lowerBound < 255)
                {
                    sumLower += (int)histogram[lowerBound];
                    lowerBound++;
                }

                int upperBound = 255;
                int sumUpper = 0;
                while (sumUpper < pixelCount * 0.05 && upperBound > 0)
                {
                    sumUpper += (int)histogram[upperBound];
                    upperBound--;
                }

                // Apply contrast stretching if there's room for improvement
                if (upperBound - lowerBound > 50)
                {
                    for (int y = 0; y < image.Height; y++)
                    {
                        for (int x = 0; x < image.Width; x++)
                        {
                            Color pixel = image.GetPixel(x, y);

                            // Apply contrast stretching to each channel
                            int r = AdjustPixelValue(pixel.R, lowerBound, upperBound);
                            int g = AdjustPixelValue(pixel.G, lowerBound, upperBound);
                            int b = AdjustPixelValue(pixel.B, lowerBound, upperBound);

                            enhancedImage.SetPixel(x, y, Color.FromArgb(r, g, b));
                        }
                    }
                    inputVersions.Add(enhancedImage);
                    versionDescriptions.Add("Enhanced");
                    Log($"[GenerateMasksFromImageSam2] Added contrast enhanced image [{lowerBound}-{upperBound}] -> [0-255]");
                }

                // Process each input version
                for (int versionIndex = 0; versionIndex < inputVersions.Count; versionIndex++)
                {
                    Bitmap inputImage = inputVersions[versionIndex];
                    string versionDesc = versionDescriptions[versionIndex];

                    Log($"[GenerateMasksFromImageSam2] Processing version: {versionDesc}");

                    // Prepare image tensor with proper normalization
                    float[] imageTensorData = new float[3 * _imageSize * _imageSize];
                    Bitmap resizedImage = new Bitmap(inputImage, new Size(_imageSize, _imageSize));

                    // Normalization parameters for SAM
                    float[] mean = new float[] { 0.485f, 0.456f, 0.406f };
                    float[] std = new float[] { 0.229f, 0.224f, 0.225f };

                    for (int y = 0; y < _imageSize; y++)
                    {
                        for (int x = 0; x < _imageSize; x++)
                        {
                            Color pixel = resizedImage.GetPixel(x, y);

                            // Apply normalization (RGB)
                            float r = (pixel.R / 255f - mean[0]) / std[0];
                            float g = (pixel.G / 255f - mean[1]) / std[1];
                            float b = (pixel.B / 255f - mean[2]) / std[2];

                            // NCHW format (channels first for ONNX)
                            imageTensorData[0 * _imageSize * _imageSize + y * _imageSize + x] = r;
                            imageTensorData[1 * _imageSize * _imageSize + y * _imageSize + x] = g;
                            imageTensorData[2 * _imageSize * _imageSize + y * _imageSize + x] = b;
                        }
                    }
                    resizedImage.Dispose();

                    // Create tensor and run encoder - handle exceptions explicitly
                    var imageInput = new DenseTensor<float>(imageTensorData, new[] { 1, 3, _imageSize, _imageSize });
                    Tensor<float> imageEmbeddings;
                    Tensor<float> highResFeatures0;
                    Tensor<float> highResFeatures1;

                    try
                    {
                        Log($"[GenerateMasksFromImageSam2] Running encoder on {versionDesc} image");
                        using (var encoderOutputs = _encoderSession.Run(new[] { NamedOnnxValue.CreateFromTensor("image", imageInput) }))
                        {
                            imageEmbeddings = encoderOutputs.FirstOrDefault(o => o.Name == "image_embeddings")?.AsTensor<float>();
                            highResFeatures0 = encoderOutputs.FirstOrDefault(o => o.Name == "high_res_feats_0")?.AsTensor<float>();
                            highResFeatures1 = encoderOutputs.FirstOrDefault(o => o.Name == "high_res_feats_1")?.AsTensor<float>();

                            if (StorePreviousEmbeddings)
                            {
                                SaveEmbeddingsForPropagation(imageEmbeddings, highResFeatures0, highResFeatures1);
                            }
                        }
                        Log($"[GenerateMasksFromImageSam2] Encoder completed successfully for {versionDesc}");
                    }
                    catch (Exception ex)
                    {
                        Log($"[GenerateMasksFromImageSam2] Encoder error on {versionDesc}: {ex.Message}");
                        continue; // Skip to next version
                    }

                    // Process all points and prepare decoder inputs
                    int pointCount = promptPoints.Count;
                    float[] pointInputs = new float[pointCount * 2];
                    float[] pointLabels = new float[pointCount];

                    for (int i = 0; i < pointCount; i++)
                    {
                        var point = promptPoints[i];

                        // Scale coordinates to model input size
                        float xClamped = Math.Max(0, Math.Min(point.X, image.Width - 1));
                        float yClamped = Math.Max(0, Math.Min(point.Y, image.Height - 1));
                        float xScale = (_imageSize - 1f) / Math.Max(1, image.Width - 1f);
                        float yScale = (_imageSize - 1f) / Math.Max(1, image.Height - 1f);

                        pointInputs[i * 2] = xClamped * xScale;
                        pointInputs[i * 2 + 1] = yClamped * yScale;
                        pointLabels[i] = point.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase) ? 0.0f : 1.0f;

                        Log($"[Point {i}] ({point.X}, {point.Y}) → ({pointInputs[i * 2]:F1}, {pointInputs[i * 2 + 1]:F1}), label: {pointLabels[i]}");
                    }

                    var pointInputsTensor = new DenseTensor<float>(pointInputs, new[] { 1, pointCount, 2 });
                    var pointLabelsTensor = new DenseTensor<float>(pointLabels, new[] { 1, pointCount });
                    var origSizeTensor = new DenseTensor<int>(new[] { image.Height, image.Width }, new[] { 2 });

                    // Run decoder
                    Tensor<byte> masksTensor;
                    Tensor<float> iousTensor;

                    try
                    {
                        Log($"[GenerateMasksFromImageSam2] Running decoder for {versionDesc}");
                        using (var decoderOutputs = _decoderSession.Run(new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("image_embeddings", imageEmbeddings),
                    NamedOnnxValue.CreateFromTensor("high_res_feats_0", highResFeatures0),
                    NamedOnnxValue.CreateFromTensor("high_res_feats_1", highResFeatures1),
                    NamedOnnxValue.CreateFromTensor("original_image_size", origSizeTensor),
                    NamedOnnxValue.CreateFromTensor("point_inputs", pointInputsTensor),
                    NamedOnnxValue.CreateFromTensor("point_labels", pointLabelsTensor)
                }))
                        {
                            masksTensor = decoderOutputs.FirstOrDefault(o => o.Name == "masks")?.AsTensor<byte>();
                            iousTensor = decoderOutputs.FirstOrDefault(o => o.Name == "ious")?.AsTensor<float>();

                            if (masksTensor == null || iousTensor == null)
                            {
                                Log($"[GenerateMasksFromImageSam2] Warning: Missing output tensor for {versionDesc}");
                                continue;
                            }
                        }

                        // Get mask dimensions
                        int maskCount = masksTensor.Dimensions[1];
                        int maskHeight = masksTensor.Dimensions[2];
                        int maskWidth = masksTensor.Dimensions[3];

                        Log($"[GenerateMasksFromImageSam2] Decoder output for {versionDesc}: {maskCount} masks of size {maskWidth}x{maskHeight}");

                        // Log IoU scores
                        for (int i = 0; i < maskCount; i++)
                        {
                            Log($"[GenerateMasksFromImageSam2] Mask {i} IoU: {iousTensor[0, i]:F4}");
                        }

                        // Analyze the mask content to understand what we got
                        byte[] maxValues = new byte[maskCount];
                        byte[] minValues = new byte[maskCount];
                        double[] avgValues = new double[maskCount];
                        int[] nonZeroPixelCounts = new int[maskCount];

                        for (int maskIdx = 0; maskIdx < maskCount; maskIdx++)
                        {
                            byte maxVal = 0;
                            byte minVal = 255;
                            double total = 0;
                            int nonZeroCount = 0;

                            for (int y = 0; y < maskHeight; y++)
                            {
                                for (int x = 0; x < maskWidth; x++)
                                {
                                    byte value = masksTensor[0, maskIdx, y, x];
                                    maxVal = Math.Max(maxVal, value);
                                    minVal = Math.Min(minVal, value);
                                    total += value;
                                    if (value > 0) nonZeroCount++;
                                }
                            }

                            maxValues[maskIdx] = maxVal;
                            minValues[maskIdx] = minVal;
                            avgValues[maskIdx] = total / (maskWidth * maskHeight);
                            nonZeroPixelCounts[maskIdx] = nonZeroCount;

                            Log($"[GenerateMasksFromImageSam2] {versionDesc} Mask {maskIdx} stats: Min={minVal}, Max={maxVal}, " +
                                $"Avg={avgValues[maskIdx]:F2}, NonZero={nonZeroCount}/{maskWidth * maskHeight}");
                        }

                        // Process each mask with adaptive thresholds
                        for (int maskIdx = 0; maskIdx < maskCount; maskIdx++)
                        {
                            // Skip masks with no signal (all zeros)
                            if (maxValues[maskIdx] == 0)
                            {
                                Log($"[GenerateMasksFromImageSam2] Skipping {versionDesc} mask {maskIdx} - no signal");
                                continue;
                            }

                            // Prepare thresholds based on the mask content
                            byte[] thresholds;

                            if (maxValues[maskIdx] <= 5)
                            {
                                // For extremely low signals, use ultra-low thresholds
                                thresholds = new byte[] { 1 };
                                Log($"[GenerateMasksFromImageSam2] {versionDesc} mask {maskIdx} - ultra-low signal, using threshold={thresholds[0]}");
                            }
                            else if (maxValues[maskIdx] < 50)
                            {
                                // For low signals, use low and very low thresholds
                                thresholds = new byte[] { 1, 5 };
                                Log($"[GenerateMasksFromImageSam2] {versionDesc} mask {maskIdx} - low signal, using thresholds={string.Join(",", thresholds)}");
                            }
                            else
                            {
                                // For normal signals, use multiple thresholds based on the max value
                                thresholds = new byte[]
                                {
                            (byte)Math.Max(1, maxValues[maskIdx] * 0.05),  // 5% of max
                            (byte)Math.Max(2, maxValues[maskIdx] * 0.15),  // 15% of max
                            (byte)Math.Max(5, maxValues[maskIdx] * 0.30)   // 30% of max
                                };
                                Log($"[GenerateMasksFromImageSam2] {versionDesc} mask {maskIdx} - normal signal, using thresholds={string.Join(",", thresholds)}");
                            }

                            // Create binary masks for each threshold
                            foreach (byte threshold in thresholds)
                            {
                                if (masks.Count >= 4) break; // Only keep top 4 masks

                                Bitmap binaryMask = new Bitmap(maskWidth, maskHeight);
                                int whitePixels = 0;

                                // Create binary mask based on the threshold
                                for (int y = 0; y < maskHeight; y++)
                                {
                                    for (int x = 0; x < maskWidth; x++)
                                    {
                                        byte value = masksTensor[0, maskIdx, y, x];
                                        Color color;

                                        if (value >= threshold)
                                        {
                                            color = Color.White;
                                            whitePixels++;
                                        }
                                        else
                                        {
                                            color = Color.Black;
                                        }

                                        binaryMask.SetPixel(x, y, color);
                                    }
                                }

                                float coverage = (float)whitePixels / (maskWidth * maskHeight);

                                // Only add masks with some coverage but not too much
                                if (coverage > 0.0001f && coverage < 0.99f)
                                {
                                    // Scale mask to original image size
                                    Bitmap scaledMask = new Bitmap(image.Width, image.Height);
                                    using (Graphics g = Graphics.FromImage(scaledMask))
                                    {
                                        g.InterpolationMode = InterpolationMode.NearestNeighbor; // Better for binary masks
                                        g.DrawImage(binaryMask, 0, 0, image.Width, image.Height);
                                    }

                                    // For inverted images, we need to invert the mask result too for consistency
                                    if (versionDesc == "Inverted" && isMicrostructureDark)
                                    {
                                        Bitmap invertedMask = new Bitmap(scaledMask.Width, scaledMask.Height);
                                        for (int y = 0; y < scaledMask.Height; y++)
                                        {
                                            for (int x = 0; x < scaledMask.Width; x++)
                                            {
                                                Color c = scaledMask.GetPixel(x, y);
                                                invertedMask.SetPixel(x, y, c.R > 128 ? Color.Black : Color.White);
                                            }
                                        }
                                        scaledMask.Dispose();
                                        scaledMask = invertedMask;
                                    }

                                    // Add to the list
                                    masks.Add(scaledMask);
                                    Log($"[GenerateMasksFromImageSam2] Added {versionDesc} mask {maskIdx}, threshold={threshold}, coverage={coverage:P2}");
                                }
                                else
                                {
                                    Log($"[GenerateMasksFromImageSam2] Skipping {versionDesc} mask {maskIdx}, threshold={threshold}, invalid coverage={coverage:P2}");
                                }

                                binaryMask.Dispose();

                                // Break early if we found a good mask
                                if (masks.Count >= 4) break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[GenerateMasksFromImageSam2] Decoder error on {versionDesc}: {ex.Message}");
                        Log($"[GenerateMasksFromImageSam2] Stack Trace: {ex.StackTrace}");
                        continue; // Move to next version
                    }

                    // Dispose the image
                    if (versionIndex > 0) // Keep original
                        inputImage.Dispose();

                    // If we have enough masks, stop processing more versions
                    if (masks.Count >= 4)
                        break;
                }

                // Clean up any remaining input versions
                for (int i = 1; i < inputVersions.Count; i++)
                {
                    try { inputVersions[i].Dispose(); } catch { }
                }

                // If no valid masks were created, make diagnostic masks
                if (masks.Count == 0)
                {
                    Log("[GenerateMasksFromImageSam2] No valid masks created, falling back to diagnostic visualization");

                    // Create distinctive fallback
                    Bitmap fallback = new Bitmap(image.Width, image.Height);
                    using (Graphics g = Graphics.FromImage(fallback))
                    {
                        g.Clear(Color.Black);

                        // Draw annotation points
                        using (Brush positiveBrush = new SolidBrush(Color.LimeGreen))
                        using (Brush negativeBrush = new SolidBrush(Color.Red))
                        {
                            foreach (var point in promptPoints)
                            {
                                bool isPositive = !point.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase);
                                g.FillEllipse(isPositive ? positiveBrush : negativeBrush,
                                              point.X - 5, point.Y - 5, 10, 10);
                            }
                        }

                        // Add text explaining the diagnostic
                        using (Font font = new Font("Arial", 10, FontStyle.Bold))
                        using (Brush textBrush = new SolidBrush(Color.White))
                        using (Brush shadowBrush = new SolidBrush(Color.Black))
                        using (StringFormat sf = new StringFormat() { Alignment = StringAlignment.Center })
                        {
                            string message = "No valid mask\nSignal too weak";
                            RectangleF rect = new RectangleF(0, 0, image.Width, image.Height);
                            g.DrawString(message, font, shadowBrush, rect, sf);
                        }
                    }
                    masks.Add(fallback);
                }

                Log($"[GenerateMasksFromImageSam2] Completed processing {sourceType} image, created {masks.Count} masks");
                return masks;
            }
            catch (Exception ex)
            {
                Log($"[GenerateMasksFromImageSam2] Critical error: {ex.Message}");
                Log($"[GenerateMasksFromImageSam2] Stack trace: {ex.StackTrace}");

                // Clean up any generated masks
                foreach (var mask in masks)
                {
                    try { mask.Dispose(); } catch { }
                }
                masks.Clear();

                // Create a simple fallback mask to avoid crashing
                try
                {
                    Bitmap errorMask = new Bitmap(image.Width, image.Height);
                    using (Graphics g = Graphics.FromImage(errorMask))
                    {
                        g.Clear(Color.Black);
                        using (Font font = new Font("Arial", 10))
                        using (Brush brush = new SolidBrush(Color.Red))
                        {
                            g.DrawString("Error: " + ex.Message, font, brush, 10, 10);
                        }
                    }
                    masks.Add(errorMask);
                }
                catch
                {
                    // Last resort - create a plain black mask
                    masks.Add(new Bitmap(image.Width, image.Height));
                }

                return masks;
            }
        }



        private Bitmap CreateSafeMask(Tensor<byte> maskTensor, int maskIndex, byte threshold, int width, int height)
        {
            try
            {
                // Check tensor validity
                if (maskTensor == null || maskIndex >= maskTensor.Dimensions[1])
                {
                    Log($"[CreateSafeMask] Invalid tensor or index: {maskIndex}");
                    return null;
                }

                // CRITICAL FIX: Check if any pixels are above threshold 
                // before creating the bitmap
                bool hasAnyPixelsAboveThreshold = false;
                for (int y = 0; y < maskTensor.Dimensions[2] && !hasAnyPixelsAboveThreshold; y++)
                {
                    for (int x = 0; x < maskTensor.Dimensions[3] && !hasAnyPixelsAboveThreshold; x++)
                    {
                        if (maskTensor[0, maskIndex, y, x] >= threshold)
                        {
                            hasAnyPixelsAboveThreshold = true;
                            break;
                        }
                    }
                }

                // Print some diagnostic values
                if (!hasAnyPixelsAboveThreshold)
                {
                    // Sample some values to see what we're getting
                    Log($"[CreateSafeMask] No pixels above threshold {threshold}. Sample values:");
                    for (int y = 0; y < maskTensor.Dimensions[2]; y += 20)
                    {
                        for (int x = 0; x < maskTensor.Dimensions[3]; x += 20)
                        {
                            byte val = maskTensor[0, maskIndex, y, x];
                            if (val > 0)
                                Log($"  Value at ({x},{y}): {val}");
                        }
                    }

                    // CRITICAL FIX: Create a mask anyway with zeros for diagnostic
                    // This prevents "Parameter is not valid" errors
                    Bitmap emptyMask = new Bitmap(width, height);
                    using (Graphics g = Graphics.FromImage(emptyMask))
                    {
                        g.Clear(Color.Black);
                    }
                    return emptyMask;
                }

                // Create a standard 24bpp bitmap (more compatible)
                Bitmap mask = new Bitmap(width, height, PixelFormat.Format24bppRgb);

                // Count white pixels
                int whitePixels = 0;

                // Fill the bitmap
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        // Get the mask value (with bounds checking)
                        byte value = 0;
                        if (y < maskTensor.Dimensions[2] && x < maskTensor.Dimensions[3])
                        {
                            value = maskTensor[0, maskIndex, y, x];
                        }

                        // Apply threshold (extremely permissive)
                        Color pixelColor = (value >= threshold) ? Color.White : Color.Black;
                        mask.SetPixel(x, y, pixelColor);

                        // Count white pixels
                        if (pixelColor.R > 128) whitePixels++;
                    }
                }

                // Log coverage info
                float coverage = (float)whitePixels / (width * height);
                Log($"[CreateSafeMask] Created mask with coverage: {coverage:P4}");

                return mask;
            }
            catch (Exception ex)
            {
                Log($"[CreateSafeMask] Error: {ex.Message}");
                // Return a valid blank bitmap on error
                Bitmap fallback = new Bitmap(width, height);
                using (Graphics g = Graphics.FromImage(fallback))
                {
                    g.Clear(Color.Black);
                }
                return fallback;
            }
        }
        private int AdjustPixelValue(int value, int min, int max)
        {
            if (max <= min) return value;
            return Math.Max(0, Math.Min(255, (int)(255.0 * (value - min) / (max - min))));
        }


        // SAM 2.1 specific mask extraction
        private Bitmap CreateMask(Tensor<byte> maskTensor, int channel, byte threshold,
                           int width, int height, bool edgeEnhanced = false)
        {
            try
            {
                // Verify tensor dimensions
                if (channel >= maskTensor.Dimensions[1] ||
                    height > maskTensor.Dimensions[2] ||
                    width > maskTensor.Dimensions[3])
                {
                    Logger.Log($"[CreateMask] Channel or dimensions out of bounds - tensor shape: " +
                               $"{maskTensor.Dimensions[0]}x{maskTensor.Dimensions[1]}x{maskTensor.Dimensions[2]}x{maskTensor.Dimensions[3]}, " +
                               $"requested: channel={channel}, width={width}, height={height}");
                    return null;
                }

                // Create a 24bpp bitmap (less memory than 32bpp)
                Bitmap mask = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                int whitePixels = 0;
                int totalPixels = width * height;

                // Lock the bitmap for faster access
                BitmapData bmpData = mask.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format24bppRgb);

                try
                {
                    unsafe
                    {
                        byte* ptr = (byte*)bmpData.Scan0;
                        int stride = bmpData.Stride;

                        if (!edgeEnhanced)
                        {
                            // Simple threshold extraction
                            for (int y = 0; y < height; y++)
                            {
                                for (int x = 0; x < width; x++)
                                {
                                    // Bounds check to prevent index errors in tensor
                                    if (y < maskTensor.Dimensions[2] && x < maskTensor.Dimensions[3])
                                    {
                                        byte value = maskTensor[0, channel, y, x];
                                        bool isOn = value >= threshold;

                                        byte pixelValue = isOn ? (byte)255 : (byte)0;
                                        if (isOn) whitePixels++;

                                        int offset = y * stride + x * 3;  // 3 bytes per pixel for 24bpp
                                        ptr[offset] = pixelValue;     // B
                                        ptr[offset + 1] = pixelValue; // G
                                        ptr[offset + 2] = pixelValue; // R
                                    }
                                }
                            }
                        }
                        else
                        {
                            // Edge-enhanced version - first create a grayscale map
                            byte[,] valueMap = new byte[width, height];

                            // Initialize value map with bounds checking
                            for (int y = 0; y < height; y++)
                            {
                                for (int x = 0; x < width; x++)
                                {
                                    if (y < maskTensor.Dimensions[2] && x < maskTensor.Dimensions[3])
                                        valueMap[x, y] = maskTensor[0, channel, y, x];
                                    else
                                        valueMap[x, y] = 0;
                                }
                            }

                            // Calculate edges using Sobel operator with safe boundary handling
                            for (int y = 0; y < height; y++)
                            {
                                for (int x = 0; x < width; x++)
                                {
                                    // Get gradient using Sobel but only for inner pixels to avoid bounds errors
                                    int gx = 0, gy = 0;
                                    byte edgeValue;

                                    if (x > 0 && x < width - 1 && y > 0 && y < height - 1)
                                    {
                                        // Horizontal gradient
                                        gx = valueMap[x + 1, y - 1] + 2 * valueMap[x + 1, y] + valueMap[x + 1, y + 1] -
                                             valueMap[x - 1, y - 1] - 2 * valueMap[x - 1, y] - valueMap[x - 1, y + 1];

                                        // Vertical gradient
                                        gy = valueMap[x - 1, y + 1] + 2 * valueMap[x, y + 1] + valueMap[x + 1, y + 1] -
                                             valueMap[x - 1, y - 1] - 2 * valueMap[x, y - 1] - valueMap[x + 1, y - 1];

                                        float gradient = (float)Math.Sqrt(gx * gx + gy * gy);

                                        // Reduce value at edges to separate structures
                                        edgeValue = (byte)Math.Max(0, Math.Min(255, valueMap[x, y] *
                                                   (1.0f - Math.Min(1.0f, gradient / 128.0f))));
                                    }
                                    else
                                    {
                                        // For border pixels, just use the original value
                                        edgeValue = valueMap[x, y];
                                    }

                                    bool isOn = edgeValue >= threshold;
                                    byte pixelValue = isOn ? (byte)255 : (byte)0;
                                    if (isOn) whitePixels++;

                                    int offset = y * stride + x * 3;  // 3 bytes per pixel for 24bpp
                                    ptr[offset] = pixelValue;     // B
                                    ptr[offset + 1] = pixelValue; // G
                                    ptr[offset + 2] = pixelValue; // R
                                }
                            }
                        }
                    }
                }
                finally
                {
                    // Always unlock the bitmap even if an exception occurs
                    mask.UnlockBits(bmpData);
                }

                // Check if the mask is reasonable (not too sparse or too dense)
                float coverage = (float)whitePixels / totalPixels;
                if (coverage < 0.001f || coverage > 0.99f)
                {
                    mask.Dispose();
                    return null;
                }

                return mask;
            }
            catch (Exception ex)
            {
                Logger.Log($"[CreateMask] Error: {ex.Message}");
                Logger.Log($"[CreateMask] Stack trace: {ex.StackTrace}");
                return null;
            }
        }

        private Dictionary<Bitmap, float> CalculateMaskDiversity(List<Bitmap> masks)
        {
            Dictionary<Bitmap, float> coverageValues = new Dictionary<Bitmap, float>();

            foreach (var mask in masks)
            {
                float coverage = CalculateCoverage(mask);
                coverageValues[mask] = coverage;
            }

            return coverageValues;
        }

        private List<Bitmap> SelectDiverseMasks(List<Bitmap> masks, Dictionary<Bitmap, float> coverageValues)
        {
            var selectedMasks = new List<Bitmap>();

            // If we have 4 or fewer masks, keep all of them
            if (masks.Count <= 4)
                return new List<Bitmap>(masks);

            // Sort masks by coverage
            var sortedMasks = coverageValues.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();

            // IMPORTANT: First, select the mask with smallest non-zero coverage for small pores
            var smallestMask = sortedMasks.FirstOrDefault(m => coverageValues[m] > 0);
            if (smallestMask != null)
            {
                selectedMasks.Add(smallestMask);
                Log($"[SelectDiverseMasks] Selected smallest mask: coverage={coverageValues[smallestMask]:P3}");
            }

            // Select largest mask below 75% coverage
            for (int i = sortedMasks.Count - 1; i >= 0; i--)
            {
                if (coverageValues[sortedMasks[i]] < 0.75f && !selectedMasks.Contains(sortedMasks[i]))
                {
                    selectedMasks.Add(sortedMasks[i]);
                    Log($"[SelectDiverseMasks] Selected largest reasonable mask: coverage={coverageValues[sortedMasks[i]]:P3}");
                    break;
                }
            }

            // Select two masks from middle range for diversity
            if (sortedMasks.Count > 2)
            {
                // Middle mask (median coverage)
                int midIndex = sortedMasks.Count / 2;
                if (!selectedMasks.Contains(sortedMasks[midIndex]))
                {
                    selectedMasks.Add(sortedMasks[midIndex]);
                    Log($"[SelectDiverseMasks] Selected median mask: coverage={coverageValues[sortedMasks[midIndex]]:P3}");
                }

                // Quarter-way mask
                int quarterIndex = sortedMasks.Count / 4;
                if (!selectedMasks.Contains(sortedMasks[quarterIndex]))
                {
                    selectedMasks.Add(sortedMasks[quarterIndex]);
                    Log($"[SelectDiverseMasks] Selected quarter mask: coverage={coverageValues[sortedMasks[quarterIndex]]:P3}");
                }
            }

            // Fill remaining slots
            for (int i = 0; i < sortedMasks.Count && selectedMasks.Count < 4; i++)
            {
                if (!selectedMasks.Contains(sortedMasks[i]))
                {
                    selectedMasks.Add(sortedMasks[i]);
                    Log($"[SelectDiverseMasks] Added additional mask: coverage={coverageValues[sortedMasks[i]]:P3}");
                }
            }

            Log($"[SelectDiverseMasks] Selected {selectedMasks.Count} diverse masks from {masks.Count} candidates");
            return selectedMasks;
        }


        private float CalculateCoverage(Bitmap mask)
        {
            int whitePixels = 0;
            int totalPixels = mask.Width * mask.Height;

            // For large images, sample for speed
            int sampleStep = (mask.Width > 500 || mask.Height > 500) ? 4 : 1;
            int sampledPixels = 0;

            // Lock bits for faster access
            BitmapData bmpData = mask.LockBits(
                new Rectangle(0, 0, mask.Width, mask.Height),
                ImageLockMode.ReadOnly,
                mask.PixelFormat);

            try
            {
                unsafe
                {
                    byte* ptr = (byte*)bmpData.Scan0;
                    int stride = bmpData.Stride;
                    bool is8bpp = mask.PixelFormat == PixelFormat.Format8bppIndexed;
                    int bytesPerPixel = is8bpp ? 1 : (mask.PixelFormat == PixelFormat.Format24bppRgb ? 3 : 4);

                    for (int y = 0; y < mask.Height; y += sampleStep)
                    {
                        for (int x = 0; x < mask.Width; x += sampleStep)
                        {
                            int offset = y * stride + x * bytesPerPixel;
                            byte pixelValue = is8bpp ? ptr[offset] : ptr[offset + 2]; // Get R component for RGB formats

                            if (pixelValue > 128)
                                whitePixels++;

                            sampledPixels++;
                        }
                    }
                }
            }
            finally
            {
                mask.UnlockBits(bmpData);
            }

            // Calculate coverage adjusted for sampling
            return sampledPixels > 0 ? (float)whitePixels / sampledPixels : 0;
        }


        private Bitmap EnhanceMicroCTImage(Bitmap original)
        {
            Bitmap enhanced = new Bitmap(original.Width, original.Height);

            try
            {
                // Calculate image statistics
                int[] histogram = new int[256];
                double totalPixels = 0;

                // Sample the image
                for (int y = 0; y < original.Height; y += 4)
                {
                    for (int x = 0; x < original.Width; x += 4)
                    {
                        Color c = original.GetPixel(x, y);
                        int intensity = (int)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);
                        histogram[intensity]++;
                        totalPixels++;
                    }
                }

                // Find percentiles for contrast stretching
                int lowerBound = 0;
                double sumLower = 0;
                while (sumLower < totalPixels * 0.05 && lowerBound < 255)
                {
                    sumLower += histogram[lowerBound];
                    lowerBound++;
                }

                int upperBound = 255;
                double sumUpper = 0;
                while (sumUpper < totalPixels * 0.05 && upperBound > 0)
                {
                    sumUpper += histogram[upperBound];
                    upperBound--;
                }

                // Apply adaptive contrast enhancement
                for (int y = 0; y < original.Height; y++)
                {
                    for (int x = 0; x < original.Width; x++)
                    {
                        Color c = original.GetPixel(x, y);
                        int intensity = (int)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);

                        // Apply contrast stretching
                        int newIntensity;
                        if (intensity <= lowerBound)
                            newIntensity = 0;
                        else if (intensity >= upperBound)
                            newIntensity = 255;
                        else
                            newIntensity = (int)(255.0 * (intensity - lowerBound) / (upperBound - lowerBound));

                        // Apply mild non-linear transformation
                        double nonLinear = Math.Pow(newIntensity / 255.0, 1.2) * 255.0;
                        nonLinear = Math.Max(0, Math.Min(255, nonLinear));

                        enhanced.SetPixel(x, y, Color.FromArgb(255, (int)nonLinear, (int)nonLinear, (int)nonLinear));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[EnhanceMicroCTImage] Error: {ex.Message}");
                enhanced.Dispose();
                return new Bitmap(original);
            }

            return enhanced;
        }

        private List<Bitmap> GenerateMasksFromImage(Bitmap image, List<AnnotationPoint> promptPoints, string sourceType)
        {
            List<Bitmap> masks = new List<Bitmap>();

            try
            {
                // Convert image to tensor for SAM 2.1
                float[] imageTensorData = BitmapToFloatTensor(image, _imageSize, _imageSize);
                var imageInput = new DenseTensor<float>(imageTensorData, new[] { 1, 3, _imageSize, _imageSize });

                // Run the encoder
                Tensor<float> imageEmbeddings;
                Tensor<float> highResFeatures0;
                Tensor<float> highResFeatures1;

                using (var encoderOutputs = _encoderSession.Run(
                    new[] { NamedOnnxValue.CreateFromTensor("image", imageInput) }))
                {
                    imageEmbeddings = GetFirstTensor<float>(encoderOutputs, "image_embeddings");
                    highResFeatures0 = GetFirstTensor<float>(encoderOutputs, "high_res_feats_0");
                    highResFeatures1 = GetFirstTensor<float>(encoderOutputs, "high_res_feats_1");
                }

                // Prepare point prompts for SAM 2.1
                int pointCount = promptPoints.Count;
                List<AnnotationPoint> positivePoints = new List<AnnotationPoint>();
                List<AnnotationPoint> negativePoints = new List<AnnotationPoint>();

                // Separate points into positive and negative
                foreach (var point in promptPoints)
                {
                    bool isNegative = point.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase);
                    if (isNegative)
                        negativePoints.Add(point);
                    else
                        positivePoints.Add(point);
                }

                // Prepare point inputs and labels for SAM 2.1
                float[] pointInputs = new float[pointCount * 2];
                float[] pointLabels = new float[pointCount];

                // Process all points
                for (int i = 0; i < pointCount; i++)
                {
                    var point = promptPoints[i];

                    // Scale coordinates to model input size
                    float xClamped = Math.Max(0, Math.Min(point.X, image.Width - 1));
                    float yClamped = Math.Max(0, Math.Min(point.Y, image.Height - 1));
                    float xScale = (_imageSize - 1f) / Math.Max(1, image.Width - 1f);
                    float yScale = (_imageSize - 1f) / Math.Max(1, image.Height - 1f);

                    pointInputs[i * 2] = xClamped * xScale;
                    pointInputs[i * 2 + 1] = yClamped * yScale;

                    // Set label (1.0 for positive, 0.0 for negative)
                    pointLabels[i] = point.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase) ? 0.0f : 1.0f;
                }

                // Format tensors for SAM 2.1
                var pointInputsTensor = new DenseTensor<float>(pointInputs, new[] { 1, pointCount, 2 });
                var pointLabelsTensor = new DenseTensor<float>(pointLabels, new[] { 1, pointCount });

                // Original image size tensor
                var origSizeTensor = new DenseTensor<int>(new[] { image.Height, image.Width }, new[] { 2 });

                // Run the decoder
                Tensor<byte> masksTensor;
                Tensor<float> iousTensor;

                using (var decoderOutputs = _decoderSession.Run(new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("image_embeddings", imageEmbeddings),
                    NamedOnnxValue.CreateFromTensor("high_res_feats_0", highResFeatures0),
                    NamedOnnxValue.CreateFromTensor("high_res_feats_1", highResFeatures1),
                    NamedOnnxValue.CreateFromTensor("original_image_size", origSizeTensor),
                    NamedOnnxValue.CreateFromTensor("point_inputs", pointInputsTensor),
                    NamedOnnxValue.CreateFromTensor("point_labels", pointLabelsTensor)
                }))
                {
                    masksTensor = GetFirstTensor<byte>(decoderOutputs, "masks");
                    iousTensor = GetFirstTensor<float>(decoderOutputs, "ious");
                }

                // Process all output masks
                int maskCount = masksTensor.Dimensions[1]; // Number of masks
                int maskHeight = masksTensor.Dimensions[2];
                int maskWidth = masksTensor.Dimensions[3];

                // For different thresholds to generate diverse masks
                float[] thresholds = new float[] { 0.35f, 0.5f, 0.65f };

                for (int maskIdx = 0; maskIdx < maskCount; maskIdx++)
                {
                    foreach (float threshold in thresholds)
                    {
                        // Create binary mask with the current threshold
                        byte thresholdValue = (byte)(threshold * 255);

                        // Extract standard mask
                        Bitmap normalMask = ExtractMask(masksTensor, maskIdx, thresholdValue, maskWidth, maskHeight, false);
                        if (normalMask != null)
                        {
                            // Scale to original size
                            Bitmap fullSizeNormal = new Bitmap(image.Width, image.Height);
                            using (Graphics g = Graphics.FromImage(fullSizeNormal))
                            {
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.DrawImage(normalMask, 0, 0, image.Width, image.Height);
                            }

                            masks.Add(fullSizeNormal);
                            normalMask.Dispose();
                        }

                        // Create edge-enhanced mask for better structure separation (only for first mask to save time)
                        if (maskIdx == 0)
                        {
                            Bitmap edgeMask = ExtractMask(masksTensor, maskIdx, thresholdValue, maskWidth, maskHeight, true);
                            if (edgeMask != null)
                            {
                                Bitmap fullSizeEdge = new Bitmap(image.Width, image.Height);
                                using (Graphics g = Graphics.FromImage(fullSizeEdge))
                                {
                                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                    g.DrawImage(edgeMask, 0, 0, image.Width, image.Height);
                                }

                                masks.Add(fullSizeEdge);
                                edgeMask.Dispose();
                            }
                        }
                    }
                }

                Logger.Log($"[GenerateMasksFromImage] Generated {masks.Count} masks from {sourceType} image");
            }
            catch (Exception ex)
            {
                Logger.Log($"[GenerateMasksFromImage] Error with {sourceType} image: {ex.Message}");
            }

            return masks;
        }

        private Bitmap ExtractMask(Tensor<byte> maskTensor, int channel, byte threshold,
                               int width, int height, bool edgeEnhanced = false, bool inverted = false)
        {
            try
            {
                Bitmap mask = new Bitmap(width, height);
                int whitePixels = 0;

                // Lock bitmap for faster access
                BitmapData bmpData = mask.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);

                unsafe
                {
                    byte* ptr = (byte*)bmpData.Scan0;
                    int stride = bmpData.Stride;

                    if (!edgeEnhanced)
                    {
                        // Simple threshold extraction
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                byte value = maskTensor[0, channel, y, x];
                                bool isOn = inverted ? (value < threshold) : (value >= threshold);

                                byte pixelValue = isOn ? (byte)255 : (byte)0;
                                if (isOn) whitePixels++;

                                int offset = y * stride + x * 4;
                                ptr[offset] = pixelValue;     // B
                                ptr[offset + 1] = pixelValue; // G
                                ptr[offset + 2] = pixelValue; // R
                                ptr[offset + 3] = 255;        // A
                            }
                        }
                    }
                    else
                    {
                        // Edge-enhanced version
                        // First create a grayscale map of the mask
                        byte[,] valueMap = new byte[width, height];
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                valueMap[x, y] = maskTensor[0, channel, y, x];
                            }
                        }

                        // Calculate gradients to detect edges
                        for (int y = 1; y < height - 1; y++)
                        {
                            for (int x = 1; x < width - 1; x++)
                            {
                                // Sobel operator for edge detection
                                int gx = valueMap[x + 1, y - 1] + 2 * valueMap[x + 1, y] + valueMap[x + 1, y + 1] -
                                         valueMap[x - 1, y - 1] - 2 * valueMap[x - 1, y] - valueMap[x - 1, y + 1];

                                int gy = valueMap[x - 1, y + 1] + 2 * valueMap[x, y + 1] + valueMap[x + 1, y + 1] -
                                         valueMap[x - 1, y - 1] - 2 * valueMap[x, y - 1] - valueMap[x + 1, y - 1];

                                float gradient = (float)Math.Sqrt(gx * gx + gy * gy);

                                // Reduce value at edges to separate structures
                                byte edgeValue = (byte)Math.Max(0, Math.Min(255, valueMap[x, y] * (1.0f - Math.Min(1.0f, gradient / 128.0f))));

                                bool isOn = edgeValue >= threshold;
                                byte pixelValue = isOn ? (byte)255 : (byte)0;
                                if (isOn) whitePixels++;

                                int offset = y * stride + x * 4;
                                ptr[offset] = pixelValue;     // B
                                ptr[offset + 1] = pixelValue; // G
                                ptr[offset + 2] = pixelValue; // R
                                ptr[offset + 3] = 255;        // A
                            }
                        }
                    }
                }

                mask.UnlockBits(bmpData);

                // Check if the mask is reasonable
                float coverage = (float)whitePixels / (width * height);
                if (coverage < 0.001f || coverage > 0.99f)
                {
                    mask.Dispose();
                    return null;
                }

                return mask;
            }
            catch (Exception ex)
            {
                Logger.Log($"[ExtractMask] Error: {ex.Message}");
                return null;
            }
        }

        private Bitmap CreateFallbackMask(int width, int height, List<AnnotationPoint> positivePoints, int index)
        {
            Bitmap fallback = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(fallback))
            {
                g.Clear(Color.Black);

                using (Brush brush = new SolidBrush(Color.White))
                {
                    // Different strategies for different fallbacks
                    if (index == 0)
                    {
                        // Simple points
                        foreach (var pt in positivePoints)
                        {
                            g.FillEllipse(brush, pt.X - 15, pt.Y - 15, 30, 30);
                        }
                    }
                    else if (index == 1)
                    {
                        // Centroid with radius
                        float centerX = positivePoints.Average(p => p.X);
                        float centerY = positivePoints.Average(p => p.Y);
                        g.FillEllipse(brush, centerX - 50, centerY - 50, 100, 100);
                    }
                    else
                    {
                        // Points with connecting lines
                        if (positivePoints.Count > 1)
                        {
                            // Draw lines between points
                            using (Pen pen = new Pen(Color.White, 5))
                            {
                                for (int i = 0; i < positivePoints.Count - 1; i++)
                                {
                                    g.DrawLine(pen,
                                        positivePoints[i].X, positivePoints[i].Y,
                                        positivePoints[i + 1].X, positivePoints[i + 1].Y);
                                }

                                // Close the polygon
                                g.DrawLine(pen,
                                    positivePoints[positivePoints.Count - 1].X, positivePoints[positivePoints.Count - 1].Y,
                                    positivePoints[0].X, positivePoints[0].Y);
                            }

                            // Draw points
                            foreach (var pt in positivePoints)
                            {
                                g.FillEllipse(brush, pt.X - 10, pt.Y - 10, 20, 20);
                            }
                        }
                        else
                        {
                            // Just one point with large radius
                            foreach (var pt in positivePoints)
                            {
                                g.FillEllipse(brush, pt.X - 30, pt.Y - 30, 60, 60);
                            }
                        }
                    }
                }
            }

            return fallback;
        }

        /// <summary>
        /// Processes a CT slice in the XZ view using a list of AnnotationPoints.
        /// </summary>
        public Bitmap ProcessXZSlice(
            int sliceIndex,
            Bitmap baseXZ,
            List<AnnotationPoint> promptPoints,
            object p2,
            object p3)
        {
            try
            {
                Logger.Log($"[ProcessXZSlice] slice={sliceIndex}, points={(promptPoints?.Count ?? 0)}");
                if (baseXZ == null)
                    throw new ArgumentNullException(nameof(baseXZ), "XZ bitmap is null");
                if (promptPoints == null || promptPoints.Count == 0)
                    throw new ArgumentException("No prompt points", nameof(promptPoints));

                // The logic is identical to ProcessXYSlice but we just re-use the same code:
                return ProcessXYSlice_Internal(baseXZ, promptPoints);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ProcessXZSlice] Error: {ex.Message}\n{ex.StackTrace}");
                // Return empty black
                Bitmap errMask = new Bitmap(baseXZ.Width, baseXZ.Height);
                using (var g = Graphics.FromImage(errMask)) g.Clear(Color.Black);
                return errMask;
            }
        }

        /// <summary>
        /// Processes a CT slice in the XZ view using a list of Points and bool flag.
        /// </summary>
        public Bitmap ProcessXZSlice(
            int sliceIndex,
            Bitmap baseXZ,
            List<Point> promptPoints,
            object p2,
            object p3,
            bool invertPrompt)
        {
            try
            {
                Logger.Log($"[ProcessXZSlice] Processing {promptPoints?.Count ?? 0} simple Points for slice {sliceIndex}");

                if (promptPoints == null || promptPoints.Count == 0)
                {
                    Logger.Log($"Warning: No prompt points provided for XZ slice {sliceIndex}");
                    throw new ArgumentException("No prompt points provided");
                }

                // Convert Point to AnnotationPoint
                List<AnnotationPoint> ann = new List<AnnotationPoint>();
                foreach (var p in promptPoints)
                {
                    // Validate point coordinates
                    if (p.X < 0 || p.Y < 0 ||
                        (baseXZ != null && (p.X >= baseXZ.Width || p.Y >= baseXZ.Height)))
                    {
                        Logger.Log($"Warning: Skipping out-of-bounds point ({p.X}, {p.Y}) in XZ slice");
                        continue;
                    }

                    ann.Add(new AnnotationPoint
                    {
                        X = p.X,
                        Y = p.Y,
                        Z = sliceIndex,
                        Label = invertPrompt ? "Exterior" : "Foreground"
                    });
                }

                if (ann.Count == 0)
                    throw new ArgumentException("No valid points after filtering");

                return ProcessXYSlice(sliceIndex, baseXZ, ann, p2, p3);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ProcessXZSlice] Error in Points conversion: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Processes a CT slice in the YZ view, multi-channel approach. 
        /// Calls same internal routine as XY to keep code consistent.
        /// </summary>
        public Bitmap ProcessYZSlice(
            int sliceIndex,
            Bitmap baseYZ,
            List<AnnotationPoint> promptPoints,
            object p2,
            object p3)
        {
            try
            {
                Logger.Log($"[ProcessYZSlice] slice={sliceIndex}, points={(promptPoints?.Count ?? 0)}");
                if (baseYZ == null)
                    throw new ArgumentNullException(nameof(baseYZ), "YZ bitmap is null");
                if (promptPoints == null || promptPoints.Count == 0)
                    throw new ArgumentException("No prompt points", nameof(promptPoints));

                // Re-use the same multi-channel logic:
                return ProcessXYSlice_Internal(baseYZ, promptPoints);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ProcessYZSlice] Error: {ex.Message}\n{ex.StackTrace}");
                // Return empty black
                Bitmap errMask = new Bitmap(baseYZ.Width, baseYZ.Height);
                using (var g = Graphics.FromImage(errMask)) g.Clear(Color.Black);
                return errMask;
            }
        }

        /// <summary>
        /// Processes a CT slice in the YZ view using a list of Points and bool flag.
        /// </summary>
        public Bitmap ProcessYZSlice(
            int sliceIndex,
            Bitmap baseYZ,
            List<Point> promptPoints,
            object p2,
            object p3,
            bool invertPrompt)
        {
            try
            {
                Logger.Log($"[ProcessYZSlice] Processing {promptPoints?.Count ?? 0} simple Points for slice {sliceIndex}");

                if (promptPoints == null || promptPoints.Count == 0)
                {
                    Logger.Log($"Warning: No prompt points provided for YZ slice {sliceIndex}");
                    throw new ArgumentException("No prompt points provided");
                }

                if (baseYZ == null)
                {
                    Logger.Log($"Warning: Null bitmap provided for YZ slice {sliceIndex}");
                    throw new ArgumentNullException(nameof(baseYZ), "YZ bitmap cannot be null");
                }

                // For YZ view, carefully validate coordinates
                List<AnnotationPoint> ann = new List<AnnotationPoint>();
                foreach (var p in promptPoints)
                {
                    // Validate coordinates are within bitmap bounds
                    if (p.X < 0 || p.Y < 0 || p.X >= baseYZ.Width || p.Y >= baseYZ.Height)
                    {
                        Logger.Log($"Warning: Skipping out-of-bounds point ({p.X}, {p.Y}) in YZ slice");
                        continue;
                    }

                    ann.Add(new AnnotationPoint
                    {
                        X = p.X,
                        Y = p.Y,
                        Z = sliceIndex,
                        Label = invertPrompt ? "Exterior" : "Foreground"
                    });
                }

                if (ann.Count == 0)
                    throw new ArgumentException("No valid points for YZ slice after filtering");

                return ProcessXYSlice(sliceIndex, baseYZ, ann, p2, p3);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ProcessYZSlice] Error in Points conversion: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Shared internal implementation for processing slices with the SAM 2.1 model
        /// </summary>
        private Bitmap ProcessXYSlice_Internal(Bitmap sliceBmp, List<AnnotationPoint> promptPoints)
        {
            try
            {
                Logger.Log($"[ProcessXYSlice_Internal] #points={(promptPoints?.Count ?? 0)}");

                if (sliceBmp == null)
                    throw new ArgumentNullException(nameof(sliceBmp), "sliceBmp is null");
                if (promptPoints == null || promptPoints.Count == 0)
                    throw new ArgumentException("No prompt points", nameof(promptPoints));
                if (sliceBmp.Width <= 0 || sliceBmp.Height <= 0)
                    throw new ArgumentException("Invalid sliceBmp dimensions");

                // Convert to model's input size
                float[] imageTensorData = BitmapToFloatTensor(sliceBmp, _imageSize, _imageSize);
                var imageInput = new DenseTensor<float>(imageTensorData, new[] { 1, 3, _imageSize, _imageSize });

                // Run the encoder
                Tensor<float> imageEmbeddings;
                Tensor<float> highResFeatures0;
                Tensor<float> highResFeatures1;

                using (var encoderOutputs = _encoderSession.Run(
                    new[] { NamedOnnxValue.CreateFromTensor("image", imageInput) }))
                {
                    imageEmbeddings = GetFirstTensor<float>(encoderOutputs, "image_embeddings");
                    highResFeatures0 = GetFirstTensor<float>(encoderOutputs, "high_res_feats_0");
                    highResFeatures1 = GetFirstTensor<float>(encoderOutputs, "high_res_feats_1");
                }

                // Prepare point prompts for SAM 2.1
                int pointCount = promptPoints.Count;
                float[] pointInputs = new float[pointCount * 2];
                float[] pointLabels = new float[pointCount];

                for (int i = 0; i < pointCount; i++)
                {
                    // Scale coordinates to model input size
                    float xClamped = Math.Max(0, Math.Min(promptPoints[i].X, sliceBmp.Width - 1));
                    float yClamped = Math.Max(0, Math.Min(promptPoints[i].Y, sliceBmp.Height - 1));
                    float xScale = (_imageSize - 1f) / Math.Max(1, sliceBmp.Width - 1f);
                    float yScale = (_imageSize - 1f) / Math.Max(1, sliceBmp.Height - 1f);

                    pointInputs[i * 2] = xClamped * xScale;
                    pointInputs[i * 2 + 1] = yClamped * yScale;

                    // Set label (1.0 for positive, 0.0 for negative)
                    pointLabels[i] = promptPoints[i].Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase) ? 0.0f : 1.0f;
                }

                // Format tensors for SAM 2.1
                var pointInputsTensor = new DenseTensor<float>(pointInputs, new[] { 1, pointCount, 2 });
                var pointLabelsTensor = new DenseTensor<float>(pointLabels, new[] { 1, pointCount });

                // Original image size tensor
                var origSizeTensor = new DenseTensor<int>(new[] { sliceBmp.Height, sliceBmp.Width }, new[] { 2 });

                // Run the decoder
                Tensor<byte> masksTensor;
                Tensor<float> iousTensor;

                using (var decoderOutputs = _decoderSession.Run(new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("image_embeddings", imageEmbeddings),
                    NamedOnnxValue.CreateFromTensor("high_res_feats_0", highResFeatures0),
                    NamedOnnxValue.CreateFromTensor("high_res_feats_1", highResFeatures1),
                    NamedOnnxValue.CreateFromTensor("original_image_size", origSizeTensor),
                    NamedOnnxValue.CreateFromTensor("point_inputs", pointInputsTensor),
                    NamedOnnxValue.CreateFromTensor("point_labels", pointLabelsTensor)
                }))
                {
                    masksTensor = GetFirstTensor<byte>(decoderOutputs, "masks");
                    iousTensor = GetFirstTensor<float>(decoderOutputs, "ious");
                }

                // Find best mask based on IoU
                int maskCount = masksTensor.Dimensions[1];
                int height = masksTensor.Dimensions[2];
                int width = masksTensor.Dimensions[3];

                float bestIoU = -1f;
                int bestMaskIdx = 0;

                for (int i = 0; i < maskCount; i++)
                {
                    float iou = iousTensor[0, i];
                    if (iou > bestIoU)
                    {
                        bestIoU = iou;
                        bestMaskIdx = i;
                    }
                }

                // Create binary mask from best mask
                byte threshold = (byte)(MaskThreshold / 255.0f * 255);
                Bitmap bestMask = new Bitmap(width, height);

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        byte value = masksTensor[0, bestMaskIdx, y, x];
                        bestMask.SetPixel(x, y, value >= threshold ? Color.White : Color.Black);
                    }
                }

                // Optional dilation if coverage < 15%
                float coverage = CalculateCoverage(bestMask);
                if (coverage > 0 && coverage < 15f)
                {
                    Bitmap dilated = Dilate(bestMask, 3);
                    bestMask.Dispose();
                    bestMask = dilated;
                }

                // Resize to original dimensions
                Bitmap finalMask = new Bitmap(sliceBmp.Width, sliceBmp.Height);
                using (Graphics g = Graphics.FromImage(finalMask))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bestMask, new Rectangle(0, 0, sliceBmp.Width, sliceBmp.Height));
                }
                bestMask.Dispose();

                Logger.Log($"[ProcessXYSlice_Internal] Best mask IoU={bestIoU:F3}, coverage={coverage:F1}%");
                return finalMask;
            }
            catch (Exception ex)
            {
                Logger.Log($"[ProcessXYSlice_Internal] Error: {ex.Message}\n{ex.StackTrace}");
                Bitmap errorMask = new Bitmap(sliceBmp.Width, sliceBmp.Height);
                using (Graphics g = Graphics.FromImage(errorMask))
                {
                    g.Clear(Color.Black);
                }
                return errorMask;
            }
        }

        #region Helper Methods
        private void EnsureModelPaths()
        {
            if (usingSam2)
            {
                // First try user-specified paths
                if (string.IsNullOrEmpty(_encoderModelPath) || !File.Exists(_encoderModelPath))
                {
                    // Try some fallback locations
                    string[] possibleLocations = new string[]
                    {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ONNX", "sam2.1_large.encoder.onnx"),
                Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "ONNX", "sam2.1_large.encoder.onnx"),
                Path.Combine(Environment.CurrentDirectory, "ONNX", "sam2.1_large.encoder.onnx")
                    };

                    foreach (string path in possibleLocations)
                    {
                        if (File.Exists(path))
                        {
                            _encoderModelPath = path;
                            Log($"[EnsureModelPaths] Found encoder at alternate location: {path}");
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(_decoderModelPath) || !File.Exists(_decoderModelPath))
                {
                    // Try some fallback locations
                    string[] possibleLocations = new string[]
                    {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ONNX", "sam2.1_large.decoder.onnx"),
                Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "ONNX", "sam2.1_large.decoder.onnx"),
                Path.Combine(Environment.CurrentDirectory, "ONNX", "sam2.1_large.decoder.onnx")
                    };

                    foreach (string path in possibleLocations)
                    {
                        if (File.Exists(path))
                        {
                            _decoderModelPath = path;
                            Log($"[EnsureModelPaths] Found decoder at alternate location: {path}");
                            break;
                        }
                    }
                }

                // Final validation
                if (string.IsNullOrEmpty(_encoderModelPath) || !File.Exists(_encoderModelPath))
                {
                    throw new FileNotFoundException($"Could not find encoder model file. Please check that the ONNX folder contains sam2.1_large.encoder.onnx");
                }

                if (string.IsNullOrEmpty(_decoderModelPath) || !File.Exists(_decoderModelPath))
                {
                    throw new FileNotFoundException($"Could not find decoder model file. Please check that the ONNX folder contains sam2.1_large.decoder.onnx");
                }
            }
        }

        private float[] BitmapToFloatTensor(Bitmap bmp, int targetWidth, int targetHeight)
        {
            // Create a high-quality resized copy first
            Bitmap resized = new Bitmap(targetWidth, targetHeight);
            using (Graphics g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(bmp, 0, 0, targetWidth, targetHeight);
            }

            float[] tensor = new float[1 * 3 * targetHeight * targetWidth];

            // Using standard ImageNet normalization values
            float[] mean = new float[] { 0.485f, 0.456f, 0.406f };
            float[] std = new float[] { 0.229f, 0.224f, 0.225f };

            // Log some image stats to check contrast
            int minGray = 255;
            int maxGray = 0;
            float avgGray = 0;
            int pixelCount = 0;

            for (int y = 0; y < targetHeight; y++)
            {
                for (int x = 0; x < targetWidth; x++)
                {
                    Color pixel = resized.GetPixel(x, y);
                    int gray = (pixel.R + pixel.G + pixel.B) / 3;
                    minGray = Math.Min(minGray, gray);
                    maxGray = Math.Max(maxGray, gray);
                    avgGray += gray;
                    pixelCount++;

                    // Apply normalization as per SAM requirements (RGB)
                    float r = (pixel.R / 255f - mean[0]) / std[0];
                    float g = (pixel.G / 255f - mean[1]) / std[1];
                    float b = (pixel.B / 255f - mean[2]) / std[2];

                    tensor[0 * targetHeight * targetWidth + y * targetWidth + x] = r;
                    tensor[1 * targetHeight * targetWidth + y * targetWidth + x] = g;
                    tensor[2 * targetHeight * targetWidth + y * targetWidth + x] = b;
                }
            }

            avgGray /= pixelCount;
            Log($"[BitmapToFloatTensor] Image stats: Min={minGray}, Max={maxGray}, Avg={avgGray:F1}");

            resized.Dispose();
            return tensor;
        }
        // Add this as a helper method to visualize the raw model output
        private Bitmap CreateDirectVisualization(Tensor<byte> maskTensor, int maskIndex, int width, int height)
        {
            Bitmap visual = new Bitmap(width, height);

            // Find min and max for contrast enhancement
            byte min = 255;
            byte max = 0;

            for (int y = 0; y < maskTensor.Dimensions[2]; y++)
            {
                for (int x = 0; x < maskTensor.Dimensions[3]; x++)
                {
                    byte val = maskTensor[0, maskIndex, y, x];
                    if (val < min) min = val;
                    if (val > max) max = val;
                }
            }

            Log($"[CreateDirectVisualization] Mask {maskIndex} value range: {min}-{max}");

            // If there's no range, use default representation
            if (max - min <= 0)
            {
                using (Graphics g = Graphics.FromImage(visual))
                {
                    g.Clear(Color.DarkGray);
                    string message = "No mask signal";
                    using (Font font = new Font("Arial", 10))
                    using (Brush brush = new SolidBrush(Color.White))
                    {
                        g.DrawString(message, font, brush, 10, 10);
                    }
                }
                return visual;
            }

            // Normalize and display all values
            for (int y = 0; y < Math.Min(height, maskTensor.Dimensions[2]); y++)
            {
                for (int x = 0; x < Math.Min(width, maskTensor.Dimensions[3]); x++)
                {
                    byte val = maskTensor[0, maskIndex, y, x];
                    // Normalize to full range for better visibility
                    byte normVal = (byte)(255 * (val - min) / (max - min));
                    visual.SetPixel(x, y, Color.FromArgb(normVal, normVal, normVal));
                }
            }

            return visual;
        }

        private Bitmap AutoContrastImage(Bitmap input)
        {
            // Create a copy to modify
            Bitmap output = new Bitmap(input.Width, input.Height);

            // First analyze the histogram
            int[] histogram = new int[256];
            for (int y = 0; y < input.Height; y++)
            {
                for (int x = 0; x < input.Width; x++)
                {
                    Color c = input.GetPixel(x, y);
                    int gray = (c.R + c.G + c.B) / 3;
                    histogram[gray]++;
                }
            }

            // Find 5% and 95% percentiles
            int totalPixels = input.Width * input.Height;
            int lowerThreshold = 0;
            int upperThreshold = 255;
            int sum = 0;

            for (int i = 0; i < 256; i++)
            {
                sum += histogram[i];
                if (sum >= totalPixels * 0.05)
                {
                    lowerThreshold = i;
                    break;
                }
            }

            sum = 0;
            for (int i = 255; i >= 0; i--)
            {
                sum += histogram[i];
                if (sum >= totalPixels * 0.05)
                {
                    upperThreshold = i;
                    break;
                }
            }

            // Apply contrast stretching
            for (int y = 0; y < input.Height; y++)
            {
                for (int x = 0; x < input.Width; x++)
                {
                    Color inputColor = input.GetPixel(x, y);
                    int r = inputColor.R;
                    int g = inputColor.G;
                    int b = inputColor.B;

                    // Apply contrast stretching to each channel
                    if (upperThreshold > lowerThreshold)
                    {
                        r = (int)(255.0 * (r - lowerThreshold) / (upperThreshold - lowerThreshold));
                        g = (int)(255.0 * (g - lowerThreshold) / (upperThreshold - lowerThreshold));
                        b = (int)(255.0 * (b - lowerThreshold) / (upperThreshold - lowerThreshold));
                    }

                    // Clamp values
                    r = Math.Max(0, Math.Min(255, r));
                    g = Math.Max(0, Math.Min(255, g));
                    b = Math.Max(0, Math.Min(255, b));

                    output.SetPixel(x, y, Color.FromArgb(r, g, b));
                }
            }

            Log($"[AutoContrastImage] Stretched contrast from ({lowerThreshold}-{upperThreshold}) to (0-255)");
            return output;
        }


        private Bitmap Dilate(Bitmap bmp, int kernelSize)
        {
            Bitmap result = new Bitmap(bmp.Width, bmp.Height);
            int radius = kernelSize / 2;

            // For each pixel in the output image
            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++)
                {
                    bool setWhite = false;

                    // Check the neighborhood
                    for (int ky = -radius; ky <= radius && !setWhite; ky++)
                    {
                        int ny = y + ky;
                        if (ny < 0 || ny >= bmp.Height) continue;

                        for (int kx = -radius; kx <= radius && !setWhite; kx++)
                        {
                            int nx = x + kx;
                            if (nx < 0 || nx >= bmp.Width) continue;

                            // If any neighbor is white, set this pixel to white
                            if (bmp.GetPixel(nx, ny).R > 128)
                            {
                                setWhite = true;
                            }
                        }
                    }

                    result.SetPixel(x, y, setWhite ? Color.White : Color.Black);
                }
            }

            return result;
        }
        private string GetTempFilePath(string prefix, string extension)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "CTSegmenter");

            try
            {
                // Ensure the directory exists
                if (!Directory.Exists(tempDir))
                {
                    Directory.CreateDirectory(tempDir);
                }

                // Generate a timestamped filename
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                string fileName = $"{prefix}_{timestamp}{extension}";
                return Path.Combine(tempDir, fileName);
            }
            catch (Exception ex)
            {
                Log($"[GetTempFilePath] Error creating temp path: {ex.Message}");
                // Fall back to the application directory
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{prefix}_{Guid.NewGuid()}{extension}");
            }
        }

        // Helper for creating raw mask visualizations
        private Bitmap CreateRawMaskVisualization(Tensor<byte> maskTensor, int maskIdx, int targetWidth, int targetHeight)
        {
            int height = maskTensor.Dimensions[2];
            int width = maskTensor.Dimensions[3];

            // Find min and max values for better contrast
            byte minVal = 255;
            byte maxVal = 0;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte val = maskTensor[0, maskIdx, y, x];
                    minVal = Math.Min(minVal, val);
                    maxVal = Math.Max(maxVal, val);
                }
            }

            // Create visualization with normalized values
            Bitmap rawViz = new Bitmap(width, height);

            // If no range, create a special indicator
            if (maxVal <= minVal)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        // Checkerboard pattern to indicate no data
                        bool isEven = ((x / 10) + (y / 10)) % 2 == 0;
                        rawViz.SetPixel(x, y, isEven ? Color.DarkGray : Color.Gray);
                    }
                }
            }
            else
            {
                // Normalize and apply colormap
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        byte val = maskTensor[0, maskIdx, y, x];
                        // Stretch to full range for better visibility
                        int normalizedVal = (maxVal > minVal) ?
                            (int)(255.0 * (val - minVal) / (maxVal - minVal)) : 0;

                        // Use heat map coloring (black -> red -> yellow -> white)
                        Color color;
                        if (normalizedVal < 85)
                        {
                            // Black to red
                            int r = 3 * normalizedVal;
                            color = Color.FromArgb(r, 0, 0);
                        }
                        else if (normalizedVal < 170)
                        {
                            // Red to yellow
                            int g = 3 * (normalizedVal - 85);
                            color = Color.FromArgb(255, g, 0);
                        }
                        else
                        {
                            // Yellow to white
                            int b = 3 * (normalizedVal - 170);
                            color = Color.FromArgb(255, 255, b);
                        }

                        rawViz.SetPixel(x, y, color);
                    }
                }
            }

            // Add annotation text
            using (Graphics g = Graphics.FromImage(rawViz))
            {
                using (Font font = new Font("Arial", 8))
                using (Brush textBrush = new SolidBrush(Color.White))
                using (Brush shadowBrush = new SolidBrush(Color.Black))
                {
                    string info = $"Mask {maskIdx} - Range: {minVal}-{maxVal}";
                    g.DrawString(info, font, shadowBrush, 11, 11);
                    g.DrawString(info, font, textBrush, 10, 10);
                }
            }

            // Resize to target dimensions
            Bitmap resized = new Bitmap(targetWidth, targetHeight);
            using (Graphics g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(rawViz, 0, 0, targetWidth, targetHeight);
            }

            rawViz.Dispose();
            return resized;
        }
        private Tensor<T> GetFirstTensor<T>(IEnumerable<DisposableNamedOnnxValue> outputs, string name)
        {
            var output = outputs.FirstOrDefault(x => x.Name == name);
            if (output == null)
                throw new InvalidOperationException($"Output with name '{name}' not found.");
            return output.AsTensor<T>();
        }

        #endregion
        private void ValidateModelFiles()
        {
            Logger.Log("[ValidateModelFiles] Checking model file paths...");

            if (usingSam2)
            {
                // Sam 2.1 requires encoder and decoder
                if (string.IsNullOrEmpty(_encoderModelPath))
                {
                    Logger.Log("[ValidateModelFiles] ERROR: Encoder model path is null or empty");
                    // Try to automatically determine the path
                    _encoderModelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ONNX", "sam2.1_large.encoder.onnx");
                    Logger.Log($"[ValidateModelFiles] Trying default encoder path: {_encoderModelPath}");
                }

                if (string.IsNullOrEmpty(_decoderModelPath))
                {
                    Logger.Log("[ValidateModelFiles] ERROR: Decoder model path is null or empty");
                    // Try to automatically determine the path
                    _decoderModelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ONNX", "sam2.1_large.decoder.onnx");
                    Logger.Log($"[ValidateModelFiles] Trying default decoder path: {_decoderModelPath}");
                }

                // Now check if files exist
                if (!File.Exists(_encoderModelPath))
                {
                    Logger.Log($"[ValidateModelFiles] CRITICAL ERROR: Encoder model file not found at: {_encoderModelPath}");
                }
                else
                {
                    Logger.Log($"[ValidateModelFiles] Encoder model file exists: {_encoderModelPath}");
                }

                if (!File.Exists(_decoderModelPath))
                {
                    Logger.Log($"[ValidateModelFiles] CRITICAL ERROR: Decoder model file not found at: {_decoderModelPath}");
                }
                else
                {
                    Logger.Log($"[ValidateModelFiles] Decoder model file exists: {_decoderModelPath}");
                }
            }
            else
            {
                // Original SAM models - similar checks for other files
                // Implement as needed for the old SAM model files
                Logger.Log("[ValidateModelFiles] Using original SAM model format - path validation not implemented for this format");
            }
        }



        public void Dispose()
        {
            _usingSam2Model = usingSam2;
            if (_usingSam2Model)
            {
                _encoderSession?.Dispose();
                _decoderSession?.Dispose();

                // DenseTensor doesn't implement IDisposable, so just null the references
                _cachedImageEmbeddings = null;
                _cachedHighResFeatures0 = null;
                _cachedHighResFeatures1 = null;
            }
            else
            {
                _imageEncoderSession?.Dispose();
                _promptEncoderSession?.Dispose();
                _maskDecoderSession?.Dispose();
                _memoryEncoderSession?.Dispose();
                _memoryAttentionSession?.Dispose();
                _mlpSession?.Dispose();
            }
        }
    }


    public class SliceMemory
    {
        public float[] VisionFeatures { get; set; }
        public float[] VisionPosEnc { get; set; }
        public float[] MaskLogits { get; set; }
    }
}