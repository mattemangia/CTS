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


namespace CTSegmenter
{
    public class CTMemorySegmenter : IDisposable
    {
        private readonly int _imageSize;
        private readonly bool _canUseTextPrompts;
        private readonly bool _enableMlp;
        private readonly Dictionary<int, SliceMemory> _sliceMem;

        private InferenceSession _imageEncoderSession;
        private InferenceSession _promptEncoderSession;
        private InferenceSession _maskDecoderSession;
        private InferenceSession _memoryEncoderSession;
        private InferenceSession _memoryAttentionSession;
        private InferenceSession _mlpSession;
        public bool UseSelectiveHoleFilling { get; set; } = false;

        private static void Log(string message) => Logger.Log(message);
        public int MaskThreshold { get; set; } = 128; // Default threshold value
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
        {
            Log("[CTMemorySegmenter] Constructor start");
            _imageSize = imageInputSize;
            _canUseTextPrompts = canUseTextPrompts;
            _enableMlp = enableMlp;
            _sliceMem = new Dictionary<int, SliceMemory>();

            SessionOptions options = new SessionOptions();
            bool useDml = true;
            try
            {
                options.AppendExecutionProvider_DML();
                Log("[CTMemorySegmenter] Using DirectML Execution Provider");
            }
            catch (Exception ex)
            {
                Log("[CTMemorySegmenter] DML not available, falling back to CPU: " + ex.Message);
                useDml = false;
                options = new SessionOptions();
                options.AppendExecutionProvider_CPU();
            }
            try
            {
                _imageEncoderSession = new InferenceSession(imageEncoderPath, options);
                _promptEncoderSession = new InferenceSession(promptEncoderPath, options);
                _maskDecoderSession = new InferenceSession(maskDecoderPath, options);
                _memoryEncoderSession = new InferenceSession(memoryEncoderPath, options);
                _memoryAttentionSession = new InferenceSession(memoryAttentionPath, options);
                _mlpSession = new InferenceSession(mlpPath, options);
                Log("[CTMemorySegmenter] All sessions loaded successfully.");
                Logger.Log($"[CTMemorySegmenter] Model requires input size: {_imageSize}");
            }
            catch (Exception ex)
            {
                Log("[CTMemorySegmenter] Exception during initialization: " + ex.Message);
                if (useDml)
                {
                    Log("[CTMemorySegmenter] Falling back to CPU Execution Provider.");
                    var cpuOptions = new SessionOptions();
                    cpuOptions.AppendExecutionProvider_CPU();
                    _imageEncoderSession = new InferenceSession(imageEncoderPath, cpuOptions);
                    _promptEncoderSession = new InferenceSession(promptEncoderPath, cpuOptions);
                    _maskDecoderSession = new InferenceSession(maskDecoderPath, cpuOptions);
                    _memoryEncoderSession = new InferenceSession(memoryEncoderPath, cpuOptions);
                    _memoryAttentionSession = new InferenceSession(memoryAttentionPath, cpuOptions);
                    _mlpSession = new InferenceSession(mlpPath, cpuOptions);
                    Log("[CTMemorySegmenter] CPU fallback successful.");
                }
                else
                {
                    throw;
                }
            }
            Log("[CTMemorySegmenter] Constructor end");
            LogAllModelMetadata();
        }

        private void LogAllModelMetadata()
        {
            Log("[CTMemorySegmenter] Logging model metadata...");

            Log("Image Encoder:");
            LogSessionMetadata(_imageEncoderSession);

            Log("Prompt Encoder:");
            LogSessionMetadata(_promptEncoderSession);

            Log("Mask Decoder:");
            LogSessionMetadata(_maskDecoderSession);

            Log("Memory Encoder:");
            LogSessionMetadata(_memoryEncoderSession);

            Log("Memory Attention:");
            LogSessionMetadata(_memoryAttentionSession);

            Log("MLP:");
            LogSessionMetadata(_mlpSession);
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
        /// INTERNAL helper that runs the same multi-channel prompt+mask steps
        /// on any 2D Bitmap (whether XY, XZ, or YZ).
        /// We re-use the same code in ProcessXZSlice, ProcessYZSlice, etc.
        /// 
        /// NOTE: Because the logic is the same, you might just call the same pipeline
        /// or factor out XZ-scaling of points if needed. If your XZ or YZ slice points
        /// need different coordinate scaling, handle that before calling here.
        /// </summary>
        /// <summary>
        /// Shared internal method that processes a generic 2D slice (sliceBmp)
        /// with promptPoints. We scale the points to the model input size (_imageSize),
        /// run SAM, gather all channels from the decoder, pick the one with largest coverage.
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

                // 1) Convert slice to model input
                float[] imageTensorData = BitmapToFloatTensor(sliceBmp, _imageSize, _imageSize);
                var imageInput = new DenseTensor<float>(imageTensorData, new[] { 1, 3, _imageSize, _imageSize });

                // 2) Run image encoder
                Tensor<float> visionFeatures;
                Tensor<float> visionPosEnc;
                Tensor<float> highResFeatures1;
                Tensor<float> highResFeatures2;

                using (var imageEncoderOutputs = _imageEncoderSession.Run(
                    new[] { NamedOnnxValue.CreateFromTensor("input_image", imageInput) }))
                {
                    visionFeatures = GetFirstTensor<float>(imageEncoderOutputs, "vision_features");
                    visionPosEnc = GetFirstTensor<float>(imageEncoderOutputs, "vision_pos_enc_2");
                    highResFeatures1 = GetFirstTensor<float>(imageEncoderOutputs, "backbone_fpn_0");
                    highResFeatures2 = GetFirstTensor<float>(imageEncoderOutputs, "backbone_fpn_1");
                }

                // 3) Prepare prompt points (Exterior=0 => negative, else=1 => positive)
                int pointCount = promptPoints.Count;
                float[] coordsArray = new float[pointCount * 2];
                int[] labelArray = new int[pointCount];

                for (int i = 0; i < pointCount; i++)
                {
                    // clamp & scale coords
                    float xClamped = Math.Max(0, Math.Min(promptPoints[i].X, sliceBmp.Width - 1));
                    float yClamped = Math.Max(0, Math.Min(promptPoints[i].Y, sliceBmp.Height - 1));

                    float xScale = (_imageSize - 1f) / Math.Max(1, sliceBmp.Width - 1f);
                    float yScale = (_imageSize - 1f) / Math.Max(1, sliceBmp.Height - 1f);

                    coordsArray[i * 2] = xClamped * xScale;
                    coordsArray[i * 2 + 1] = yClamped * yScale;

                    bool isExt = promptPoints[i].Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase);
                    labelArray[i] = isExt ? 0 : 1;
                }

                var coordsTensor = new DenseTensor<float>(coordsArray, new[] { 1, pointCount, 2 });
                var labelsTensor = new DenseTensor<int>(labelArray, new[] { 1, pointCount });
                // trivial mask input
                var maskInputTensor = new DenseTensor<float>(
                    Enumerable.Repeat(1f, 256 * 256).ToArray(),
                    new[] { 1, 256, 256 });

                // 4) Prompt encoder
                Tensor<float> sparseEmb;
                Tensor<float> denseEmb;

                using (var promptEncoderOutputs = _promptEncoderSession.Run(new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("coords", coordsTensor),
            NamedOnnxValue.CreateFromTensor("labels", labelsTensor),
            NamedOnnxValue.CreateFromTensor("masks", maskInputTensor),
            NamedOnnxValue.CreateFromTensor("masks_enable", new DenseTensor<int>(new[]{0}, new[]{1}))
        }))
                {
                    sparseEmb = GetFirstTensor<float>(promptEncoderOutputs, "sparse_embeddings");
                    denseEmb = GetFirstTensor<float>(promptEncoderOutputs, "dense_embeddings");
                }

