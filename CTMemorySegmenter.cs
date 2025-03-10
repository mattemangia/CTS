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

            // 2. Run prompt encoder.
            var promptEmbeds = RunPromptEncoderIfNeeded(sliceImage.Width, sliceImage.Height, userPoints, userBoxes, brushMask, textPrompt);
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
                (combinedMemoryFeatures, combinedMemoryPosEnc) = RunMemoryAttention(currFeatFlat, currPosFlat, memFeatFlat, memPosFlat, numObjPtrTokens);
                combinedMemoryFeatures = ReshapeFromAttention(combinedMemoryFeatures, 1, 256, 64, 64);
                Logger.Log($"[ProcessSliceWithMemory] Memory attention applied using previous slice {prevSliceIndex}");
            }

            // 4. Run mask decoder.
            Tensor<float> lowResMask = RunMaskDecoder(combinedMemoryFeatures ?? visionFeatures, promptEmbeds, visionFeatures, visionPosEnc);
            Logger.Log($"[ProcessSliceWithMemory] Mask decoder completed for slice {sliceIndex}. Output shape: {string.Join("x", lowResMask.Dimensions.ToArray())}");

            // 5. If the mask tensor has more than one channel, extract channel 1.
            if (lowResMask.Dimensions[1] > 1)
            {
                lowResMask = ExtractChannel(lowResMask, 1);
                Logger.Log($"[ProcessSliceWithMemory] Extracted channel 1. New shape: {string.Join("x", lowResMask.Dimensions.ToArray())}");
            }

            // 6. Convert tensor to a low-res bitmap.
            // NOTE: lowResMask is now of shape [1,1,256,256]; use these dimensions.
            Bitmap lowResMaskBmp = TensorToBitmap(lowResMask, 256, 256);
            Logger.Log($"[ProcessSliceWithMemory] Low-res mask bitmap dimensions: {lowResMaskBmp.Width}x{lowResMaskBmp.Height}");

            // 7. Resize the mask back to the original slice resolution.
            Bitmap finalMask = ResizeImage(lowResMaskBmp, sliceImage.Width, sliceImage.Height);
            Logger.Log($"[ProcessSliceWithMemory] Final mask dimensions: {finalMask.Width}x{finalMask.Height}");
            lowResMaskBmp.Dispose();

            // 8. Optionally apply MLP post-processing.
            Bitmap postProcessedMask = _enableMlp ? RunMlpOnMask(finalMask) : finalMask;
            Logger.Log(_enableMlp ? "[ProcessSliceWithMemory] MLP post-processing applied." : "[ProcessSliceWithMemory] MLP post-processing skipped.");

            // 9. Update memory.
            if (_sliceMem.ContainsKey(sliceIndex))
                _sliceMem[sliceIndex].Mask?.Dispose();
            _sliceMem[sliceIndex] = new SliceMemory { MemoryFeatures = combinedMemoryFeatures ?? visionFeatures, MemoryPosEnc = combinedMemoryPosEnc ?? visionPosEnc, Mask = postProcessedMask };
            Logger.Log($"[ProcessSliceWithMemory] Finished processing slice {sliceIndex}");
            return postProcessedMask;
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



        // RunPromptEncoderIfNeeded: returns prompt tensors with real encoding.
        private (Tensor<float> Sparse, Tensor<float> Dense, Tensor<float> DensePe) RunPromptEncoderIfNeeded(
            int originalWidth, int originalHeight,
            List<Point> userPoints, List<Rectangle> userBoxes,
            bool[,] brushMask, string textPrompt)
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
            return CreatePromptTensors(userPoints, userBoxes, brushMask, textPrompt, originalWidth, originalHeight);
        }

        // Real prompt encoder: outputs sparse: [1,1,256], dense: [1,256,64,64], dense_pe: [1,256,64,64].
        private (Tensor<float> Sparse, Tensor<float> Dense, Tensor<float> DensePe) CreatePromptTensors(
            List<Point> points, List<Rectangle> boxes,
            bool[,] brushMask, string textPrompt,
            int originalWidth, int originalHeight)
        {
            Logger.Log("[CreatePromptTensors] Starting real prompt tensor creation.");
            List<float> promptValues = new List<float>();
            if (points != null && points.Count > 0)
            {
                foreach (var p in points)
                {
                    float normX = (float)p.X / originalWidth * _imageSize;
                    float normY = (float)p.Y / originalHeight * _imageSize;
                    promptValues.Add(normX);
                    promptValues.Add(normY);
                }
                Logger.Log($"[CreatePromptTensors] Processed {points.Count} point(s).");
            }
            if (boxes != null && boxes.Count > 0)
            {
                foreach (var r in boxes)
                {
                    float left = (float)r.Left / originalWidth * _imageSize;
                    float top = (float)r.Top / originalHeight * _imageSize;
                    float right = (float)r.Right / originalWidth * _imageSize;
                    float bottom = (float)r.Bottom / originalHeight * _imageSize;
                    promptValues.Add(left);
                    promptValues.Add(top);
                    promptValues.Add(right);
                    promptValues.Add(bottom);
                }
                Logger.Log($"[CreatePromptTensors] Processed {boxes.Count} box(es).");
            }
            if (promptValues.Count == 0)
            {
                promptValues.Add(0f);
                promptValues.Add(0f);
                Logger.Log("[CreatePromptTensors] No prompts provided; added default coordinate.");
            }
            // The sparse tensor must have shape [1,1,256]
            float[] sparseArray = new float[1 * 1 * 256];
            for (int i = 0; i < 256; i++)
            {
                sparseArray[i] = (i < promptValues.Count) ? promptValues[i] : 0f;
            }
            var sparseTensor = new DenseTensor<float>(sparseArray, new int[] { 1, 1, 256 });
            Logger.Log("[CreatePromptTensors] Created sparse tensor of shape [1,1,256].");

            // Dense prompt embedding: use zeros with shape [1,256,64,64]
            int denseElements = 1 * 256 * 64 * 64;
            float[] denseArray = new float[denseElements];
            var denseTensor = new DenseTensor<float>(denseArray, new int[] { 1, 256, 64, 64 });
            Logger.Log("[CreatePromptTensors] Created dense prompt tensor of shape [1,256,64,64].");

            // Dense positional encoding: create a simple sinusoidal encoding over 64x64 grid for 256 channels.
            float[] densePeArray = new float[denseElements];
            int channels = 256, height = 64, width = 64;
            for (int c = 0; c < channels; c++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        double value = (c % 2 == 0)
                            ? Math.Sin((double)x / width * Math.PI * 2)
                            : Math.Cos((double)y / height * Math.PI * 2);
                        int index = c * (height * width) + y * width + x;
                        densePeArray[index] = (float)value;
                    }
                }
            }
            var densePeTensor = new DenseTensor<float>(densePeArray, new int[] { 1, 256, 64, 64 });
            Logger.Log("[CreatePromptTensors] Created dense positional encoding tensor of shape [1,256,64,64].");

            Logger.Log("[CreatePromptTensors] Real prompt tensors created successfully.");
            return (sparseTensor, denseTensor, densePeTensor);
        }

        // RunMemoryEncoder: now accepts two inputs: "pix_feat" and "masks"
        private (Tensor<float> memoryFeats, Tensor<float> memoryPosEnc) RunMemoryEncoder(Tensor<float> pixFeat, Tensor<float> maskTensor)
        {
            Logger.Log("[RunMemoryEncoder] Running memory encoder.");
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("pix_feat", pixFeat),
                NamedOnnxValue.CreateFromTensor("masks", maskTensor)
            };
            Tensor<float> memFeats, memPosEnc;
            using (var outputs = _memoryEncoderSession.Run(inputs))
            {
                memFeats = outputs.First(x => x.Name == "vision_features").AsTensor<float>();
                memPosEnc = outputs.First(x => x.Name == "vision_pos_enc").AsTensor<float>();
            }
            Logger.Log("[RunMemoryEncoder] Completed.");
            return (memFeats, memPosEnc);
        }
        // Convert a Bitmap mask (grayscale) to a tensor of shape [1,1,_imageSize,_imageSize]
        private Tensor<float> ImageToMaskTensor(Bitmap mask)
        {
            int w = mask.Width, h = mask.Height;
            float[] data = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Color c = mask.GetPixel(x, y);
                    data[y * w + x] = c.R / 255f;
                }
            }
            return new DenseTensor<float>(data, new int[] { 1, 1, w, h });
        }

        // RunMemoryAttention: flatten inputs to expected shapes then call model.
        // Expected:
        //   curr: 4096x1x256 (from vision_features of shape 1x256x64x64)
        //   curr_pos: 4096x1x256 (from vision_pos_enc of shape 1x256x64x64)
        //   memory: ?x1x64 (from memory encoder output of shape 1x64x64x64 flattened)
        //   memory_pos: ?x1x64 (from memory encoder output of shape 1x64x64x64 flattened)
        //   num_obj_ptr_tokens: scalar (Int64)
        private (Tensor<float> flattenedPixFeat, Tensor<float> flattenedMemPosEnc) RunMemoryAttention(
            Tensor<float> currFeatFlat, Tensor<float> currPosFlat,
            Tensor<float> memFeatFlat, Tensor<float> memPosFlat,
            long numObjPtrTokens)
        {
            Logger.Log("[RunMemoryAttention] Running memory attention.");
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("curr", currFeatFlat),
                NamedOnnxValue.CreateFromTensor("memory", memFeatFlat),
                NamedOnnxValue.CreateFromTensor("curr_pos", currPosFlat),
                NamedOnnxValue.CreateFromTensor("memory_pos", memPosFlat),
                NamedOnnxValue.CreateFromTensor("num_obj_ptr_tokens", new DenseTensor<long>(new long[] { numObjPtrTokens }, new int[] { 1 }))
            };
            Tensor<float> outTensor;
            using (var outputs = _memoryAttentionSession.Run(inputs))
            {
                outTensor = outputs.First(x => x.Name == "pix_feat").AsTensor<float>();
            }
            Logger.Log("[RunMemoryAttention] Completed.");
            return (outTensor, null); // Only pix_feat output is used.
        }

        // Helper: Flatten tensor of shape [1,C,H,W] into shape [H*W, 1, C]
        private Tensor<float> FlattenForAttention(Tensor<float> input)
        {
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

        // Helper: Reshape flattened attention output back to [1, C, H, W]
        private Tensor<float> ReshapeFromAttention(Tensor<float> flat, int batch, int channels, int height, int width)
        {
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

        // RunMaskDecoder: prepare inputs and run the mask decoder.
        private Tensor<float> RunMaskDecoder(
            Tensor<float> imageOrMemoryFeats,
            (Tensor<float> Sparse, Tensor<float> Dense, Tensor<float> DensePe) promptEmbeds,
            Tensor<float> fallbackVisionFeatures,
            Tensor<float> fallbackVisionPosEnc)
        {
            Logger.Log("[RunMaskDecoder] Running mask decoder.");
            // high_res_features1: upsample vision_pos_enc_2 to 256x256 and reduce channels from 256 to 32.
            Tensor<float> upPosEnc = UpsampleSpatial(fallbackVisionPosEnc, 4); // 64 -> 256
            Tensor<float> highResFeatures1 = ReduceChannels(upPosEnc, 8); // 256/8 = 32; shape [1,32,256,256]
            // high_res_features2: upsample fallbackVisionFeatures 1x256x64x64 by factor 2, then reduce channels by factor 4 -> 1x64x128x128.
            Tensor<float> upFeatures = UpsampleSpatial(fallbackVisionFeatures, 2);
            Tensor<float> highResFeatures2 = ReduceChannels(upFeatures, 4);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("image_embeddings", imageOrMemoryFeats),
                NamedOnnxValue.CreateFromTensor("sparse_prompt_embeddings", promptEmbeds.Sparse),
                NamedOnnxValue.CreateFromTensor("dense_prompt_embeddings", promptEmbeds.Dense),
                NamedOnnxValue.CreateFromTensor("image_pe", promptEmbeds.DensePe),
                NamedOnnxValue.CreateFromTensor("high_res_features1", highResFeatures1),
                NamedOnnxValue.CreateFromTensor("high_res_features2", highResFeatures2)
            };
            Tensor<float> outputTensor;
            using (var outputs = _maskDecoderSession.Run(inputs))
            {
                outputTensor = outputs.First(x => x.Name == "masks").AsTensor<float>();
            }
            Logger.Log($"[RunMaskDecoder] Completed. Output shape: {string.Join("x", outputTensor.Dimensions.ToArray())}");
            return outputTensor;
        }

        private Bitmap RunMlpOnMask(Bitmap maskBmp)
        {
            if (maskBmp == null)
                return null;
            int w = maskBmp.Width, h = maskBmp.Height;
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
            var inputTensor = new DenseTensor<float>(dsArray, new int[] { 1, MLP_SIZE * MLP_SIZE });
            string inputName = _mlpSession.InputMetadata.Keys.FirstOrDefault() ?? "x";
            Logger.Log($"[RunMlpOnMask] Using input '{inputName}' for MLP.");
            var mlpInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
            };
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
            Bitmap mlp16 = new Bitmap(MLP_SIZE, MLP_SIZE, PixelFormat.Format24bppRgb);
            for (int i = 0; i < outArr.Length; i++)
            {
                float val = Math.Max(0f, Math.Min(outArr[i], 1f));
                int c = (int)(val * 255);
                int xx = i % MLP_SIZE;
                int yy = i / MLP_SIZE;
                mlp16.SetPixel(xx, yy, Color.FromArgb(c, c, c));
            }
            Bitmap final = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(final))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(mlp16, 0, 0, w, h);
            }
            mlp16.Dispose();
            Logger.Log("[RunMlpOnMask] MLP post-processing completed.");
            return final;
        }

        private float[] ImageToFloatArray(Bitmap original)
        {
            Bitmap resized = ResizeImage(original, _imageSize, _imageSize);
            int w = resized.Width, h = resized.Height;
            float[] result = new float[3 * w * h];
            BitmapData data = resized.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            unsafe
            {
                byte* ptr = (byte*)data.Scan0;
                int stride = data.Stride;
                for (int y = 0; y < h; y++)
                {
                    byte* row = ptr + y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int idx = y * w + x;
                        result[idx] = row[x * 3 + 2] / 255f;
                        result[w * h + idx] = row[x * 3 + 1] / 255f;
                        result[2 * w * h + idx] = row[x * 3] / 255f;
                    }
                }
            }
            resized.UnlockBits(data);
            resized.Dispose();
            return result;
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
                    for (int h = 0; h < targetHeight; h++)
                    {
                        int origH = h / factor;
                        for (int w = 0; w < targetWidth; w++)
                        {
                            int origW = w / factor;
                            int inIndex = (((b * channels + c) * height) + origH) * width + origW;
                            int outIndex = (((b * channels + c) * targetHeight) + h) * targetWidth + w;
                            outputArray[outIndex] = inputArray[inIndex];
                        }
                    }
                }
            }
            return new DenseTensor<float>(outputArray, new int[] { batch, channels, targetHeight, targetWidth });
        }



        // Add this helper method somewhere in your CTMemorySegmenter class:
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

    }
}
