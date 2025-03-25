// CTMemorySegmenter.cs
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;

namespace CTSegmenter
{
    /// <summary>
    /// Handles the memory-based segmentation workflow for SAM 2.1.
    /// Performs all necessary pre-processing, model inference, and optional post-processing.
    /// 
    /// By default, the mask binarization threshold is 0.5 (50% probability). The prompt logic:
    /// for a given material, points from other materials (including "Exterior") become negative prompts.
    /// 
    /// This class provides methods for both multi-candidate and single-candidate segmentation:
    ///   - ProcessXYSlice_GetAllMasks()
    ///   - ProcessXYSlice()
    ///   - ProcessXZSlice()
    ///   - ProcessYZSlice()
    /// 
    /// (Also includes a minimal static segmentation propagator stub for legacy calls, ignoring any threshold parameter.)
    /// </summary>
    /// 
    public class CTMemorySegmenter : IDisposable
    {
        private readonly int _imageSize;
        private readonly Dictionary<int, SliceMemory> _sliceMem;

        // SAM 2.1 model sessions and model file paths
        private InferenceSession _encoderSession;
        private InferenceSession _decoderSession;
        private string _encoderModelPath;
        private string _decoderModelPath;

        // If true, using the new SAM 2.1 architecture (otherwise legacy – not supported here)
        private bool _usingSam2Model;

        // Caching for image embeddings (if enabled for reuse across slices)
        private bool _hasCachedEmbeddings = false;
        private DenseTensor<float> _cachedImageEmbeddings;
        private DenseTensor<float> _cachedHighResFeatures0;
        private DenseTensor<float> _cachedHighResFeatures1;

        // Main flags and threshold
        public bool StorePreviousEmbeddings { get; set; } = true;
        public bool UseSelectiveHoleFilling { get; set; } = false;

        /// <summary>
        /// Mask binarization threshold in [0..1], default 0.5 (50% probability).
        /// </summary>
        public float MaskBinarizationThreshold { get; set; } = 0.5f;

        private static void Log(string msg) => Logger.Log(msg);

        /// <summary>
        /// Basic container for old hierarchical SAM slice memory (not used by SAM 2.1).
        /// </summary>
        private class SliceMemory
        {
            // placeholder
        }

        /// <summary>
        /// CTMemorySegmenter constructor for SAM 2.1 usage.
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
            bool useCpuExecutionProvider = true)
        {
            Log("[CTMemorySegmenter] Constructor start");
            _imageSize = imageInputSize;
            _sliceMem = new Dictionary<int, SliceMemory>();

            // Determine if using SAM 2.1 model (by filename convention)
            _usingSam2Model = (!string.IsNullOrEmpty(imageEncoderPath) &&
                               Path.GetFileName(imageEncoderPath).Contains("sam2.1"));
            if (_usingSam2Model)
            {
                // Compose the encoder and decoder ONNX model paths
                _encoderModelPath = Path.Combine(Path.GetDirectoryName(imageEncoderPath) ?? "", "sam2.1_large.encoder.onnx");
                _decoderModelPath = Path.Combine(Path.GetDirectoryName(maskDecoderPath) ?? "", "sam2.1_large.decoder.onnx");

                Log($"[CTMemorySegmenter] Using SAM 2.1: {_encoderModelPath} / {_decoderModelPath}");

                // Check that model files exist
                if (!File.Exists(_encoderModelPath))
                    Log($"[CTMemorySegmenter] ERROR: Encoder file not found: {_encoderModelPath}");
                if (!File.Exists(_decoderModelPath))
                    Log($"[CTMemorySegmenter] ERROR: Decoder file not found: {_decoderModelPath}");

                // Set up ONNX runtime session options (use DirectML if available and requested, otherwise CPU)
                SessionOptions options = new SessionOptions();
                if (!useCpuExecutionProvider)
                {
                    try
                    {
                        options.AppendExecutionProvider_DML();
                        Log("[CTMemorySegmenter] Using DirectML Execution Provider for SAM 2.1");
                    }
                    catch (Exception dmlEx)
                    {
                        Log($"[CTMemorySegmenter] DirectML not available, falling back to CPU: {dmlEx.Message}");
                        options = new SessionOptions();
                        options.AppendExecutionProvider_CPU();
                    }
                }
                else
                {
                    options.AppendExecutionProvider_CPU();
                }

                // Create the encoder and decoder inference sessions
                try
                {
                    _encoderSession = new InferenceSession(_encoderModelPath, options);
                    _decoderSession = new InferenceSession(_decoderModelPath, options);
                    Log("[CTMemorySegmenter] SAM 2.1 model sessions loaded successfully");
                }
                catch (Exception ex)
                {
                    Log("[CTMemorySegmenter] Exception loading SAM 2.1 models: " + ex.Message);
                    if (!useCpuExecutionProvider)
                    {
                        // Retry on CPU if GPU/DML failed
                        try
                        {
                            Log("[CTMemorySegmenter] Attempting CPU fallback for SAM 2.1 models");
                            var cpuOptions = new SessionOptions();
                            cpuOptions.AppendExecutionProvider_CPU();
                            _encoderSession = new InferenceSession(_encoderModelPath, cpuOptions);
                            _decoderSession = new InferenceSession(_decoderModelPath, cpuOptions);
                            Log("[CTMemorySegmenter] CPU fallback successful for SAM 2.1");
                        }
                        catch (Exception ex2)
                        {
                            Log("[CTMemorySegmenter] CPU fallback failed: " + ex2.Message);
                            throw;
                        }
                    }
                    else
                    {
                        throw;
                    }
                }
            }
            else
            {
                // Legacy (non-SAM2.1) model usage is not supported in this implementation
                Log("[CTMemorySegmenter] Using older SAM model (deprecated and not supported in this class)");
            }

            Log("[CTMemorySegmenter] Constructor end");
        }