                // 5) Mask decoder
                Tensor<float> maskTensor;
                using (var maskDecoderOutputs = _maskDecoderSession.Run(new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("image_embeddings", visionFeatures),
            NamedOnnxValue.CreateFromTensor("image_pe",         visionPosEnc),
            NamedOnnxValue.CreateFromTensor("sparse_prompt_embeddings", sparseEmb),
            NamedOnnxValue.CreateFromTensor("dense_prompt_embeddings",  denseEmb),
            NamedOnnxValue.CreateFromTensor("high_res_features1", highResFeatures1),
            NamedOnnxValue.CreateFromTensor("high_res_features2", highResFeatures2),
        }))
                {
                    maskTensor = GetFirstTensor<float>(maskDecoderOutputs, "masks");
                }

                // shape typically [1, 3, 256, 256]
                int outChannels = maskTensor.Dimensions[1];
                int maskH = maskTensor.Dimensions[2];
                int maskW = maskTensor.Dimensions[3];
                if (outChannels < 1)
                    throw new Exception($"[ProcessXYSlice_Internal] No mask channels found. outChannels={outChannels}");

                float[] maskData = maskTensor.ToArray();

                float bestCoverage = -1f;
                Bitmap bestMask = null;

                // user can define threshold 0..255 => prob cutoff
                float probCutoff = MaskThreshold / 255f; // e.g. 128 => 0.5

                // 6) For each channel, threshold & pick best coverage
                for (int c = 0; c < outChannels; c++)
                {
                    Bitmap rawMask = new Bitmap(maskW, maskH);
                    int whiteCount = 0;
                    int channelOffset = c * maskH * maskW;

                    // (a) Build 256x256 raw mask
                    for (int yy = 0; yy < maskH; yy++)
                    {
                        int rowOffset = channelOffset + yy * maskW;
                        for (int xx = 0; xx < maskW; xx++)
                        {
                            float logit = maskData[rowOffset + xx];
                            float prob = 1f / (1f + (float)Math.Exp(-logit));
                            if (prob >= probCutoff)
                            {
                                rawMask.SetPixel(xx, yy, Color.White);
                                whiteCount++;
                            }
                            else
                            {
                                rawMask.SetPixel(xx, yy, Color.Black);
                            }
                        }
                    }

                    float coveragePercent = whiteCount / (float)(maskH * maskW) * 100f;

                    // (b) Optional dilation if coverage < 15%
                    Bitmap processedMask = rawMask;
                    if (coveragePercent > 0 && coveragePercent < 15f)
                    {
                        Bitmap dilated = Dilate(rawMask, 3);
                        processedMask = dilated;
                        rawMask.Dispose();
                    }

                    // (c) Upscale to sliceBmp size
                    Bitmap finalMask = new Bitmap(sliceBmp.Width, sliceBmp.Height);
                    using (Graphics g = Graphics.FromImage(finalMask))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(processedMask, new Rectangle(0, 0, sliceBmp.Width, sliceBmp.Height));
                    }
                    if (processedMask != rawMask) processedMask.Dispose();

                    // measure coverage in final sized mask
                    int finalWhiteCount = 0;
                    for (int y2 = 0; y2 < finalMask.Height; y2++)
                    {
                        for (int x2 = 0; x2 < finalMask.Width; x2++)
                        {
                            if (finalMask.GetPixel(x2, y2).R > 128)
                                finalWhiteCount++;
                        }
                    }
                    float finalPerc = finalWhiteCount / (float)(sliceBmp.Width * sliceBmp.Height) * 100f;

                    if (finalPerc > bestCoverage)
                    {
                        bestCoverage = finalPerc;
                        if (bestMask != null) bestMask.Dispose();
                        bestMask = finalMask;
                    }
                    else
                    {
                        finalMask.Dispose();
                    }
                }

                Logger.Log($"[ProcessXYSlice_Internal] best coverage={bestCoverage:F1}%");
                return bestMask ?? new Bitmap(sliceBmp.Width, sliceBmp.Height);
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

        /// <summary>
        /// Completely revised implementation that directly addresses the coral segmentation
        /// problem with multiple specialized thresholds and prioritizes quality over speed.
        /// </summary>
        /// <summary>
        /// Optimized version for microCT data that addresses the ring segmentation issue
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
                Logger.Log($"[ProcessXYSlice_GetAllMasks] Starting slice={sliceIndex}, #points={(promptPoints?.Count ?? 0)}");

                // Separate positive and negative points
                var positivePoints = promptPoints.Where(p => !p.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase)).ToList();
                var negativePoints = promptPoints.Where(p => p.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase)).ToList();

                if (positivePoints.Count == 0)
                {
                    Logger.Log("[ProcessXYSlice_GetAllMasks] No positive points found, cannot proceed");
                    return candidateMasks;
                }

                // STEP 1: Preprocess the image to enhance structure visibility
                Bitmap enhancedImage = EnhanceMicroCTImage(sliceBmp);

                // Run with both the original and enhanced images for different results
                List<Bitmap> allMasks = new List<Bitmap>();

                // First run with original image
                allMasks.AddRange(GenerateMasksFromImage(sliceBmp, promptPoints, "Original"));

                // Then run with enhanced image
                allMasks.AddRange(GenerateMasksFromImage(enhancedImage, promptPoints, "Enhanced"));
                enhancedImage.Dispose();

                // STEP 3: Select diverse set of best candidates
                if (allMasks.Count > 0)
                {
                    // Calculate mask diversity (we want different types of segmentations)
                    Dictionary<Bitmap, float> coverageValues = new Dictionary<Bitmap, float>();

                    foreach (var mask in allMasks)
                    {
                        // Calculate coverage percentage (white pixels)
                        int whitePixels = 0;
                        int totalPixels = mask.Width * mask.Height;

                        for (int y = 0; y < mask.Height; y += 4) // Sample every 4th pixel for speed
                        {
                            for (int x = 0; x < mask.Width; x += 4)
                            {
                                if (mask.GetPixel(x, y).R > 128) whitePixels++;
                            }
                        }

                        float coverage = (float)whitePixels / (totalPixels / 16); // Adjusted for sampling
                        coverageValues[mask] = coverage;
                    }

                    // Try to select masks with different coverage percentages
                    var selectedMasks = new List<Bitmap>();

                    // Get the masks sorted by coverage
                    var sortedMasks = coverageValues.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();

                    // 1. Always take the smallest mask for fine details (if it's not too small)
                    if (coverageValues[sortedMasks[0]] > 0.002f)
                    {
                        selectedMasks.Add(sortedMasks[0]);
                    }

                    // 2. Take the largest mask that's not too large
                    for (int i = sortedMasks.Count - 1; i >= 0; i--)
                    {
                        if (coverageValues[sortedMasks[i]] < 0.95f && !selectedMasks.Contains(sortedMasks[i]))
                        {
                            selectedMasks.Add(sortedMasks[i]);
                            break;
                        }
                    }

                    // 3. Take medium-coverage masks to fill in the selection to 4 total
                    int medianIndex = sortedMasks.Count / 2;
                    if (medianIndex < sortedMasks.Count && !selectedMasks.Contains(sortedMasks[medianIndex]))
                    {
                        selectedMasks.Add(sortedMasks[medianIndex]);
                    }

                    // 4. Add one quarter coverage mask if available
                    int quarterIndex = sortedMasks.Count / 4;
                    if (quarterIndex < sortedMasks.Count && !selectedMasks.Contains(sortedMasks[quarterIndex]))
                    {
                        selectedMasks.Add(sortedMasks[quarterIndex]);
                    }

                    // 5. Add three-quarter coverage mask if available
                    int threeQuarterIndex = (sortedMasks.Count * 3) / 4;
                    if (threeQuarterIndex < sortedMasks.Count && !selectedMasks.Contains(sortedMasks[threeQuarterIndex]))
                    {
                        selectedMasks.Add(sortedMasks[threeQuarterIndex]);
                    }

                    // Fill to 4 with any remaining masks
                    for (int i = 0; i < sortedMasks.Count && selectedMasks.Count < 4; i++)
                    {
                        if (!selectedMasks.Contains(sortedMasks[i]))
                        {
                            selectedMasks.Add(sortedMasks[i]);
                        }
                    }

                    // Add selected masks to result
                    foreach (var mask in selectedMasks)
                    {
                        candidateMasks.Add(mask);
                        if (candidateMasks.Count >= 4) break;
                    }

                    // Dispose unselected masks
                    foreach (var mask in allMasks)
                    {
                        if (!candidateMasks.Contains(mask))
                        {
                            mask.Dispose();
                        }
                    }
                }

                // Create fallbacks if needed
                while (candidateMasks.Count < 4)
                {
                    Bitmap fallback = CreateFallbackMask(sliceBmp.Width, sliceBmp.Height, positivePoints, candidateMasks.Count);
                    candidateMasks.Add(fallback);
                }

                return candidateMasks;
            }
            catch (Exception ex)
            {
                Logger.Log($"[ProcessXYSlice_GetAllMasks] Error: {ex.Message}");
                foreach (var mask in candidateMasks) mask?.Dispose();

                // Create simple fallbacks
                List<Bitmap> fallbacks = new List<Bitmap>();
                for (int i = 0; i < 4; i++)
                {
                    Bitmap fallback = new Bitmap(sliceBmp.Width, sliceBmp.Height);
                    using (Graphics g = Graphics.FromImage(fallback))
                    {
                        g.Clear(Color.Black);
                        using (Brush brush = new SolidBrush(Color.White))
                        {
                            int radius = 10 + i * 5;
                            foreach (var pt in promptPoints)
                            {
                                if (!pt.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase))
                                {
                                    g.FillEllipse(brush, pt.X - radius, pt.Y - radius, radius * 2, radius * 2);
                                }
                            }
                        }
                    }
                    fallbacks.Add(fallback);
                }

                return fallbacks;
            }
        }

        /// <summary>
        /// Enhances microCT image to make structures more visible for segmentation
        /// </summary>
        private Bitmap EnhanceMicroCTImage(Bitmap original)
        {
            Bitmap enhanced = new Bitmap(original.Width, original.Height);

            try
            {
                // STEP 1: Calculate image statistics for adaptive processing
                int[] histogram = new int[256];
                double totalPixels = 0;

                // Sample the image (every 4th pixel for speed)
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

                // STEP 2: Apply adaptive contrast enhancement with edge preservation
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

                        // Apply mild non-linear transformation to enhance separation
                        double nonLinear = Math.Pow(newIntensity / 255.0, 1.2) * 255.0;
                        nonLinear = Math.Max(0, Math.Min(255, nonLinear));

                        // Set new color
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

        /// <summary>
        /// Generate a diverse set of masks from the given image
        /// </summary>
        private List<Bitmap> GenerateMasksFromImage(Bitmap image, List<AnnotationPoint> promptPoints, string sourceType)
        {
            List<Bitmap> masks = new List<Bitmap>();

            try
            {
                // Run the SAM model pipeline
                float[] imageTensorData = BitmapToFloatTensor(image, _imageSize, _imageSize);
                var imageInput = new DenseTensor<float>(imageTensorData, new[] { 1, 3, _imageSize, _imageSize });

                // Run image encoder
                Tensor<float> visionFeatures, posEnc, highRes1, highRes2;
                using (var outputs = _imageEncoderSession.Run(
                    new[] { NamedOnnxValue.CreateFromTensor("input_image", imageInput) }))
                {
                    visionFeatures = GetFirstTensor<float>(outputs, "vision_features");
                    posEnc = GetFirstTensor<float>(outputs, "vision_pos_enc_2");
                    highRes1 = GetFirstTensor<float>(outputs, "backbone_fpn_0");
                    highRes2 = GetFirstTensor<float>(outputs, "backbone_fpn_1");
                }

                // Prepare point prompts
                int pointCount = promptPoints.Count;
                float[] coordsArray = new float[pointCount * 2];
                int[] labelArray = new int[pointCount];

                for (int i = 0; i < pointCount; i++)
                {
                    // Scale coordinates
                    float xScale = (_imageSize - 1f) / Math.Max(1, image.Width - 1f);
                    float yScale = (_imageSize - 1f) / Math.Max(1, image.Height - 1f);
                    float xClamped = Math.Max(0, Math.Min(promptPoints[i].X, image.Width - 1));
                    float yClamped = Math.Max(0, Math.Min(promptPoints[i].Y, image.Height - 1));

                    coordsArray[i * 2] = xClamped * xScale;
                    coordsArray[i * 2 + 1] = yClamped * yScale;

                    // Convert label (IMPORTANT: Exterior=0, anything else=1)
                    labelArray[i] = promptPoints[i].Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                }

                // Create tensors for prompt encoder
                var coordsTensor = new DenseTensor<float>(coordsArray, new[] { 1, pointCount, 2 });
                var labelsTensor = new DenseTensor<int>(labelArray, new[] { 1, pointCount });
                var emptyMaskTensor = new DenseTensor<float>(new float[256 * 256], new[] { 1, 256, 256 });
                var maskEnableTensor = new DenseTensor<int>(new[] { 0 }, new[] { 1 });

                // Run prompt encoder
                Tensor<float> sparseEmb, denseEmb;
                using (var outputs = _promptEncoderSession.Run(new[] {
            NamedOnnxValue.CreateFromTensor("coords", coordsTensor),
            NamedOnnxValue.CreateFromTensor("labels", labelsTensor),
            NamedOnnxValue.CreateFromTensor("masks", emptyMaskTensor),
            NamedOnnxValue.CreateFromTensor("masks_enable", maskEnableTensor)
        }))
                {
                    sparseEmb = GetFirstTensor<float>(outputs, "sparse_embeddings");
                    denseEmb = GetFirstTensor<float>(outputs, "dense_embeddings");
                }

                // Run mask decoder
                Tensor<float> maskTensor;
                using (var outputs = _maskDecoderSession.Run(new[] {
            NamedOnnxValue.CreateFromTensor("image_embeddings", visionFeatures),
            NamedOnnxValue.CreateFromTensor("image_pe", posEnc),
            NamedOnnxValue.CreateFromTensor("sparse_prompt_embeddings", sparseEmb),
            NamedOnnxValue.CreateFromTensor("dense_prompt_embeddings", denseEmb),
            NamedOnnxValue.CreateFromTensor("high_res_features1", highRes1),
            NamedOnnxValue.CreateFromTensor("high_res_features2", highRes2)
        }))
                {
                    maskTensor = GetFirstTensor<float>(outputs, "masks");
                }

                int maskChannels = maskTensor.Dimensions[1]; // Typically 3-4
                int maskHeight = maskTensor.Dimensions[2];   // Typically 256 
                int maskWidth = maskTensor.Dimensions[3];    // Typically 256

                // For speed, use fewer thresholds
                float[] thresholds = new float[] { 0.35f, 0.5f, 0.65f };

                // Process masks from all channels
                for (int c = 0; c < maskChannels; c++)
                {
                    foreach (float threshold in thresholds)
                    {
                        // Create both normal and gradient-enhanced masks
                        Bitmap normalMask = ExtractMask(maskTensor, c, threshold, maskWidth, maskHeight, false);
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

                        // Create a mask with edge enhancement to better separate structures
                        if (c == 0) // Only for first channel to save time
                        {
                            Bitmap edgeMask = ExtractMask(maskTensor, c, threshold, maskWidth, maskHeight, true);
                            if (edgeMask != null)
                            {
                                // Scale to original size
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

                // Try inverted mask for the first channel
                Bitmap invertedMask = ExtractMask(maskTensor, 0, 0.5f, maskWidth, maskHeight, false, true);
                if (invertedMask != null)
                {
                    // Scale to original size
                    Bitmap fullSizeInverted = new Bitmap(image.Width, image.Height);
                    using (Graphics g = Graphics.FromImage(fullSizeInverted))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(invertedMask, 0, 0, image.Width, image.Height);
                    }

                    masks.Add(fullSizeInverted);
                    invertedMask.Dispose();
                }

                Logger.Log($"[GenerateMasksFromImage] Generated {masks.Count} masks from {sourceType} image");
            }
            catch (Exception ex)
            {
                Logger.Log($"[GenerateMasksFromImage] Error with {sourceType} image: {ex.Message}");
            }

            return masks;
        }

        /// <summary>
        /// Extract mask using the specified parameters
        /// </summary>
        private Bitmap ExtractMask(Tensor<float> maskTensor, int channel, float threshold,
                                  int width, int height, bool edgeEnhanced = false, bool inverted = false)
        {
            try
            {
                Bitmap mask = new Bitmap(width, height);
                int whitePixels = 0;

                // Lock the bitmap for faster pixel access
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
                                float logit = maskTensor[0, channel, y, x];
                                float prob = 1.0f / (1.0f + (float)Math.Exp(-logit));

                                bool isOn = inverted ? (prob < threshold) : (prob >= threshold);

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
                        // Edge-enhanced version (helps separate rings)
                        // First create a probability map
                        float[,] probMap = new float[width, height];
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                float logit = maskTensor[0, channel, y, x];
                                probMap[x, y] = 1.0f / (1.0f + (float)Math.Exp(-logit));
                            }
                        }

                        // Calculate gradients to detect edges
                        for (int y = 1; y < height - 1; y++)
                        {
                            for (int x = 1; x < width - 1; x++)
                            {
                                // Sobel operator for edge detection
                                float gx = probMap[x + 1, y - 1] + 2 * probMap[x + 1, y] + probMap[x + 1, y + 1] -
                                           probMap[x - 1, y - 1] - 2 * probMap[x - 1, y] - probMap[x - 1, y + 1];

                                float gy = probMap[x - 1, y + 1] + 2 * probMap[x, y + 1] + probMap[x + 1, y + 1] -
                                           probMap[x - 1, y - 1] - 2 * probMap[x, y - 1] - probMap[x + 1, y - 1];

                                float gradient = (float)Math.Sqrt(gx * gx + gy * gy);

                                // Reduce probability at edges to separate structures
                                float edgeProb = probMap[x, y] * (1.0f - Math.Min(1.0f, gradient * 2.0f));

                                bool isOn = edgeProb >= threshold;

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

        /// <summary>
        /// Creates a basic fallback mask
        /// </summary>
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
        /// Fast implementation of dilate + erode in a single pass
        /// </summary>
        private Bitmap FastDilateErode(Bitmap input, int kernelSize)
        {
            int width = input.Width;
            int height = input.Height;

            // Create temporary mask for dilate result
            Bitmap temp = new Bitmap(width, height);

            // First copy the original to a bool array for speed
            bool[,] original = new bool[width, height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    original[x, y] = input.GetPixel(x, y).R > 128;
                }
            }

            // Dilate
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool setWhite = false;

                    // Check neighborhood
                    for (int dy = -kernelSize; dy <= kernelSize && !setWhite; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= height) continue;

                        for (int dx = -kernelSize; dx <= kernelSize && !setWhite; dx++)
                        {
                            int nx = x + dx;
                            if (nx < 0 || nx >= width) continue;

                            if (original[nx, ny])
                            {
                                setWhite = true;
                            }
                        }
                    }

                    temp.SetPixel(x, y, setWhite ? Color.White : Color.Black);
                }
            }

            // Now copy dilated result to a bool array
            bool[,] dilated = new bool[width, height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    dilated[x, y] = temp.GetPixel(x, y).R > 128;
                }
            }

            // Create result for erode
            Bitmap result = new Bitmap(width, height);

            // Erode
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool setBlack = false;

                    // Check neighborhood
                    for (int dy = -kernelSize; dy <= kernelSize && !setBlack; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= height)
                        {
                            setBlack = true;
                            continue;
                        }

                        for (int dx = -kernelSize; dx <= kernelSize && !setBlack; dx++)
                        {
                            int nx = x + dx;
                            if (nx < 0 || nx >= width)
                            {
                                setBlack = true;
                                continue;
                            }

                            if (!dilated[nx, ny])
                            {
                                setBlack = true;
                            }
                        }
                    }

                    result.SetPixel(x, y, setBlack ? Color.Black : Color.White);
                }
            }

            temp.Dispose();
            return result;
        }
        /// <summary>
        /// Fast minimal post-processing for masks
        /// </summary>
        private Bitmap QuickPostProcess(Bitmap mask)
        {
            // Apply a very minimal morphological closing to connect nearby regions
            // This is just a simple dilate + erode
            try
            {
                Bitmap result = FastDilateErode(mask, 2);
                return result;
            }
            catch
            {
                // On any error, return the original
                return mask;
            }
        }
        /// <summary>
        /// Creates a simple fallback mask when no good masks are found
        /// </summary>
        private Bitmap FastCreateFallbackMask(int width, int height, List<AnnotationPoint> positivePoints)
        {
            Bitmap fallback = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(fallback))
            {
                g.Clear(Color.Black);

                if (positivePoints.Count > 0)
                {
                    // Calculate centroid
                    float centerX = positivePoints.Average(p => p.X);
                    float centerY = positivePoints.Average(p => p.Y);

                    // Draw core area
                    using (Brush brush = new SolidBrush(Color.White))
                    {
                        // Draw at centroid with reasonable size
                        float radius = 60;
                        g.FillEllipse(brush, centerX - radius, centerY - radius, radius * 2, radius * 2);

                        // Also draw at each positive point
                        foreach (var pt in positivePoints)
                        {
                            g.FillEllipse(brush, pt.X - 15, pt.Y - 15, 30, 30);
                        }
                    }
                }
            }

            return fallback;
        }

        /// <summary>
        /// Post-processing specialized for medical/CT image masks
        /// </summary>
        private Bitmap PostProcessForMedicalData(Bitmap mask, List<AnnotationPoint> positivePoints)
        {
            // First apply smoothing to reduce blockiness
            Bitmap smoothed = ApplyAntiAliasedSmoothing(mask);

            // Apply morphological closing to connect nearby regions
            Bitmap closed = ApplyMorphologicalClosing(smoothed, 3);
            smoothed.Dispose();

            // Fill small holes
            Bitmap filled = FillSmallHoles(closed, 20);
            closed.Dispose();

            // Ensure all positive points are covered
            Bitmap withPoints = EnsurePointsCovered(filled, positivePoints, 5);
            filled.Dispose();

            return withPoints;
        }

        /// <summary>
        /// Better smoothing for CT/medical data
        /// </summary>
        private Bitmap ApplyAntiAliasedSmoothing(Bitmap input)
        {
            int width = input.Width;
            int height = input.Height;

            Bitmap result = new Bitmap(width, height);

            // Apply a 3x3 Gaussian-like blur to edges only
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool isEdge = false;
                    bool centerValue = input.GetPixel(x, y).R > 128;

                    // Check if this is an edge pixel by looking at neighbors
                    for (int dy = -1; dy <= 1 && !isEdge; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= height) continue;

                        for (int dx = -1; dx <= 1 && !isEdge; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;

                            int nx = x + dx;
                            if (nx < 0 || nx >= width) continue;

                            bool neighborValue = input.GetPixel(nx, ny).R > 128;
                            if (neighborValue != centerValue)
                            {
                                isEdge = true;
                                break;
                            }
                        }
                    }

                    if (isEdge)
                    {
                        // For edge pixels, apply weighted average
                        int sum = 0;
                        int count = 0;

                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int ny = y + dy;
                            if (ny < 0 || ny >= height) continue;

                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx;
                                if (nx < 0 || nx >= width) continue;

                                int value = input.GetPixel(nx, ny).R;
                                int weight = (dx == 0 && dy == 0) ? 4 : 1;

                                sum += value * weight;
                                count += weight;
                            }
                        }

                        int finalValue = sum / count;
                        result.SetPixel(x, y, Color.FromArgb(finalValue, finalValue, finalValue));
                    }
                    else
                    {
                        // For non-edge pixels, keep original value
                        Color c = input.GetPixel(x, y);
                        result.SetPixel(x, y, c);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Optimized morphological closing
        /// </summary>
        private Bitmap ApplyMorphologicalClosing(Bitmap input, int kernelSize)
        {
            Bitmap dilated = ApplyDilation(input, kernelSize);
            Bitmap result = ApplyErosion(dilated, kernelSize);
            dilated.Dispose();
            return result;
        }

        /// <summary>
        /// Optimized dilation
        /// </summary>
        private Bitmap ApplyDilation(Bitmap input, int kernelSize)
        {
            int width = input.Width;
            int height = input.Height;
            int radius = kernelSize / 2;

            Bitmap result = new Bitmap(width, height);

            // Create lookup for faster processing
            bool[,] binary = new bool[width, height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    binary[x, y] = input.GetPixel(x, y).R > 128;
                }
            }

            // Apply dilation
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool setWhite = false;

                    // Check neighborhood
                    for (int dy = -radius; dy <= radius && !setWhite; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= height) continue;

                        for (int dx = -radius; dx <= radius && !setWhite; dx++)
                        {
                            int nx = x + dx;
                            if (nx < 0 || nx >= width) continue;

                            if (binary[nx, ny])
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

        /// <summary>
        /// Optimized erosion
        /// </summary>
        private Bitmap ApplyErosion(Bitmap input, int kernelSize)
        {
            int width = input.Width;
            int height = input.Height;
            int radius = kernelSize / 2;

            Bitmap result = new Bitmap(width, height);

            // Create lookup for faster processing
            bool[,] binary = new bool[width, height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    binary[x, y] = input.GetPixel(x, y).R > 128;
                }
            }

            // Apply erosion
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool setBlack = false;

                    // Check neighborhood
                    for (int dy = -radius; dy <= radius && !setBlack; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= height)
                        {
                            setBlack = true;
                            continue;
                        }

                        for (int dx = -radius; dx <= radius && !setBlack; dx++)
                        {
                            int nx = x + dx;
                            if (nx < 0 || nx >= width)
                            {
                                setBlack = true;
                                continue;
                            }

                            if (!binary[nx, ny])
                            {
                                setBlack = true;
                            }
                        }
                    }

                    result.SetPixel(x, y, setBlack ? Color.Black : Color.White);
                }
            }

            return result;
        }

        /// <summary>
        /// Fill small holes in binary mask
        /// </summary>
        private Bitmap FillSmallHoles(Bitmap input, int maxHoleSize)
        {
            int width = input.Width;
            int height = input.Height;
            Bitmap result = new Bitmap(input);

            // Create lookup arrays
            bool[,] binary = new bool[width, height];
            bool[,] visited = new bool[width, height];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    binary[x, y] = input.GetPixel(x, y).R > 128;
                }
            }

            // Mark exterior regions (flood fill from edges)
            for (int x = 0; x < width; x++)
            {
                if (!binary[x, 0] && !visited[x, 0])
                    FloodFillBackground(binary, visited, x, 0, width, height);

                if (!binary[x, height - 1] && !visited[x, height - 1])
                    FloodFillBackground(binary, visited, x, height - 1, width, height);
            }

            for (int y = 0; y < height; y++)
            {
                if (!binary[0, y] && !visited[0, y])
                    FloodFillBackground(binary, visited, 0, y, width, height);

                if (!binary[width - 1, y] && !visited[width - 1, y])
                    FloodFillBackground(binary, visited, width - 1, y, width, height);
            }

            // Find interior holes
            for (int y = 1; y < height - 1; y++)
            {
                for (int x = 1; x < width - 1; x++)
                {
                    if (!binary[x, y] && !visited[x, y])
                    {
                        // Detect hole
                        List<Point> hole = new List<Point>();
                        FloodFillWithTracking(binary, visited, x, y, width, height, hole);

                        // Fill small holes
                        if (hole.Count <= maxHoleSize)
                        {
                            foreach (var p in hole)
                            {
                                result.SetPixel(p.X, p.Y, Color.White);
                            }
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Flood fill background regions
        /// </summary>
        private void FloodFillBackground(bool[,] binary, bool[,] visited, int startX, int startY, int width, int height)
        {
            Queue<Point> queue = new Queue<Point>();
            queue.Enqueue(new Point(startX, startY));
            visited[startX, startY] = true;

            // 4-connected neighbors
            int[] dx = { 0, 1, 0, -1 };
            int[] dy = { -1, 0, 1, 0 };

            while (queue.Count > 0)
            {
                Point p = queue.Dequeue();

                for (int i = 0; i < 4; i++)
                {
                    int nx = p.X + dx[i];
                    int ny = p.Y + dy[i];

                    if (nx < 0 || nx >= width || ny < 0 || ny >= height || visited[nx, ny])
                        continue;

                    if (!binary[nx, ny])
                    {
                        visited[nx, ny] = true;
                        queue.Enqueue(new Point(nx, ny));
                    }
                }
            }
        }

        /// <summary>
        /// Flood fill with point tracking
        /// </summary>
        private void FloodFillWithTracking(bool[,] binary, bool[,] visited, int startX, int startY,
                                          int width, int height, List<Point> points)
        {
            Queue<Point> queue = new Queue<Point>();
            queue.Enqueue(new Point(startX, startY));
            visited[startX, startY] = true;
            points.Add(new Point(startX, startY));

            // 4-connected neighbors
            int[] dx = { 0, 1, 0, -1 };
            int[] dy = { -1, 0, 1, 0 };

            while (queue.Count > 0)
            {
                Point p = queue.Dequeue();

                for (int i = 0; i < 4; i++)
                {
                    int nx = p.X + dx[i];
                    int ny = p.Y + dy[i];

                    if (nx < 0 || nx >= width || ny < 0 || ny >= height || visited[nx, ny])
                        continue;

                    if (!binary[nx, ny])
                    {
                        visited[nx, ny] = true;
                        points.Add(new Point(nx, ny));
                        queue.Enqueue(new Point(nx, ny));
                    }
                }
            }
        }

        /// <summary>
        /// Ensure all positive points are included in the mask
        /// </summary>
        private Bitmap EnsurePointsCovered(Bitmap mask, List<AnnotationPoint> positivePoints, int radius)
        {
            Bitmap result = new Bitmap(mask);

            using (Graphics g = Graphics.FromImage(result))
            {
                using (Brush brush = new SolidBrush(Color.White))
                {
                    foreach (var point in positivePoints)
                    {
                        int x = (int)Math.Min(Math.Max(0, point.X), mask.Width - 1);
                        int y = (int)Math.Min(Math.Max(0, point.Y), mask.Height - 1);

                        // Check if point is already covered
                        if (mask.GetPixel(x, y).R <= 128)
                        {
                            // Not covered, add a small circle
                            g.FillEllipse(brush, x - radius, y - radius, radius * 2, radius * 2);
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Create a fallback mask that focuses on the core structure
        /// </summary>
        private Bitmap CreateFallbackCoreMask(int width, int height, List<AnnotationPoint> positivePoints)
        {
            Bitmap mask = new Bitmap(width, height);

            if (positivePoints.Count == 0)
                return mask;

            // Calculate centroid of positive points
            float centerX = 0, centerY = 0;
            foreach (var pt in positivePoints)
            {
                centerX += pt.X;
                centerY += pt.Y;
            }
            centerX /= positivePoints.Count;
            centerY /= positivePoints.Count;

            // Calculate average distance from centroid to points
            float avgDistance = 0;
            foreach (var pt in positivePoints)
            {
                float dx = pt.X - centerX;
                float dy = pt.Y - centerY;
                avgDistance += (float)Math.Sqrt(dx * dx + dy * dy);
            }
            avgDistance /= positivePoints.Count;

            // Add some padding
            float radius = avgDistance * 1.2f;

            using (Graphics g = Graphics.FromImage(mask))
            {
                g.Clear(Color.Black);

                // Draw main circle at centroid
                using (Brush brush = new SolidBrush(Color.White))
                {
                    g.FillEllipse(brush, centerX - radius, centerY - radius, radius * 2, radius * 2);
                }

                // Add circles at each positive point
                using (Brush pointBrush = new SolidBrush(Color.White))
                {
                    foreach (var pt in positivePoints)
                    {
                        g.FillEllipse(pointBrush, pt.X - 10, pt.Y - 10, 20, 20);
                    }
                }
            }

            // Apply morphological closing to create a solid shape
            Bitmap closed = ApplyMorphologicalClosing(mask, 5);
            mask.Dispose();

            return closed;
        }

        /// <summary>
        /// Calculates the percentage of positive points covered by the mask
        /// </summary>
        private float CalculatePositivePointCoverage(Bitmap mask, List<AnnotationPoint> points, int width, int height)
        {
            int totalPositive = 0;
            int coveredPositive = 0;

            // Lock the bitmap for faster access
            BitmapData bmpData = mask.LockBits(
                new Rectangle(0, 0, mask.Width, mask.Height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            int stride = bmpData.Stride;
            IntPtr scan0 = bmpData.Scan0;

            unsafe
            {
                byte* p = (byte*)scan0;

                foreach (var point in points)
                {
                    // Skip negative points
                    if (point.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase))
                        continue;

                    totalPositive++;

                    // Scale point coordinates to mask dimensions
                    int x = (int)Math.Min(Math.Max(0, point.X * mask.Width / width), mask.Width - 1);
                    int y = (int)Math.Min(Math.Max(0, point.Y * mask.Height / height), mask.Height - 1);

                    // Check if point is covered (any channel > 128)
                    int offset = y * stride + x * 4;
                    if (p[offset] > 128 || p[offset + 1] > 128 || p[offset + 2] > 128)
                    {
                        coveredPositive++;
                    }
                }
            }

            mask.UnlockBits(bmpData);

            return totalPositive > 0 ? 100.0f * coveredPositive / totalPositive : 0.0f;
        }

        /// <summary>
        /// Fast bitmap to float tensor conversion for SAM model input
        /// </summary>
        private float[] FastBitmapToFloatTensor(Bitmap bmp, int targetWidth, int targetHeight)
        {
            // Resize the bitmap first
            Bitmap resized = new Bitmap(targetWidth, targetHeight);
            using (Graphics g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(bmp, 0, 0, targetWidth, targetHeight);
            }

            float[] tensor = new float[3 * targetHeight * targetWidth];
            float[] mean = new float[] { 0.485f, 0.456f, 0.406f };
            float[] std = new float[] { 0.229f, 0.224f, 0.225f };

            // Lock the bitmap for faster access
            BitmapData bmpData = resized.LockBits(
                new Rectangle(0, 0, targetWidth, targetHeight),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            int stride = bmpData.Stride;
            IntPtr scan0 = bmpData.Scan0;

            unsafe
            {
                byte* p = (byte*)scan0;

                Parallel.For(0, targetHeight, y =>
                {
                    for (int x = 0; x < targetWidth; x++)
                    {
                        int offset = y * stride + x * 4;

                        // Extract BGR (reverse order from standard bitmap format)
                        byte b = p[offset];
                        byte g = p[offset + 1];
                        byte r = p[offset + 2];

                        // Normalize and store in CHW format
                        tensor[0 * targetHeight * targetWidth + y * targetWidth + x] = (r / 255f - mean[0]) / std[0];
                        tensor[1 * targetHeight * targetWidth + y * targetWidth + x] = (g / 255f - mean[1]) / std[1];
                        tensor[2 * targetHeight * targetWidth + y * targetWidth + x] = (b / 255f - mean[2]) / std[2];
                    }
                });
            }

            resized.UnlockBits(bmpData);
            resized.Dispose();

            return tensor;
        }

        /// <summary>
        /// Parallel fast mask cleanup using multithreading
        /// </summary>
        private Bitmap ParallelFastMaskCleanup(Bitmap inputMask)
        {
            int width = inputMask.Width;
            int height = inputMask.Height;

            // Create result bitmap
            Bitmap result = new Bitmap(width, height);

            // Copy input to result using fast LockBits
            BitmapData inputData = inputMask.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            BitmapData resultData = result.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);

            int inputStride = inputData.Stride;
            int resultStride = resultData.Stride;
            IntPtr inputScan0 = inputData.Scan0;
            IntPtr resultScan0 = resultData.Scan0;

            // First pass: copy pixels
            unsafe
            {
                byte* pInput = (byte*)inputScan0;
                byte* pResult = (byte*)resultScan0;

                Parallel.For(0, height, y =>
                {
                    for (int x = 0; x < width; x++)
                    {
                        int inputOffset = y * inputStride + x * 4;
                        int resultOffset = y * resultStride + x * 4;

                        pResult[resultOffset] = pInput[inputOffset];       // B
                        pResult[resultOffset + 1] = pInput[inputOffset + 1]; // G
                        pResult[resultOffset + 2] = pInput[inputOffset + 2]; // R
                        pResult[resultOffset + 3] = 255;                    // A
                    }
                });
            }

            inputMask.UnlockBits(inputData);
            result.UnlockBits(resultData);

            // Apply a single pass of morphological closing
            Bitmap closed = FastMorphologicalClosing(result, 3);
            result.Dispose();

            // Fill small holes
            Bitmap filledHoles = FastFillHoles(closed, 20);
            closed.Dispose();

            return filledHoles;
        }

        /// <summary>
        /// Fast implementation of morphological closing
        /// </summary>
        private Bitmap FastMorphologicalClosing(Bitmap input, int kernelSize)
        {
            // First dilate then erode
            Bitmap dilated = FastDilate(input, kernelSize);
            Bitmap result = FastErode(dilated, kernelSize);
            dilated.Dispose();
            return result;
        }

        /// <summary>
        /// Fast dilation implementation using LockBits
        /// </summary>
        private Bitmap FastDilate(Bitmap input, int kernelSize)
        {
            int width = input.Width;
            int height = input.Height;
            int radius = kernelSize / 2;

            // Create a binary array for faster neighborhood checking
            bool[,] binary = new bool[width, height];

            // Lock the bitmap for reading
            BitmapData inputData = input.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            int stride = inputData.Stride;
            IntPtr scan0 = inputData.Scan0;

            unsafe
            {
                byte* p = (byte*)scan0;

                Parallel.For(0, height, y =>
                {
                    for (int x = 0; x < width; x++)
                    {
                        int offset = y * stride + x * 4;
                        binary[x, y] = p[offset + 2] > 128; // Check red channel
                    }
                });
            }

            input.UnlockBits(inputData);

            // Create result bitmap
            Bitmap result = new Bitmap(width, height);
            BitmapData resultData = result.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);

            int resultStride = resultData.Stride;
            IntPtr resultScan0 = resultData.Scan0;

            unsafe
            {
                byte* pResult = (byte*)resultScan0;

                Parallel.For(0, height, y =>
                {
                    for (int x = 0; x < width; x++)
                    {
                        bool setWhite = false;

                        // Check neighborhood
                        for (int dy = -radius; dy <= radius && !setWhite; dy++)
                        {
                            int ny = y + dy;
                            if (ny < 0 || ny >= height) continue;

                            for (int dx = -radius; dx <= radius && !setWhite; dx++)
                            {
                                int nx = x + dx;
                                if (nx < 0 || nx >= width) continue;

                                if (binary[nx, ny])
                                {
                                    setWhite = true;
                                }
                            }
                        }

                        int resultOffset = y * resultStride + x * 4;
                        byte value = setWhite ? (byte)255 : (byte)0;

                        pResult[resultOffset] = value;     // B
                        pResult[resultOffset + 1] = value; // G
                        pResult[resultOffset + 2] = value; // R
                        pResult[resultOffset + 3] = 255;   // A
                    }
                });
            }

            result.UnlockBits(resultData);

            return result;
        }

        /// <summary>
        /// Fast erosion implementation using LockBits
        /// </summary>
        private Bitmap FastErode(Bitmap input, int kernelSize)
        {
            int width = input.Width;
            int height = input.Height;
            int radius = kernelSize / 2;

            // Create a binary array for faster neighborhood checking
            bool[,] binary = new bool[width, height];

            // Lock the bitmap for reading
            BitmapData inputData = input.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            int stride = inputData.Stride;
            IntPtr scan0 = inputData.Scan0;

            unsafe
            {
                byte* p = (byte*)scan0;

                Parallel.For(0, height, y =>
                {
                    for (int x = 0; x < width; x++)
                    {
                        int offset = y * stride + x * 4;
                        binary[x, y] = p[offset + 2] > 128; // Check red channel
                    }
                });
            }

            input.UnlockBits(inputData);

            // Create result bitmap
            Bitmap result = new Bitmap(width, height);
            BitmapData resultData = result.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);

            int resultStride = resultData.Stride;
            IntPtr resultScan0 = resultData.Scan0;

            unsafe
            {
                byte* pResult = (byte*)resultScan0;

                Parallel.For(0, height, y =>
                {
                    for (int x = 0; x < width; x++)
                    {
                        bool setBlack = false;

                        // Check neighborhood
                        for (int dy = -radius; dy <= radius && !setBlack; dy++)
                        {
                            int ny = y + dy;
                            if (ny < 0 || ny >= height)
                            {
                                setBlack = true;
                                continue;
                            }

                            for (int dx = -radius; dx <= radius && !setBlack; dx++)
                            {
                                int nx = x + dx;
                                if (nx < 0 || nx >= width)
                                {
                                    setBlack = true;
                                    continue;
                                }

                                if (!binary[nx, ny])
                                {
                                    setBlack = true;
                                }
                            }
                        }

                        int resultOffset = y * resultStride + x * 4;
                        byte value = (!setBlack && binary[x, y]) ? (byte)255 : (byte)0;

                        pResult[resultOffset] = value;     // B
                        pResult[resultOffset + 1] = value; // G
                        pResult[resultOffset + 2] = value; // R
                        pResult[resultOffset + 3] = 255;   // A
                    }
                });
            }

            result.UnlockBits(resultData);

            return result;
        }

        /// <summary>
        /// Fast hole filling using scanline flood fill
        /// </summary>
        private Bitmap FastFillHoles(Bitmap input, int minHoleSize)
        {
            int width = input.Width;
            int height = input.Height;

            // Create result bitmap
            Bitmap result = new Bitmap(input);

            // Create binary mask from input
            bool[,] binary = new bool[width, height];
            bool[,] visited = new bool[width, height];

            using (Bitmap temp = new Bitmap(input))
            {
                BitmapData tempData = temp.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb);

                int stride = tempData.Stride;
                IntPtr scan0 = tempData.Scan0;

                unsafe
                {
                    byte* p = (byte*)scan0;

                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int offset = y * stride + x * 4;
                            binary[x, y] = p[offset + 2] > 128; // Use red channel
                        }
                    }
                }

                temp.UnlockBits(tempData);
            }

            // Fill from borders (mark exterior)
            for (int x = 0; x < width; x++)
            {
                if (!binary[x, 0] && !visited[x, 0])
                    FloodFillFast(binary, visited, x, 0, width, height);

                if (!binary[x, height - 1] && !visited[x, height - 1])
                    FloodFillFast(binary, visited, x, height - 1, width, height);
            }

            for (int y = 0; y < height; y++)
            {
                if (!binary[0, y] && !visited[0, y])
                    FloodFillFast(binary, visited, 0, y, width, height);

                if (!binary[width - 1, y] && !visited[width - 1, y])
                    FloodFillFast(binary, visited, width - 1, y, width, height);
            }

            // Process interior holes
            for (int y = 1; y < height - 1; y++)
            {
                for (int x = 1; x < width - 1; x++)
                {
                    if (!binary[x, y] && !visited[x, y])
                    {
                        // Found potential hole
                        List<Point> holePoints = new List<Point>();
                        FloodFillWithList(binary, visited, x, y, width, height, holePoints);

                        // Fill hole if it's small enough
                        if (holePoints.Count <= minHoleSize)
                        {
                            using (Graphics g = Graphics.FromImage(result))
                            {
                                using (Brush brush = new SolidBrush(Color.White))
                                {
                                    foreach (var pt in holePoints)
                                    {
                                        g.FillRectangle(brush, pt.X, pt.Y, 1, 1);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Fast flood fill without storing points
        /// </summary>
        private void FloodFillFast(bool[,] binary, bool[,] visited, int startX, int startY, int width, int height)
        {
            Queue<Point> queue = new Queue<Point>();
            queue.Enqueue(new Point(startX, startY));
            visited[startX, startY] = true;

            int[] dx = { 0, 1, 0, -1 };
            int[] dy = { -1, 0, 1, 0 };

            while (queue.Count > 0)
            {
                Point p = queue.Dequeue();

                for (int i = 0; i < 4; i++)
                {
                    int nx = p.X + dx[i];
                    int ny = p.Y + dy[i];

                    if (nx < 0 || nx >= width || ny < 0 || ny >= height || visited[nx, ny])
                        continue;

                    if (!binary[nx, ny])
                    {
                        visited[nx, ny] = true;
                        queue.Enqueue(new Point(nx, ny));
                    }
                }
            }
        }

        /// <summary>
        /// Flood fill that stores points for later processing
        /// </summary>
        private void FloodFillWithList(bool[,] binary, bool[,] visited, int startX, int startY, int width, int height, List<Point> points)
        {
            Queue<Point> queue = new Queue<Point>();
            queue.Enqueue(new Point(startX, startY));
            visited[startX, startY] = true;
            points.Add(new Point(startX, startY));

            int[] dx = { 0, 1, 0, -1 };
            int[] dy = { -1, 0, 1, 0 };

            while (queue.Count > 0)
            {
                Point p = queue.Dequeue();

                for (int i = 0; i < 4; i++)
                {
                    int nx = p.X + dx[i];
                    int ny = p.Y + dy[i];

                    if (nx < 0 || nx >= width || ny < 0 || ny >= height || visited[nx, ny])
                        continue;

                    if (!binary[nx, ny])
                    {
                        visited[nx, ny] = true;
                        points.Add(new Point(nx, ny));
                        queue.Enqueue(new Point(nx, ny));
                    }
                }
            }
        }

        /// <summary>
        /// Creates a fallback mask when SAM fails
        /// </summary>
        private Bitmap CreateFallbackMask(int width, int height, List<AnnotationPoint> points)
        {
            Bitmap mask = new Bitmap(width, height);

            using (Graphics g = Graphics.FromImage(mask))
            {
                g.Clear(Color.Black);
                using (Brush brush = new SolidBrush(Color.White))
                {
                    // Find positive points
                    var positivePoints = points.Where(p =>
                        !p.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase)).ToList();

                    if (positivePoints.Count > 0)
                    {
                        // Calculate centroid
                        float avgX = positivePoints.Average(p => p.X);
                        float avgY = positivePoints.Average(p => p.Y);

                        // Draw larger circle at centroid
                        g.FillEllipse(brush, avgX - 40, avgY - 40, 80, 80);

                        // Draw circles at each positive point
                        foreach (var pt in positivePoints)
                        {
                            g.FillEllipse(brush, pt.X - 10, pt.Y - 10, 20, 20);
                        }

                        // Connect points to centroid
                        using (Pen pen = new Pen(Color.White, 5))
                        {
                            foreach (var pt in positivePoints)
                            {
                                g.DrawLine(pen, avgX, avgY, pt.X, pt.Y);
                            }
                        }
                    }
                }
            }

            return mask;
        }

        /// <summary>
        /// Creates a fallback mask based on positive points when SAM fails
        /// </summary>
        private Bitmap CreateFallbackFromPoints(int width, int height, List<AnnotationPoint> points)
        {
            Bitmap mask = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(mask))
            {
                g.Clear(Color.Black);
                using (Brush brush = new SolidBrush(Color.White))
                {
                    // Draw circular regions around positive points
                    foreach (var point in points)
                    {
                        if (!point.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase))
                        {
                            float x = Math.Min(Math.Max(0, point.X), width - 1);
                            float y = Math.Min(Math.Max(0, point.Y), height - 1);
                            float radius = 10;
                            g.FillEllipse(brush, x - radius, y - radius, radius * 2, radius * 2);
                        }
                    }
                }
            }

            // Apply dilate+erode to connect nearby points
            Bitmap dilated = Dilate(mask, 5);
            Bitmap result = Dilate(dilated, 5);

            mask.Dispose();
            dilated.Dispose();

            return result;
        }
        /// <summary>
        /// Counts how many positive points are covered by the mask
        /// </summary>
        private int CountCoveredPositivePoints(Bitmap mask, List<AnnotationPoint> points, int width, int height)
        {
            int covered = 0;

            foreach (var point in points)
            {
                // Skip negative points
                if (point.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Check if point is inside mask bounds
                int x = (int)Math.Min(Math.Max(0, point.X), width - 1);
                int y = (int)Math.Min(Math.Max(0, point.Y), height - 1);

                // Check if mask covers this point
                if (mask.GetPixel(x, y).R > 128)
                {
                    covered++;
                }
            }

            return covered;
        }
        /// <summary>
        /// Improved method to build prompt lists that correctly handles negative prompts
        /// from both "Exterior" label and other materials.
        /// </summary>
        private List<AnnotationPoint> BuildMixedPrompts(
            IEnumerable<AnnotationPoint> slicePoints,
            string targetMaterialName)
        {
            Logger.Log($"[BuildMixedPrompts] Building prompts for material: {targetMaterialName}");

            // Create a new list for our processed points
            List<AnnotationPoint> finalList = new List<AnnotationPoint>();

            // Identify all material names present in this slice
            var allMaterials = slicePoints
                .Select(p => p.Label)
                .Where(lbl => !string.IsNullOrEmpty(lbl))
                .Distinct()
                .ToList();

            Logger.Log($"[BuildMixedPrompts] Found {allMaterials.Count} distinct materials in slice");

            // Process each point in the slice
            foreach (var pt in slicePoints)
            {
                if (string.IsNullOrEmpty(pt.Label))
                    continue;

                AnnotationPoint newPoint = new AnnotationPoint
                {
                    ID = pt.ID,
                    X = pt.X,
                    Y = pt.Y,
                    Z = pt.Z,
                    Type = pt.Type
                };

                // Three cases:
                // 1. Point is already marked as "Exterior" - keep as negative
                // 2. Point belongs to targetMaterial - mark as "Foreground" (positive)
                // 3. Point belongs to a different material - mark as "Exterior" (negative)

                if (pt.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase))
                {
                    newPoint.Label = "Exterior"; // Keep as negative
                }
                else if (pt.Label.Equals(targetMaterialName, StringComparison.OrdinalIgnoreCase))
                {
                    newPoint.Label = "Foreground"; // Target material (positive)
                }
                else
                {
                    newPoint.Label = "Exterior"; // Other material - treat as negative
                }

                finalList.Add(newPoint);
            }

            // Log counts for debugging
            int positiveCount = finalList.Count(p => p.Label == "Foreground");
            int negativeCount = finalList.Count(p => p.Label == "Exterior");

            Logger.Log($"[BuildMixedPrompts] Generated {finalList.Count} total prompts: " +
                      $"{positiveCount} positive, {negativeCount} negative");

            return finalList;
        }
        /// <summary>
        /// Processes a CT slice in the XY view using a list of AnnotationPoints.
        /// Negative prompts (Exterior) => label=0, positive => label=1 in this pass.
        /// We decode ALL mask channels from SAM, pick whichever channel has the largest coverage,
        /// and return that as a single final Bitmap.
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

                // Convert to model’s input size
                float[] imageTensorData = BitmapToFloatTensor(baseXY, _imageSize, _imageSize);
                // You can store into DenseTensor<float> or Tensor<float>; doesn't matter now.
                var imageInput = new DenseTensor<float>(imageTensorData, new[] { 1, 3, _imageSize, _imageSize });

                // 1) Run image encoder
                Tensor<float> visionFeatures;
                Tensor<float> visionPosEnc;
                Tensor<float> highResFeatures1;
                Tensor<float> highResFeatures2;

                using (var imageEncoderOutputs = _imageEncoderSession.Run(
                    new[] { NamedOnnxValue.CreateFromTensor("input_image", imageInput) }))
                {
                    visionFeatures = GetFirstTensor<float>(imageEncoderOutputs, "vision_features");
                    visionPosEnc = GetFirstTensor<float>(imageEncoderOutputs, "vision_pos_enc_2");
                    highResFeatures1 = GetFirstTensor<float>(imageEncoderOutputs, "backbone_fpn_0");
                    highResFeatures2 = GetFirstTensor<float>(imageEncoderOutputs, "backbone_fpn_1");
                }

                // 2) Prepare point prompts
                int pointCount = promptPoints.Count;
                float[] coordsArray = new float[pointCount * 2];
                int[] labelArray = new int[pointCount];

                for (int i = 0; i < pointCount; i++)
                {
                    // clamp & scale coords
                    float xClamped = Math.Max(0, Math.Min(promptPoints[i].X, baseXY.Width - 1));
                    float yClamped = Math.Max(0, Math.Min(promptPoints[i].Y, baseXY.Height - 1));
                    float xScale = (_imageSize - 1f) / Math.Max(1, baseXY.Width - 1f);
                    float yScale = (_imageSize - 1f) / Math.Max(1, baseXY.Height - 1f);

                    coordsArray[i * 2] = xClamped * xScale;
                    coordsArray[i * 2 + 1] = yClamped * yScale;

                    // label=0 => negative, label=1 => positive
                    bool isExterior = promptPoints[i].Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase);
                    labelArray[i] = isExterior ? 0 : 1;
                }

                var coordsTensor = new DenseTensor<float>(coordsArray, new[] { 1, pointCount, 2 });
                var labelsTensor = new DenseTensor<int>(labelArray, new[] { 1, pointCount });
                // trivial mask input
                var maskInputTensor = new DenseTensor<float>(
                    Enumerable.Repeat(1f, 256 * 256).ToArray(),
                    new[] { 1, 256, 256 });

                // 3) Run prompt encoder
                Tensor<float> sparseEmb;
                Tensor<float> denseEmb;
                using (var promptEncoderOutputs = _promptEncoderSession.Run(new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("coords", coordsTensor),
            NamedOnnxValue.CreateFromTensor("labels", labelsTensor),
            NamedOnnxValue.CreateFromTensor("masks", maskInputTensor),
            NamedOnnxValue.CreateFromTensor("masks_enable", new DenseTensor<int>(new[]{0}, new[]{1}))
        }))
                {
                    sparseEmb = GetFirstTensor<float>(promptEncoderOutputs, "sparse_embeddings");
                    denseEmb = GetFirstTensor<float>(promptEncoderOutputs, "dense_embeddings");
                }

                // 4) Run mask decoder
                Tensor<float> maskTensor;
                using (var maskDecoderOutputs = _maskDecoderSession.Run(new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("image_embeddings", visionFeatures),
            NamedOnnxValue.CreateFromTensor("image_pe",         visionPosEnc),
            NamedOnnxValue.CreateFromTensor("sparse_prompt_embeddings", sparseEmb),
            NamedOnnxValue.CreateFromTensor("dense_prompt_embeddings",  denseEmb),
            NamedOnnxValue.CreateFromTensor("high_res_features1", highResFeatures1),
            NamedOnnxValue.CreateFromTensor("high_res_features2", highResFeatures2),
        }))
                {
                    maskTensor = GetFirstTensor<float>(maskDecoderOutputs, "masks");
                }

                // Typically shape [1, 3, 256, 256]
                int outChannels = maskTensor.Dimensions[1]; // typically 3
                int maskH = maskTensor.Dimensions[2]; // 256
                int maskW = maskTensor.Dimensions[3]; // 256
                if (outChannels < 1)
                    throw new Exception($"[ProcessXYSlice] Unexpected mask channels = {outChannels}");

                // Convert entire maskTensor to float[] so we can index manually
                float[] maskData = maskTensor.ToArray();
                // The layout: [batch=0, channel=c, y, x]
                // offset = (0 * outChannels * maskH * maskW) + (c * maskH * maskW) + (yy * maskW) + xx

                float bestCoverage = -1f;
                Bitmap bestMask = null;

                float probCutoff = MaskThreshold / 255f;  // e.g. 128 => 0.5

                // For each channel
                for (int c = 0; c < outChannels; c++)
                {
                    Bitmap rawMask = new Bitmap(maskW, maskH);
                    int whiteCount = 0;
                    int channelOffset = c * maskH * maskW; // ignoring batch=0

                    // (a) build raw 256x256 mask
                    for (int yy = 0; yy < maskH; yy++)
                    {
                        int rowOffset = channelOffset + yy * maskW;
                        for (int xx = 0; xx < maskW; xx++)
                        {
                            float logit = maskData[rowOffset + xx];
                            float prob = 1f / (1f + (float)Math.Exp(-logit));
                            if (prob >= probCutoff)
                            {
                                rawMask.SetPixel(xx, yy, Color.White);
                                whiteCount++;
                            }
                            else
                            {
                                rawMask.SetPixel(xx, yy, Color.Black);
                            }
                        }
                    }

                    float coveragePercent = whiteCount / (float)(maskW * maskH) * 100f;

                    // (b) Optionally do small dilation if coverage <15%
                    Bitmap processedMask = rawMask;
                    if (coveragePercent > 0 && coveragePercent < 15f)
                    {
                        Bitmap dilated = Dilate(rawMask, 3);
                        processedMask = dilated;
                        rawMask.Dispose();
                    }

                    // (c) Upscale to baseXY size
                    Bitmap finalMask = new Bitmap(baseXY.Width, baseXY.Height);
                    using (Graphics g = Graphics.FromImage(finalMask))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(processedMask, new Rectangle(0, 0, baseXY.Width, baseXY.Height));
                    }
                    if (processedMask != rawMask) processedMask.Dispose();

                    // measure coverage
                    int finalWhiteCount = 0;
                    for (int y2 = 0; y2 < finalMask.Height; y2++)
                    {
                        for (int x2 = 0; x2 < finalMask.Width; x2++)
                        {
                            if (finalMask.GetPixel(x2, y2).R > 128)
                                finalWhiteCount++;
                        }
                    }
                    float finalPerc = finalWhiteCount / (float)(baseXY.Width * baseXY.Height) * 100f;

                    if (finalPerc > bestCoverage)
                    {
                        bestCoverage = finalPerc;
                        if (bestMask != null) bestMask.Dispose();
                        bestMask = finalMask;
                    }
                    else
                    {
                        finalMask.Dispose();
                    }
                }

                Logger.Log($"[ProcessXYSlice] Best channel coverage={bestCoverage:F1}%");
                return bestMask ?? new Bitmap(baseXY.Width, baseXY.Height);
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


        private float Sigmoid(float x)
        {
            return 1.0f / (1.0f + (float)Math.Exp(-x));
        }
        #region Overloads for XZ and YZ

        /// <summary>
        /// Processes a CT slice in the XZ view using a list of AnnotationPoints.
        /// </summary>
        /// <summary>
        /// Processes a CT slice in the XZ view. Same multi-channel approach as XY.
        /// Returns whichever channel has largest coverage after thresholding.
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
        #endregion

        #region Helper Methods

        private float[] BitmapToFloatTensor(Bitmap bmp, int targetWidth, int targetHeight)
        {
            Bitmap resized = new Bitmap(bmp, new Size(targetWidth, targetHeight));
            float[] tensor = new float[1 * 3 * targetHeight * targetWidth];
            float[] mean = new float[] { 0.485f, 0.456f, 0.406f };
            float[] std = new float[] { 0.229f, 0.224f, 0.225f };

            for (int y = 0; y < targetHeight; y++)
            {
                for (int x = 0; x < targetWidth; x++)
                {
                    Color pixel = resized.GetPixel(x, y);
                    // Apply normalization (RGB)
                    float r = (pixel.R / 255f - mean[0]) / std[0];
                    float g = (pixel.G / 255f - mean[1]) / std[1];
                    float b = (pixel.B / 255f - mean[2]) / std[2];
                    tensor[0 * targetHeight * targetWidth + y * targetWidth + x] = r;
                    tensor[1 * targetHeight * targetWidth + y * targetWidth + x] = g;
                    tensor[2 * targetHeight * targetWidth + y * targetWidth + x] = b;
                }
            }
            resized.Dispose();
            return tensor;
        }

        private Bitmap UpsampleMask(float[] maskData, int sourceH, int sourceW, int targetH, int targetW)
        {
            // Create low-res mask
            Bitmap lowRes = new Bitmap(sourceW, sourceH, PixelFormat.Format32bppArgb);
            for (int y = 0; y < sourceH; y++)
            {
                for (int x = 0; x < sourceW; x++)
                {
                    // Apply sigmoid with more contrast
                    float value = Sigmoid(maskData[y * sourceW + x] * 2.0f); // Double the contrast
                    int alpha = (int)(255 * value);
                    lowRes.SetPixel(x, y, Color.FromArgb(alpha, 255, 255, 255));
                }
            }

            // Upscale with bicubic interpolation
            Bitmap highRes = new Bitmap(targetW, targetH);
            using (Graphics g = Graphics.FromImage(highRes))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawImage(lowRes, new Rectangle(0, 0, targetW, targetH));
            }
            lowRes.Dispose();
            return highRes;
        }

        /// <summary>
        /// We raise the threshold to 200 to avoid large over-segmentation
        /// and use a smaller morphological kernel (3) for closing.
        /// </summary>
        /*private Bitmap PostProcessMask(Bitmap mask)
        {
            const int threshold = 128;
            Bitmap binary = new Bitmap(mask.Width, mask.Height, PixelFormat.Format32bppArgb);

            // Apply fixed threshold
            for (int y = 0; y < mask.Height; y++)
            {
                for (int x = 0; x < mask.Width; x++)
                {
                    Color c = mask.GetPixel(x, y);
                    binary.SetPixel(x, y, (c.R > threshold) ? Color.White : Color.Black);
                }
            }

            // Skip hole filling for small materials to preserve details
            Bitmap filled = UseSelectiveHoleFilling ? FillHolesSelective(binary, 50) : binary.Clone() as Bitmap;

            // Use smaller kernel (3x3) for closing
            Bitmap closed = MorphologicalClosing(filled, kernelSize: 3);

            filled.Dispose();
            binary.Dispose();
            mask.Dispose();
            return closed;
        }*/
        /// <summary>
        /// Segments any number of materials on a single 2D slice (XY),
        /// automatically excluding previously segmented areas for each subsequent material.
        /// 
        /// Usage Example:
        ///   var multiResults = SegmentMultipleMaterialsOnSlice(
        ///       sliceIndex,
        ///       sliceBitmap,
        ///       new List<(string MaterialName, List<AnnotationPoint> Positives)>
        ///       {
        ///           ("Material1", mat1PositivePoints),
        ///           ("Material2", mat2PositivePoints),
        ///           ("Material3", mat3PositivePoints),
        ///           ...
        ///       }
        ///   );
        /// Each entry in `multiResults` is the final mask for that material (in the same order).
        /// 
        /// The second pass automatically includes negative prompts from the region of the
        /// first pass, so you won't re-segment the same region. The third pass excludes the
        /// first + second, etc. Indefinite number of materials is supported.
        /// </summary>
        public List<Bitmap> SegmentMultipleMaterialsOnSlice(
            int sliceIndex,
            Bitmap sliceXY,
            List<(string MaterialName, List<AnnotationPoint> PositivePoints)> materials)
        {
            if (sliceXY == null)
                throw new ArgumentNullException(nameof(sliceXY), "Slice bitmap cannot be null");
            if (materials == null || materials.Count == 0)
                throw new ArgumentException("No materials specified");

            Logger.Log($"[SegmentMultipleMaterialsOnSlice] slice={sliceIndex}, #materials={materials.Count}");

            // We'll store the final mask for each material in this list:
            var finalMasks = new List<Bitmap>();

            // We'll keep a union of all previously segmented areas as a black-and-white “accumulated” mask.
            // That union is used to generate negative prompts for each new material pass.
            Bitmap accumulatedMask = new Bitmap(sliceXY.Width, sliceXY.Height);
            using (Graphics g = Graphics.FromImage(accumulatedMask))
            {
                g.Clear(Color.Black);
            }

            foreach (var (materialName, positivePoints) in materials)
            {
                Logger.Log($"[SegmentMultipleMaterialsOnSlice] Now segmenting: {materialName}");

                // 1) Convert user’s positive points for this material into AnnotationPoints labeled "Foreground"
                //    We'll also generate negative points labeled "Exterior" from the ACCUMULATED mask so far.

                // The user-supplied positives:
                var allPrompts = new List<AnnotationPoint>();
                foreach (var pt in positivePoints)
                {
                    // Make sure it is within slice bounds
                    float xClamped = Math.Max(0, Math.Min(pt.X, sliceXY.Width - 1));
                    float yClamped = Math.Max(0, Math.Min(pt.Y, sliceXY.Height - 1));

                    allPrompts.Add(new AnnotationPoint
                    {
                        X = xClamped,
                        Y = yClamped,
                        Z = sliceIndex,
                        Label = "Foreground"  // user wants this region
                    });
                }

                // 2) Add negative points inside the union of previously segmented areas
                //    This ensures we do NOT re-segment them. We'll sample some random
                //    points from the accumulatedMask (where it is white).
                var negativeSamples = SampleNegativePointsFromMask(accumulatedMask, 30);
                // 30 => up to 30 negative points, adjust as needed
                foreach (var (nx, ny) in negativeSamples)
                {
                    allPrompts.Add(new AnnotationPoint
                    {
                        X = nx,
                        Y = ny,
                        Z = sliceIndex,
                        Label = "Exterior"  // force it negative
                    });
                }

                // 3) Now call your normal multi-channel method (the same code as `ProcessXYSlice_Internal`)
                Bitmap matMask = ProcessXYSlice_Internal(sliceXY, allPrompts);

                finalMasks.Add(matMask);

                // 4) Merge this material’s new mask into the ACCUMULATED mask
                //    so that future materials exclude it
                using (Graphics gAcc = Graphics.FromImage(accumulatedMask))
                {
                    // We can do a per-pixel combine, or simpler:
                    //   - for each white pixel in matMask => set white in accumulatedMask
                    for (int y = 0; y < matMask.Height; y++)
                    {
                        for (int x = 0; x < matMask.Width; x++)
                        {
                            if (matMask.GetPixel(x, y).R > 128)
                            {
                                accumulatedMask.SetPixel(x, y, Color.White);
                            }
                        }
                    }
                }

                Logger.Log($"[SegmentMultipleMaterialsOnSlice] Done with material: {materialName}");
            }

            return finalMasks;
        }
        /// <summary>
        /// Randomly selects up to 'count' coordinates inside 'mask' where pixel is White,
        /// returning them as (x,y) pairs. We label them "Exterior" to exclude previously
        /// segmented areas in the next pass.
        /// </summary>
        private List<(int X, int Y)> SampleNegativePointsFromMask(Bitmap mask, int count)
        {
            // We'll gather all white coords, then pick random up to 'count' of them
            var whiteCoords = new List<(int, int)>();
            for (int y = 0; y < mask.Height; y++)
            {
                for (int x = 0; x < mask.Width; x++)
                {
                    if (mask.GetPixel(x, y).R > 128)
                    {
                        whiteCoords.Add((x, y));
                    }
                }
            }

            var rnd = new Random();
            // If less than 'count' white coords, we just sample all
            // otherwise pick random subset
            if (whiteCoords.Count <= count)
                return whiteCoords;

            // shuffle
            for (int i = whiteCoords.Count - 1; i > 0; i--)
            {
                int j = rnd.Next(i + 1);
                (whiteCoords[i], whiteCoords[j]) = (whiteCoords[j], whiteCoords[i]);
            }
            // take the first 'count'
            return whiteCoords.Take(count).ToList();
        }

        private Bitmap PostProcessMask(Bitmap mask)
        {
            // Use a customizable threshold instead of hardcoded value
            Bitmap binary = new Bitmap(mask.Width, mask.Height, PixelFormat.Format32bppArgb);

            // Apply threshold from property
            for (int y = 0; y < mask.Height; y++)
            {
                for (int x = 0; x < mask.Width; x++)
                {
                    Color c = mask.GetPixel(x, y);
                    binary.SetPixel(x, y, (c.R > MaskThreshold) ? Color.White : Color.Black);
                }
            }

            Logger.Log($"[PostProcessMask] Applied threshold {MaskThreshold}");

            // Skip hole filling which can cause over-segmentation
            mask.Dispose();
            return binary;
        }
        /// <summary>
        /// Enhanced edge smoothing for masks
        /// </summary>
        private Bitmap ApplyEdgeSmoothing(Bitmap mask)
        {
            try
            {
                if (mask == null)
                    return null;

                int width = mask.Width;
                int height = mask.Height;

                // Create output bitmap
                Bitmap smoothed = new Bitmap(width, height);

                // Find edge pixels (those with different-valued neighbors)
                List<Point> edgePixels = new List<Point>();
                for (int y = 1; y < height - 1; y++)
                {
                    for (int x = 1; x < width - 1; x++)
                    {
                        bool isWhite = mask.GetPixel(x, y).R > 128;
                        bool isEdge = false;

                        // Check 4-connected neighbors
                        int[] dx = { 0, 1, 0, -1 };
                        int[] dy = { -1, 0, 1, 0 };

                        for (int i = 0; i < 4; i++)
                        {
                            int nx = x + dx[i], ny = y + dy[i];
                            if (nx >= 0 && nx < width && ny >= 0 && ny < height)
                            {
                                bool neighborIsWhite = mask.GetPixel(nx, ny).R > 128;
                                if (isWhite != neighborIsWhite)
                                {
                                    isEdge = true;
                                    break;
                                }
                            }
                        }

                        if (isEdge)
                        {
                            edgePixels.Add(new Point(x, y));
                        }
                    }
                }

                // Copy original pixels to output
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        smoothed.SetPixel(x, y, mask.GetPixel(x, y));
                    }
                }

                // Apply smoothing only at edges
                foreach (var p in edgePixels)
                {
                    int x = p.X;
                    int y = p.Y;

                    int sum = 0;
                    int count = 0;

                    // 3x3 neighborhood
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx;
                            int ny = y + dy;

                            if (nx >= 0 && nx < width && ny >= 0 && ny < height)
                            {
                                int weight = (dx == 0 && dy == 0) ? 4 : 1;
                                sum += mask.GetPixel(nx, ny).R * weight;
                                count += weight;
                            }
                        }
                    }

                    // Apply interpolated value
                    int avgValue = sum / count;
                    smoothed.SetPixel(x, y, Color.FromArgb(avgValue, avgValue, avgValue));
                }

                return smoothed;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error in ApplyEdgeSmoothing: {ex.Message}");
                return mask;
            }
        }

        /// <summary>
        /// Determines if a mask covers the majority of positive points
        /// Used to identify masks that focus on the central coral structure
        /// </summary>
        private bool IsMaskCoveringPositivePoints(Bitmap mask, List<AnnotationPoint> positivePoints, int imgWidth, int imgHeight)
        {
            if (positivePoints.Count == 0)
                return false;

            int coveredPoints = 0;

            foreach (var pt in positivePoints)
            {
                // Scale point to mask coordinates
                int x = (int)Math.Min(Math.Max(0, pt.X), imgWidth - 1);
                int y = (int)Math.Min(Math.Max(0, pt.Y), imgHeight - 1);

                // Check if the point is covered by the mask
                if (mask.GetPixel(x, y).R > 128)
                {
                    coveredPoints++;
                }
            }

            // Consider it a match if more than 60% of positive points are covered
            float coverage = coveredPoints / (float)positivePoints.Count;
            return coverage > 0.6f;
        }

        /// <summary>
        /// Creates a mask based on the location of positive points
        /// This is a fallback segmentation method when SAM fails
        /// </summary>
        private Bitmap CreatePointBasedMask(int width, int height, List<AnnotationPoint> positivePoints, int radius)
        {
            Bitmap mask = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(mask))
            {
                g.Clear(Color.Black);

                using (SolidBrush whiteBrush = new SolidBrush(Color.White))
                {
                    // Find average position of positive points (center of structure)
                    float avgX = 0, avgY = 0;
                    foreach (var pt in positivePoints)
                    {
                        avgX += pt.X;
                        avgY += pt.Y;
                    }

                    if (positivePoints.Count > 0)
                    {
                        avgX /= positivePoints.Count;
                        avgY /= positivePoints.Count;

                        // Draw region around center and each positive point
                        g.FillEllipse(whiteBrush, avgX - radius * 2, avgY - radius * 2, radius * 4, radius * 4);

                        foreach (var pt in positivePoints)
                        {
                            g.FillEllipse(whiteBrush, pt.X - radius, pt.Y - radius, radius * 2, radius * 2);
                        }
                    }
                }
            }

            // Apply morphological closing to connect nearby regions
            Bitmap closed = MorphologicalClosing(mask, 5);
            mask.Dispose();

            return closed;
        }

        /// <summary>
        /// Improved morphological closing with better handling of edges
        /// </summary>
        private Bitmap MorphologicalClosing(Bitmap bmp, int kernelSize)
        {
            // Dilate first (expand white regions)
            Bitmap dilated = Dilate(bmp, kernelSize);

            // Then erode (shrink back, but holes remain filled)
            Bitmap closed = Erode(dilated, kernelSize);

            dilated.Dispose();
            return closed;
        }

        /// <summary>
        /// Removes small isolated regions from the mask
        /// </summary>
        private Bitmap RemoveSmallRegions(Bitmap inputMask, int minSize)
        {
            int width = inputMask.Width;
            int height = inputMask.Height;

            Bitmap result = new Bitmap(width, height);
            bool[,] visited = new bool[width, height];

            // First, copy the input mask
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    result.SetPixel(x, y, inputMask.GetPixel(x, y));
                }
            }

            // Find and process connected regions
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (!visited[x, y] && inputMask.GetPixel(x, y).R > 128)
                    {
                        // Found a white region, flood fill to get all pixels
                        List<Point> region = new List<Point>();
                        Queue<Point> queue = new Queue<Point>();

                        queue.Enqueue(new Point(x, y));
                        visited[x, y] = true;
                        region.Add(new Point(x, y));

                        while (queue.Count > 0)
                        {
                            Point p = queue.Dequeue();

                            // Check 4-connected neighbors
                            int[] dx = { 0, 1, 0, -1 };
                            int[] dy = { -1, 0, 1, 0 };

                            for (int i = 0; i < 4; i++)
                            {
                                int nx = p.X + dx[i];
                                int ny = p.Y + dy[i];

                                if (nx < 0 || nx >= width || ny < 0 || ny >= height || visited[nx, ny])
                                    continue;

                                if (inputMask.GetPixel(nx, ny).R > 128)
                                {
                                    visited[nx, ny] = true;
                                    region.Add(new Point(nx, ny));
                                    queue.Enqueue(new Point(nx, ny));
                                }
                            }
                        }

                        // If region is too small, remove it
                        if (region.Count < minSize)
                        {
                            foreach (var pt in region)
                            {
                                result.SetPixel(pt.X, pt.Y, Color.Black);
                            }
                        }
                    }
                }
            }

            return result;
        }
        /// <summary>
        /// Cleans up the mask by applying morphological operations and ensuring positive points are covered
        /// </summary>
        private Bitmap CleanUpMask(Bitmap inputMask, List<AnnotationPoint> positivePoints, int width, int height)
        {
            // First, remove small isolated regions
            Bitmap cleanedMask = RemoveSmallRegions(inputMask, 100);

            // Apply morphological closing to fill small holes and connect regions
            Bitmap closedMask = MorphologicalClosing(cleanedMask, 3);
            cleanedMask.Dispose();

            // Ensure all positive points are included in the mask
            Bitmap finalMask = new Bitmap(closedMask);
            using (Graphics g = Graphics.FromImage(finalMask))
            {
                using (Brush brush = new SolidBrush(Color.White))
                {
                    foreach (var pt in positivePoints)
                    {
                        int x = (int)Math.Min(Math.Max(0, pt.X), width - 1);
                        int y = (int)Math.Min(Math.Max(0, pt.Y), height - 1);

                        // Only add point if it's not already in the mask
                        if (closedMask.GetPixel(x, y).R < 128)
                        {
                            g.FillEllipse(brush, x - 5, y - 5, 10, 10);
                        }
                    }
                }
            }

            closedMask.Dispose();

            // One final pass of closing to connect any added points
            Bitmap result = MorphologicalClosing(finalMask, 5);
            finalMask.Dispose();

            return result;
        }
        /// <summary>
        /// Grows regions starting from positive point seeds, stopping at boundaries or negative points
        /// </summary>
        private Bitmap RegionGrowFromSeeds(byte[,] intensities, List<AnnotationPoint> seeds, List<AnnotationPoint> negativePoints, int intensityTolerance)
        {
            int width = intensities.GetLength(0);
            int height = intensities.GetLength(1);

            Bitmap result = new Bitmap(width, height);
            bool[,] visited = new bool[width, height];

            // Mark negative points as visited to prevent growth into these areas
            foreach (var pt in negativePoints)
            {
                int x = (int)Math.Min(Math.Max(0, pt.X), width - 1);
                int y = (int)Math.Min(Math.Max(0, pt.Y), height - 1);
                visited[x, y] = true;
            }

            // Process each seed point
            foreach (var seed in seeds)
            {
                int seedX = (int)Math.Min(Math.Max(0, seed.X), width - 1);
                int seedY = (int)Math.Min(Math.Max(0, seed.Y), height - 1);

                if (visited[seedX, seedY])
                    continue;

                byte seedIntensity = intensities[seedX, seedY];

                // Use queue for region growing
                Queue<Point> queue = new Queue<Point>();
                queue.Enqueue(new Point(seedX, seedY));
                visited[seedX, seedY] = true;
                result.SetPixel(seedX, seedY, Color.White);

                while (queue.Count > 0)
                {
                    Point p = queue.Dequeue();

                    // Check 4-connected neighbors
                    int[] dx = { 0, 1, 0, -1 };
                    int[] dy = { -1, 0, 1, 0 };

                    for (int i = 0; i < 4; i++)
                    {
                        int nx = p.X + dx[i];
                        int ny = p.Y + dy[i];

                        // Skip if outside image bounds or already visited
                        if (nx < 0 || nx >= width || ny < 0 || ny >= height || visited[nx, ny])
                            continue;

                        // Check if neighbor intensity is similar to seed
                        byte neighborIntensity = intensities[nx, ny];
                        if (Math.Abs(neighborIntensity - seedIntensity) <= intensityTolerance)
                        {
                            visited[nx, ny] = true;
                            result.SetPixel(nx, ny, Color.White);
                            queue.Enqueue(new Point(nx, ny));
                        }
                    }
                }
            }

            return result;
        }
        /// <summary>
        /// Creates a binary mask where pixels are white if their intensity is within the specified range
        /// </summary>
        private Bitmap CreateIntensityRangeMask(byte[,] intensities, byte lowerThreshold, byte upperThreshold)
        {
            int width = intensities.GetLength(0);
            int height = intensities.GetLength(1);

            Bitmap mask = new Bitmap(width, height);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte value = intensities[x, y];
                    if (value >= lowerThreshold && value <= upperThreshold)
                    {
                        mask.SetPixel(x, y, Color.White);
                    }
                    else
                    {
                        mask.SetPixel(x, y, Color.Black);
                    }
                }
            }

            return mask;
        }
        /// <summary>
        /// Computes an optimal threshold using Otsu's method.
        /// </summary>
        private int ComputeOtsuThreshold(Bitmap bmp)
        {
            // Build histogram for grayscale intensities (0-255)
            int[] histogram = new int[256];
            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++)
                {
                    // Assume mask is grayscale stored in R channel.
                    int intensity = bmp.GetPixel(x, y).R;
                    histogram[intensity]++;
                }
            }

            int total = bmp.Width * bmp.Height;
            float sum = 0;
            for (int t = 0; t < 256; t++)
                sum += t * histogram[t];

            float sumB = 0;
            int weightB = 0;
            int weightF = 0;
            float varMax = 0;
            int threshold = 0;

            for (int t = 0; t < 256; t++)
            {
                weightB += histogram[t];               // Weight Background
                if (weightB == 0)
                    continue;

                weightF = total - weightB;             // Weight Foreground
                if (weightF == 0)
                    break;

                sumB += t * histogram[t];

                float meanB = sumB / weightB;
                float meanF = (sum - sumB) / weightF;

                // Calculate Between Class Variance
                float varBetween = weightB * weightF * (meanB - meanF) * (meanB - meanF);

                // Check if new maximum found
                if (varBetween > varMax)
                {
                    varMax = varBetween;
                    threshold = t;
                }
            }

            return threshold;
        }


        private Bitmap FillHoles(Bitmap bmp)
        {
            Bitmap filled = (Bitmap)bmp.Clone();
            Color fillColor = Color.Red;
            Queue<Point> queue = new Queue<Point>();

            for (int x = 0; x < filled.Width; x++)
            {
                queue.Enqueue(new Point(x, 0));
                queue.Enqueue(new Point(x, filled.Height - 1));
            }
            for (int y = 0; y < filled.Height; y++)
            {
                queue.Enqueue(new Point(0, y));
                queue.Enqueue(new Point(filled.Width - 1, y));
            }

            while (queue.Count > 0)
            {
                Point p = queue.Dequeue();
                if (p.X < 0 || p.X >= filled.Width || p.Y < 0 || p.Y >= filled.Height)
                    continue;
                Color current = filled.GetPixel(p.X, p.Y);
                if (current.ToArgb() == Color.Black.ToArgb())
                {
                    filled.SetPixel(p.X, p.Y, fillColor);
                    queue.Enqueue(new Point(p.X + 1, p.Y));
                    queue.Enqueue(new Point(p.X - 1, p.Y));
                    queue.Enqueue(new Point(p.X, p.Y + 1));
                    queue.Enqueue(new Point(p.X, p.Y - 1));
                }
            }

            Bitmap output = new Bitmap(filled.Width, filled.Height, PixelFormat.Format32bppArgb);
            for (int y = 0; y < filled.Height; y++)
            {
                for (int x = 0; x < filled.Width; x++)
                {
                    bool isFill = filled.GetPixel(x, y).ToArgb() == fillColor.ToArgb();
                    output.SetPixel(x, y, isFill ? Color.Black : Color.White);
                }
            }
            filled.Dispose();
            return output;
        }

        /// <summary>
        /// We reduce the kernel size to 3 for less aggressive morphological closing.
        /// </summary>
        private Bitmap MorphologicalClosing(Bitmap bmp)
        {
            Bitmap dilated = Dilate(bmp, 3);
            Bitmap closed = Erode(dilated, 3);
            dilated.Dispose();
            return closed;
        }

        /// <summary>
        /// Improved dilation with better edge handling
        /// </summary>
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



        /// <summary>
        /// Improved erosion with better edge handling
        /// </summary>
        private Bitmap Erode(Bitmap bmp, int kernelSize)
        {
            Bitmap result = new Bitmap(bmp.Width, bmp.Height);
            int radius = kernelSize / 2;

            // For each pixel in the output image
            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++)
                {
                    bool setBlack = false;

                    // Check the neighborhood
                    for (int ky = -radius; ky <= radius && !setBlack; ky++)
                    {
                        int ny = y + ky;
                        if (ny < 0 || ny >= bmp.Height)
                        {
                            // Treat out-of-bounds as black
                            setBlack = true;
                            continue;
                        }

                        for (int kx = -radius; kx <= radius && !setBlack; kx++)
                        {
                            int nx = x + kx;
                            if (nx < 0 || nx >= bmp.Width)
                            {
                                // Treat out-of-bounds as black
                                setBlack = true;
                                continue;
                            }

                            // If any neighbor is black, set this pixel to black
                            if (bmp.GetPixel(nx, ny).R <= 128)
                            {
                                setBlack = true;
                            }
                        }
                    }

                    result.SetPixel(x, y, setBlack ? Color.Black : Color.White);
                }
            }

            return result;
        }
        // Create more conservative fallback segmentation 
        private Bitmap CreateFallbackSegmentation(int width, int height, List<AnnotationPoint> points)
        {
            Bitmap mask = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(mask))
            {
                g.Clear(Color.Black);

                // Get only positive points
                var positivePoints = points.Where(p => p.Label.Equals("Foreground", StringComparison.OrdinalIgnoreCase)).ToList();

                // Draw smaller circles around positive points
                const int RADIUS = 10; // Reduced from 15 to be more conservative
                using (Brush whiteBrush = new SolidBrush(Color.White))
                {
                    foreach (var point in positivePoints)
                    {
                        int x = (int)Math.Max(0, Math.Min(point.X, width - 1));
                        int y = (int)Math.Max(0, Math.Min(point.Y, height - 1));

                        g.FillEllipse(whiteBrush, x - RADIUS, y - RADIUS, RADIUS * 2, RADIUS * 2);
                    }
                }
            }

            // Count white pixels in fallback mask
            int whiteCount = 0;
            for (int y = 0; y < mask.Height; y++)
            {
                for (int x = 0; x < mask.Width; x++)
                {
                    if (mask.GetPixel(x, y).R > 128)
                        whiteCount++;
                }
            }

            double percentage = (double)whiteCount / (mask.Width * mask.Height) * 100;
            Logger.Log($"[CreateFallbackSegmentation] Created mask with {whiteCount} pixels ({percentage:F1}%)");

            return mask;
        }
        /// <summary>
        /// Improved hole filling that preserves important structures
        /// </summary>
        private Bitmap FillHolesSelective(Bitmap binaryMask, int minHoleArea = 50)
        {
            int width = binaryMask.Width;
            int height = binaryMask.Height;
            Bitmap filledMask = new Bitmap(width, height);
            bool[,] visited = new bool[width, height];

            // Copy the input mask
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    filledMask.SetPixel(x, y, binaryMask.GetPixel(x, y));
                }
            }

            // Flood fill function
            List<Point> FloodFill(int startX, int startY)
            {
                List<Point> region = new List<Point>();
                Queue<Point> queue = new Queue<Point>();
                queue.Enqueue(new Point(startX, startY));
                visited[startX, startY] = true;

                while (queue.Count > 0)
                {
                    Point p = queue.Dequeue();
                    region.Add(p);

                    // Check 4-connected neighbors
                    int[] dx = { 0, 1, 0, -1 };
                    int[] dy = { -1, 0, 1, 0 };

                    for (int i = 0; i < 4; i++)
                    {
                        int nx = p.X + dx[i], ny = p.Y + dy[i];
                        if (nx >= 0 && nx < width && ny >= 0 && ny < height &&
                            !visited[nx, ny] && filledMask.GetPixel(nx, ny).R < 128)
                        {
                            visited[nx, ny] = true;
                            queue.Enqueue(new Point(nx, ny));
                        }
                    }
                }
                return region;
            }

            // First identify background regions connected to the border
            for (int x = 0; x < width; x++)
            {
                if (!visited[x, 0] && filledMask.GetPixel(x, 0).R < 128)
                    FloodFill(x, 0);
                if (!visited[x, height - 1] && filledMask.GetPixel(x, height - 1).R < 128)
                    FloodFill(x, height - 1);
            }

            for (int y = 0; y < height; y++)
            {
                if (!visited[0, y] && filledMask.GetPixel(0, y).R < 128)
                    FloodFill(0, y);
                if (!visited[width - 1, y] && filledMask.GetPixel(width - 1, y).R < 128)
                    FloodFill(width - 1, y);
            }

            // Now look for interior holes
            List<List<Point>> holes = new List<List<Point>>();
            for (int y = 1; y < height - 1; y++)
            {
                for (int x = 1; x < width - 1; x++)
                {
                    if (!visited[x, y] && filledMask.GetPixel(x, y).R < 128)
                    {
                        List<Point> hole = FloodFill(x, y);
                        if (hole.Count >= minHoleArea)
                        {
                            holes.Add(hole);
                        }
                    }
                }
            }

            // Fill holes that pass size threshold
            foreach (var hole in holes)
            {
                foreach (var p in hole)
                {
                    filledMask.SetPixel(p.X, p.Y, Color.White);
                }
            }

            return filledMask;
        }




        private Tensor<T> GetFirstTensor<T>(IEnumerable<DisposableNamedOnnxValue> outputs, string name)
        {
            var output = outputs.FirstOrDefault(x => x.Name == name);
            if (output == null)
                throw new InvalidOperationException($"Output with name '{name}' not found.");
            return output.AsTensor<T>();
        }

        public void Dispose()
        {
            _imageEncoderSession?.Dispose();
            _promptEncoderSession?.Dispose();
            _maskDecoderSession?.Dispose();
            _memoryEncoderSession?.Dispose();
            _memoryAttentionSession?.Dispose();
            _mlpSession?.Dispose();
        }
    }

    public class SliceMemory
    {
        public float[] VisionFeatures { get; set; }
        public float[] VisionPosEnc { get; set; }
        public float[] MaskLogits { get; set; }
    }
}
#endregion