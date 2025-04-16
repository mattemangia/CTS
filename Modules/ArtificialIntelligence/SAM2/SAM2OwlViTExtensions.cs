using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CTSegmenter
{
    // Extension methods for SegmentAnythingCT class
    public partial class SegmentAnythingCT
    {
        /// <summary>
        /// Performs segmentation using a bounding box as input by placing strategic prompt points
        /// </summary>
        /// <param name="box">Bounding box in normalized coordinates (0-1)</param>
        /// <returns>A task representing the segmentation operation</returns>
        public async Task PerformSegmentationWithBox(RectangleF box)
        {
            if (encoderSession == null || decoderSession == null)
            {
                MessageBox.Show("Models not loaded. Please load models first.");
                return;
            }

            // Ensure UI is updated from the UI thread
            Action updateStatus = () => statusLabel.Text = "Segmenting with box...";
            if (samForm != null)
            {
                if (samForm.InvokeRequired)
                    samForm.Invoke(updateStatus);
                else
                    updateStatus();
            }

            Logger.Log($"[SegmentAnythingCT] Starting segmentation with box on {currentActiveView} view");

            try
            {
                // Create a tensor with the image data
                Tensor<float> imageInput = await Task.Run(() => PreprocessImage());

                // Create input and run the encoder on a background thread
                var encoderInputs = new List<NamedOnnxValue> {
                    NamedOnnxValue.CreateFromTensor("image", imageInput)
                };

                var encoderOutputs = await Task.Run(() => encoderSession.Run(encoderInputs));

                try
                {
                    // Extract encoder outputs
                    var imageEmbed = encoderOutputs.First(x => x.Name == "image_embed").AsTensor<float>();
                    var highResFeats0 = encoderOutputs.First(x => x.Name == "high_res_feats_0").AsTensor<float>();
                    var highResFeats1 = encoderOutputs.First(x => x.Name == "high_res_feats_1").AsTensor<float>();

                    // Scale factors depend on the active view
                    float scaleX, scaleY;
                    switch (currentActiveView)
                    {
                        case ActiveView.XY:
                            scaleX = 1024.0f / mainForm.GetWidth();
                            scaleY = 1024.0f / mainForm.GetHeight();
                            break;
                        case ActiveView.XZ:
                            scaleX = 1024.0f / mainForm.GetWidth();
                            scaleY = 1024.0f / mainForm.GetDepth();
                            break;
                        case ActiveView.YZ:
                            scaleX = 1024.0f / mainForm.GetDepth();
                            scaleY = 1024.0f / mainForm.GetHeight();
                            break;
                        default:
                            scaleX = scaleY = 1.0f;
                            break;
                    }

                    // Create points from the box - using a grid of 9 points across the box
                    // This gives better coverage than just using center or corners
                    const int numPoints = 9;
                    DenseTensor<float> pointCoords = new DenseTensor<float>(new[] { 1, numPoints, 2 });
                    DenseTensor<float> pointLabels = new DenseTensor<float>(new[] { 1, numPoints });

                    // Calculate normalized box coordinates
                    float x1 = box.Left;
                    float y1 = box.Top;
                    float x2 = box.Left + box.Width;
                    float y2 = box.Top + box.Height;

                    // Create a 3x3 grid of points within the box
                    int pointIndex = 0;
                    for (int i = 0; i < 3; i++)
                    {
                        for (int j = 0; j < 3; j++)
                        {
                            // Calculate position within box (0.25, 0.5, 0.75 for each dimension)
                            float xPos = x1 + box.Width * (0.25f + 0.25f * i);
                            float yPos = y1 + box.Height * (0.25f + 0.25f * j);

                            // Apply scaling
                            pointCoords[0, pointIndex, 0] = xPos * scaleX;
                            pointCoords[0, pointIndex, 1] = yPos * scaleY;

                            // Set all points as positive (1.0)
                            pointLabels[0, pointIndex] = 1.0f;

                            pointIndex++;
                        }
                    }

                    Logger.Log($"[SegmentAnythingCT] Created {numPoints} prompt points from box");

                    // Rest of tensor preparation
                    DenseTensor<float> maskInput = new DenseTensor<float>(new[] { 1, 1, 256, 256 });
                    DenseTensor<float> hasMaskInput = new DenseTensor<float>(new[] { 1 });
                    hasMaskInput[0] = 0; // 0 means no mask input

                    // Original image size depends on active view
                    int origWidth, origHeight;
                    switch (currentActiveView)
                    {
                        case ActiveView.XY:
                            origWidth = mainForm.GetWidth();
                            origHeight = mainForm.GetHeight();
                            break;
                        case ActiveView.XZ:
                            origWidth = mainForm.GetWidth();
                            origHeight = mainForm.GetDepth();
                            break;
                        case ActiveView.YZ:
                            origWidth = mainForm.GetDepth();
                            origHeight = mainForm.GetHeight();
                            break;
                        default:
                            origWidth = mainForm.GetWidth();
                            origHeight = mainForm.GetHeight();
                            break;
                    }

                    DenseTensor<int> origImSize = new DenseTensor<int>(new[] { 2 });
                    origImSize[0] = origHeight;
                    origImSize[1] = origWidth;

                    // Run decoder on background thread with the grid of points
                    var decoderInputs = new List<NamedOnnxValue> {
                        NamedOnnxValue.CreateFromTensor("image_embed", imageEmbed),
                        NamedOnnxValue.CreateFromTensor("high_res_feats_0", highResFeats0),
                        NamedOnnxValue.CreateFromTensor("high_res_feats_1", highResFeats1),
                        NamedOnnxValue.CreateFromTensor("point_coords", pointCoords),
                        NamedOnnxValue.CreateFromTensor("point_labels", pointLabels),
                        NamedOnnxValue.CreateFromTensor("mask_input", maskInput),
                        NamedOnnxValue.CreateFromTensor("has_mask_input", hasMaskInput),
                        NamedOnnxValue.CreateFromTensor("orig_im_size", origImSize)
                    };

                    var decoderOutputs = await Task.Run(() => decoderSession.Run(decoderInputs));

                    try
                    {
                        // Process decoder outputs
                        byte[,] tempMask = null;
                        float bestIoU = 0;

                        await Task.Run(() => {
                            var masks = decoderOutputs.First(x => x.Name == "masks").AsTensor<float>();
                            var iouPredictions = decoderOutputs.First(x => x.Name == "iou_predictions").AsTensor<float>();

                            // Save masks for debugging
                            SaveAllMasks(masks, iouPredictions);

                            int bestMaskIdx = 0;
                            bestIoU = iouPredictions[0, 0];

                            for (int i = 1; i < iouPredictions.Dimensions[1]; i++)
                            {
                                if (iouPredictions[0, i] > bestIoU)
                                {
                                    bestIoU = iouPredictions[0, i];
                                    bestMaskIdx = i;
                                }
                            }

                            Logger.Log($"[SegmentAnythingCT] Best mask with box prompts IoU: {bestIoU}");

                            // Create mask with dimensions appropriate for the active view
                            switch (currentActiveView)
                            {
                                case ActiveView.XY:
                                    tempMask = new byte[mainForm.GetWidth(), mainForm.GetHeight()];
                                    for (int y = 0; y < mainForm.GetHeight(); y++)
                                    {
                                        for (int x = 0; x < mainForm.GetWidth(); x++)
                                        {
                                            tempMask[x, y] = masks[0, bestMaskIdx, y, x] > 0.0f ? selectedMaterial.ID : (byte)0;
                                        }
                                    }
                                    break;

                                case ActiveView.XZ:
                                    tempMask = new byte[mainForm.GetWidth(), mainForm.GetDepth()];
                                    for (int z = 0; z < mainForm.GetDepth(); z++)
                                    {
                                        for (int x = 0; x < mainForm.GetWidth(); x++)
                                        {
                                            tempMask[x, z] = masks[0, bestMaskIdx, z, x] > 0.0f ? selectedMaterial.ID : (byte)0;
                                        }
                                    }
                                    break;

                                case ActiveView.YZ:
                                    tempMask = new byte[mainForm.GetDepth(), mainForm.GetHeight()];
                                    for (int y = 0; y < mainForm.GetHeight(); y++)
                                    {
                                        for (int z = 0; z < mainForm.GetDepth(); z++)
                                        {
                                            tempMask[z, y] = masks[0, bestMaskIdx, y, z] > 0.0f ? selectedMaterial.ID : (byte)0;
                                        }
                                    }
                                    break;
                            }
                        });

                        // Update UI on the UI thread
                        if (samForm != null)
                        {
                            samForm.Invoke(new Action(() => {
                                segmentationMask = tempMask;
                                UpdateViewers();
                                statusLabel.Text = $"Segmentation with box complete (IoU: {bestIoU:F3})";
                            }));
                        }
                        else
                        {
                            // Handle headless mode
                            segmentationMask = tempMask;
                        }

                        Logger.Log("[SegmentAnythingCT] Segmentation with box complete");
                    }
                    finally
                    {
                        decoderOutputs.Dispose();
                    }
                }
                finally
                {
                    encoderOutputs.Dispose();
                }
            }
            catch (Exception ex)
            {
                if (samForm != null)
                {
                    samForm.Invoke(new Action(() => {
                        MessageBox.Show($"Error during segmentation with box: {ex.Message}");
                        statusLabel.Text = $"Error: {ex.Message}";
                    }));
                }

                Logger.Log($"[SegmentAnythingCT] Segmentation with box error: {ex.Message}");
            }
        }

        /// <summary>
        /// Segments a slice using a bounding box with optional mask guidance by placing prompt points
        /// </summary>
        private async Task<byte[,]> SegmentSliceWithBox(int sliceZ, RectangleF box, byte[,] previousMask = null)
        {
            try
            {
                // Check if we can reuse cached features from a nearby slice
                bool useCache = Math.Abs(sliceZ - cachedFeatureSlice) <= featureCacheRadius &&
                                cachedImageEmbed != null &&
                                cachedHighResFeats0 != null &&
                                cachedHighResFeats1 != null;

                DenseTensor<float> imageEmbed;
                DenseTensor<float> highResFeats0;
                DenseTensor<float> highResFeats1;

                if (!useCache)
                {
                    // Need to run the encoder for this slice
                    Tensor<float> imageInput = await Task.Run(() => PreprocessSliceImage(sliceZ));

                    var encoderInputs = new List<NamedOnnxValue> {
                        NamedOnnxValue.CreateFromTensor("image", imageInput)
                    };

                    var encoderOutputs = await Task.Run(() => encoderSession.Run(encoderInputs));

                    try
                    {
                        // Extract encoder outputs
                        var imageEmbedTensor = encoderOutputs.First(x => x.Name == "image_embed").AsTensor<float>();
                        var highResFeats0Tensor = encoderOutputs.First(x => x.Name == "high_res_feats_0").AsTensor<float>();
                        var highResFeats1Tensor = encoderOutputs.First(x => x.Name == "high_res_feats_1").AsTensor<float>();

                        // Create new tensors to store cached values
                        cachedImageEmbed = new DenseTensor<float>(imageEmbedTensor.Dimensions);
                        cachedHighResFeats0 = new DenseTensor<float>(highResFeats0Tensor.Dimensions);
                        cachedHighResFeats1 = new DenseTensor<float>(highResFeats1Tensor.Dimensions);

                        // Copy tensor data
                        CopyTensorData(imageEmbedTensor, cachedImageEmbed);
                        CopyTensorData(highResFeats0Tensor, cachedHighResFeats0);
                        CopyTensorData(highResFeats1Tensor, cachedHighResFeats1);

                        cachedFeatureSlice = sliceZ;

                        // Use the cached tensors for this run
                        imageEmbed = cachedImageEmbed;
                        highResFeats0 = cachedHighResFeats0;
                        highResFeats1 = cachedHighResFeats1;
                    }
                    finally
                    {
                        // Make sure to dispose of the encoder outputs
                        foreach (var output in encoderOutputs)
                        {
                            output.Dispose();
                        }
                    }
                }
                else
                {
                    // Use cached features
                    imageEmbed = cachedImageEmbed;
                    highResFeats0 = cachedHighResFeats0;
                    highResFeats1 = cachedHighResFeats1;
                    Logger.Log($"[SegmentAnythingCT] Using cached features for slice {sliceZ} from slice {cachedFeatureSlice}");
                }

                // Get dimensions
                int width = mainForm.GetWidth();
                int height = mainForm.GetHeight();
                float scaleX = 1024.0f / width;
                float scaleY = 1024.0f / height;

                // Create points from the box - using a grid of 9 points across the box
                const int numPoints = 9;
                DenseTensor<float> pointCoords = new DenseTensor<float>(new[] { 1, numPoints, 2 });
                DenseTensor<float> pointLabels = new DenseTensor<float>(new[] { 1, numPoints });

                // Calculate normalized box coordinates
                float x1 = box.Left;
                float y1 = box.Top;
                float x2 = box.Left + box.Width;
                float y2 = box.Top + box.Height;

                // Create a 3x3 grid of points within the box
                int pointIndex = 0;
                for (int i = 0; i < 3; i++)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        // Calculate position within box (0.25, 0.5, 0.75 for each dimension)
                        float xPos = x1 + box.Width * (0.25f + 0.25f * i);
                        float yPos = y1 + box.Height * (0.25f + 0.25f * j);

                        // Apply scaling
                        pointCoords[0, pointIndex, 0] = xPos * scaleX;
                        pointCoords[0, pointIndex, 1] = yPos * scaleY;

                        // Set all points as positive (1.0)
                        pointLabels[0, pointIndex] = 1.0f;

                        pointIndex++;
                    }
                }

                // Prepare mask input if we have a previous mask
                DenseTensor<float> maskInput = new DenseTensor<float>(new[] { 1, 1, 256, 256 });
                DenseTensor<float> hasMaskInput = new DenseTensor<float>(new[] { 1 });

                if (previousMask != null)
                {
                    // Resize previous mask to 256x256
                    float maskScaleX = 256.0f / width;
                    float maskScaleY = 256.0f / height;

                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            if (x < previousMask.GetLength(0) && y < previousMask.GetLength(1) && previousMask[x, y] > 0)
                            {
                                int targetX = (int)(x * maskScaleX);
                                int targetY = (int)(y * maskScaleY);

                                if (targetX < 256 && targetY < 256)
                                {
                                    maskInput[0, 0, targetY, targetX] = 1.0f;
                                }
                            }
                        }
                    }

                    hasMaskInput[0] = 1; // 1 means we're using a mask
                }
                else
                {
                    hasMaskInput[0] = 0; // 0 means no mask input
                }

                // Original image size tensor
                DenseTensor<int> origImSize = new DenseTensor<int>(new[] { 2 });
                origImSize[0] = height;
                origImSize[1] = width;

                // Run decoder with box prompts
                var decoderInputs = new List<NamedOnnxValue> {
                    NamedOnnxValue.CreateFromTensor("image_embed", imageEmbed),
                    NamedOnnxValue.CreateFromTensor("high_res_feats_0", highResFeats0),
                    NamedOnnxValue.CreateFromTensor("high_res_feats_1", highResFeats1),
                    NamedOnnxValue.CreateFromTensor("point_coords", pointCoords),
                    NamedOnnxValue.CreateFromTensor("point_labels", pointLabels),
                    NamedOnnxValue.CreateFromTensor("mask_input", maskInput),
                    NamedOnnxValue.CreateFromTensor("has_mask_input", hasMaskInput),
                    NamedOnnxValue.CreateFromTensor("orig_im_size", origImSize)
                };

                var decoderOutputs = await Task.Run(() => decoderSession.Run(decoderInputs));

                try
                {
                    byte[,] resultMask = null;

                    await Task.Run(() => {
                        var masks = decoderOutputs.First(x => x.Name == "masks").AsTensor<float>();
                        var iouPredictions = decoderOutputs.First(x => x.Name == "iou_predictions").AsTensor<float>();

                        // Find best mask
                        int bestMaskIdx = 0;
                        float bestIoU = iouPredictions[0, 0];

                        for (int i = 1; i < iouPredictions.Dimensions[1]; i++)
                        {
                            if (iouPredictions[0, i] > bestIoU)
                            {
                                bestIoU = iouPredictions[0, i];
                                bestMaskIdx = i;
                            }
                        }

                        // For boundary slices, reduce the IoU threshold
                        float minIoU = 0.5f;

                        // If we're at the beginning or end of the volume, use lower threshold
                        int maxSlice = mainForm.GetDepth() - 1;
                        if (sliceZ <= 3 || sliceZ >= maxSlice - 3)
                        {
                            minIoU = 0.3f;  // Lower threshold for boundary slices
                        }

                        // If IoU is too low, stop propagation (with adjusted threshold for boundary cases)
                        if (bestIoU < minIoU)
                        {
                            Logger.Log($"[SegmentAnythingCT] Stopping at slice {sliceZ} due to low IoU ({bestIoU:F3})");
                            return;
                        }

                        Logger.Log($"[SegmentAnythingCT] Slice {sliceZ} mask IoU: {bestIoU:F3}");

                        // Convert mask to byte array
                        resultMask = new byte[width, height];
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                if (masks[0, bestMaskIdx, y, x] > 0.0f)
                                {
                                    resultMask[x, y] = selectedMaterial.ID;
                                }
                            }
                        }
                    });

                    return resultMask;
                }
                finally
                {
                    // Dispose decoder outputs
                    foreach (var output in decoderOutputs)
                    {
                        output.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SegmentAnythingCT] Error segmenting slice {sliceZ} with box: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Performs segmentation on a volume using a bounding box prompt
        /// </summary>
        /// <param name="box">The bounding box in normalized coordinates (0-1)</param>
        /// <param name="startSlice">The start slice for segmentation</param>
        /// <param name="endSlice">The end slice for segmentation</param>
        /// <param name="materialID">The material ID to use for segmentation</param>
        /// <returns>A task representing the asynchronous operation</returns>
        public async Task SegmentVolumeWithBox(RectangleF box, int startSlice, int endSlice, byte materialID)
        {
            try
            {
                // Create progress form
                ProgressForm progressForm = new ProgressForm("Segmenting volume with box...");
                progressForm.Show();

                int totalSlices = endSlice - startSlice + 1;
                int processedSlices = 0;

                // Start with the middle slice for better propagation
                int middleSlice = (startSlice + endSlice) / 2;
                byte[,] currentMask = await SegmentSliceWithBox(middleSlice, box);

                if (currentMask == null)
                {
                    MessageBox.Show("Failed to segment initial slice with box.");
                    progressForm.Close();
                    return;
                }

                // Apply mask to the middle slice
                ApplyMaskToVolume(currentMask, middleSlice, materialID);

                // Update progress
                processedSlices++;
                progressForm.UpdateProgress(processedSlices, totalSlices, $"Processed slice {middleSlice}");

                // Propagate forward from middle slice
                byte[,] forwardMask = currentMask;
                for (int slice = middleSlice + 1; slice <= endSlice; slice++)
                {
                    // Update progress
                    processedSlices++;
                    progressForm.SafeUpdateProgress(processedSlices, totalSlices, $"Processing slice {slice} (forward)...");

                    // Segment this slice using previous mask as guidance and box
                    forwardMask = await SegmentSliceWithBox(slice, box, forwardMask);

                    // If segmentation failed or mask is empty, stop
                    if (forwardMask == null)
                    {
                        Logger.Log($"[SegmentAnythingCT] Forward propagation stopped at slice {slice}");
                        break;
                    }

                    // Apply mask to volume
                    ApplyMaskToVolume(forwardMask, slice, materialID);
                }

                // Propagate backward from middle slice
                byte[,] backwardMask = currentMask;
                for (int slice = middleSlice - 1; slice >= startSlice; slice--)
                {
                    // Update progress
                    processedSlices++;
                    progressForm.SafeUpdateProgress(processedSlices, totalSlices, $"Processing slice {slice} (backward)...");

                    // Segment this slice using previous mask as guidance and box
                    backwardMask = await SegmentSliceWithBox(slice, box, backwardMask);

                    // If segmentation failed or mask is empty, stop
                    if (backwardMask == null)
                    {
                        Logger.Log($"[SegmentAnythingCT] Backward propagation stopped at slice {slice}");
                        break;
                    }

                    // Apply mask to volume
                    ApplyMaskToVolume(backwardMask, slice, materialID);
                }

                // Update MainForm's views
                mainForm.RenderViews();
                await mainForm.RenderOrthoViewsAsync();
                mainForm.SaveLabelsChk();

                progressForm.Close();
                MessageBox.Show("Volume segmentation with box complete!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during volume segmentation with box: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[SegmentAnythingCT] Volume segmentation with box error: {ex.Message}");
            }
        }

        /// <summary>
        /// Performs segmentation using a detection result from OwlViTDetector
        /// </summary>
        /// <param name="detection">The detection result containing the bounding box</param>
        /// <returns>A task representing the asynchronous operation</returns>
        public async Task SegmentWithDetection(OwlVitDetector.DetectionResult detection)
        {
            if (detection == null)
            {
                MessageBox.Show("No detection provided.");
                return;
            }

            try
            {
                // Set the current slice to the detection slice
                xySlice = detection.Slice;
                UpdateSliceControls();

                // Create a RectangleF from the normalized coordinates
                RectangleF box = new RectangleF(
                    detection.X,
                    detection.Y,
                    detection.Width,
                    detection.Height
                );

                // Perform segmentation with the box
                await PerformSegmentationWithBox(box);

                // Update the UI
                UpdateViewers();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error segmenting with detection: {ex.Message}");
                Logger.Log($"[SegmentAnythingCT] Error segmenting with detection: {ex.Message}");
            }
        }

        /// <summary>
        /// Segments a volume using a detection result from OwlViTDetector
        /// </summary>
        /// <param name="detection">The detection result</param>
        /// <param name="sliceRange">Number of slices to segment in each direction</param>
        /// <param name="materialID">Material ID to use</param>
        /// <returns>A task representing the asynchronous operation</returns>
        public async Task SegmentVolumeWithDetection(OwlVitDetector.DetectionResult detection, int sliceRange, byte materialID)
        {
            if (detection == null)
            {
                MessageBox.Show("No detection provided.");
                return;
            }

            try
            {
                // Calculate slice range
                int startSlice = Math.Max(0, detection.Slice - sliceRange);
                int endSlice = Math.Min(mainForm.GetDepth() - 1, detection.Slice + sliceRange);

                // Create a RectangleF from the normalized coordinates
                RectangleF box = new RectangleF(
                    detection.X,
                    detection.Y,
                    detection.Width,
                    detection.Height
                );

                // Perform volume segmentation with the box
                await SegmentVolumeWithBox(box, startSlice, endSlice, materialID);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error segmenting volume with detection: {ex.Message}");
                Logger.Log($"[SegmentAnythingCT] Error segmenting volume with detection: {ex.Message}");
            }
        }
    }
}