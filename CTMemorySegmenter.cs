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
    /// Demonstrates CT slice segmentation via a SAM2-like pipeline with
    /// multi-frame memory & attention. Possibilities:
    ///  - Provide user prompts for a middle slice (points, boxes, brush, text).
    ///  - Automatically propagate segmentation backward & forward in the volume.
    ///  - Use an MLP for final post-processing.
    ///
    /// Key methods:
    ///  - PropagateSegmentationInVolume(...)
    ///  - ResetState()
    ///  - ProcessSliceWithMemory(...)
    ///  
    /// This version stores memory as Tensor<float> (rather than DenseTensor<float>) 
    /// to avoid implicit conversion errors.
    /// </summary>
    public class CTMemorySegmenter : IDisposable
    {
        // -------------------------------------------------------------------
        // ONNX runtime sessions for different model parts
        // -------------------------------------------------------------------
        private InferenceSession _imageEncoderSession;
        private InferenceSession _promptEncoderSession;
        private InferenceSession _maskDecoderSession;
        private InferenceSession _memoryEncoderSession;
        private InferenceSession _memoryAttentionSession;
        private InferenceSession _mlpSession;

        // -------------------------------------------------------------------
        // Basic config
        // -------------------------------------------------------------------
        private int _imageSize;            // e.g. 1024 for SAM2
        private bool _canUseTextPrompts;   // if model handles text

        // -------------------------------------------------------------------
        // Internal memory store per slice
        // We store:
        //   - memory embeddings (maskMemFeatures) after each slice
        //   - memory positional enc (maskMemPosEnc) if needed
        //   - final mask from each slice
        // For a single object. Can expand to multiple objects as needed.
        // -------------------------------------------------------------------
        private class SliceMemory
        {
            public Tensor<float> MemoryFeatures; // previously encoded mask embeddings
            public Tensor<float> MemoryPosEnc;   // positional enc, if needed
            public Bitmap Mask;                  // final segmentation result for that slice
        }
        private Dictionary<int, SliceMemory> _sliceMem;

        /// <summary>
        /// Constructor: Provide each model’s ONNX path, plus optional config.
        /// </summary>
        /// <param name="imageEncoderPath">ONNX for image encoder</param>
        /// <param name="promptEncoderPath">ONNX for prompt encoder</param>
        /// <param name="maskDecoderPath">ONNX for mask decoder</param>
        /// <param name="memoryEncoderPath">ONNX for memory encoder</param>
        /// <param name="memoryAttentionPath">ONNX for memory attention step</param>
        /// <param name="mlpPath">ONNX for MLP post-processing</param>
        /// <param name="imageInputSize">SAM2 typically uses 1024</param>
        /// <param name="canUseTextPrompts">Set true if can pass text to the prompt model</param>
        public CTMemorySegmenter(
            string imageEncoderPath,
            string promptEncoderPath,
            string maskDecoderPath,
            string memoryEncoderPath,
            string memoryAttentionPath,
            string mlpPath,
            int imageInputSize = 1024,
            bool canUseTextPrompts = false)
        {
            _imageSize = imageInputSize;
            _canUseTextPrompts = canUseTextPrompts;

            // If GPU is wanted, might do:
            // var options = new SessionOptions();
            // options.AppendExecutionProvider_DML();
            // else, just CPU:

           
            var options = new SessionOptions();
            try { options.AppendExecutionProvider_DML(); }
            catch { options.AppendExecutionProvider_CPU();
                Logger.Log("[CTMemorySegmenter] DirectML not available. Falling back to CPU Execution Provider");
            }

            _imageEncoderSession = new InferenceSession(imageEncoderPath, options);
            _promptEncoderSession = new InferenceSession(promptEncoderPath, options);
            _maskDecoderSession = new InferenceSession(maskDecoderPath, options);
            _memoryEncoderSession = new InferenceSession(memoryEncoderPath, options);
            _memoryAttentionSession = new InferenceSession(memoryAttentionPath, options);
            _mlpSession = new InferenceSession(mlpPath, options);

            _sliceMem = new Dictionary<int, SliceMemory>();
        }

        /// <summary>
        /// Resets any stored memory or results across slices.
        /// </summary>
        public void ResetState()
        {
            // Clear the dictionary that held memory embeddings and final masks.
            foreach (var kvp in _sliceMem)
            {
                // If needed, dispose bitmaps
                if (kvp.Value.Mask != null)
                {
                    kvp.Value.Mask.Dispose();
                    kvp.Value.Mask = null;
                }
            }
            _sliceMem.Clear();
        }

        // -------------------------------------------------------------------
        // MAIN method: Propagate segmentation across a volume
        //    1) user picks a middle slice, provides prompts
        //    2) we do that slice first
        //    3) propagate backward, then forward
        // -------------------------------------------------------------------
        /// <summary>
        /// The user provides a set of CT slices (Bitmaps). They pick
        /// one slice index with user prompts. We do that slice, then
        /// propagate backward and forward in index order, storing
        /// final masks for each slice. We return them as an array.
        /// </summary>
        public Bitmap[] PropagateSegmentationInVolume(
            Bitmap[] slices,
            int middleSliceIndex,
            List<Point> userPoints = null,
            List<Rectangle> userBoxes = null,
            bool[,] userBrushMask = null,
            string textPrompt = null)
        {
            if (slices == null || slices.Length == 0)
                throw new ArgumentException("No slices provided.");
            if (middleSliceIndex < 0 || middleSliceIndex >= slices.Length)
                throw new ArgumentOutOfRangeException(nameof(middleSliceIndex));

            // 1) Segment the middle slice with the user prompts
            int numSlices = slices.Length;
            Bitmap[] results = new Bitmap[numSlices];

            var middleMask = ProcessSliceWithMemory(
                sliceIndex: middleSliceIndex,
                sliceImage: slices[middleSliceIndex],
                userPoints: userPoints,
                userBoxes: userBoxes,
                brushMask: userBrushMask,
                textPrompt: textPrompt,
                prevSliceIndex: null,   // no memory from previous
                nextSliceIndex: null    // no memory from next
            );
            results[middleSliceIndex] = middleMask;

            // 2) go backward from [middleSliceIndex-1 .. 0]
            for (int i = middleSliceIndex - 1; i >= 0; i--)
            {
                Bitmap mask = ProcessSliceWithMemory(
                    sliceIndex: i,
                    sliceImage: slices[i],
                    userPoints: null,         // no new user points for these
                    userBoxes: null,
                    brushMask: null,
                    textPrompt: null,
                    prevSliceIndex: i + 1,      // memory from slice i+1
                    nextSliceIndex: null
                );
                results[i] = mask;
            }

            // 3) go forward from [middleSliceIndex+1 .. last]
            for (int i = middleSliceIndex + 1; i < numSlices; i++)
            {
                Bitmap mask = ProcessSliceWithMemory(
                    sliceIndex: i,
                    sliceImage: slices[i],
                    userPoints: null,
                    userBoxes: null,
                    brushMask: null,
                    textPrompt: null,
                    prevSliceIndex: i - 1,  // memory from slice i-1
                    nextSliceIndex: null
                );
                results[i] = mask;
            }

            return results;
        }

        // -------------------------------------------------------------------
        // Core function: process a single slice using multi-frame memory
        //    1) run image encoder
        //    2) run prompt encoder (if we have new user prompts)
        //    3) run memory attention (if we have memory from prev or next)
        //    4) run mask decoder => get new mask
        //    5) re-encode that mask to update memory
        //    6) run optional MLP post-processing
        // -------------------------------------------------------------------
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
            // 1) image encoder
            //    Convert sliceImage => float => run session
            //    Suppose the outputs are "vision_features" and "vision_pos_enc"
            var (visionFeatures, visionPosEnc) = RunImageEncoder(sliceImage);

            // 2) prompt encoder if the user provided new points, boxes, etc.
            //    If we have no new prompts, we can feed empty or skip it.
            //    We'll produce "sparse_embeddings", "dense_embeddings", "dense_pe"
            var promptEmbeds = RunPromptEncoderIfNeeded(
                sliceImage.Width,
                sliceImage.Height,
                userPoints,
                userBoxes,
                brushMask,
                textPrompt
            );

            // 3) memory attention
            //    If we have memory from a previous slice or next slice,
            //    feed it into the memory attention model (with the new image).
            //    For demonstration, we only check previous slice memory. 
            Tensor<float> combinedMemoryFeatures = null;
            Tensor<float> combinedMemoryPosEnc = null;
            // If we have memory from prev slice
            if (prevSliceIndex.HasValue && _sliceMem.ContainsKey(prevSliceIndex.Value))
            {
                var prevMem = _sliceMem[prevSliceIndex.Value];
                // run memory attention here:
                (combinedMemoryFeatures, combinedMemoryPosEnc) = RunMemoryAttention(
                    currentVisionFeatures: visionFeatures,
                    currentVisionPosEnc: visionPosEnc,
                    prevMemoryFeatures: prevMem.MemoryFeatures,
                    prevMemoryPosEnc: prevMem.MemoryPosEnc
                );
            }
            // else if we have memory from next slice (rare in forward pass?), do similarly
            // We'll skip that. Can combine both if wanted.

            // If no memory from anywhere, we can just treat combinedMemoryFeatures = null => no memory.

            // 4) run mask decoder => we pass
            //      - image embeddings   (visionFeatures or combinedMemoryFeatures, depending on design)
            //      - prompt embeddings (from promptEmbeds)
            //      - pos enc
            //    Then we get a low-res mask
            var lowResMask = RunMaskDecoder(
                (combinedMemoryFeatures ?? visionFeatures),
                promptEmbeds,
                fallbackVisionFeatures: visionFeatures,
                fallbackVisionPosEnc: visionPosEnc
            );

            // 5) re-encode the new mask => memory
            //    so that future slices can see it
            var (maskMemFeatures, maskMemPosEnc) = RunMemoryEncoder(
                sliceImage,
                lowResMask
            );

            // store in dictionary
            if (_sliceMem.ContainsKey(sliceIndex) && _sliceMem[sliceIndex].Mask != null)
            {
                // dispose the old mask if any
                _sliceMem[sliceIndex].Mask.Dispose();
            }
            _sliceMem[sliceIndex] = new SliceMemory
            {
                MemoryFeatures = maskMemFeatures,
                MemoryPosEnc = maskMemPosEnc,
                Mask = null // we'll fill it after MLP below
            };

            // 6) upsample that mask to the original slice resolution,
            //    then optionally run MLP for final post-processing.
            Bitmap lowResMaskBmp = TensorToBitmap(lowResMask, _imageSize, _imageSize);
            Bitmap finalMask = ResizeImage(lowResMaskBmp, sliceImage.Width, sliceImage.Height);
            lowResMaskBmp.Dispose();

            Bitmap postProcessedMask = RunMlpOnMask(finalMask);
            finalMask.Dispose();
            // store final in dictionary
            _sliceMem[sliceIndex].Mask = postProcessedMask;

            return postProcessedMask;
        }

        // -------------------------------------------------------------------
        // Steps
        // -------------------------------------------------------------------
        private (Tensor<float> visionFeatures, Tensor<float> visionPosEnc) RunImageEncoder(Bitmap sliceImage)
        {
            // Convert to float array [1,3,H,W]
            float[] floatData = ImageToFloatArray(sliceImage);
            var imageTensor = new DenseTensor<float>(floatData, new int[] { 1, 3, _imageSize, _imageSize });
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_image", imageTensor)
            };

            Tensor<float> features;
            Tensor<float> posEnc;
            using (var outputs = _imageEncoderSession.Run(inputs))
            {
                features = outputs.First(x => x.Name == "vision_features").AsTensor<float>();
                posEnc = outputs.First(x => x.Name == "vision_pos_enc").AsTensor<float>();
            }
            return (features, posEnc);
        }

        private (Tensor<float> Sparse, Tensor<float> Dense, Tensor<float> DensePe)
        RunPromptEncoderIfNeeded(
            int originalWidth,
            int originalHeight,
            List<Point> userPoints,
            List<Rectangle> userBoxes,
            bool[,] brushMask,
            string textPrompt)
        {
            bool hasPrompts = ((userPoints != null && userPoints.Count > 0)
                             || (userBoxes != null && userBoxes.Count > 0)
                             || (brushMask != null)
                             || (!string.IsNullOrEmpty(textPrompt) && _canUseTextPrompts));

            if (!hasPrompts)
            {
                // return empty or dummy embeddings
                // shape depends on model
                // For demonstration, we create shape [1,256], [1,256,64,64], etc.
                var sparseEmpty = new DenseTensor<float>(new int[] { 1, 256 });
                var denseEmpty = new DenseTensor<float>(new int[] { 1, 256, 64, 64 });
                return (sparseEmpty, denseEmpty, densePe: new DenseTensor<float>(new int[] { 1, 256, 64, 64 }));
            }

            // Build coords, labels, brush mask, text embedding, etc.
            var promptTensors = CreatePromptTensors(
                userPoints, userBoxes, brushMask,
                textPrompt,
                originalWidth, originalHeight
            );

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("coords", promptTensors.Coords),
                NamedOnnxValue.CreateFromTensor("labels", promptTensors.Labels),
                NamedOnnxValue.CreateFromTensor("masks", promptTensors.MaskPrompt),
                NamedOnnxValue.CreateFromTensor("masks_enable", promptTensors.MaskEnable)
            };
            if (_canUseTextPrompts && !string.IsNullOrEmpty(textPrompt))
            {
                float[] txtEmb = EncodeTextToFloatArray(textPrompt);
                var txtTensor = new DenseTensor<float>(txtEmb, new int[] { 1, txtEmb.Length });
                inputs.Add(NamedOnnxValue.CreateFromTensor("text_embed", txtTensor));
            }

            Tensor<float> sparseEmb;
            Tensor<float> denseEmb;
            Tensor<float> densePe;
            using (var outputs = _promptEncoderSession.Run(inputs))
            {
                sparseEmb = outputs.First(x => x.Name == "sparse_embeddings").AsTensor<float>();
                denseEmb = outputs.First(x => x.Name == "dense_embeddings").AsTensor<float>();
                densePe = outputs.First(x => x.Name == "dense_pe").AsTensor<float>();
            }
            return (sparseEmb, denseEmb, densePe);
        }

        private (Tensor<float>, Tensor<float>) RunMemoryAttention(
            Tensor<float> currentVisionFeatures,
            Tensor<float> currentVisionPosEnc,
            Tensor<float> prevMemoryFeatures,
            Tensor<float> prevMemoryPosEnc)
        {
            // shape depends on ONNX
            // We'll assume something like:
            //   inputs: "current_feats", "current_pos", "prev_feats", "prev_pos"
            //   outputs: "combined_feats", "combined_pos"
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("current_feats", currentVisionFeatures),
                NamedOnnxValue.CreateFromTensor("current_pos",   currentVisionPosEnc),
                NamedOnnxValue.CreateFromTensor("prev_feats",    prevMemoryFeatures),
                NamedOnnxValue.CreateFromTensor("prev_pos",      prevMemoryPosEnc)
            };
            Tensor<float> combinedFeats;
            Tensor<float> combinedPos;
            using (var results = _memoryAttentionSession.Run(inputs))
            {
                combinedFeats = results.First(x => x.Name == "combined_feats").AsTensor<float>();
                combinedPos = results.First(x => x.Name == "combined_pos").AsTensor<float>();
            }
            return (combinedFeats, combinedPos);
        }

        private Tensor<float> RunMaskDecoder(
            Tensor<float> imageOrMemoryFeats,
            (Tensor<float> Sparse, Tensor<float> Dense, Tensor<float> DensePe) promptEmbeds,
            Tensor<float> fallbackVisionFeatures,
            Tensor<float> fallbackVisionPosEnc)
        {
            // build inputs
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("image_embeddings", imageOrMemoryFeats),
                NamedOnnxValue.CreateFromTensor("sparse_prompt_embeddings", promptEmbeds.Sparse),
                NamedOnnxValue.CreateFromTensor("dense_prompt_embeddings",  promptEmbeds.Dense),
                NamedOnnxValue.CreateFromTensor("image_pe",                 promptEmbeds.DensePe),
                // Some SAM2 models also want "high_res_features1" & "high_res_features2"
                NamedOnnxValue.CreateFromTensor("high_res_features1", fallbackVisionFeatures),
                NamedOnnxValue.CreateFromTensor("high_res_features2", fallbackVisionFeatures)
                // or we might pass fallbackVisionPosEnc if needed...
            };

            Tensor<float> lowResMask;
            using (var outputs = _maskDecoderSession.Run(inputs))
            {
                lowResMask = outputs.First(x => x.Name == "low_res_masks").AsTensor<float>();
            }
            return lowResMask;
        }

        private (Tensor<float> memoryFeats, Tensor<float> memoryPosEnc) RunMemoryEncoder(
            Bitmap sliceImage,
            Tensor<float> lowResMask)
        {
            // The memory encoder typically wants a [1,1,H,W] mask, etc.
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_mask", lowResMask)
            };

            Tensor<float> memFeats;
            Tensor<float> memPosEnc;
            using (var outputs = _memoryEncoderSession.Run(inputs))
            {
                memFeats = outputs.First(x => x.Name == "maskmem_features").AsTensor<float>();
                memPosEnc = outputs.First(x => x.Name == "maskmem_pos_enc").AsTensor<float>();
            }
            return (memFeats, memPosEnc);
        }

        /// <summary>
        /// Runs an MLP for final post-processing of the mask. If no MLP,
        /// Could skip this step or return the original mask.
        /// </summary>
        private Bitmap RunMlpOnMask(Bitmap maskBmp)
        {
            if (maskBmp == null) return null;

            int w = maskBmp.Width;
            int h = maskBmp.Height;
            float[] maskData = new float[w * h];

            // read mask => float
            BitmapData data = maskBmp.LockBits(new Rectangle(0, 0, w, h),
                ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            unsafe
            {
                byte* ptr = (byte*)data.Scan0;
                int stride = data.Stride;
                for (int y = 0; y < h; y++)
                {
                    byte* row = ptr + (y * stride);
                    for (int x = 0; x < w; x++)
                    {
                        byte b = row[x * 3];
                        maskData[y * w + x] = b / 255f;
                    }
                }
            }
            maskBmp.UnlockBits(data);

            var inputTensor = new DenseTensor<float>(maskData, new int[] { 1, w * h });
            var mlpInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_mask", inputTensor)
            };
            Tensor<float> mlpOutput;
            using (var results = _mlpSession.Run(mlpInputs))
            {
                mlpOutput = results.First().AsTensor<float>();
            }
            // mlpOutput shape [1, w*h], clamp & convert back to image
            float[] outArr = mlpOutput.ToArray();
            Bitmap final = new Bitmap(w, h);
            for (int i = 0; i < outArr.Length; i++)
            {
                float val = Math.Max(0f, Math.Min(outArr[i], 1f));
                int c = (int)(val * 255);
                int xx = i % w;
                int yy = i / w;
                final.SetPixel(xx, yy, Color.FromArgb(c, c, c));
            }
            return final;
        }

        // -------------------------------------------------------------------
        // Helper data structures & conversions
        // -------------------------------------------------------------------
        private struct PromptTensors
        {
            public Tensor<float> Coords;
            public Tensor<int> Labels;
            public Tensor<float> MaskPrompt;
            public Tensor<int> MaskEnable;
        }

        private PromptTensors CreatePromptTensors(
            List<Point> points,
            List<Rectangle> boxes,
            bool[,] brushMask,
            string textPrompt,
            int originalWidth,
            int originalHeight)
        {
            // Collect coords & labels
            var allCoords = new List<float>();
            var allLabels = new List<int>();

            // 1) Points => label=1
            if (points != null)
            {
                foreach (var p in points)
                {
                    float normX = (float)p.X / (float)originalWidth * _imageSize;
                    float normY = (float)p.Y / (float)originalHeight * _imageSize;
                    allCoords.Add(normX);
                    allCoords.Add(normY);
                    allLabels.Add(1);
                }
            }
            // 2) Boxes => typically we do 2 coords for the corners => labels 2,3
            if (boxes != null)
            {
                foreach (var r in boxes)
                {
                    float left = r.Left / (float)originalWidth * _imageSize;
                    float top = r.Top / (float)originalHeight * _imageSize;
                    float right = r.Right / (float)originalWidth * _imageSize;
                    float bottom = r.Bottom / (float)originalHeight * _imageSize;

                    // corner1 => label=2
                    allCoords.Add(left);
                    allCoords.Add(top);
                    allLabels.Add(2);
                    // corner2 => label=3
                    allCoords.Add(right);
                    allCoords.Add(bottom);
                    allLabels.Add(3);
                }
            }

            // If no coords => add dummy
            if (allCoords.Count == 0)
            {
                allCoords.Add(0f);
                allCoords.Add(0f);
                allLabels.Add(-1);
            }
            int pointCount = allCoords.Count / 2;
            var coordsTensor = new DenseTensor<float>(allCoords.ToArray(), new int[] { 1, pointCount, 2 });
            var labelsTensor = new DenseTensor<int>(allLabels.ToArray(), new int[] { 1, pointCount });

            // 3) Brush mask => shape [1,1,_imageSize,_imageSize]
            float[] maskData = new float[_imageSize * _imageSize];
            if (brushMask != null)
            {
                int srcW = brushMask.GetLength(0);
                int srcH = brushMask.GetLength(1);
                for (int yy = 0; yy < _imageSize; yy++)
                {
                    int srcY = (int)((float)yy / (float)_imageSize * srcH);
                    for (int xx = 0; xx < _imageSize; xx++)
                    {
                        int srcX = (int)((float)xx / (float)_imageSize * srcW);
                        if (brushMask[srcX, srcY]) maskData[yy * _imageSize + xx] = 1f;
                    }
                }
            }
            var maskPromptTensor = new DenseTensor<float>(maskData, new int[] { 1, 1, _imageSize, _imageSize });
            int maskEnableVal = (brushMask != null) ? 1 : 0;
            var maskEnableTensor = new DenseTensor<int>(new int[] { maskEnableVal }, new int[] { 1 });

            var prompts = new PromptTensors
            {
                Coords = coordsTensor,
                Labels = labelsTensor,
                MaskPrompt = maskPromptTensor,
                MaskEnable = maskEnableTensor
            };
            return prompts;
        }

        /// <summary>
        /// Convert text to a dummy embedding, shape=256. Replace with real text model if needed.
        /// </summary>
        private float[] EncodeTextToFloatArray(string text)
        {
            int dim = 256;
            float[] arr = new float[dim];
            // trivial pattern
            for (int i = 0; i < dim; i++)
            {
                arr[i] = (float)((i % 7) / 7.0);
            }
            return arr;
        }

        /// <summary>
        /// Convert a CT slice (24-bit grayscale, or color) to float array [1,3,H,W], normalized.
        /// We first resize the input to _imageSize x _imageSize inside here, so that
        /// the shape matches SAM2’s expected input dimension.
        /// </summary>
        private float[] ImageToFloatArray(Bitmap original)
        {
            // 1) resize
            Bitmap resized = ResizeImage(original, _imageSize, _imageSize);
            int w = resized.Width;
            int h = resized.Height;

            float[] result = new float[1 * 3 * w * h];
            BitmapData data = resized.LockBits(new Rectangle(0, 0, w, h),
                ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

            unsafe
            {
                byte* ptr = (byte*)data.Scan0;
                int stride = data.Stride;
                for (int y = 0; y < h; y++)
                {
                    byte* row = ptr + (y * stride);
                    for (int x = 0; x < w; x++)
                    {
                        byte b = row[x * 3];
                        byte g = row[x * 3 + 1];
                        byte r = row[x * 3 + 2];
                        int idxR = 0 * (w * h) + y * w + x;
                        int idxG = 1 * (w * h) + y * w + x;
                        int idxB = 2 * (w * h) + y * w + x;
                        result[idxR] = r / 255f;
                        result[idxG] = g / 255f;
                        result[idxB] = b / 255f;
                    }
                }
            }
            resized.UnlockBits(data);
            resized.Dispose();
            return result;
        }

        /// <summary>
        /// Convert a mask tensor (shape [1,1,h,w]) to a grayscale bitmap of desired size.
        /// </summary>
        private Bitmap TensorToBitmap(Tensor<float> tensor, int targetW, int targetH)
        {
            int h = tensor.Dimensions[2];
            int w = tensor.Dimensions[3];
            // build a small bmp
            Bitmap smallBmp = new Bitmap(w, h);
            for (int yy = 0; yy < h; yy++)
            {
                for (int xx = 0; xx < w; xx++)
                {
                    float val = tensor[0, 0, yy, xx];
                    val = Math.Max(0f, Math.Min(1f, val));
                    int c = (int)(val * 255);
                    smallBmp.SetPixel(xx, yy, Color.FromArgb(c, c, c));
                }
            }
            // scale up
            Bitmap final = ResizeImage(smallBmp, targetW, targetH);
            smallBmp.Dispose();
            return final;
        }

        /// <summary>
        /// Quickly process a single CT slice with SAM2:
        ///   1) Resizes the image to 1024 x 1024 (or your `_imageSize`) 
        ///   2) Encodes it via <see cref="_imageEncoderSession"/> 
        ///   3) Encodes user prompts (points, boxes, brush, text) via <see cref="_promptEncoderSession"/> 
        ///   4) Runs the <see cref="_maskDecoderSession"/> 
        ///   5) Optionally runs <see cref="_mlpSession"/> for final post-processing 
        ///   6) Resizes the mask back to the original resolution 
        /// This method does NOT use memory/attention. 
        /// </summary>
        /// <param name="sliceImage">One CT slice (any size, typically 24-bit grayscale). </param>
        /// <param name="userPoints">Optional 2D points (in original slice coords) for prompting. </param>
        /// <param name="userBoxes">Optional bounding boxes (Rectangles) in original coords. </param>
        /// <param name="userBrushMask">
        ///   Optional brush overlay: a bool[,] that is true wherever the user brushed. 
        ///   Dimensions = (width, height) of original slice. 
        /// </param>
        /// <param name="textPrompt">Optional text prompt if the model supports it. </param>
        /// <returns>A segmented mask as a Bitmap, same resolution as sliceImage. </returns>
        public Bitmap ProcessSingleSlice(
            Bitmap sliceImage,
            List<Point> userPoints = null,
            List<Rectangle> userBoxes = null,
            bool[,] userBrushMask = null,
            string textPrompt = null
        )
        {
            if (sliceImage == null)
                throw new ArgumentNullException(nameof(sliceImage));

            // 1) Run the image encoder. We produce e.g. "vision_features" & "vision_pos_enc".
            //    Dimensions for SAM2 typically [1,3,1024,1024] if _imageSize=1024.
            (Tensor<float> visionFeatures, Tensor<float> visionPosEnc) = RunImageEncoder(sliceImage);

            // 2) Build user prompt embeddings if any:
            //    Points, boxes => scaled to _imageSize
            //    Brush => up/downsample to 1024x1024
            //    Text => dummy or real embedding
            //    We'll get (sparseEmb, denseEmb, densePosEnc).
            (Tensor<float> sparseEmb, Tensor<float> denseEmb, Tensor<float> densePe) =
                RunPromptEncoderIfNeeded(
                    sliceImage.Width,
                    sliceImage.Height,
                    userPoints,
                    userBoxes,
                    userBrushMask,
                    textPrompt
                );

            // 3) Run the mask decoder. We pass:
            //      - "image_embeddings"  => visionFeatures
            //      - "sparse_prompt_embeddings" => sparseEmb
            //      - "dense_prompt_embeddings"  => denseEmb
            //      - "image_pe" => densePe
            //    Typically the output is named "low_res_masks".
            //    We'll store it in "lowResMask".
            Tensor<float> lowResMask = RunMaskDecoder(
                visionFeatures,
                (sparseEmb, denseEmb, densePe),
                fallbackVisionFeatures: visionFeatures,
                fallbackVisionPosEnc: visionPosEnc
            );

            // 4) Convert the low-resolution mask from shape [1,1,256,256] (or [1,1, H/4, W/4]) 
            //    up to [1,1,_imageSize,_imageSize] => then to a Bitmap
            //    Then finally up to original slice dimension.
            //    If we also want an MLP step, we apply it now:
            Bitmap lowResMaskBmp = TensorToBitmap(lowResMask, _imageSize, _imageSize);
            Bitmap maskResizedToSlice = ResizeImage(lowResMaskBmp, sliceImage.Width, sliceImage.Height);
            lowResMaskBmp.Dispose();

            // 5) Optionally run MLP for final post-processing if the pipeline uses it
            Bitmap final = RunMlpOnMask(maskResizedToSlice);
            maskResizedToSlice.Dispose();

            return final;
        }


        /// <summary>
        /// Resizes a bitmap with high-quality interpolation.
        /// </summary>
        private Bitmap ResizeImage(Bitmap src, int newW, int newH)
        {
            Bitmap dst = new Bitmap(newW, newH);
            dst.SetResolution(src.HorizontalResolution, src.VerticalResolution);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using (var attr = new ImageAttributes())
                {
                    attr.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(src,
                        new Rectangle(0, 0, newW, newH),
                        0, 0, src.Width, src.Height,
                        GraphicsUnit.Pixel,
                        attr);
                }
            }
            return dst;
        }

        // -------------------------------------------------------------------
        // Disposal
        // -------------------------------------------------------------------
        public void Dispose()
        {
            if (_imageEncoderSession != null) _imageEncoderSession.Dispose();
            if (_promptEncoderSession != null) _promptEncoderSession.Dispose();
            if (_maskDecoderSession != null) _maskDecoderSession.Dispose();
            if (_memoryEncoderSession != null) _memoryEncoderSession.Dispose();
            if (_memoryAttentionSession != null) _memoryAttentionSession.Dispose();
            if (_mlpSession != null) _mlpSession.Dispose();

            ResetState();
        }
    }
}
