using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CTSegmenter
{
    public class VideoPredictor : IDisposable
    {
        // ONNX Runtime sessions for different model parts.
        private InferenceSession _imageEncoderSession;
        private InferenceSession _promptEncoderSession;
        private InferenceSession _maskDecoderSession;
        private InferenceSession _memoryEncoderSession;
        private InferenceSession _memoryAttentionSession;
        private InferenceSession _mlpSession;

        // Some basic state variables.
        private int _imageSize = 1024;  // SAM2 expected input size (e.g., 1024x1024)

        /// <summary>
        /// Initializes a new VideoPredictor.
        /// The provided model paths must point to ONNX files for each component.
        /// </summary>
        public VideoPredictor(
            string imageEncoderPath,
            string promptEncoderPath,
            string maskDecoderPath,
            string memoryEncoderPath,
            string memoryAttentionPath,
            string mlpPath)
        {
            // Create session options and append DirectML provider.
            var options = new SessionOptions();
            options.AppendExecutionProvider_DML();

            // Load the ONNX models.
            _imageEncoderSession = new InferenceSession(imageEncoderPath, options);
            _promptEncoderSession = new InferenceSession(promptEncoderPath, options);
            _maskDecoderSession = new InferenceSession(maskDecoderPath, options);
            _memoryEncoderSession = new InferenceSession(memoryEncoderPath, options);
            _memoryAttentionSession = new InferenceSession(memoryAttentionPath, options);
            _mlpSession = new InferenceSession(mlpPath, options);
        }

        /// <summary>
        /// Resets any stored state (for video propagation, object tracking, etc.).
        /// </summary>
        public void ResetState()
        {
            // Reset or clear any caches/state as needed.
        }

        /// <summary>
        /// Process a single image and return a mask (as a Bitmap).
        /// If promptInput is null then zero‐shot (no prompt/points) mode is used.
        /// The image is resized to the SAM2 expected shape.
        /// </summary>
        /// <param name="image">Input image (any size)</param>
        /// <param name="promptInput">Optional prompt input tensor (for points or box cues).
        /// If null, an “empty” prompt will be used.</param>
        /// <returns>A Bitmap representing the output mask (same resolution as the input image).</returns>
        /// Advised to use Multi Layer Perceptron for post processing
        public Bitmap ProcessSingleImage(Bitmap image, float[] promptInput = null)
        {
            // Step 1: Preprocess the image.
            Bitmap resized = ResizeImage(image, _imageSize, _imageSize);
            float[] imageData = ImageToFloatArray(resized);
            // Assume imageData is in shape [1,3,1024,1024] (CHW, normalized as needed).

            // Create input tensor for the image encoder.
            var imageTensor = new DenseTensor<float>(imageData, new int[] { 1, 3, _imageSize, _imageSize });
            var imageInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_image", imageTensor)
            };

            // Step 2: Run image encoder to get visual features.
            Tensor<float> visionFeatures;
            Tensor<float> visionPosEnc;
            using (IDisposableReadOnlyCollection<DisposableNamedOnnxValue> imageOutputs = _imageEncoderSession.Run(imageInputs))
            {
                visionFeatures = imageOutputs.First(x => x.Name == "vision_features").AsTensor<float>();
                visionPosEnc = imageOutputs.First(x => x.Name == "vision_pos_enc").AsTensor<float>();
            }

            // Step 3: Prepare prompt input.
            float[] coords;
            int[] labels;
            if (promptInput == null)
            {
                coords = new float[] { 0, 0 }; // Dummy coordinate.
                labels = new int[] { -1 };     // Indicates "no prompt."
            }
            else
            {
                coords = promptInput;
                labels = new int[promptInput.Length / 2];
                for (int i = 0; i < labels.Length; i++)
                    labels[i] = 1; // Treat all as positive prompts.
            }
            int pointCount = coords.Length / 2;
            var coordsTensor = new DenseTensor<float>(coords, new int[] { 1, pointCount, 2 });
            var labelsTensor = new DenseTensor<int>(labels, new int[] { 1, pointCount });

            // Prepare a dummy mask prompt.
            var maskPromptTensor = new DenseTensor<float>(new float[_imageSize * _imageSize], new int[] { 1, 1, _imageSize, _imageSize });
            var promptInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("coords", coordsTensor),
                NamedOnnxValue.CreateFromTensor("labels", labelsTensor),
                NamedOnnxValue.CreateFromTensor("masks", maskPromptTensor),
                NamedOnnxValue.CreateFromTensor("masks_enable", new DenseTensor<int>(new int[] { promptInput == null ? 0 : 1 }, new int[]{1}))
            };

            // Step 4: Run the prompt encoder.
            Tensor<float> sparseEmbeddings;
            Tensor<float> denseEmbeddings;
            Tensor<float> densePosEnc;
            using (IDisposableReadOnlyCollection<DisposableNamedOnnxValue> promptOutputs = _promptEncoderSession.Run(promptInputs))
            {
                sparseEmbeddings = promptOutputs.First(x => x.Name == "sparse_embeddings").AsTensor<float>();
                denseEmbeddings = promptOutputs.First(x => x.Name == "dense_embeddings").AsTensor<float>();
                densePosEnc = promptOutputs.First(x => x.Name == "dense_pe").AsTensor<float>();
            }

            // Step 5: Run the mask decoder.
            var maskDecoderInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("image_embeddings", visionFeatures),
                NamedOnnxValue.CreateFromTensor("image_pe", densePosEnc),
                NamedOnnxValue.CreateFromTensor("sparse_prompt_embeddings", sparseEmbeddings),
                NamedOnnxValue.CreateFromTensor("dense_prompt_embeddings", denseEmbeddings),
                NamedOnnxValue.CreateFromTensor("high_res_features1", visionFeatures),
                NamedOnnxValue.CreateFromTensor("high_res_features2", visionFeatures)
            };

            Tensor<float> lowResMasks;
            using (IDisposableReadOnlyCollection<DisposableNamedOnnxValue> maskOutputs = _maskDecoderSession.Run(maskDecoderInputs))
            {
                lowResMasks = maskOutputs.First(x => x.Name == "low_res_masks").AsTensor<float>();
            }

            // Step 6: Upsample the low-res mask to the original image resolution.
            Bitmap maskBmp = TensorToBitmap(lowResMasks, _imageSize, _imageSize);

            // (Optional) Run memory encoder/attention and MLP postprocessing if needed.

            return maskBmp;
        }

        /// <summary>
        /// Resizes a bitmap to the given width and height.
        /// </summary>
        private Bitmap ResizeImage(Bitmap image, int width, int height)
        {
            var destRect = new Rectangle(0, 0, width, height);
            var destImage = new Bitmap(width, height);
            destImage.SetResolution(image.HorizontalResolution, image.VerticalResolution);

            using (var graphics = Graphics.FromImage(destImage))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                using (var wrapMode = new ImageAttributes())
                {
                    wrapMode.SetWrapMode(WrapMode.TileFlipXY);
                    graphics.DrawImage(image, destRect, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, wrapMode);
                }
            }
            return destImage;
        }

        /// <summary>
        /// Converts a Bitmap image to a float array.
        /// The result is arranged as [1, 3, height, width] in CHW order and normalized to [0,1].
        /// </summary>
        private float[] ImageToFloatArray(Bitmap image)
        {
            int width = image.Width;
            int height = image.Height;
            float[] result = new float[1 * 3 * width * height];

            BitmapData data = image.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

            unsafe
            {
                byte* ptr = (byte*)data.Scan0;
                int stride = data.Stride;
                for (int y = 0; y < height; y++)
                {
                    byte* row = ptr + (y * stride);
                    for (int x = 0; x < width; x++)
                    {
                        byte blue = row[x * 3];
                        byte green = row[x * 3 + 1];
                        byte red = row[x * 3 + 2];

                        result[0 * (3 * width * height) + 0 * (width * height) + y * width + x] = red / 255f;
                        result[0 * (3 * width * height) + 1 * (width * height) + y * width + x] = green / 255f;
                        result[0 * (3 * width * height) + 2 * (width * height) + y * width + x] = blue / 255f;
                    }
                }
            }
            image.UnlockBits(data);
            return result;
        }

        /// <summary>
        /// Converts the output tensor (assumed to be a low-resolution mask) into a Bitmap.
        /// This helper upsamples the mask to the given width and height using nearest-neighbor.
        /// </summary>
        private Bitmap TensorToBitmap(Tensor<float> tensor, int targetWidth, int targetHeight)
        {
            int h = tensor.Dimensions[2];
            int w = tensor.Dimensions[3];

            float[,] maskData = new float[h, w];
            for (int i = 0; i < h; i++)
            {
                for (int j = 0; j < w; j++)
                {
                    // Use the indexer instead of GetValue.
                    maskData[i, j] = tensor[0, 0, i, j];
                }
            }

            Bitmap lowResBmp = new Bitmap(w, h);
            for (int i = 0; i < h; i++)
            {
                for (int j = 0; j < w; j++)
                {
                    int v = (int)(Math.Min(Math.Max(maskData[i, j], 0), 1) * 255);
                    Color c = Color.FromArgb(v, v, v);
                    lowResBmp.SetPixel(j, i, c);
                }
            }

            Bitmap upsampled = ResizeImage(lowResBmp, targetWidth, targetHeight);
            return upsampled;
        }

        /// <summary>
        /// Disposes all ONNX sessions.
        /// </summary>
        public void Dispose()
        {
            if (_imageEncoderSession != null)
            {
                _imageEncoderSession.Dispose();
                _imageEncoderSession = null;
            }
            if (_promptEncoderSession != null)
            {
                _promptEncoderSession.Dispose();
                _promptEncoderSession = null;
            }
            if (_maskDecoderSession != null)
            {
                _maskDecoderSession.Dispose();
                _maskDecoderSession = null;
            }
            if (_memoryEncoderSession != null)
            {
                _memoryEncoderSession.Dispose();
                _memoryEncoderSession = null;
            }
            if (_memoryAttentionSession != null)
            {
                _memoryAttentionSession.Dispose();
                _memoryAttentionSession = null;
            }
            if (_mlpSession != null)
            {
                _mlpSession.Dispose();
                _mlpSession = null;
            }
        }
    }
}