        public void Dispose()
        {
            _encoderSession?.Dispose();
            _decoderSession?.Dispose();
        }

        /// <summary>
        /// Produces a SINGLE best mask for an XY slice using SAM 2.1.
        /// All annotation points whose Label == <paramref name="targetMaterialName"/> are treated as positive prompts (label=1),
        /// while all other points (including "Exterior") are negative prompts (label=0).
        /// Returns the mask with the highest predicted IoU among up to 3 candidate masks from the decoder.
        /// </summary>
        /// <param name="sliceIndex">Z-slice index (for logging/reference)</param>
        /// <param name="baseXY">Grayscale XY slice image (width = X, height = Y)</param>
        /// <param name="slicePoints">All annotation points in this slice (for all materials)</param>
        /// <param name="targetMaterialName">Name of the material to segment (positive class label)</param>
        /// <returns>The best mask as a binarized Bitmap (white = foreground, black = background), or null if no mask found</returns>
        public Bitmap ProcessXYSlice(int sliceIndex, Bitmap baseXY, List<AnnotationPoint> slicePoints, string targetMaterialName)
        {
            if (!_usingSam2Model) return null;
            if (baseXY == null) return null;
            if (slicePoints == null || slicePoints.Count == 0) return null;

            Log($"[ProcessXYSlice] Segmenting '{targetMaterialName}' in XY slice Z={sliceIndex}");
            // 1. Pre-process image: rescale intensity and apply Jet colormap
            Bitmap rescaled = RescaleCT(baseXY);
            rescaled = ApplyJetColormap(rescaled);

            // 2. Image encoder forward pass to get image embeddings and high-res features
            float[] imageTensorData = BitmapToFloatTensor(rescaled, _imageSize, _imageSize);
            var imageInput = new DenseTensor<float>(imageTensorData, new[] { 1, 3, _imageSize, _imageSize });
            Tensor<float> imageEmbed, highRes0, highRes1;
            using (var encoderOutputs = _encoderSession.Run(new[] {
                NamedOnnxValue.CreateFromTensor("image", imageInput)
            }))
            {
                imageEmbed = GetFirstTensor<float>(encoderOutputs, "image_embeddings");
                highRes0 = GetFirstTensor<float>(encoderOutputs, "high_res_feats_0");
                highRes1 = GetFirstTensor<float>(encoderOutputs, "high_res_feats_1");
            }

            // 3. Build prompt tensors (positive and negative points for the target material)
            int origW = baseXY.Width;
            int origH = baseXY.Height;
            (DenseTensor<float> coordsTensor, DenseTensor<float> labelsTensor) =
                BuildSinglePromptTensors(slicePoints, origW, origH, targetMaterialName);
            var origSizeTensor = new DenseTensor<int>(new[] { origH, origW }, new[] { 2 });

            // Initialize mask inputs (no previous mask provided in this single-step use case)
            int numLabels = coordsTensor.Dimensions[0];  // usually 1
            var maskInputTensor = new DenseTensor<float>(new float[numLabels * 1 * 256 * 256], new[] { numLabels, 1, 256, 256 });
            var hasMaskInputTensor = new DenseTensor<float>(new float[numLabels], new[] { numLabels });

            // 4. Decoder forward pass to get mask predictions and IoU scores
            Tensor<float> masksTensor;
            Tensor<float> iouTensor;
            using (var decoderOutputs = _decoderSession.Run(new List<NamedOnnxValue> {
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
                masksTensor = GetFirstTensor<float>(decoderOutputs, "masks");
                iouTensor = GetFirstTensor<float>(decoderOutputs, "iou_predictions");
            }

            // 5. Choose the mask with the highest predicted IoU
            int numMasks = masksTensor.Dimensions[1];  // number of mask candidates (typically 3)
            if (numMasks == 0) return null;
            int maskH = masksTensor.Dimensions[2];
            int maskW = masksTensor.Dimensions[3];
            float bestIoU = float.MinValue;
            int bestIndex = 0;
            for (int m = 0; m < numMasks; m++)
            {
                float iou = iouTensor[0, m];
                if (iou > bestIoU)
                {
                    bestIoU = iou;
                    bestIndex = m;
                }
            }

            // 6. Convert the best mask to a Bitmap (binary) and scale to original image size
            return BuildMaskFromDecoder(masksTensor, bestIndex, maskW, maskH, origW, origH);
        }

        /// <summary>
        /// Returns ALL candidate masks (multi-candidate) for an XY slice using SAM 2.1.
        /// Points labeled as <paramref name="targetMaterialName"/> are positive prompts, all other points are negative prompts.
        /// The decoder typically returns 3 mask candidates; if exactly 3, we duplicate the third to produce a 4th mask (for uniform list length).
        /// </summary>
        /// <param name="sliceIndex">Z-slice index (for logging/reference)</param>
        /// <param name="baseXY">Grayscale XY slice image (width = X, height = Y)</param>
        /// <param name="slicePoints">All annotation points in this slice (for any materials)</param>
        /// <param name="targetMaterialName">The material label to segment (positive class)</param>
        /// <returns>List of candidate mask Bitmaps (white=foreground, black=background)</returns>
        public List<Bitmap> ProcessXYSlice_GetAllMasks(int sliceIndex, Bitmap baseXY, List<AnnotationPoint> slicePoints, string targetMaterialName)
        {
            if (!_usingSam2Model || baseXY == null || slicePoints == null || slicePoints.Count == 0)
            {
                return new List<Bitmap>();
            }
            Log($"[ProcessXYSlice_GetAllMasks] Getting all masks for material='{targetMaterialName}' in XY slice Z={sliceIndex}");

            // 1. Pre-process image (rescale intensities and apply colormap)
            Bitmap rescaled = RescaleCT(baseXY);
            rescaled = ApplyJetColormap(rescaled);

            // 2. Encode image to get embeddings
            float[] imageTensorData = BitmapToFloatTensor(rescaled, _imageSize, _imageSize);
            var imageInput = new DenseTensor<float>(imageTensorData, new[] { 1, 3, _imageSize, _imageSize });
            Tensor<float> imageEmbed, highRes0, highRes1;
            using (var encOut = _encoderSession.Run(new[] {
                NamedOnnxValue.CreateFromTensor("image", imageInput)
            }))
            {
                imageEmbed = GetFirstTensor<float>(encOut, "image_embeddings");
                highRes0 = GetFirstTensor<float>(encOut, "high_res_feats_0");
                highRes1 = GetFirstTensor<float>(encOut, "high_res_feats_1");
            }

            // 3. Build prompt tensors (target material = positive, others = negative)
            int origW = baseXY.Width;
            int origH = baseXY.Height;
            (DenseTensor<float> coordsTensor, DenseTensor<float> labelsTensor) =
                BuildSinglePromptTensors(slicePoints, origW, origH, targetMaterialName);
            var origSizeTensor = new DenseTensor<int>(new[] { origH, origW }, new[] { 2 });

            // Initialize mask inputs to zero (no previous mask)
            int numLabels = coordsTensor.Dimensions[0];  // typically 1
            var maskInputTensor = new DenseTensor<float>(new float[numLabels * 1 * 256 * 256], new[] { numLabels, 1, 256, 256 });
            var hasMaskInputTensor = new DenseTensor<float>(new float[numLabels], new[] { numLabels });

            // 4. Decoder forward pass (get all mask candidates; IoUs optional)
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
                masksTensor = GetFirstTensor<float>(decOut, "masks");
            }

            // 5. Convert each mask candidate to a Bitmap at original size
            int outCount = masksTensor.Dimensions[1];  // e.g. 3
            int maskH = masksTensor.Dimensions[2];
            int maskW = masksTensor.Dimensions[3];
            List<Bitmap> candidateMasks = new List<Bitmap>();
            for (int m = 0; m < outCount; m++)
            {
                Bitmap maskBmp = BuildMaskFromDecoder(masksTensor, m, maskW, maskH, origW, origH);
                candidateMasks.Add(maskBmp);
            }
            // If only 3 masks, replicate the last one to make 4 (optional step for consistency)
            if (candidateMasks.Count == 3)
            {
                candidateMasks.Add(new Bitmap(candidateMasks[2]));
            }
            return candidateMasks;
        }

        /// <summary>
        /// Produces a SINGLE best mask for an XZ slice using SAM 2.1.
        /// (An XZ slice image has width = X and height = Z.)
        /// Points with Label == <paramref name="targetMaterialName"/> are positive prompts; all others are negative.
        /// Returns the mask with highest IoU among up to 3 decoder outputs.
        /// </summary>
        public Bitmap ProcessXZSlice(int sliceIndex, Bitmap baseXZ, List<AnnotationPoint> slicePoints, string targetMaterialName)
        {
            if (!_usingSam2Model) return null;
            if (baseXZ == null) return null;
            if (slicePoints == null || slicePoints.Count == 0) return null;

            Log($"[ProcessXZSlice] Segmenting '{targetMaterialName}' in XZ slice index={sliceIndex}");

            // 1. Encode the XZ slice image
            float[] imageTensorData = BitmapToFloatTensor(baseXZ, _imageSize, _imageSize);
            var imageInput = new DenseTensor<float>(imageTensorData, new[] { 1, 3, _imageSize, _imageSize });
            Tensor<float> imageEmbed, highRes0, highRes1;
            using (var encoderOutputs = _encoderSession.Run(new[] {
                NamedOnnxValue.CreateFromTensor("image", imageInput)
            }))
            {
                imageEmbed = GetFirstTensor<float>(encoderOutputs, "image_embeddings");
                highRes0 = GetFirstTensor<float>(encoderOutputs, "high_res_feats_0");
                highRes1 = GetFirstTensor<float>(encoderOutputs, "high_res_feats_1");
            }

            // 2. Build prompt tensors (target material positive, others negative)
            int origW = baseXZ.Width;   // X dimension
            int origH = baseXZ.Height;  // Z dimension
            (DenseTensor<float> coordsTensor, DenseTensor<float> labelsTensor) =
                BuildSinglePromptTensors(slicePoints, origW, origH, targetMaterialName);
            var origSizeTensor = new DenseTensor<int>(new[] { origH, origW }, new[] { 2 });

            // Prepare mask inputs as empty (no prior mask)
            int numLabels = coordsTensor.Dimensions[0];
            var maskInputTensor = new DenseTensor<float>(new float[numLabels * 1 * 256 * 256], new[] { numLabels, 1, 256, 256 });
            var hasMaskInputTensor = new DenseTensor<float>(new float[numLabels], new[] { numLabels });

            // 3. Decoder forward pass
            Tensor<float> masksTensor;
            Tensor<float> iouTensor;
            using (var decoderOutputs = _decoderSession.Run(new List<NamedOnnxValue> {
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
                masksTensor = GetFirstTensor<float>(decoderOutputs, "masks");
                iouTensor = GetFirstTensor<float>(decoderOutputs, "iou_predictions");
            }

            int outCount = masksTensor.Dimensions[1];  // typically 3
            int maskH = masksTensor.Dimensions[2];
            int maskW = masksTensor.Dimensions[3];
            if (outCount == 0) return null;

            // 4. Pick the best mask via IoU
            float bestIoU = -1f;
            int bestIndex = 0;
            for (int m = 0; m < outCount; m++)
            {
                float iou = iouTensor[0, m];
                if (iou > bestIoU)
                {
                    bestIoU = iou;
                    bestIndex = m;
                }
            }
            Log($"[ProcessXZSlice] bestIndex={bestIndex}, IoU={bestIoU:F3}");

            // 5. Build and return the final mask Bitmap
            return BuildMaskFromDecoder(masksTensor, bestIndex, maskW, maskH, origW, origH);
        }

        /// <summary>
        /// Returns ALL candidate masks for a single pass in the XZ slice.
        /// Points labeled <paramref name="targetMaterialName"/> are positive prompts; all others are negative.
        /// The decoder usually provides 3 masks; if 3, we duplicate the last to make 4.
        /// </summary>
        public List<Bitmap> ProcessXZSlice_GetAllMasks(int sliceIndex, Bitmap baseXZ, List<AnnotationPoint> slicePoints, string targetMaterialName)
        {
            if (!_usingSam2Model || baseXZ == null || slicePoints == null || slicePoints.Count == 0)
            {
                return new List<Bitmap>();
            }
            Log($"[ProcessXZSlice_GetAllMasks] Gathering all masks for material='{targetMaterialName}', index={sliceIndex}");

            // 1. Encode the XZ image
            float[] imageTensorData = BitmapToFloatTensor(baseXZ, _imageSize, _imageSize);
            var imageInput = new DenseTensor<float>(imageTensorData, new[] { 1, 3, _imageSize, _imageSize });
            Tensor<float> imageEmbed, highRes0, highRes1;
            using (var encOut = _encoderSession.Run(new[] {
                NamedOnnxValue.CreateFromTensor("image", imageInput)
            }))
            {
                imageEmbed = GetFirstTensor<float>(encOut, "image_embeddings");
                highRes0 = GetFirstTensor<float>(encOut, "high_res_feats_0");
                highRes1 = GetFirstTensor<float>(encOut, "high_res_feats_1");
            }

            // 2. Build prompt tensors for target vs others
            int origW = baseXZ.Width;
            int origH = baseXZ.Height;
            (DenseTensor<float> coordsTensor, DenseTensor<float> labelsTensor) =
                BuildSinglePromptTensors(slicePoints, origW, origH, targetMaterialName);
            var origSizeTensor = new DenseTensor<int>(new[] { origH, origW }, new[] { 2 });

            // Mask input (none provided initially)
            int numLabels = coordsTensor.Dimensions[0];
            var maskInputTensor = new DenseTensor<float>(new float[numLabels * 1 * 256 * 256], new[] { numLabels, 1, 256, 256 });
            var hasMaskInputTensor = new DenseTensor<float>(new float[numLabels], new[] { numLabels });

            // 3. Decoder forward (get all masks)
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
                masksTensor = GetFirstTensor<float>(decOut, "masks");
            }

            // 4. Convert all mask outputs to bitmaps
            int outCount = masksTensor.Dimensions[1];  // typically 3
            int maskH = masksTensor.Dimensions[2];
            int maskW = masksTensor.Dimensions[3];
            List<Bitmap> candidates = new List<Bitmap>();
            for (int m = 0; m < outCount; m++)
            {
                candidates.Add(BuildMaskFromDecoder(masksTensor, m, maskW, maskH, origW, origH));
            }
            if (candidates.Count == 3)
            {
                candidates.Add(new Bitmap(candidates[2]));
            }
            return candidates;
        }

        /// <summary>
        /// Produces a SINGLE best mask for a YZ slice (width = Z, height = Y) using SAM 2.1.
        /// Points with Label == <paramref name="targetMaterialName"/> are positive prompts, others are negative.
        /// Returns the mask with highest IoU among up to 3 candidates.
        /// </summary>
        public Bitmap ProcessYZSlice(int sliceIndex, Bitmap baseYZ, List<AnnotationPoint> slicePoints, string targetMaterialName)
        {
            if (!_usingSam2Model) return null;
            if (baseYZ == null) return null;
            if (slicePoints == null || slicePoints.Count == 0) return null;

            Log($"[ProcessYZSlice] Segmenting '{targetMaterialName}' in YZ slice index={sliceIndex}");

            // 1. Encode the YZ image
            float[] imageTensorData = BitmapToFloatTensor(baseYZ, _imageSize, _imageSize);
            var imageInput = new DenseTensor<float>(imageTensorData, new[] { 1, 3, _imageSize, _imageSize });
            Tensor<float> imageEmbed, highRes0, highRes1;
            using (var encoderOutputs = _encoderSession.Run(new[] {
                NamedOnnxValue.CreateFromTensor("image", imageInput)
            }))
            {
                imageEmbed = GetFirstTensor<float>(encoderOutputs, "image_embeddings");
                highRes0 = GetFirstTensor<float>(encoderOutputs, "high_res_feats_0");
                highRes1 = GetFirstTensor<float>(encoderOutputs, "high_res_feats_1");
            }

            // 2. Build prompt tensors
            int origW = baseYZ.Width;   // Z dimension
            int origH = baseYZ.Height;  // Y dimension
            (DenseTensor<float> coordsTensor, DenseTensor<float> labelsTensor) =
                BuildSinglePromptTensors(slicePoints, origW, origH, targetMaterialName);
            var origSizeTensor = new DenseTensor<int>(new[] { origH, origW }, new[] { 2 });

            // Mask input placeholders (no prior mask for single step)
            int numLabels = coordsTensor.Dimensions[0];
            var maskInputTensor = new DenseTensor<float>(new float[numLabels * 1 * 256 * 256], new[] { numLabels, 1, 256, 256 });
            var hasMaskInputTensor = new DenseTensor<float>(new float[numLabels], new[] { numLabels });

            // 3. Decoder forward pass
            Tensor<float> masksTensor;
            Tensor<float> iouTensor;
            using (var decoderOutputs = _decoderSession.Run(new List<NamedOnnxValue> {
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
                masksTensor = GetFirstTensor<float>(decoderOutputs, "masks");
                iouTensor = GetFirstTensor<float>(decoderOutputs, "iou_predictions");
            }

            int outCount = masksTensor.Dimensions[1];
            int maskH = masksTensor.Dimensions[2];
            int maskW = masksTensor.Dimensions[3];
            if (outCount == 0) return null;

            // 4. Select best mask by IoU
            float bestIoU = -1f;
            int bestIndex = 0;
            for (int m = 0; m < outCount; m++)
            {
                float iou = iouTensor[0, m];
                if (iou > bestIoU)
                {
                    bestIoU = iou;
                    bestIndex = m;
                }
            }
            Log($"[ProcessYZSlice] bestIndex={bestIndex}, IoU={bestIoU:F3}");

            // 5. Return the best mask as Bitmap
            return BuildMaskFromDecoder(masksTensor, bestIndex, maskW, maskH, origW, origH);
        }

        /// <summary>
        /// Returns ALL candidate masks for a YZ slice (width = Z, height = Y).
        /// Points with Label == <paramref name="targetMaterialName"/> are positive, all others negative.
        /// Typically the decoder gives 3 masks; we replicate the last one if needed to make 4.
        /// </summary>
        public List<Bitmap> ProcessYZSlice_GetAllMasks(int sliceIndex, Bitmap baseYZ, List<AnnotationPoint> slicePoints, string targetMaterialName)
        {
            if (!_usingSam2Model || baseYZ == null || slicePoints == null || slicePoints.Count == 0)
            {
                return new List<Bitmap>();
            }
            Log($"[ProcessYZSlice_GetAllMasks] Gathering all masks for '{targetMaterialName}', index={sliceIndex}");

            // 1. Encode the YZ image
            float[] imageTensorData = BitmapToFloatTensor(baseYZ, _imageSize, _imageSize);
            var imageInput = new DenseTensor<float>(imageTensorData, new[] { 1, 3, _imageSize, _imageSize });
            Tensor<float> imageEmbed, highRes0, highRes1;
            using (var encOut = _encoderSession.Run(new[] {
                NamedOnnxValue.CreateFromTensor("image", imageInput)
            }))
            {
                imageEmbed = GetFirstTensor<float>(encOut, "image_embeddings");
                highRes0 = GetFirstTensor<float>(encOut, "high_res_feats_0");
                highRes1 = GetFirstTensor<float>(encOut, "high_res_feats_1");
            }

            // 2. Build prompt tensors (target material positive, others negative)
            int origW = baseYZ.Width;
            int origH = baseYZ.Height;
            (DenseTensor<float> coordsTensor, DenseTensor<float> labelsTensor) =
                BuildSinglePromptTensors(slicePoints, origW, origH, targetMaterialName);
            var origSizeTensor = new DenseTensor<int>(new[] { origH, origW }, new[] { 2 });

            // Empty mask input for decoder
            int numLabels = coordsTensor.Dimensions[0];
            var maskInputTensor = new DenseTensor<float>(new float[numLabels * 1 * 256 * 256], new[] { numLabels, 1, 256, 256 });
            var hasMaskInputTensor = new DenseTensor<float>(new float[numLabels], new[] { numLabels });

            // 3. Decoder forward (all masks)
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
                masksTensor = GetFirstTensor<float>(decOut, "masks");
            }

            // 4. Convert all mask outputs to bitmaps
            int outCount = masksTensor.Dimensions[1];
            int maskH = masksTensor.Dimensions[2];
            int maskW = masksTensor.Dimensions[3];
            List<Bitmap> candidates = new List<Bitmap>();
            for (int m = 0; m < outCount; m++)
            {
                candidates.Add(BuildMaskFromDecoder(masksTensor, m, maskW, maskH, origW, origH));
            }
            if (candidates.Count == 3)
            {
                candidates.Add(new Bitmap(candidates[2]));
            }
            return candidates;
        }

        /// <summary>
        /// Builds prompt tensors for a SINGLE material segmentation pass.
        ///   - Points with Label == targetMaterialName are assigned label=1 (positive).
        ///   - All other points are assigned label=0 (negative).
        /// 
        /// The output tensor shapes are:
        ///   coords: [1, numPoints, 2]
        ///   labels: [1, numPoints]
        /// 
        /// Coordinates are scaled from original image pixels (0..origW-1, 0..origH-1) to the model input scale (0.._imageSize-1).
        /// </summary>
        private (DenseTensor<float>, DenseTensor<float>) BuildSinglePromptTensors(List<AnnotationPoint> prompts, int origW, int origH, string targetMaterialName)
        {
            float xScale = (_imageSize - 1f) / Math.Max(1, origW - 1);
            float yScale = (_imageSize - 1f) / Math.Max(1, origH - 1);

            float[] coords = new float[prompts.Count * 2];
            float[] labels = new float[prompts.Count];

            for (int i = 0; i < prompts.Count; i++)
            {
                var p = prompts[i];
                // Clamp point within the image bounds
                float cx = Math.Max(0, Math.Min(p.X, origW - 1));
                float cy = Math.Max(0, Math.Min(p.Y, origH - 1));

                // Note: coords are [y, x] format for SAM, scaled to encoder resolution
                coords[i * 2 + 0] = cy * yScale;
                coords[i * 2 + 1] = cx * xScale;
                labels[i] = p.Label.Equals(targetMaterialName, StringComparison.OrdinalIgnoreCase) ? 1f : 0f;
            }

            var coordTensor = new DenseTensor<float>(coords, new[] { 1, prompts.Count, 2 });
            var labelTensor = new DenseTensor<float>(labels, new[] { 1, prompts.Count });
            return (coordTensor, labelTensor);
        }

        /// <summary>
        /// Converts the decoder's raw mask (float values 0..1) into a binary black/white Bitmap,
        /// then scales it to the desired output size (outW x outH).
        /// Each mask pixel is compared against MaskBinarizationThreshold (e.g. 0.5) to decide foreground vs background.
        /// </summary>
        private Bitmap BuildMaskFromDecoder(Tensor<float> masksTensor, int maskIndex, int maskW, int maskH, int outW, int outH)
        {
           
        
            // (DEBUG) Inspect min/max
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
            Logger.Log($"[DEBUG] Mask index={maskIndex}, min={minVal:F4}, max={maxVal:F4}");
            float threshold = MaskBinarizationThreshold;
            // Optional: matrix to store boolean mask bits (True = foreground)
            bool[,] maskBits = new bool[maskH, maskW];

            // 1. Reconstruct mask at model resolution (maskW x maskH), binarize with threshold
            Bitmap rawMask = new Bitmap(maskW, maskH, PixelFormat.Format24bppRgb);

            for (int yy = 0; yy < maskH; yy++)
            {
                for (int xx = 0; xx < maskW; xx++)
                {
                    float logit = masksTensor[0, maskIndex, yy, xx];
                    // Sigmoid
                    float prob = 1f / (1f + (float)Math.Exp(-logit));
                    if (prob > MaskBinarizationThreshold) // e.g. 0.5
                    {
                        rawMask.SetPixel(xx, yy, Color.White);
                    }
                    else
                    {
                        rawMask.SetPixel(xx, yy, Color.Black);
                    }
                }
            }

            // 2. Scale the mask up to the original image size
            Bitmap finalMask = new Bitmap(outW, outH, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(finalMask))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor; // preserve sharp edges
                g.DrawImage(rawMask, 0, 0, outW, outH);
            }
            rawMask.Dispose();
            return finalMask;
        }

        /// <summary>
        /// Linearly rescales a grayscale CT slice to enhance contrast (maps [min..max] intensity to [0..255]).
        /// If the input is already 8-bit and flat, returns it unchanged.
        /// </summary>
        private Bitmap RescaleCT(Bitmap rawCtSlice)
        {
            // 1. Find min and max grayscale values in the slice
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
            if (minVal >= maxVal)
            {
                // Flat image (no contrast range)
                return rawCtSlice;
            }

            // 2. Scale intensities to 0..255 range
            Bitmap scaled = new Bitmap(rawCtSlice.Width, rawCtSlice.Height);
            double range = maxVal - minVal;
            for (int y = 0; y < rawCtSlice.Height; y++)
            {
                for (int x = 0; x < rawCtSlice.Width; x++)
                {
                    byte g = rawCtSlice.GetPixel(x, y).R;
                    int newG = (int)(((g - minVal) / range) * 255.0);
                    newG = Math.Max(0, Math.Min(255, newG));
                    scaled.SetPixel(x, y, Color.FromArgb(newG, newG, newG));
                }
            }
            return scaled;
        }

        private Bitmap ApplyJetColormap(Bitmap graySlice)
        {
            // Map each gray value [0..255] to a color using the Jet colormap
            Color[] jetTable = BuildJetLookupTable();  // 256-color lookup table for Jet
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

        /// <summary>
        /// Builds a 256-color Jet colormap ranging from deep blue (index 0) through cyan/green/yellow to red (index 255).
        /// This approximates MATLAB's classic "Jet" colormap.
        /// </summary>
        private Color[] BuildJetLookupTable()
        {
            Color[] table = new Color[256];
            for (int i = 0; i < 256; i++)
            {
                float x = i / 255f;
                float r = 0f, g = 0f, b = 0f;
                if (x <= 0.35f)
                {
                    // Blue -> Cyan
                    float t = x / 0.35f;
                    r = 0f;
                    g = t;
                    b = 1f;
                }
                else if (x <= 0.65f)
                {
                    // Cyan -> Yellow
                    float t = (x - 0.35f) / (0.30f);
                    r = t;
                    g = 1f;
                    b = 1f - t;
                }
                else
                {
                    // Yellow -> Red
                    float t = (x - 0.65f) / 0.35f;
                    r = 1f;
                    g = 1f - t;
                    b = 0f;
                }
                table[i] = Color.FromArgb((int)(r * 255), (int)(g * 255), (int)(b * 255));
            }
            return table;
        }

        private float[] BitmapToFloatTensor(Bitmap bmp, int targetWidth, int targetHeight)
        {
            // Mean and standard deviation for normalization (precomputed for SAM's image encoder)
            float[] pixel_mean = new float[] { 123.675f, 116.28f, 103.53f };
            float[] pixel_std = new float[] { 58.395f, 57.12f, 57.375f };

            // Resize the image to the target dimensions
            Bitmap resized = new Bitmap(targetWidth, targetHeight);
            using (Graphics g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(bmp, 0, 0, targetWidth, targetHeight);
            }

            // Convert pixel values to float tensor with normalization
            float[] tensorData = new float[3 * targetWidth * targetHeight];
            int idx = 0;
            for (int y = 0; y < targetHeight; y++)
            {
                for (int x = 0; x < targetWidth; x++)
                {
                    Color c = resized.GetPixel(x, y);
                    // Note: We use RGB order in the tensor
                    float r = c.R;
                    float gVal = c.G;
                    float b = c.B;
                    tensorData[idx++] = (r - pixel_mean[0]) / pixel_std[0];
                    tensorData[idx++] = (gVal - pixel_mean[1]) / pixel_std[1];
                    tensorData[idx++] = (b - pixel_mean[2]) / pixel_std[2];
                }
            }
            resized.Dispose();
            return tensorData;
        }

        // Helper to fetch a named tensor from model outputs
        private static Tensor<T> GetFirstTensor<T>(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs, string name)
        {
            foreach (var o in outputs)
            {
                if (o.Name == name)
                    return o.AsTensor<T>();
            }
            throw new Exception($"Output '{name}' not found in model outputs.");
        }

        // Perform a simple morphological dilation on a binary mask bitmap
        public Bitmap Dilate(Bitmap input, int kernelSize = 3)
        {
            if (input == null) return null;
            int w = input.Width, h = input.Height;
            Bitmap output = new Bitmap(w, h, PixelFormat.Format24bppRgb);

            int r = kernelSize / 2;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool anyWhite = false;
                    for (int dy = -r; dy <= r; dy++)
                    {
                        for (int dx = -r; dx <= r; dx++)
                        {
                            int nx = x + dx;
                            int ny = y + dy;
                            if (nx >= 0 && nx < w && ny >= 0 && ny < h)
                            {
                                if (input.GetPixel(nx, ny).R > 128)
                                {
                                    anyWhite = true;
                                    break;
                                }
                            }
                        }
                        if (anyWhite) break;
                    }
                    output.SetPixel(x, y, anyWhite ? Color.White : Color.Black);
                }
            }
            return output;
        }

        // Calculate percentage of white (foreground) pixels in a binary mask
        public float CalculateCoverage(Bitmap bmp)
        {
            if (bmp == null) return 0f;
            int w = bmp.Width, h = bmp.Height;
            int total = w * h;
            int whiteCount = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (bmp.GetPixel(x, y).R > 128) whiteCount++;
                }
            }
            return 100f * whiteCount / total;
        }
    }
}
