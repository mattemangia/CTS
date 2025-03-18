using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

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
        public int MaskThreshold { get; set; } = 220; // Default threshold value
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
        /// Processes a CT slice in the XY view using a list of AnnotationPoints.
        /// Negative prompts have label="Exterior", positive="Foreground" for the target material.
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
                Logger.Log($"[ProcessXYSlice] Start processing slice {sliceIndex} with {promptPoints?.Count ?? 0} points");

                // Validate input bitmap
                if (baseXY == null)
                    throw new ArgumentNullException(nameof(baseXY), "Input bitmap cannot be null");

                if (baseXY.Width <= 0 || baseXY.Height <= 0)
                    throw new ArgumentException("Invalid bitmap dimensions");

                // Validate prompt points
                if (promptPoints == null || promptPoints.Count == 0)
                {
                    Logger.Log("[ProcessXYSlice] Warning: No prompt points provided");
                    throw new ArgumentException("No prompt points provided");
                }

                // Log input details
                int positiveCount = promptPoints.Count(p => p.Label.Equals("Foreground", StringComparison.OrdinalIgnoreCase));
                int negativeCount = promptPoints.Count(p => p.Label.Equals("Exterior", StringComparison.OrdinalIgnoreCase));
                Logger.Log($"[ProcessXYSlice] Points: {positiveCount} positive, {negativeCount} negative");

                // Convert bitmap to tensor
                float[] imageTensor = BitmapToFloatTensor(baseXY, _imageSize, _imageSize);
                var imageInput = new DenseTensor<float>(imageTensor, new[] { 1, 3, _imageSize, _imageSize });

                // Run image encoder
                using (var imageEncoderOutputs = _imageEncoderSession.Run(new[] {
            NamedOnnxValue.CreateFromTensor("input_image", imageInput)
        }))
                {
                    // Get encoder outputs
                    var visionFeatures = GetFirstTensor<float>(imageEncoderOutputs, "vision_features");
                    var visionPosEnc = GetFirstTensor<float>(imageEncoderOutputs, "vision_pos_enc_2");
                    var highResFeatures1 = GetFirstTensor<float>(imageEncoderOutputs, "backbone_fpn_0");
                    var highResFeatures2 = GetFirstTensor<float>(imageEncoderOutputs, "backbone_fpn_1");

                    // Prepare prompt coordinates
                    int pointCount = promptPoints.Count;
                    float[] flattenedPromptCoords = new float[pointCount * 2];
                    int[] flattenedPromptLabels = new int[pointCount];

                    for (int i = 0; i < pointCount; i++)
                    {
                        // Clamp to original image dimensions
                        float x = Math.Max(0, Math.Min(promptPoints[i].X, baseXY.Width - 1));
                        float y = Math.Max(0, Math.Min(promptPoints[i].Y, baseXY.Height - 1));

                        // Scale to model's input space
                        float xScaleFactor = (_imageSize - 1f) / Math.Max(1, baseXY.Width - 1f);
                        float yScaleFactor = (_imageSize - 1f) / Math.Max(1, baseXY.Height - 1f);
                        float xScaled = x * xScaleFactor;
                        float yScaled = y * yScaleFactor;

                        flattenedPromptCoords[i * 2] = xScaled;
                        flattenedPromptCoords[i * 2 + 1] = yScaled;

                        // Set label (0 = Exterior/negative, 1 = Foreground/positive)
                        string label = promptPoints[i].Label ?? "Foreground";
                        flattenedPromptLabels[i] = label.Equals("Exterior", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                    }

                    // Create prompt tensors with explicit shapes
                    var promptCoordsTensor = new DenseTensor<float>(
                        flattenedPromptCoords,
                        new[] { 1, pointCount, 2 }  // Batch x Points x XY
                    );

                    var promptLabelsTensor = new DenseTensor<int>(
                        flattenedPromptLabels,
                        new[] { 1, pointCount }     // Batch x Points
                    );

                    // Create mask input tensor (all ones)
                    var maskInputTensor = new DenseTensor<float>(
                        Enumerable.Repeat(1f, 256 * 256).ToArray(),
                        new[] { 1, 256, 256 }
                    );

                    // Run prompt encoder
                    var promptEncoderInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("coords", promptCoordsTensor),
                NamedOnnxValue.CreateFromTensor("labels", promptLabelsTensor),
                NamedOnnxValue.CreateFromTensor("masks", maskInputTensor),
                NamedOnnxValue.CreateFromTensor("masks_enable", new DenseTensor<int>(new[] { 0 }, new[] { 1 }))
            };

                    using (var promptEncoderOutputs = _promptEncoderSession.Run(promptEncoderInputs))
                    {
                        // Process mask decoder
                        var sparseEmbeddings = GetFirstTensor<float>(promptEncoderOutputs, "sparse_embeddings");
                        var denseEmbeddings = GetFirstTensor<float>(promptEncoderOutputs, "dense_embeddings");

                        var maskDecoderInputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("image_embeddings", visionFeatures),
                    NamedOnnxValue.CreateFromTensor("image_pe", visionPosEnc),
                    NamedOnnxValue.CreateFromTensor("sparse_prompt_embeddings", sparseEmbeddings),
                    NamedOnnxValue.CreateFromTensor("dense_prompt_embeddings", denseEmbeddings),
                    NamedOnnxValue.CreateFromTensor("high_res_features1", highResFeatures1),
                    NamedOnnxValue.CreateFromTensor("high_res_features2", highResFeatures2)
                };

                        using (var maskDecoderOutputs = _maskDecoderSession.Run(maskDecoderInputs))
                        {
                            var maskTensor = GetFirstTensor<float>(maskDecoderOutputs, "masks");

                            // Extract mask data and analyze values
                            int maskHeight = maskTensor.Dimensions[2];
                            int maskWidth = maskTensor.Dimensions[3];
                            float[] maskData = new float[maskHeight * maskWidth];

                            // Find min/max to understand value distribution
                            float minValue = float.MaxValue;
                            float maxValue = float.MinValue;
                            float sum = 0;

                            for (int y = 0; y < maskHeight; y++)
                            {
                                for (int x = 0; x < maskWidth; x++)
                                {
                                    float val = maskTensor[0, 0, y, x];
                                    maskData[y * maskWidth + x] = val;
                                    minValue = Math.Min(minValue, val);
                                    maxValue = Math.Max(maxValue, val);
                                    sum += val;
                                }
                            }

                            float meanValue = sum / maskData.Length;
                            Logger.Log($"[ProcessXYSlice] Mask raw logits - Min: {minValue:F2}, Max: {maxValue:F2}, Mean: {meanValue:F2}");

                            // ADAPTIVE APPROACH: Find best parameters for this specific image

                            // 1. Test multiple thresholds to find a good one
                            int[] thresholds = new int[] { 80, 100, 120, 140, 160, 180 };
                            float[] coveragePercents = new float[thresholds.Length];

                            // First convert logits to probabilities with standard sigmoid
                            float[] probabilities = new float[maskData.Length];
                            for (int i = 0; i < maskData.Length; i++)
                            {
                                probabilities[i] = 1.0f / (1.0f + (float)Math.Exp(-maskData[i]));
                            }

                            // Test different thresholds
                            for (int t = 0; t < thresholds.Length; t++)
                            {
                                int threshold = thresholds[t];
                                int whiteCount = 0;

                                for (int i = 0; i < probabilities.Length; i++)
                                {
                                    if ((probabilities[i] * 255) > threshold)
                                        whiteCount++;
                                }

                                coveragePercents[t] = (float)whiteCount / probabilities.Length * 100;
                                Logger.Log($"[ProcessXYSlice] Threshold {threshold}: {coveragePercents[t]:F1}% coverage");
                            }

                            // 2. Find the best threshold - preferably 5-20% coverage, not too large or small
                            int selectedThresholdIndex = 0;
                            for (int i = 0; i < thresholds.Length; i++)
                            {
                                if (coveragePercents[i] >= 5 && coveragePercents[i] <= 20)
                                {
                                    selectedThresholdIndex = i;
                                    break;
                                }
                            }

                            // If nothing in ideal range, find something reasonable
                            if (coveragePercents[selectedThresholdIndex] < 1 || coveragePercents[selectedThresholdIndex] > 30)
                            {
                                // Find the threshold closest to 10% coverage
                                float bestDistance = float.MaxValue;
                                for (int i = 0; i < thresholds.Length; i++)
                                {
                                    float distance = Math.Abs(coveragePercents[i] - 10);
                                    if (distance < bestDistance)
                                    {
                                        bestDistance = distance;
                                        selectedThresholdIndex = i;
                                    }
                                }
                            }

                            int selectedThreshold = thresholds[selectedThresholdIndex];
                            Logger.Log($"[ProcessXYSlice] Selected threshold: {selectedThreshold} ({coveragePercents[selectedThresholdIndex]:F1}% coverage)");

                            // 3. Create raw mask with selected threshold
                            Bitmap rawMask = new Bitmap(maskWidth, maskHeight);
                            for (int y = 0; y < maskHeight; y++)
                            {
                                for (int x = 0; x < maskWidth; x++)
                                {
                                    float prob = probabilities[y * maskWidth + x];
                                    bool isWhite = (prob * 255) > selectedThreshold;
                                    rawMask.SetPixel(x, y, isWhite ? Color.White : Color.Black);
                                }
                            }

                            // 4. Apply moderate dilation only if mask is sparse
                            Bitmap processedMask = rawMask;
                            if (coveragePercents[selectedThresholdIndex] < 15)
                            {
                                // Only dilate if coverage is low
                                Bitmap dilatedMask = Dilate(rawMask, 3);  // Small kernel
                                rawMask.Dispose();
                                processedMask = dilatedMask;
                            }

                            // 5. Upscale to final dimensions
                            Bitmap finalMask = new Bitmap(baseXY.Width, baseXY.Height);
                            using (Graphics g = Graphics.FromImage(finalMask))
                            {
                                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                g.DrawImage(processedMask, new Rectangle(0, 0, baseXY.Width, baseXY.Height));
                            }
                            processedMask.Dispose();

                            // 6. Verify final results
                            int finalWhiteCount = 0;
                            for (int y = 0; y < finalMask.Height; y++)
                            {
                                for (int x = 0; x < finalMask.Width; x++)
                                {
                                    if (finalMask.GetPixel(x, y).R > 128)
                                        finalWhiteCount++;
                                }
                            }

                            double finalPercent = (double)finalWhiteCount / (finalMask.Width * finalMask.Height) * 100;
                            Logger.Log($"[ProcessXYSlice] Final mask: {finalWhiteCount} white pixels ({finalPercent:F1}%)");

                            // 7. SAFETY CHECK: If too much of the image is white, use fallback
                            if (finalPercent > 50)
                            {
                                Logger.Log("[ProcessXYSlice] Warning: Mask covers >50% of image. Using conservative fallback.");
                                finalMask.Dispose();

                                if (positiveCount > 0)
                                {
                                    finalMask = CreateFallbackSegmentation(baseXY.Width, baseXY.Height, promptPoints);
                                }
                                else
                                {
                                    // Create empty mask if no positive points
                                    finalMask = new Bitmap(baseXY.Width, baseXY.Height);
                                    using (Graphics g = Graphics.FromImage(finalMask))
                                    {
                                        g.Clear(Color.Black);
                                    }
                                }
                            }

                            return finalMask;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ProcessXYSlice] Error: {ex.Message}");
                Logger.Log($"[ProcessXYSlice] Stack trace: {ex.StackTrace}");

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
        public Bitmap ProcessXZSlice(
            int sliceIndex,
            Bitmap baseXZ,
            List<AnnotationPoint> promptPoints,
            object p2,
            object p3)
        {
            try
            {
                Logger.Log($"[ProcessXZSlice] Processing slice {sliceIndex} with {promptPoints?.Count ?? 0} points");

                // For XZ view, coordinate validation happens in ProcessXYSlice
                return ProcessXYSlice(sliceIndex, baseXZ, promptPoints, p2, p3);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ProcessXZSlice] Error: {ex.Message}");
                throw;
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
        /// Processes a CT slice in the YZ view using a list of AnnotationPoints.
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
                Logger.Log($"[ProcessYZSlice] Processing slice {sliceIndex} with {promptPoints?.Count ?? 0} points");

                // Validate bitmap dimensions for YZ view 
                if (baseYZ == null)
                {
                    Logger.Log($"Warning: Null bitmap provided for YZ slice {sliceIndex}");
                    throw new ArgumentNullException(nameof(baseYZ), "YZ bitmap cannot be null");
                }

                Logger.Log($"YZ bitmap dimensions: {baseYZ.Width}x{baseYZ.Height}");

                // Validate points for YZ view specifically
                if (promptPoints == null || promptPoints.Count == 0)
                {
                    Logger.Log($"Warning: No prompt points for YZ slice {sliceIndex}");
                    throw new ArgumentException("No prompt points provided for YZ slice");
                }

                // Filter out invalid points for YZ view
                var validPoints = promptPoints.Where(p => p != null &&
                                                   !float.IsNaN(p.X) && !float.IsNaN(p.Y) &&
                                                   !float.IsInfinity(p.X) && !float.IsInfinity(p.Y) &&
                                                   p.X >= 0 && p.Y >= 0 &&
                                                   p.X < baseYZ.Width && p.Y < baseYZ.Height).ToList();

                if (validPoints.Count == 0)
                {
                    Logger.Log($"Warning: No valid points remain for YZ slice {sliceIndex} after filtering");
                    throw new ArgumentException("No valid points for YZ slice after filtering");
                }

                // Delegate to main processing method
                return ProcessXYSlice(sliceIndex, baseYZ, validPoints, p2, p3);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ProcessYZSlice] Error: {ex.Message}");
                throw;
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
        private Bitmap ApplyEdgeSmoothing(Bitmap mask)
        {
            try
            {
                if (mask == null)
                    return null;

                // Create a copy of the original bitmap
                Bitmap blurred = new Bitmap(mask.Width, mask.Height);

                // Lock the bitmap bits for faster processing
                BitmapData sourceData = mask.LockBits(
                    new Rectangle(0, 0, mask.Width, mask.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

                BitmapData targetData = blurred.LockBits(
                    new Rectangle(0, 0, blurred.Width, blurred.Height),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

                int bytesPerPixel = 4; // ARGB
                int stride = sourceData.Stride;
                int width = mask.Width;
                int height = mask.Height;

                unsafe
                {
                    byte* src = (byte*)sourceData.Scan0;
                    byte* dst = (byte*)targetData.Scan0;

                    // Simple box blur (3x3)
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int r = 0, g = 0, b = 0, a = 0;
                            int count = 0;

                            // Sample 3x3 neighborhood
                            for (int ky = -1; ky <= 1; ky++)
                            {
                                int sy = y + ky;
                                if (sy < 0 || sy >= height) continue;

                                for (int kx = -1; kx <= 1; kx++)
                                {
                                    int sx = x + kx;
                                    if (sx < 0 || sx >= width) continue;

                                    int srcOffset = sy * stride + sx * bytesPerPixel;
                                    b += src[srcOffset];
                                    g += src[srcOffset + 1];
                                    r += src[srcOffset + 2];
                                    a += src[srcOffset + 3];
                                    count++;
                                }
                            }

                            // Write averaged pixel
                            int dstOffset = y * stride + x * bytesPerPixel;
                            dst[dstOffset] = (byte)(b / count);
                            dst[dstOffset + 1] = (byte)(g / count);
                            dst[dstOffset + 2] = (byte)(r / count);
                            dst[dstOffset + 3] = (byte)(a / count);
                        }
                    }
                }

                // Unlock the bits
                mask.UnlockBits(sourceData);
                blurred.UnlockBits(targetData);
                mask.Dispose();
                return blurred;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error in ApplyEdgeSmoothing: {ex.Message}");
                // Return the original mask if smoothing fails
                return mask;
            }
        }

        // Update MorphologicalClosing to accept kernel size
        private Bitmap MorphologicalClosing(Bitmap bmp, int kernelSize)
        {
            Bitmap dilated = Dilate(bmp, kernelSize);
            Bitmap closed = Erode(dilated, kernelSize);
            dilated.Dispose();
            return closed;
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

        private Bitmap Dilate(Bitmap bmp, int kernelSize)
        {
            Bitmap result = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
            int k = kernelSize / 2;

            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++)
                {
                    bool white = false;
                    for (int dy = -k; dy <= k && !white; dy++)
                    {
                        for (int dx = -k; dx <= k && !white; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx >= 0 && nx < bmp.Width && ny >= 0 && ny < bmp.Height)
                            {
                                if (bmp.GetPixel(nx, ny).R > 40) // Match our low threshold
                                {
                                    white = true;
                                    break;
                                }
                            }
                        }
                    }
                    result.SetPixel(x, y, white ? Color.White : Color.Black);
                }
            }

            return result;
        }

        
        private Bitmap Erode(Bitmap bmp, int kernelSize)
        {
            Bitmap result = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
            int k = kernelSize / 2;
            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++)
                {
                    bool black = false;
                    for (int dy = -k; dy <= k; dy++)
                    {
                        for (int dx = -k; dx <= k; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx >= 0 && nx < bmp.Width && ny >= 0 && ny < bmp.Height)
                            {
                                if (bmp.GetPixel(nx, ny).R < 128)
                                {
                                    black = true;
                                    break;
                                }
                            }
                        }
                        if (black) break;
                    }
                    result.SetPixel(x, y, black ? Color.Black : Color.White);
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
        private Bitmap FillHolesSelective(Bitmap binaryMask, int minHoleArea = 50)
        {
            int width = binaryMask.Width;
            int height = binaryMask.Height;
            Bitmap filledMask = (Bitmap)binaryMask.Clone();
            bool[,] visited = new bool[width, height];

            bool InBounds(int x, int y) => (x >= 0 && x < width && y >= 0 && y < height);

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
                    foreach (Point offset in new[] { new Point(1, 0), new Point(-1, 0), new Point(0, 1), new Point(0, -1) })
                    {
                        int nx = p.X + offset.X, ny = p.Y + offset.Y;
                        if (InBounds(nx, ny) && !visited[nx, ny])
                        {
                            if (filledMask.GetPixel(nx, ny).R == 0)
                            {
                                visited[nx, ny] = true;
                                queue.Enqueue(new Point(nx, ny));
                            }
                        }
                    }
                }
                return region;
            }

            // Mark background connected to the border
            for (int x = 0; x < width; x++)
            {
                if (filledMask.GetPixel(x, 0).R == 0 && !visited[x, 0])
                    foreach (var p in FloodFill(x, 0)) { }
                if (filledMask.GetPixel(x, height - 1).R == 0 && !visited[x, height - 1])
                    foreach (var p in FloodFill(x, height - 1)) { }
            }
            for (int y = 0; y < height; y++)
            {
                if (filledMask.GetPixel(0, y).R == 0 && !visited[0, y])
                    foreach (var p in FloodFill(0, y)) { }
                if (filledMask.GetPixel(width - 1, y).R == 0 && !visited[width - 1, y])
                    foreach (var p in FloodFill(width - 1, y)) { }
            }

            // Fill holes not connected to border if bigger than minHoleArea
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    if (!visited[x, y] && filledMask.GetPixel(x, y).R == 0)
                    {
                        List<Point> hole = FloodFill(x, y);
                        if (hole.Count >= minHoleArea)
                        {
                            foreach (var p in hole)
                            {
                                filledMask.SetPixel(p.X, p.Y, Color.White);
                            }
                        }
                    }
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