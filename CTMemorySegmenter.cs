using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CTSegmenter
{
    /// <summary>
    /// CTMemorySegmenter processes CT slices for segmentation. It implements separate pipelines
    /// for XY, XZ and YZ views and uses real prompt encoding. Memory propagation is supported,
    /// and MLP post-processing is disabled by default. 
    ///
    /// This improved version adds:
    /// 1) Automatic thresholding
    /// 2) Hole-filling algorithm 
    /// 3) Optional morphological closing
    /// at the end of mask generation to produce smoother masks.
    /// </summary>
    public class CTMemorySegmenter : IDisposable
    {
        // ONNX sessions
        private InferenceSession _imageEncoderSession;
        private InferenceSession _promptEncoderSession;
        private InferenceSession _maskDecoderSession;
        private InferenceSession _memoryEncoderSession;
        private InferenceSession _memoryAttentionSession;
        private InferenceSession _mlpSession;

        // Configuration
        private int _imageSize; // e.g., 1024
        private bool _canUseTextPrompts;
        private bool _enableMlp; // MLP post-processing enabled if true

        // Memory storage for propagation
        private Dictionary<int, SliceMemory> _sliceMem;
        private const int MLP_SIZE = 16; // MLP expects tensor of shape [*,256]

        private class SliceMemory
        {
            public Tensor<float> MemoryFeatures;
            public Tensor<float> MemoryPosEnc;
            public Bitmap Mask;
        }

        public CTMemorySegmenter(
            string imageEncoderPath,
            string promptEncoderPath,
            string maskDecoderPath,
            string memoryEncoderPath,
            string memoryAttentionPath,
            string mlpPath,
            int imageInputSize = 1024,
            bool canUseTextPrompts = false,
            bool enableMlp = false)
        {
            Logger.Log("[CTMemorySegmenter] Constructor start");
            _imageSize = imageInputSize;
            _canUseTextPrompts = canUseTextPrompts;
            _enableMlp = enableMlp;
            _sliceMem = new Dictionary<int, SliceMemory>();

            SessionOptions options = new SessionOptions();
            bool useDml = true;
            try
            {
                options.AppendExecutionProvider_DML();
                Logger.Log("[CTMemorySegmenter] Using DirectML Execution Provider");
            }
            catch (Exception ex)
            {
                Logger.Log("[CTMemorySegmenter] DML not available, falling back to CPU: " + ex.Message);
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
                Logger.Log("[CTMemorySegmenter] All sessions loaded successfully.");
            }
            catch (Exception ex)
            {
                Logger.Log("[CTMemorySegmenter] Exception during initialization: " + ex.Message);
                if (useDml)
                {
                    Logger.Log("[CTMemorySegmenter] Falling back to CPU Execution Provider.");
                    SessionOptions cpuOptions = new SessionOptions();
                    cpuOptions.AppendExecutionProvider_CPU();
                    _imageEncoderSession = new InferenceSession(imageEncoderPath, cpuOptions);
                    _promptEncoderSession = new InferenceSession(promptEncoderPath, cpuOptions);
                    _maskDecoderSession = new InferenceSession(maskDecoderPath, cpuOptions);
                    _memoryEncoderSession = new InferenceSession(memoryEncoderPath, cpuOptions);
                    _memoryAttentionSession = new InferenceSession(memoryAttentionPath, cpuOptions);
                    _mlpSession = new InferenceSession(mlpPath, cpuOptions);
                    Logger.Log("[CTMemorySegmenter] CPU fallback successful.");
                }
                else
                {
                    throw;
                }
            }
            Logger.Log("[CTMemorySegmenter] Constructor end");
            LogAllModelMetadata();
        }

        public void LogAllModelMetadata()
        {
            LogSessionMetadata("ImageEncoder", _imageEncoderSession);
            LogSessionMetadata("PromptEncoder", _promptEncoderSession);
            LogSessionMetadata("MaskDecoder", _maskDecoderSession);
            LogSessionMetadata("MemoryEncoder", _memoryEncoderSession);
            LogSessionMetadata("MemoryAttention", _memoryAttentionSession);
            LogSessionMetadata("MLP", _mlpSession);
        }

        private void LogSessionMetadata(string sessionName, InferenceSession session)
        {
            Logger.Log($"==== Metadata for {sessionName} Session ====");
            Logger.Log("Inputs:");
            foreach (var input in session.InputMetadata)
            {
                Logger.Log($"   Name: {input.Key}, Type: {input.Value.ElementType}, Dimensions: {string.Join("x", input.Value.Dimensions.ToArray())}");
            }
            Logger.Log("Outputs:");
            foreach (var output in session.OutputMetadata)
            {
                Logger.Log($"   Name: {output.Key}, Type: {output.Value.ElementType}, Dimensions: {string.Join("x", output.Value.Dimensions.ToArray())}");
            }
            Logger.Log("===============================================");
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

        public void ResetState()
        {
            Logger.Log("[CTMemorySegmenter.ResetState] Resetting state");
            foreach (var kvp in _sliceMem)
            {
                kvp.Value.Mask?.Dispose();
            }
            _sliceMem.Clear();
            Logger.Log("[CTMemorySegmenter.ResetState] State cleared");
        }

        // --- Public methods for different projections (they now accept a slice index) ---
        public Bitmap ProcessXYSlice(int sliceIndex, Bitmap sliceImage, List<Point> promptPoints, List<Rectangle> promptBoxes, bool[,] brushMask, string textPrompt = null)
        {
            return ProcessSliceWithMemory(sliceIndex, sliceImage, promptPoints, promptBoxes, brushMask, textPrompt, null, null);
        }

        public Bitmap ProcessXZSlice(int sliceIndex, Bitmap sliceImage, List<Point> promptPoints, List<Rectangle> promptBoxes, bool[,] brushMask, string textPrompt = null)
        {
            // For XZ view, sliceIndex indicates the fixed Y (row) value.
            return ProcessSliceWithMemory(sliceIndex, sliceImage, promptPoints, promptBoxes, brushMask, textPrompt, null, null);
        }

        public Bitmap ProcessYZSlice(int sliceIndex, Bitmap sliceImage, List<Point> promptPoints, List<Rectangle> promptBoxes, bool[,] brushMask, string textPrompt = null)
        {
            // For YZ view, sliceIndex indicates the fixed X (column) value.
            return ProcessSliceWithMemory(sliceIndex, sliceImage, promptPoints, promptBoxes, brushMask, textPrompt, null, null);
        }

        // Also provide legacy method:
        public Bitmap ProcessSingleSlice(Bitmap sliceImage, List<Point> promptPoints, List<Rectangle> promptBoxes, bool[,] brushMask, string textPrompt = null)
        {
            return ProcessXYSlice(0, sliceImage, promptPoints, promptBoxes, brushMask, textPrompt);
        }

        // MAIN PIPELINE
        private Bitmap ProcessSliceWithMemory(
            int sliceIndex,
            Bitmap sliceImage,
            List<Point> userPoints,
            List<Rectangle> userBoxes,
            bool[,] brushMask,
            string textPrompt,
            int? prevSliceIndex,
            int? nextSliceIndex)
        {
            Logger.Log($"[ProcessSliceWithMemory] Start processing slice {sliceIndex} with original dimensions {sliceImage.Width}x{sliceImage.Height}");

            // 1. Resize the input to the model resolution.
            Bitmap resizedImage = ResizeImage(sliceImage, _imageSize, _imageSize);
            var (visionFeatures, visionPosEnc) = RunImageEncoder(resizedImage);
            Logger.Log($"[ProcessSliceWithMemory] Image encoder completed for slice {sliceIndex}");
            resizedImage.Dispose();

            // 2. Run prompt encoder.
            var promptEmbeds = RunPromptEncoderIfNeeded(
                sliceImage.Width,
                sliceImage.Height,
                userPoints,
                userBoxes,
                brushMask,
                textPrompt
            );
            Logger.Log("[ProcessSliceWithMemory] Prompt encoder completed.");

            // 3. Memory attention (if previous slice exists)
            Tensor<float> combinedMemoryFeatures = null;
            Tensor<float> combinedMemoryPosEnc = null;
            if (prevSliceIndex.HasValue && _sliceMem.ContainsKey(prevSliceIndex.Value))
            {
                var prevMem = _sliceMem[prevSliceIndex.Value];
                var currFeatFlat = FlattenForAttention(visionFeatures);
                var currPosFlat = FlattenForAttention(visionPosEnc);
                var memFeatFlat = FlattenForAttention(prevMem.MemoryFeatures);
                var memPosFlat = FlattenForAttention(prevMem.MemoryPosEnc);
                long numObjPtrTokens = 1;
                (combinedMemoryFeatures, _) = RunMemoryAttention(
                    currFeatFlat,
                    currPosFlat,
                    memFeatFlat,
                    memPosFlat,
                    numObjPtrTokens
                );
                combinedMemoryFeatures = ReshapeFromAttention(combinedMemoryFeatures, 1, 256, 64, 64);
                combinedMemoryPosEnc = visionPosEnc; // We'll keep the same position encoding
                Logger.Log($"[ProcessSliceWithMemory] Memory attention applied using previous slice {prevSliceIndex}");
            }

            // 4. Run mask decoder.
            Tensor<float> lowResMask = RunMaskDecoder(
                combinedMemoryFeatures ?? visionFeatures,
                promptEmbeds,
                visionFeatures,
                visionPosEnc
            );
            Logger.Log($"[ProcessSliceWithMemory] Mask decoder completed for slice {sliceIndex}. Output shape: {string.Join("x", lowResMask.Dimensions.ToArray())}");

            // 5. If the mask tensor has more than one channel, extract channel 1 (or 0 if that’s the relevant one).
            if (lowResMask.Dimensions[1] > 1)
            {
                // Often channel 0 is "background" and channel 1 is "foreground", 
                // but it depends on your model. Adjust as needed:
                lowResMask = ExtractChannel(lowResMask, 1);
                Logger.Log($"[ProcessSliceWithMemory] Extracted channel 1. New shape: {string.Join("x", lowResMask.Dimensions.ToArray())}");
            }

            // 6. Convert tensor to a low-res bitmap.
            // NOTE: lowResMask is now of shape [1,1,256,256]; use these dimensions.
            Bitmap lowResMaskBmp = TensorToBitmap(lowResMask, 256, 256);
            Logger.Log($"[ProcessSliceWithMemory] Low-res mask bitmap dimensions: {lowResMaskBmp.Width}x{lowResMaskBmp.Height}");

            // 7. Resize the mask back to the original slice resolution.
            Bitmap finalMask = ResizeImage(lowResMaskBmp, sliceImage.Width, sliceImage.Height);
            lowResMaskBmp.Dispose();
            Logger.Log($"[ProcessSliceWithMemory] Final mask dimensions: {finalMask.Width}x{finalMask.Height}");

            // 8. Optionally apply MLP post-processing.
            Bitmap postProcessedMask = _enableMlp ? RunMlpOnMask(finalMask) : finalMask;
            Logger.Log(_enableMlp ? "[ProcessSliceWithMemory] MLP post-processing applied."
                                  : "[ProcessSliceWithMemory] MLP post-processing skipped.");

            // 9. **New**: Apply threshold, hole-filling, morphological closing, etc.
            Bitmap cleanedUpMask = PostProcessMask(
            postProcessedMask,
            sliceIndex,
            threshold: 0.5f,
            fillHoles: true,
            doMorphologicalClose: true
            );

            if (_enableMlp)
            {
                postProcessedMask.Dispose();
            }

            // 10. Update memory.
            if (_sliceMem.ContainsKey(sliceIndex))
            {
                _sliceMem[sliceIndex].Mask?.Dispose();
            }
            _sliceMem[sliceIndex] = new SliceMemory
            {
                MemoryFeatures = combinedMemoryFeatures ?? visionFeatures,
                MemoryPosEnc = combinedMemoryPosEnc ?? visionPosEnc,
                Mask = cleanedUpMask
            };

            Logger.Log($"[ProcessSliceWithMemory] Finished processing slice {sliceIndex}");
            return cleanedUpMask;
        }

        // --- Helper methods for each stage ---
        private (Tensor<float> visionFeatures, Tensor<float> visionPosEnc) RunImageEncoder(Bitmap image)
        {
            Logger.Log("[RunImageEncoder] Running image encoder.");
            float[] floatData = ImageToFloatArray(image);
            var imageTensor = new DenseTensor<float>(floatData, new int[] { 1, 3, _imageSize, _imageSize });
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_image", imageTensor)
            };
            Tensor<float> features, posEnc;
            using (var outputs = _imageEncoderSession.Run(inputs))
            {
                features = outputs.First(x => x.Name == "vision_features").AsTensor<float>();
                // Use vision_pos_enc_2 (1x256x64x64) for spatial alignment
                posEnc = outputs.First(x => x.Name == "vision_pos_enc_2").AsTensor<float>();
            }
            Logger.Log("[RunImageEncoder] Completed.");
            return (features, posEnc);
        }

        // RunPromptEncoderIfNeeded: returns prompt tensors with real encoding or dummy if no prompts.
        private (Tensor<float> Sparse, Tensor<float> Dense, Tensor<float> DensePe) RunPromptEncoderIfNeeded(
            int originalWidth,
            int originalHeight,
            List<Point> userPoints,
            List<Rectangle> userBoxes,
            bool[,] brushMask,
            string textPrompt)
        {
            bool hasPrompts = (userPoints != null && userPoints.Count > 0) ||
                              (userBoxes != null && userBoxes.Count > 0) ||
                              (brushMask != null) ||
                              (!string.IsNullOrEmpty(textPrompt) && _canUseTextPrompts);

            if (!hasPrompts)
            {
                Logger.Log("[RunPromptEncoderIfNeeded] No prompts provided; using dummy tensors.");
                float[] dummySparse = new float[1 * 1 * 256];
                var sparse = new DenseTensor<float>(dummySparse, new int[] { 1, 1, 256 });
                float[] dummyDense = new float[1 * 256 * 64 * 64];
                var dense = new DenseTensor<float>(dummyDense, new int[] { 1, 256, 64, 64 });
                float[] dummyDensePe = new float[1 * 256 * 64 * 64];
                var densePe = new DenseTensor<float>(dummyDensePe, new int[] { 1, 256, 64, 64 });
                return (sparse, dense, densePe);
            }

            return CreatePromptTensors(
                userPoints,
                userBoxes,
                brushMask,
                textPrompt,
                originalWidth,
                originalHeight
            );
        }

        // Real prompt encoder: outputs sparse: [1,1,256], dense: [1,256,64,64], dense_pe: [1,256,64,64].
        private (Tensor<float> Sparse, Tensor<float> Dense, Tensor<float> DensePe) CreatePromptTensors(
    List<Point> points,
    List<Rectangle> boxes,
    bool[,] brushMask,
    string textPrompt,
    int originalWidth,
    int originalHeight)
        {
            // 1. Initialize tensors
            var sparseTensor = new DenseTensor<float>(new float[256], new[] { 1, 1, 256 });
            var denseTensor = new DenseTensor<float>(new[] { 1, 256, 64, 64 });
            var densePeTensor = new DenseTensor<float>(new[] { 1, 256, 64, 64 });

            // 2. Process geometry prompts
            List<float> coordBuffer = new List<float>();

            // Convert points to normalized coordinates
            if (points != null)
            {
                foreach (Point p in points)
                {
                    float nx = (float)p.X / originalWidth * _imageSize;
                    float ny = (float)p.Y / originalHeight * _imageSize;
                    coordBuffer.AddRange(new[] { nx, ny });
                }
            }

            // Convert boxes to normalized coordinates
            if (boxes != null)
            {
                foreach (Rectangle box in boxes)
                {
                    float left = (float)box.Left / originalWidth * _imageSize;
                    float top = (float)box.Top / originalHeight * _imageSize;
                    float right = (float)box.Right / originalWidth * _imageSize;
                    float bottom = (float)box.Bottom / originalHeight * _imageSize;
                    coordBuffer.AddRange(new[] { left, top, right, bottom });
                }
            }

            // Pad or truncate to 256 elements
            int numCoords = Math.Min(coordBuffer.Count, 256);
            for (int i = 0; i < 256; i++)
            {
                sparseTensor[0, 0, i] = i < numCoords ? coordBuffer[i] : 0f;
            }

            // 3. Process brush mask
            if (brushMask != null)
            {
                // Convert boolean mask to bitmap
                Bitmap maskBmp = new Bitmap(originalWidth, originalHeight);
                using (Graphics g = Graphics.FromImage(maskBmp))
                {
                    g.Clear(Color.Black);
                    for (int y = 0; y < originalHeight; y++)
                    {
                        for (int x = 0; x < originalWidth; x++)
                        {
                            if (brushMask[x, y])
                                maskBmp.SetPixel(x, y, Color.White);
                        }
                    }
                }

                // Resize to 64x64 and convert to tensor - fixed using statement
                Bitmap smallMask = null;
                try
                {
                    smallMask = ResizeImage(maskBmp, 64, 64);
                    BitmapData maskData = smallMask.LockBits(new Rectangle(0, 0, 64, 64),
                        ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

                    float[] denseArray = new float[64 * 64];
                    unsafe
                    {
                        byte* ptr = (byte*)maskData.Scan0;
                        for (int i = 0; i < 64 * 64; i++)
                        {
                            denseArray[i] = ptr[i * 3] > 128 ? 1f : 0f; // Use red channel
                        }
                    }
                    smallMask.UnlockBits(maskData);

                    // Manual copy to dense tensor (alternative to BlockCopy)
                    for (int i = 0; i < denseArray.Length; i++)
                    {
                        denseTensor[0, 0, i / 64, i % 64] = denseArray[i];
                    }
                }
                finally
                {
                    smallMask?.Dispose();
                }
            }

            // 4. Generate positional encoding
            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    float angleX = (float)(x / 64f * 2 * Math.PI);
                    float angleY = (float)(y / 64f * 2 * Math.PI);

                    for (int c = 0; c < 256; c++)
                    {
                        float freq = (float)Math.Pow(10000f, (c % 2 == 0 ? -c : -(c - 1)) / 256f);
                        float val = c % 2 == 0 ?
                            (float)Math.Sin(angleX * freq) :
                            (float)Math.Cos(angleY * freq);

                        densePeTensor[0, c, y, x] = val;
                    }
                }
            }

            return (sparseTensor, denseTensor, densePeTensor);
        }

        // RunMemoryAttention: flatten inputs to expected shapes, then call model.
        private (Tensor<float> flattenedPixFeat, Tensor<float> flattenedMemPosEnc) RunMemoryAttention(
    Tensor<float> currFeatFlat,
    Tensor<float> currPosFlat,
    Tensor<float> memFeatFlat,
    Tensor<float> memPosFlat,
    long numObjPtrTokens)
        {
            // 1. Create attention mask
            int seqLength = currFeatFlat.Dimensions[0];
            var attentionMask = new DenseTensor<float>(new[] { 1, seqLength, seqLength });
            for (int i = 0; i < seqLength; i++)
            {
                for (int j = 0; j < seqLength; j++)
                {
                    attentionMask[0, i, j] = (i >= j) ? 1f : 0f; // Causal mask
                }
            }

            // 2. Prepare inputs
            var inputs = new List<NamedOnnxValue>
    {
        NamedOnnxValue.CreateFromTensor("query", currFeatFlat),
        NamedOnnxValue.CreateFromTensor("key", memFeatFlat),
        NamedOnnxValue.CreateFromTensor("value", memFeatFlat),
        NamedOnnxValue.CreateFromTensor("pos_query", currPosFlat),
        NamedOnnxValue.CreateFromTensor("pos_key", memPosFlat),
        NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
        NamedOnnxValue.CreateFromTensor("num_objects",
            new DenseTensor<long>(new[] { numObjPtrTokens }, new[] { 1 }))
    };
            Tensor<float> attnOutput;
            Tensor<float> posOutput;

            // 3. Run attention
            using (var outputs = _memoryAttentionSession.Run(inputs))
            {// 4. Extract and reshape outputs
                attnOutput = outputs.First().AsTensor<float>();
                posOutput = outputs.Skip(1).First().AsTensor<float>();

            }

                


                return (attnOutput, posOutput);
        }

        private Tensor<float> FlattenForAttention(Tensor<float> input)
        {
            // Helper: Flatten tensor of shape [1,C,H,W] into shape [H*W, 1, C]
            int batch = input.Dimensions[0];
            int channels = input.Dimensions[1];
            int height = input.Dimensions[2];
            int width = input.Dimensions[3];
            int flatSize = height * width;
            float[] inputData = input.ToArray();
            float[] flatData = new float[flatSize * 1 * channels];

            for (int h = 0; h < height; h++)
            {
                for (int w = 0; w < width; w++)
                {
                    int flatIndex = h * width + w;
                    for (int c = 0; c < channels; c++)
                    {
                        int origIndex = ((0 * channels + c) * height + h) * width + w;
                        flatData[flatIndex * channels + c] = inputData[origIndex];
                    }
                }
            }
            return new DenseTensor<float>(flatData, new int[] { flatSize, 1, channels });
        }

        private Tensor<float> ReshapeFromAttention(Tensor<float> flat, int batch, int channels, int height, int width)
        {
            // Helper: Reshape flattened attention output back to [1, C, H, W]
            float[] flatData = flat.ToArray();
            float[] outData = new float[batch * channels * height * width];

            for (int i = 0; i < height * width; i++)
            {
                for (int c = 0; c < channels; c++)
                {
                    outData[c * height * width + i] = flatData[i * channels + c];
                }
            }
            return new DenseTensor<float>(outData, new int[] { batch, channels, height, width });
        }

        private Tensor<float> RunMaskDecoder(
    Tensor<float> imageOrMemoryFeats,
    (Tensor<float> Sparse, Tensor<float> Dense, Tensor<float> DensePe) promptEmbeds,
    Tensor<float> fallbackVisionFeatures,
    Tensor<float> fallbackVisionPosEnc)
        {
            // 1. Prepare high-res features
            Tensor<float> upFeatures = UpsampleSpatial(fallbackVisionFeatures, 2);
            Tensor<float> hrFeatures = ReduceChannels(upFeatures, 4);

            Tensor<float> upPosEnc = UpsampleSpatial(fallbackVisionPosEnc, 4);
            Tensor<float> hrPosEnc = ReduceChannels(upPosEnc, 8);

            // 2. Prepare inputs
            var inputs = new List<NamedOnnxValue>
    {
        NamedOnnxValue.CreateFromTensor("image_embeddings", imageOrMemoryFeats),
        NamedOnnxValue.CreateFromTensor("sparse_embeddings", promptEmbeds.Sparse),
        NamedOnnxValue.CreateFromTensor("dense_embeddings", promptEmbeds.Dense),
        NamedOnnxValue.CreateFromTensor("image_pe", promptEmbeds.DensePe),
        NamedOnnxValue.CreateFromTensor("hr_features1", hrFeatures),
        NamedOnnxValue.CreateFromTensor("hr_features2", hrPosEnc)
    };

            // 3. Run decoder - fixed using statement
            Tensor<float> maskLogits;
            using (var outputs = _maskDecoderSession.Run(inputs))
            {
                maskLogits = outputs.First().AsTensor<float>();
            }

            // 4. Apply sigmoid activation
            float[] maskData = maskLogits.ToArray();
            for (int i = 0; i < maskData.Length; i++)
            {
                maskData[i] = 1.0f / (1.0f + (float)Math.Exp(-maskData[i]));
            }

            return new DenseTensor<float>(maskData, maskLogits.Dimensions);
        }

        private Bitmap RunMlpOnMask(Bitmap maskBmp)
        {
            if (maskBmp == null) return null;

            int w = maskBmp.Width;
            int h = maskBmp.Height;

            // Downsample mask to MLP_SIZE x MLP_SIZE
            float[] dsArray = new float[MLP_SIZE * MLP_SIZE];
            BitmapData data = maskBmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                int stride = data.Stride;
                unsafe
                {
                    byte* scan0 = (byte*)data.Scan0;
                    float blockW = (float)w / MLP_SIZE;
                    float blockH = (float)h / MLP_SIZE;
                    for (int oy = 0; oy < MLP_SIZE; oy++)
                    {
                        for (int ox = 0; ox < MLP_SIZE; ox++)
                        {
                            int startX = (int)(ox * blockW);
                            int startY = (int)(oy * blockH);
                            int endX = (int)Math.Min((ox + 1) * blockW, w);
                            int endY = (int)Math.Min((oy + 1) * blockH, h);

                            double sum = 0;
                            int count = 0;
                            for (int yy = startY; yy < endY; yy++)
                            {
                                byte* rowPtr = scan0 + yy * stride;
                                for (int xx = startX; xx < endX; xx++)
                                {
                                    byte r = rowPtr[xx * 3 + 2];
                                    byte g = rowPtr[xx * 3 + 1];
                                    byte b = rowPtr[xx * 3];
                                    double lum = 0.299 * r + 0.587 * g + 0.114 * b;
                                    sum += lum;
                                    count++;
                                }
                            }
                            double avg = (count > 0) ? sum / count : 0.0;
                            float norm = (float)(avg / 255.0);
                            dsArray[oy * MLP_SIZE + ox] = norm;
                        }
                    }
                }
            }
            finally
            {
                maskBmp.UnlockBits(data);
            }

            // Create input tensor for MLP
            var inputTensor = new DenseTensor<float>(dsArray, new int[] { 1, MLP_SIZE * MLP_SIZE });
            string inputName = _mlpSession.InputMetadata.Keys.FirstOrDefault() ?? "x";
            Logger.Log($"[RunMlpOnMask] Using input '{inputName}' for MLP.");
            var mlpInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
            };

            // Run MLP
            Tensor<float> mlpOutput;
            using (var results = _mlpSession.Run(mlpInputs))
            {
                mlpOutput = results.First().AsTensor<float>();
            }

            float[] outArr = mlpOutput.ToArray();
            if (outArr.Length != MLP_SIZE * MLP_SIZE)
            {
                Logger.Log($"[RunMlpOnMask] Unexpected output size: {outArr.Length}");
                return maskBmp;
            }

            // Convert MLP result back to tiny 16x16 or MLP_SIZE x MLP_SIZE
            Bitmap mlpSmall = new Bitmap(MLP_SIZE, MLP_SIZE, PixelFormat.Format24bppRgb);
            for (int i = 0; i < outArr.Length; i++)
            {
                float val = Math.Max(0f, Math.Min(outArr[i], 1f));
                int c = (int)(val * 255);
                int xx = i % MLP_SIZE;
                int yy = i / MLP_SIZE;
                mlpSmall.SetPixel(xx, yy, Color.FromArgb(c, c, c));
            }

            // Upsample back to original
            Bitmap final = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(final))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(mlpSmall, 0, 0, w, h);
            }
            mlpSmall.Dispose();
            Logger.Log("[RunMlpOnMask] MLP post-processing completed.");
            maskBmp.Dispose();
            return final;
        }

        private float[] ImageToFloatArray(Bitmap original)
        {
            const int TARGET_SIZE = 1024;
            Bitmap resized = new Bitmap(TARGET_SIZE, TARGET_SIZE);
            using (Graphics g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(original, 0, 0, TARGET_SIZE, TARGET_SIZE);
            }

            float[] result = new float[1 * TARGET_SIZE * TARGET_SIZE]; // Single channel

            // CT-specific parameters for rock samples
            const float HU_MIN = -1000f;  // Air
            const float HU_MAX = 3071f;    // Dense mineral
            const float ROCK_WINDOW_CENTER = 500f;
            const float ROCK_WINDOW_WIDTH = 2000f;

            BitmapData data = resized.LockBits(new Rectangle(0, 0, TARGET_SIZE, TARGET_SIZE),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            unsafe
            {
                byte* ptr = (byte*)data.Scan0;
                int stride = data.Stride;

                for (int y = 0; y < TARGET_SIZE; y++)
                {
                    byte* row = ptr + y * stride;
                    for (int x = 0; x < TARGET_SIZE; x++)
                    {
                        // Decode 16-bit CT value from ARGB channels
                        short ctValue = (short)((row[x * 4 + 3] << 8) | row[x * 4 + 2]);

                        // Apply rock-focused windowing
                        float windowMin = ROCK_WINDOW_CENTER - ROCK_WINDOW_WIDTH / 2;
                        float windowMax = ROCK_WINDOW_CENTER + ROCK_WINDOW_WIDTH / 2;
                        float clamped = Clamp(ctValue, windowMin, windowMax);

                        // Normalize to [0,1] range
                        float normalized = (clamped - HU_MIN) / (HU_MAX - HU_MIN);
                        result[y * TARGET_SIZE + x] = Clamp(normalized, 0f, 1f);
                    }
                }
            }

            resized.UnlockBits(data);
            resized.Dispose();
            return result;
        }
        private static float Clamp(float value, float min, float max)
        {
            return Math.Max(min, Math.Min(value, max));
        }
        private Bitmap ResizeImage(Bitmap image, int width, int height)
        {
            Bitmap dest = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(dest))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(image, 0, 0, width, height);
            }
            return dest;
        }

        private Bitmap TensorToBitmap(Tensor<float> tensor, int width, int height)
        {
            // Verify that tensor has shape [1,1,H,W]
            if (tensor.Dimensions[0] != 1 || tensor.Dimensions[1] != 1)
            {
                throw new Exception("TensorToBitmap expects tensor shape [1,1,H,W].");
            }
            float[] data = tensor.ToArray();
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float val = data[y * width + x];
                    int gray = (int)(Math.Max(0, Math.Min(val, 1)) * 255);
                    bmp.SetPixel(x, y, Color.FromArgb(gray, gray, gray));
                }
            }
            return bmp;
        }

        private Tensor<float> ReduceChannels(Tensor<float> input, int factor)
        {
            int batch = input.Dimensions[0];
            int channels = input.Dimensions[1];
            int height = input.Dimensions[2];
            int width = input.Dimensions[3];

            if (channels % factor != 0)
                throw new ArgumentException("Channel dimension must be divisible by factor.");

            int targetChannels = channels / factor;
            float[] inputArray = input.ToArray();
            float[] outputArray = new float[batch * targetChannels * height * width];

            for (int b = 0; b < batch; b++)
            {
                for (int tc = 0; tc < targetChannels; tc++)
                {
                    for (int h = 0; h < height; h++)
                    {
                        for (int w = 0; w < width; w++)
                        {
                            float sum = 0;
                            for (int i = 0; i < factor; i++)
                            {
                                int c = tc * factor + i;
                                int inputIndex = (((b * channels + c) * height) + h) * width + w;
                                sum += inputArray[inputIndex];
                            }
                            int outputIndex = (((b * targetChannels + tc) * height) + h) * width + w;
                            outputArray[outputIndex] = sum / factor;
                        }
                    }
                }
            }
            return new DenseTensor<float>(outputArray, new int[] { batch, targetChannels, height, width });
        }

        private Tensor<float> UpsampleSpatial(Tensor<float> input, int factor)
        {
            int batch = input.Dimensions[0];
            int channels = input.Dimensions[1];
            int height = input.Dimensions[2];
            int width = input.Dimensions[3];

            int targetHeight = height * factor;
            int targetWidth = width * factor;

            float[] inputArray = input.ToArray();
            float[] outputArray = new float[batch * channels * targetHeight * targetWidth];

            for (int b = 0; b < batch; b++)
            {
                for (int c = 0; c < channels; c++)
                {
                    for (int outY = 0; outY < targetHeight; outY++)
                    {
                        int origY = outY / factor;
                        for (int outX = 0; outX < targetWidth; outX++)
                        {
                            int origX = outX / factor;
                            int inIndex = (((b * channels + c) * height) + origY) * width + origX;
                            int outIndex = (((b * channels + c) * targetHeight) + outY) * targetWidth + outX;
                            outputArray[outIndex] = inputArray[inIndex];
                        }
                    }
                }
            }

            return new DenseTensor<float>(outputArray, new int[] { batch, channels, targetHeight, targetWidth });
        }

        private Tensor<float> ExtractChannel(Tensor<float> tensor, int channelIndex)
        {
            // Assume tensor shape is [1, C, H, W]
            int batch = tensor.Dimensions[0];
            int channels = tensor.Dimensions[1];
            int height = tensor.Dimensions[2];
            int width = tensor.Dimensions[3];

            float[] input = tensor.ToArray();
            float[] output = new float[batch * 1 * height * width];

            for (int b = 0; b < batch; b++)
            {
                for (int h = 0; h < height; h++)
                {
                    for (int w = 0; w < width; w++)
                    {
                        int inputIndex = ((b * channels + channelIndex) * height + h) * width + w;
                        int outputIndex = ((b * 1 + 0) * height + h) * width + w;
                        output[outputIndex] = input[inputIndex];
                    }
                }
            }
            return new DenseTensor<float>(output, new int[] { batch, 1, height, width });
        }

        // ---------------------------------------------------------------------
        // POST-PROCESSING: Threshold + Hole Filling + (optional) Morph Closing
        // ---------------------------------------------------------------------
        private Bitmap PostProcessMask(Bitmap mask, int sliceIndex,
    float threshold = float.NaN, bool fillHoles = true, bool doMorphologicalClose = true)
        {
            // 1. Calculate adaptive threshold
            if (float.IsNaN(threshold))
            {
                float[] histogram = new float[256];
                BitmapData data = mask.LockBits(new Rectangle(0, 0, mask.Width, mask.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

                unsafe
                {
                    byte* ptr = (byte*)data.Scan0;
                    for (int i = 0; i < mask.Width * mask.Height; i++)
                    {
                        histogram[ptr[i * 3]]++;
                    }
                }
                mask.UnlockBits(data);

                float sum = histogram.Sum();
                float sumB = 0, wB = 0, maxVar = 0;
                threshold = 0f;

                for (int t = 0; t < 256; t++)
                {
                    wB += histogram[t] / sum;
                    float wF = 1 - wB;
                    if (wB == 0 || wF == 0) continue;

                    sumB += t * (histogram[t] / sum);
                    float mB = sumB / wB;
                    float mF = (sum - sumB) / wF;
                    float var = wB * wF * (mB - mF) * (mB - mF);

                    if (var > maxVar)
                    {
                        maxVar = var;
                        threshold = t / 255f;
                    }
                }
            }

            // 2. Apply threshold
            Bitmap bin = ThresholdMask(mask, threshold);

            // 3. 3D hole filling
            if (fillHoles)
            {
                Bitmap adjacentMask = null;
                if (_sliceMem.TryGetValue(sliceIndex - 1, out var prevMem))
                    adjacentMask = prevMem.Mask;

                bin = FillHoles3D(bin, adjacentMask);
            }

            // 4. Geological morphology
            if (doMorphologicalClose)
            {
                bin = MorphologicalClose3x3(bin, iterations: 2);
            }

            return bin;
        }

        private Bitmap ThresholdMask(Bitmap src, float threshold)
        {
            int w = src.Width, h = src.Height;
            Bitmap dest = new Bitmap(w, h, PixelFormat.Format24bppRgb);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Color c = src.GetPixel(x, y);
                    // Since it's grayscale, R=G=B
                    float val = c.R / 255f;
                    // Binarize
                    int bin = (val >= threshold) ? 255 : 0;
                    dest.SetPixel(x, y, Color.FromArgb(bin, bin, bin));
                }
            }
            return dest;
        }
        private Bitmap FillHoles3D(Bitmap current, Bitmap previous)
        {
            Bitmap result = new Bitmap(current.Width, current.Height);

            for (int y = 0; y < current.Height; y++)
            {
                for (int x = 0; x < current.Width; x++)
                {
                    bool currentVal = current.GetPixel(x, y).R > 128;
                    bool prevVal = (previous?.GetPixel(x, y).R ?? 0) > 128;

                    if (!currentVal && prevVal)
                    {
                        // Fill hole if present in previous slice
                        result.SetPixel(x, y, Color.White);
                    }
                    else
                    {
                        result.SetPixel(x, y, current.GetPixel(x, y));
                    }
                }
            }

            return result;
        }
        private Bitmap MorphologicalClose3x3(Bitmap input, int iterations)
        {
            Bitmap result = (Bitmap)input.Clone();
            for (int i = 0; i < iterations; i++)
            {
                result = Dilate3x3(result);
                result = Erode3x3(result);
            }
            return result;
        }
        private Bitmap Dilate3x3(Bitmap input)
        {
            Bitmap output = new Bitmap(input.Width, input.Height);
            for (int y = 0; y < input.Height; y++)
            {
                for (int x = 0; x < input.Width; x++)
                {
                    bool max = false;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = Clamp(x + dx, 0, input.Width - 1);
                            int ny = Clamp(y + dy, 0, input.Height - 1);
                            if (input.GetPixel(nx, ny).R > 128)
                            {
                                max = true;
                                break;
                            }
                        }
                        if (max) break;
                    }
                    output.SetPixel(x, y, max ? Color.White : Color.Black);
                }
            }
            return output;
        }
        private Bitmap Erode3x3(Bitmap input)
        {
            Bitmap output = new Bitmap(input.Width, input.Height);
            for (int y = 0; y < input.Height; y++)
            {
                for (int x = 0; x < input.Width; x++)
                {
                    bool min = true;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = Clamp(x + dx, 0, input.Width - 1);
                            int ny =Clamp(y + dy, 0, input.Height - 1);
                            if (input.GetPixel(nx, ny).R <= 128)
                            {
                                min = false;
                                break;
                            }
                        }
                        if (!min) break;
                    }
                    output.SetPixel(x, y, min ? Color.White : Color.Black);
                }
            }
            return output;
        }
        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(value, max));
        }
        private Bitmap FillHoles(Bitmap binMask)
        {
            // Flood-fill from edges to find "background" => everything else that is 0 inside is a hole.
            int w = binMask.Width;
            int h = binMask.Height;
            bool[,] visited = new bool[h, w];

            // Convert to array for quick access
            byte[,] pixels = new byte[h, w];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    pixels[y, x] = (binMask.GetPixel(x, y).R > 128) ? (byte)1 : (byte)0;
                }
            }

            // Flood fill from any edge pixel that is 0 to mark as external background
            Queue<(int, int)> queue = new Queue<(int, int)>();

            // Enqueue all boundary pixels that are 0
            for (int x = 0; x < w; x++)
            {
                if (pixels[0, x] == 0) queue.Enqueue((0, x));
                if (pixels[h - 1, x] == 0) queue.Enqueue((h - 1, x));
            }
            for (int y = 0; y < h; y++)
            {
                if (pixels[y, 0] == 0) queue.Enqueue((y, 0));
                if (pixels[y, w - 1] == 0) queue.Enqueue((y, w - 1));
            }

            // Directions for 4-connected (or 8-connected)
            int[] dx = { 1, -1, 0, 0 };
            int[] dy = { 0, 0, 1, -1 };

            // Mark external background as visited
            while (queue.Count > 0)
            {
                var (cy, cx) = queue.Dequeue();
                if (cy < 0 || cy >= h || cx < 0 || cx >= w) continue;
                if (visited[cy, cx]) continue;
                if (pixels[cy, cx] != 0) continue; // not background
                visited[cy, cx] = true;

                for (int i = 0; i < 4; i++)
                {
                    int ny = cy + dy[i];
                    int nx = cx + dx[i];
                    if (ny >= 0 && ny < h && nx >= 0 && nx < w)
                    {
                        if (!visited[ny, nx] && pixels[ny, nx] == 0)
                            queue.Enqueue((ny, nx));
                    }
                }
            }

            // Now, all unvisited 0 pixels are "holes" => fill them (set to 1)
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (pixels[y, x] == 0 && !visited[y, x])
                    {
                        // fill hole
                        pixels[y, x] = 1;
                    }
                }
            }

            // Create new bitmap from result
            Bitmap filled = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int val = (pixels[y, x] == 1) ? 255 : 0;
                    filled.SetPixel(x, y, Color.FromArgb(val, val, val));
                }
            }
            return filled;
        }

        private Bitmap MorphologicalClose(Bitmap binMask)
        {
            // Very simple morphological close with a 3x3 structuring element
            // (dilation followed by erosion)
            Bitmap dilated = MorphologicalDilation(binMask);
            Bitmap closed = MorphologicalErosion(dilated);
            dilated.Dispose();
            return closed;
        }

        private Bitmap MorphologicalDilation(Bitmap binMask)
        {
            int w = binMask.Width;
            int h = binMask.Height;
            Bitmap result = new Bitmap(w, h, PixelFormat.Format24bppRgb);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int maxVal = 0;
                    for (int yy = y - 1; yy <= y + 1; yy++)
                    {
                        for (int xx = x - 1; xx <= x + 1; xx++)
                        {
                            if (xx >= 0 && xx < w && yy >= 0 && yy < h)
                            {
                                int val = binMask.GetPixel(xx, yy).R; // 0 or 255
                                if (val > maxVal) maxVal = val;
                            }
                        }
                    }
                    result.SetPixel(x, y, Color.FromArgb(maxVal, maxVal, maxVal));
                }
            }
            return result;
        }

        private Bitmap MorphologicalErosion(Bitmap binMask)
        {
            int w = binMask.Width;
            int h = binMask.Height;
            Bitmap result = new Bitmap(w, h, PixelFormat.Format24bppRgb);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int minVal = 255;
                    for (int yy = y - 1; yy <= y + 1; yy++)
                    {
                        for (int xx = x - 1; xx <= x + 1; xx++)
                        {
                            if (xx >= 0 && xx < w && yy >= 0 && yy < h)
                            {
                                int val = binMask.GetPixel(xx, yy).R;
                                if (val < minVal) minVal = val;
                            }
                        }
                    }
                    result.SetPixel(x, y, Color.FromArgb(minVal, minVal, minVal));
                }
            }
            return result;
        }

    }
}
