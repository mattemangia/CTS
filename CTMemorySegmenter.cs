using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading.Tasks;
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