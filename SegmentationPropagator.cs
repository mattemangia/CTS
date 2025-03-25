using System;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Drawing.Imaging;

namespace CTSegmenter
{
    /// <summary>
    /// Static class providing 3D segmentation propagation functionality using both SAM and SAM 2.1
    /// </summary>
    public static class SegmentationPropagator
    {
        /// <summary>
        /// Propagates segmentation in selected directions based on a starting slice
        /// </summary>
        /// <param name="mainForm">The main form with volume data</param>
        /// <param name="settings">SAM settings</param>
        /// <param name="threshold">Segmentation threshold</param>
        /// <param name="directions">Directions to propagate</param>
        /// <returns>The segmented volume or null if propagation failed</returns>
        public static byte[,,] Propagate(MainForm mainForm, SAMSettingsParams settings,
                                        int threshold, SAMForm.SegmentationDirection directions)
        {
            // Get volume dimensions
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();
            Logger.Log($"[SegmentationPropagator] Volume dimensions: {width}x{height}x{depth}");

            // Initialize model paths
            string modelFolder = settings.ModelFolderPath;
            int imageSize = settings.ImageInputSize;
            bool usingSam2 = settings.UseSam2Models;
            Logger.Log($"[SegmentationPropagator] Using SAM 2.1 models: {usingSam2}");

            string imageEncoderPath, promptEncoderPath, maskDecoderPath;
            string memoryEncoderPath, memoryAttentionPath, mlpPath;

            if (usingSam2)
            {
                // SAM 2.1 paths
                imageEncoderPath = Path.Combine(modelFolder, "sam2.1_large.encoder.onnx");
                promptEncoderPath = ""; // Not used in SAM 2.1
                maskDecoderPath = Path.Combine(modelFolder, "sam2.1_large.decoder.onnx");
                memoryEncoderPath = ""; // Not used in SAM 2.1
                memoryAttentionPath = ""; // Not used in SAM 2.1
                mlpPath = ""; // Not used in SAM 2.1

                // Validate SAM 2.1 models
                if (!File.Exists(imageEncoderPath) || !File.Exists(maskDecoderPath))
                {
                    string missingModels = "";
                    if (!File.Exists(imageEncoderPath)) missingModels += $"- SAM 2.1 encoder: {imageEncoderPath}\n";
                    if (!File.Exists(maskDecoderPath)) missingModels += $"- SAM 2.1 decoder: {maskDecoderPath}\n";

                    Logger.Log($"[SegmentationPropagator] SAM 2.1 models missing: {missingModels}");
                    return null;
                }
            }
            else
            {
                // Original SAM paths
                imageEncoderPath = Path.Combine(modelFolder, "image_encoder_hiera_t.onnx");
                promptEncoderPath = Path.Combine(modelFolder, "prompt_encoder_hiera_t.onnx");
                maskDecoderPath = Path.Combine(modelFolder, "mask_decoder_hiera_t.onnx");
                memoryEncoderPath = Path.Combine(modelFolder, "memory_encoder_hiera_t.onnx");
                memoryAttentionPath = Path.Combine(modelFolder, "memory_attention_hiera_t.onnx");
                mlpPath = Path.Combine(modelFolder, "mlp_hiera_t.onnx");

                // Validate original SAM models
                if (!File.Exists(imageEncoderPath) || !File.Exists(promptEncoderPath) || !File.Exists(maskDecoderPath))
                {
                    string missingModels = "";
                    if (!File.Exists(imageEncoderPath)) missingModels += $"- Image encoder: {imageEncoderPath}\n";
                    if (!File.Exists(promptEncoderPath)) missingModels += $"- Prompt encoder: {promptEncoderPath}\n";
                    if (!File.Exists(maskDecoderPath)) missingModels += $"- Mask decoder: {maskDecoderPath}\n";

                    Logger.Log($"[SegmentationPropagator] Original SAM models missing: {missingModels}");
                    return null;
                }
            }

            // Create folder for saving debugging masks if needed
            string saveFolder = Path.Combine(Application.StartupPath, "SavedMasks");
            Directory.CreateDirectory(saveFolder);

            // Track which directions have been propagated 
            Dictionary<string, byte[,,]> directionResults = new Dictionary<string, byte[,,]>();

            using (var segmenter = new CTMemorySegmenter(
                imageEncoderPath,
                promptEncoderPath,
                maskDecoderPath,
                memoryEncoderPath,
                memoryAttentionPath,
                mlpPath,
                imageSize,
                false,
                settings.EnableMlp,
                settings.UseCpuExecutionProvider))
            {
                segmenter.UseSelectiveHoleFilling = settings.UseSelectiveHoleFilling;
                segmenter.MaskBinarizationThreshold = threshold / 255f;


                // Enable embedding storage for SAM 2.1 propagation
                segmenter.StorePreviousEmbeddings = true;

                if (usingSam2)
                {
                    // SAM 2.1 propagation methods
                    if (directions.HasFlag(SAMForm.SegmentationDirection.XY))
                    {
                        byte[,,] xyResult = PropagateSam2XYDirection(segmenter, mainForm, width, height, depth, threshold);
                        if (xyResult != null)
                        {
                            directionResults["XY"] = xyResult;
                            Logger.Log("[SegmentationPropagator] SAM 2.1 XY direction propagation completed");
                        }
                    }

                    if (directions.HasFlag(SAMForm.SegmentationDirection.XZ))
                    {
                        byte[,,] xzResult = PropagateSam2XZDirection(segmenter, mainForm, width, height, depth, threshold);
                        if (xzResult != null)
                        {
                            directionResults["XZ"] = xzResult;
                            Logger.Log("[SegmentationPropagator] SAM 2.1 XZ direction propagation completed");
                        }
                    }

                    if (directions.HasFlag(SAMForm.SegmentationDirection.YZ))
                    {
                        byte[,,] yzResult = PropagateSam2YZDirection(segmenter, mainForm, width, height, depth, threshold);
                        if (yzResult != null)
                        {
                            directionResults["YZ"] = yzResult;
                            Logger.Log("[SegmentationPropagator] SAM 2.1 YZ direction propagation completed");
                        }
                    }
                }
                else
                {
                    // Original SAM propagation methods
                    if (directions.HasFlag(SAMForm.SegmentationDirection.XY))
                    {
                        byte[,,] xyResult = PropagateXYDirection(segmenter, mainForm, width, height, depth, threshold);
                        if (xyResult != null)
                        {
                            directionResults["XY"] = xyResult;
                            Logger.Log("[SegmentationPropagator] XY direction propagation completed");
                        }
                    }

                    if (directions.HasFlag(SAMForm.SegmentationDirection.XZ))
                    {
                        byte[,,] xzResult = PropagateXZDirection(segmenter, mainForm, width, height, depth, threshold);
                        if (xzResult != null)
                        {
                            directionResults["XZ"] = xzResult;
                            Logger.Log("[SegmentationPropagator] XZ direction propagation completed");
                        }
                    }

                    if (directions.HasFlag(SAMForm.SegmentationDirection.YZ))
                    {
                        byte[,,] yzResult = PropagateYZDirection(segmenter, mainForm, width, height, depth, threshold);
                        if (yzResult != null)
                        {
                            directionResults["YZ"] = yzResult;
                            Logger.Log("[SegmentationPropagator] YZ direction propagation completed");
                        }
                    }
                }
            }

            // Apply fusion if multiple directions were propagated
            if (directionResults.Count > 1)
            {
                Logger.Log($"[SegmentationPropagator] Applying fusion for {directionResults.Count} directions using {settings.FusionAlgorithm}");
                return ApplyFusion(directionResults, width, height, depth, settings.FusionAlgorithm, mainForm);
            }
            else if (directionResults.Count == 1)
            {
                // If only one direction was propagated, use it directly
                string direction = directionResults.Keys.First();
                Logger.Log($"[SegmentationPropagator] Applying results from {direction} direction only");
                return directionResults[direction];
            }

            // No propagation was successful
            return null;
        }

        #region SAM 2.1 Propagation Methods

        /// <summary>
        /// Propagates segmentation in the XY direction using SAM 2.1 models
        /// </summary>
        private static byte[,,] PropagateSam2XYDirection(CTMemorySegmenter segmenter, MainForm mainForm,
                                                 int width, int height, int depth, int threshold)
        {
            // Find a segmented slice in XY direction
            int startZ = FindSegmentedXYSlice(mainForm, width, height, depth);
            if (startZ == -1)
            {
                Logger.Log("[SegmentationPropagator] No segmented XY slice found to start propagation");
                return null;
            }

            Logger.Log($"[SegmentationPropagator] Found segmented XY slice at Z={startZ}");

            // Create a new volume to store results
            byte[,,] resultVolume = new byte[width, height, depth];

            // First, copy the existing segmentation
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        resultVolume[x, y, z] = mainForm.volumeLabels[x, y, z];
                    }
                }
            }

            // Get all materials in the segmented slice
            var materials = GetMaterialsInXYSlice(mainForm, width, height, startZ);
            if (materials.Count == 0)
            {
                Logger.Log("[SegmentationPropagator] No materials found in the segmented XY slice");
                return null;
            }

            Logger.Log($"[SegmentationPropagator] Found {materials.Count} materials in XY slice: {string.Join(", ", materials.Select(m => m.Name))}");

            // For each material, propagate forward and/or backward as needed
            foreach (var material in materials)
            {
                Logger.Log($"[SegmentationPropagator] Propagating material '{material.Name}' (ID: {material.ID})");

                // Check if we're at boundaries and only propagate in valid directions
                bool canPropagateForward = startZ < depth - 1;
                bool canPropagateBackward = startZ > 0;

                if (canPropagateForward)
                {
                    Logger.Log($"[SegmentationPropagator] Propagating forward from Z={startZ}");
                    PropagateSam2XYForward(segmenter, mainForm, width, height, depth, startZ, material, resultVolume, threshold);
                }
                else
                {
                    Logger.Log($"[SegmentationPropagator] At last slice Z={startZ}, skipping forward propagation");
                }

                if (canPropagateBackward)
                {
                    Logger.Log($"[SegmentationPropagator] Propagating backward from Z={startZ}");
                    PropagateSam2XYBackward(segmenter, mainForm, width, height, depth, startZ, material, resultVolume, threshold);
                }
                else
                {
                    Logger.Log($"[SegmentationPropagator] At first slice Z={startZ}, skipping backward propagation");
                }
            }

            return resultVolume;
        }

        /// <summary>
        /// Propagates SAM 2.1 segmentation in forward direction (increasing Z) for XY slices
        /// </summary>
        private static void PropagateSam2XYForward(CTMemorySegmenter segmenter, MainForm mainForm,
                                         int width, int height, int depth,
                                         int startZ, Material material, byte[,,] volumeLabels, int threshold)
        {
            // Safety check - don't try to propagate from the last slice
            if (startZ >= depth - 1)
            {
                Logger.Log($"[SegmentationPropagator] Cannot propagate forward from Z={startZ} (last slice)");
                return;
            }

            Logger.Log($"[SegmentationPropagator] Propagating material '{material.Name}' forward from Z={startZ}");

            // Keep track of the previous slice's points for consistency
            List<AnnotationPoint> prevPoints = null;
            int maxStepsWithoutSegmentation = 3; // Allow up to 3 slices with no segmentation before stopping
            int stepsWithoutSegmentation = 0;

            for (int z = startZ + 1; z < depth; z++)
            {
                Logger.Log($"[SegmentationPropagator] Processing forward slice Z={z}");

                // Generate base image for current slice
                using (Bitmap baseImage = GenerateXYBitmap(mainForm, z, width, height))
                {
                    // Create annotation points from previous slice's segmentation
                    List<AnnotationPoint> points;

                    if (prevPoints == null)
                    {
                        // First slice after start - sample points from the segmented slice
                        points = SamplePointsFromVolume(mainForm, width, height, z - 1, volumeLabels, material.ID);
                    }
                    else
                    {
                        // Use previous slice's points with updated Z coordinate
                        points = prevPoints.Select(p => new AnnotationPoint
                        {
                            X = p.X,
                            Y = p.Y,
                            Z = z,
                            Label = p.Label  // Preserve the original material label
                        }).ToList();
                    }

                    if (points.Count == 0)
                    {
                        Logger.Log($"[SegmentationPropagator] No points found for material '{material.Name}' in slice Z={z - 1}, stopping forward propagation");
                        break;
                    }

                    // Convert material-labeled points to the SAM-expected format (Foreground/Exterior)
                    List<AnnotationPoint> samPoints = BuildMixedPrompts(points, material.Name);

                    // Process the slice using SAM 2.1
                    using (Bitmap mask = segmenter.ProcessXYSlice(z, baseImage, samPoints, material.Name))
                    {
                        // Apply the mask to the volume
                        int pixelsSegmented = ApplyMaskToVolume(mask, width, height, z, material.ID, volumeLabels, threshold);
                        Logger.Log($"[SegmentationPropagator] Segmented {pixelsSegmented} pixels for material '{material.Name}' in slice Z={z}");

                        // If nothing was segmented, track but continue for a few more slices
                        if (pixelsSegmented == 0)
                        {
                            stepsWithoutSegmentation++;
                            Logger.Log($"[SegmentationPropagator] No pixels segmented in slice Z={z}, steps without segmentation: {stepsWithoutSegmentation}");

                            if (stepsWithoutSegmentation >= maxStepsWithoutSegmentation)
                            {
                                Logger.Log($"[SegmentationPropagator] Reached {maxStepsWithoutSegmentation} steps without segmentation, stopping forward propagation");
                                break;
                            }
                        }
                        else
                        {
                            // Reset counter when we get successful segmentation
                            stepsWithoutSegmentation = 0;
                        }

                        // Update points for next iteration
                        List<AnnotationPoint> newPoints = SamplePointsFromNewSegmentation(mainForm, mask, z, material.ID, threshold);

                        // If we have meaningful new points, use them, otherwise keep using previous points
                        if (newPoints.Count >= 5)
                        {
                            prevPoints = newPoints;
                        }
                        // If very few points, augment with some from previous slice
                        else if (newPoints.Count > 0 && prevPoints != null)
                        {
                            // Keep some previous points but update their Z coordinate
                            var augPoints = prevPoints.Take(Math.Min(10, prevPoints.Count))
                                .Select(p => new AnnotationPoint
                                {
                                    X = p.X,
                                    Y = p.Y,
                                    Z = z,
                                    Label = p.Label
                                }).ToList();

                            // Add the new points
                            augPoints.AddRange(newPoints);
                            prevPoints = augPoints;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Propagates SAM 2.1 segmentation in backward direction (decreasing Z) for XY slices
        /// </summary>
        private static void PropagateSam2XYBackward(CTMemorySegmenter segmenter, MainForm mainForm,
                                          int width, int height, int depth,
                                          int startZ, Material material, byte[,,] volumeLabels, int threshold)
        {
            // Safety check - don't try to propagate from the first slice
            if (startZ <= 0)
            {
                Logger.Log($"[SegmentationPropagator] Cannot propagate backward from Z={startZ} (first slice)");
                return;
            }

            Logger.Log($"[SegmentationPropagator] Propagating material '{material.Name}' backward from Z={startZ}");

            // Keep track of the previous slice's points for consistency
            List<AnnotationPoint> prevPoints = null;
            int maxStepsWithoutSegmentation = 3; // Allow up to 3 slices with no segmentation before stopping
            int stepsWithoutSegmentation = 0;

            for (int z = startZ - 1; z >= 0; z--)
            {
                Logger.Log($"[SegmentationPropagator] Processing backward slice Z={z}");

                // Generate base image for current slice
                using (Bitmap baseImage = GenerateXYBitmap(mainForm, z, width, height))
                {
                    // Create annotation points from previous slice's segmentation
                    List<AnnotationPoint> points;

                    if (prevPoints == null)
                    {
                        // First slice before start - sample points from the segmented slice
                        points = SamplePointsFromVolume(mainForm, width, height, z + 1, volumeLabels, material.ID);
                    }
                    else
                    {
                        // Use previous slice's points with updated Z coordinate
                        points = prevPoints.Select(p => new AnnotationPoint
                        {
                            X = p.X,
                            Y = p.Y,
                            Z = z,
                            Label = p.Label  // Preserve the original material label
                        }).ToList();
                    }

                    if (points.Count == 0)
                    {
                        Logger.Log($"[SegmentationPropagator] No points found for material '{material.Name}' in slice Z={z + 1}, stopping backward propagation");
                        break;
                    }

                    // Convert material-labeled points to the SAM-expected format (Foreground/Exterior)
                    List<AnnotationPoint> samPoints = BuildMixedPrompts(points, material.Name);

                    // Process the slice using SAM 2.1
                    using (Bitmap mask = segmenter.ProcessXYSlice(z, baseImage, samPoints, material.Name))
                    {
                        // Apply the mask to the volume
                        int pixelsSegmented = ApplyMaskToVolume(mask, width, height, z, material.ID, volumeLabels, threshold);
                        Logger.Log($"[SegmentationPropagator] Segmented {pixelsSegmented} pixels for material '{material.Name}' in slice Z={z}");

                        // If nothing was segmented, track but continue for a few more slices
                        if (pixelsSegmented == 0)
                        {
                            stepsWithoutSegmentation++;
                            Logger.Log($"[SegmentationPropagator] No pixels segmented in slice Z={z}, steps without segmentation: {stepsWithoutSegmentation}");

                            if (stepsWithoutSegmentation >= maxStepsWithoutSegmentation)
                            {
                                Logger.Log($"[SegmentationPropagator] Reached {maxStepsWithoutSegmentation} steps without segmentation, stopping backward propagation");
                                break;
                            }
                        }
                        else
                        {
                            // Reset counter when we get successful segmentation
                            stepsWithoutSegmentation = 0;
                        }

                        // Update points for next iteration
                        List<AnnotationPoint> newPoints = SamplePointsFromNewSegmentation(mainForm, mask, z, material.ID, threshold);

                        // If we have meaningful new points, use them, otherwise keep using previous points
                        if (newPoints.Count >= 5)
                        {
                            prevPoints = newPoints;
                        }
                        // If very few points, augment with some from previous slice
                        else if (newPoints.Count > 0 && prevPoints != null)
                        {
                            // Keep some previous points but update their Z coordinate
                            var augPoints = prevPoints.Take(Math.Min(10, prevPoints.Count))
                                .Select(p => new AnnotationPoint
                                {
                                    X = p.X,
                                    Y = p.Y,
                                    Z = z,
                                    Label = p.Label
                                }).ToList();

                            // Add the new points
                            augPoints.AddRange(newPoints);
                            prevPoints = augPoints;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Propagates segmentation in the XZ direction using SAM 2.1 models
        /// </summary>
        private static byte[,,] PropagateSam2XZDirection(CTMemorySegmenter segmenter, MainForm mainForm,
                                                 int width, int height, int depth, int threshold)
        {
            // Find a segmented slice in XZ direction
            int startY = FindSegmentedXZSlice(mainForm, width, height, depth);
            if (startY == -1)
            {
                Logger.Log("[SegmentationPropagator] No segmented XZ slice found to start propagation");
                return null;
            }

            Logger.Log($"[SegmentationPropagator] Found segmented XZ slice at Y={startY}");

            // Create a new volume to store results
            byte[,,] resultVolume = new byte[width, height, depth];

            // First, copy the existing segmentation
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        resultVolume[x, y, z] = mainForm.volumeLabels[x, y, z];
                    }
                }
            }

            // Get all materials in the segmented slice
            var materials = GetMaterialsInXZSlice(mainForm, width, depth, startY);
            if (materials.Count == 0)
            {
                Logger.Log("[SegmentationPropagator] No materials found in the segmented XZ slice");
                return null;
            }

            Logger.Log($"[SegmentationPropagator] Found {materials.Count} materials in XZ slice: {string.Join(", ", materials.Select(m => m.Name))}");

            // For each material, propagate forward and/or backward as needed
            foreach (var material in materials)
            {
                Logger.Log($"[SegmentationPropagator] Propagating material '{material.Name}' (ID: {material.ID})");

                // Check if we're at boundaries and only propagate in valid directions
                bool canPropagateForward = startY < height - 1;
                bool canPropagateBackward = startY > 0;

                if (canPropagateForward)
                {
                    Logger.Log($"[SegmentationPropagator] Propagating forward from Y={startY}");
                    PropagateSam2XZForward(segmenter, mainForm, width, height, depth, startY, material, resultVolume, threshold);
                }
                else
                {
                    Logger.Log($"[SegmentationPropagator] At last slice Y={startY}, skipping forward propagation");
                }

                if (canPropagateBackward)
                {
                    Logger.Log($"[SegmentationPropagator] Propagating backward from Y={startY}");
                    PropagateSam2XZBackward(segmenter, mainForm, width, height, depth, startY, material, resultVolume, threshold);
                }
                else
                {
                    Logger.Log($"[SegmentationPropagator] At first slice Y={startY}, skipping backward propagation");
                }
            }

            return resultVolume;
        }

        /// <summary>
        /// Propagates SAM 2.1 segmentation in forward direction (increasing Y) for XZ slices
        /// </summary>
        private static void PropagateSam2XZForward(CTMemorySegmenter segmenter, MainForm mainForm,
                                         int width, int height, int depth,
                                         int startY, Material material, byte[,,] volumeLabels, int threshold)
        {
            // Safety check - don't try to propagate from the last slice
            if (startY >= height - 1)
            {
                Logger.Log($"[SegmentationPropagator] Cannot propagate forward from Y={startY} (last slice)");
                return;
            }

            Logger.Log($"[SegmentationPropagator] Propagating material '{material.Name}' forward from Y={startY}");

            // Keep track of the previous slice's points for consistency
            List<AnnotationPoint> prevPoints = null;
            int maxStepsWithoutSegmentation = 3; // Allow up to 3 slices with no segmentation before stopping
            int stepsWithoutSegmentation = 0;

            for (int y = startY + 1; y < height; y++)
            {
                Logger.Log($"[SegmentationPropagator] Processing forward slice Y={y}");

                // Generate base image for current slice
                using (Bitmap baseImage = GenerateXZBitmap(mainForm, y, width, depth))
                {
                    // Create annotation points from previous slice's segmentation
                    List<AnnotationPoint> points;

                    if (prevPoints == null)
                    {
                        // First slice after start - sample points from the XZ slice
                        points = SamplePointsFromXZSlice(mainForm, width, depth, y - 1, volumeLabels, material.ID);
                    }
                    else
                    {
                        // Use previous slice's points with updated Y coordinate
                        points = prevPoints.Select(p => new AnnotationPoint
                        {
                            X = p.X,
                            Y = y,
                            Z = p.Z,
                            Label = p.Label
                        }).ToList();
                    }

                    if (points.Count == 0)
                    {
                        Logger.Log($"[SegmentationPropagator] No points found for material '{material.Name}' in slice Y={y - 1}, stopping forward propagation");
                        break;
                    }

                    // Convert material points to SAM-expected format
                    List<AnnotationPoint> samPoints = BuildMixedPrompts(points, material.Name);

                    // Process the slice using SAM 2.1
                    using (Bitmap mask = segmenter.ProcessXZSlice(y, baseImage, samPoints, material.Name))
                    {
                        // Apply the mask to the volume
                        int pixelsSegmented = ApplyMaskToXZVolume(mask, width, depth, y, material.ID, volumeLabels, threshold);
                        Logger.Log($"[SegmentationPropagator] Segmented {pixelsSegmented} pixels for material '{material.Name}' in slice Y={y}");

                        // If nothing was segmented, track but continue for a few more slices
                        if (pixelsSegmented == 0)
                        {
                            stepsWithoutSegmentation++;
                            Logger.Log($"[SegmentationPropagator] No pixels segmented in slice Y={y}, steps without segmentation: {stepsWithoutSegmentation}");

                            if (stepsWithoutSegmentation >= maxStepsWithoutSegmentation)
                            {
                                Logger.Log($"[SegmentationPropagator] Reached {maxStepsWithoutSegmentation} steps without segmentation, stopping forward propagation");
                                break;
                            }
                        }
                        else
                        {
                            // Reset counter when we get successful segmentation
                            stepsWithoutSegmentation = 0;
                        }

                        // Update points for next iteration based on this slice's segmentation
                        List<AnnotationPoint> newPoints = SamplePointsFromXZSegmentation(mainForm, mask, y, material.ID, threshold);

                        // If we have meaningful new points, use them, otherwise keep using previous points
                        if (newPoints.Count >= 5)
                        {
                            prevPoints = newPoints;
                        }
                        // If very few points, augment with some from previous slice
                        else if (newPoints.Count > 0 && prevPoints != null)
                        {
                            // Keep some previous points but update their Y coordinate
                            var augPoints = prevPoints.Take(Math.Min(10, prevPoints.Count))
                                .Select(p => new AnnotationPoint
                                {
                                    X = p.X,
                                    Y = y,
                                    Z = p.Z,
                                    Label = p.Label
                                }).ToList();

                            // Add the new points
                            augPoints.AddRange(newPoints);
                            prevPoints = augPoints;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Propagates SAM 2.1 segmentation in backward direction (decreasing Y) for XZ slices
        /// </summary>
        private static void PropagateSam2XZBackward(CTMemorySegmenter segmenter, MainForm mainForm,
                                          int width, int height, int depth,
                                          int startY, Material material, byte[,,] volumeLabels, int threshold)
        {
            // Safety check - don't try to propagate from the first slice
            if (startY <= 0)
            {
                Logger.Log($"[SegmentationPropagator] Cannot propagate backward from Y={startY} (first slice)");
                return;
            }

            Logger.Log($"[SegmentationPropagator] Propagating material '{material.Name}' backward from Y={startY}");

            // Keep track of the previous slice's points for consistency
            List<AnnotationPoint> prevPoints = null;
            int maxStepsWithoutSegmentation = 3; // Allow up to 3 slices with no segmentation before stopping
            int stepsWithoutSegmentation = 0;

            for (int y = startY - 1; y >= 0; y--)
            {
                Logger.Log($"[SegmentationPropagator] Processing backward slice Y={y}");

                // Generate base image for current slice
                using (Bitmap baseImage = GenerateXZBitmap(mainForm, y, width, depth))
                {
                    // Create annotation points from previous slice's segmentation
                    List<AnnotationPoint> points;

                    if (prevPoints == null)
                    {
                        // First slice before start - sample points from the XZ slice
                        points = SamplePointsFromXZSlice(mainForm, width, depth, y + 1, volumeLabels, material.ID);
                    }
                    else
                    {
                        // Use previous slice's points with updated Y coordinate
                        points = prevPoints.Select(p => new AnnotationPoint
                        {
                            X = p.X,
                            Y = y,
                            Z = p.Z,
                            Label = p.Label
                        }).ToList();
                    }

                    if (points.Count == 0)
                    {
                        Logger.Log($"[SegmentationPropagator] No points found for material '{material.Name}' in slice Y={y + 1}, stopping backward propagation");
                        break;
                    }

                    // Convert material points to SAM-expected format
                    List<AnnotationPoint> samPoints = BuildMixedPrompts(points, material.Name);

                    // Process the slice using SAM 2.1
                    using (Bitmap mask = segmenter.ProcessXZSlice(y, baseImage, samPoints, material.Name))
                    {
                        // Apply the mask to the volume
                        int pixelsSegmented = ApplyMaskToXZVolume(mask, width, depth, y, material.ID, volumeLabels, threshold);
                        Logger.Log($"[SegmentationPropagator] Segmented {pixelsSegmented} pixels for material '{material.Name}' in slice Y={y}");

                        // If nothing was segmented, track but continue for a few more slices
                        if (pixelsSegmented == 0)
                        {
                            stepsWithoutSegmentation++;
                            Logger.Log($"[SegmentationPropagator] No pixels segmented in slice Y={y}, steps without segmentation: {stepsWithoutSegmentation}");

                            if (stepsWithoutSegmentation >= maxStepsWithoutSegmentation)
                            {
                                Logger.Log($"[SegmentationPropagator] Reached {maxStepsWithoutSegmentation} steps without segmentation, stopping backward propagation");
                                break;
                            }
                        }
                        else
                        {
                            // Reset counter when we get successful segmentation
                            stepsWithoutSegmentation = 0;
                        }

                        // Update points for next iteration based on this slice's segmentation
                        List<AnnotationPoint> newPoints = SamplePointsFromXZSegmentation(mainForm, mask, y, material.ID, threshold);

                        // If we have meaningful new points, use them, otherwise keep using previous points
                        if (newPoints.Count >= 5)
                        {
                            prevPoints = newPoints;
                        }
                        // If very few points, augment with some from previous slice
                        else if (newPoints.Count > 0 && prevPoints != null)
                        {
                            // Keep some previous points but update their Y coordinate
                            var augPoints = prevPoints.Take(Math.Min(10, prevPoints.Count))
                                .Select(p => new AnnotationPoint
                                {
                                    X = p.X,
                                    Y = y,
                                    Z = p.Z,
                                    Label = p.Label
                                }).ToList();

                            // Add the new points
                            augPoints.AddRange(newPoints);
                            prevPoints = augPoints;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Propagates segmentation in the YZ direction using SAM 2.1 models
        /// </summary>
        private static byte[,,] PropagateSam2YZDirection(CTMemorySegmenter segmenter, MainForm mainForm,
                                                 int width, int height, int depth, int threshold)
        {
            // Find a segmented slice in YZ direction
            int startX = FindSegmentedYZSlice(mainForm, width, height, depth);
            if (startX == -1)
            {
                Logger.Log("[SegmentationPropagator] No segmented YZ slice found to start propagation");
                return null;
            }

            Logger.Log($"[SegmentationPropagator] Found segmented YZ slice at X={startX}");

            // Create a new volume to store results
            byte[,,] resultVolume = new byte[width, height, depth];

            // First, copy the existing segmentation
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        resultVolume[x, y, z] = mainForm.volumeLabels[x, y, z];
                    }
                }
            }

            // Get all materials in the segmented slice
            var materials = GetMaterialsInYZSlice(mainForm, height, depth, startX);
            if (materials.Count == 0)
            {
                Logger.Log("[SegmentationPropagator] No materials found in the segmented YZ slice");
                return null;
            }

            Logger.Log($"[SegmentationPropagator] Found {materials.Count} materials in YZ slice: {string.Join(", ", materials.Select(m => m.Name))}");

            // For each material, propagate forward and/or backward as needed
            foreach (var material in materials)
            {
                Logger.Log($"[SegmentationPropagator] Propagating material '{material.Name}' (ID: {material.ID})");

                // Check if we're at boundaries and only propagate in valid directions
                bool canPropagateForward = startX < width - 1;
                bool canPropagateBackward = startX > 0;

                if (canPropagateForward)
                {
                    Logger.Log($"[SegmentationPropagator] Propagating forward from X={startX}");
                    PropagateSam2YZForward(segmenter, mainForm, width, height, depth, startX, material, resultVolume, threshold);
                }
                else
                {
                    Logger.Log($"[SegmentationPropagator] At last slice X={startX}, skipping forward propagation");
                }

                if (canPropagateBackward)
                {
                    Logger.Log($"[SegmentationPropagator] Propagating backward from X={startX}");
                    PropagateSam2YZBackward(segmenter, mainForm, width, height, depth, startX, material, resultVolume, threshold);
                }
                else
                {
                    Logger.Log($"[SegmentationPropagator] At first slice X={startX}, skipping backward propagation");
                }
            }

            return resultVolume;
        }

        /// <summary>
        /// Propagates SAM 2.1 segmentation in forward direction (increasing X) for YZ slices
        /// </summary>
        private static void PropagateSam2YZForward(CTMemorySegmenter segmenter, MainForm mainForm,
                                         int width, int height, int depth,
                                         int startX, Material material, byte[,,] volumeLabels, int threshold)
        {
            // Safety check - don't try to propagate from the last slice
            if (startX >= width - 1)
            {
                Logger.Log($"[SegmentationPropagator] Cannot propagate forward from X={startX} (last slice)");
                return;
            }

            Logger.Log($"[SegmentationPropagator] Propagating material '{material.Name}' forward from X={startX}");

            // Keep track of the previous slice's points for consistency
            List<AnnotationPoint> prevPoints = null;
            int maxStepsWithoutSegmentation = 3; // Allow up to 3 slices with no segmentation before stopping
            int stepsWithoutSegmentation = 0;

            for (int x = startX + 1; x < width; x++)
            {
                Logger.Log($"[SegmentationPropagator] Processing forward slice X={x}");

                // Generate base image for current slice
                using (Bitmap baseImage = GenerateYZBitmap(mainForm, x, height, depth))
                {
                    // Create annotation points from previous slice's segmentation
                    List<AnnotationPoint> points;

                    if (prevPoints == null)
                    {
                        // First slice after start - sample points from the YZ slice
                        points = SamplePointsFromYZSlice(mainForm, height, depth, x - 1, volumeLabels, material.ID);
                    }
                    else
                    {
                        // Use previous slice's points with updated X coordinate
                        points = prevPoints.Select(p => new AnnotationPoint
                        {
                            X = x,
                            Y = p.Y,
                            Z = p.Z,
                            Label = p.Label
                        }).ToList();
                    }

                    if (points.Count == 0)
                    {
                        Logger.Log($"[SegmentationPropagator] No points found for material '{material.Name}' in slice X={x - 1}, stopping forward propagation");
                        break;
                    }

                    // Convert material points to SAM-expected format
                    List<AnnotationPoint> samPoints = BuildMixedPrompts(points, material.Name);

                    // Process the slice using SAM 2.1
                    using (Bitmap mask = segmenter.ProcessYZSlice(x, baseImage, samPoints, material.Name))
                    {
                        // Apply the mask to the volume
                        int pixelsSegmented = ApplyMaskToYZVolume(mask, height, depth, x, material.ID, volumeLabels, threshold);
                        Logger.Log($"[SegmentationPropagator] Segmented {pixelsSegmented} pixels for material '{material.Name}' in slice X={x}");

                        // If nothing was segmented, track but continue for a few more slices
                        if (pixelsSegmented == 0)
                        {
                            stepsWithoutSegmentation++;
                            Logger.Log($"[SegmentationPropagator] No pixels segmented in slice X={x}, steps without segmentation: {stepsWithoutSegmentation}");

                            if (stepsWithoutSegmentation >= maxStepsWithoutSegmentation)
                            {
                                Logger.Log($"[SegmentationPropagator] Reached {maxStepsWithoutSegmentation} steps without segmentation, stopping forward propagation");
                                break;
                            }
                        }
                        else
                        {
                            // Reset counter when we get successful segmentation
                            stepsWithoutSegmentation = 0;
                        }

                        // Update points for next iteration based on this slice's segmentation
                        List<AnnotationPoint> newPoints = SamplePointsFromYZSegmentation(mainForm, mask, x, material.ID, threshold);

                        // If we have meaningful new points, use them, otherwise keep using previous points
                        if (newPoints.Count >= 5)
                        {
                            prevPoints = newPoints;
                        }
                        // If very few points, augment with some from previous slice
                        else if (newPoints.Count > 0 && prevPoints != null)
                        {
                            // Keep some previous points but update their X coordinate
                            var augPoints = prevPoints.Take(Math.Min(10, prevPoints.Count))
                                .Select(p => new AnnotationPoint
                                {
                                    X = x,
                                    Y = p.Y,
                                    Z = p.Z,
                                    Label = p.Label
                                }).ToList();

                            // Add the new points
                            augPoints.AddRange(newPoints);
                            prevPoints = augPoints;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Propagates SAM 2.1 segmentation in backward direction (decreasing X) for YZ slices
        /// </summary>
        private static void PropagateSam2YZBackward(CTMemorySegmenter segmenter, MainForm mainForm,
                                          int width, int height, int depth,
                                          int startX, Material material, byte[,,] volumeLabels, int threshold)
        {
            // Safety check - don't try to propagate from the first slice
            if (startX <= 0)
            {
                Logger.Log($"[SegmentationPropagator] Cannot propagate backward from X={startX} (first slice)");
                return;
            }

            Logger.Log($"[SegmentationPropagator] Propagating material '{material.Name}' backward from X={startX}");

            // Keep track of the previous slice's points for consistency
            List<AnnotationPoint> prevPoints = null;
            int maxStepsWithoutSegmentation = 3; // Allow up to 3 slices with no segmentation before stopping
            int stepsWithoutSegmentation = 0;

            for (int x = startX - 1; x >= 0; x--)
            {
                Logger.Log($"[SegmentationPropagator] Processing backward slice X={x}");

                // Generate base image for current slice
                using (Bitmap baseImage = GenerateYZBitmap(mainForm, x, height, depth))
                {
                    // Create annotation points from previous slice's segmentation
                    List<AnnotationPoint> points;

                    if (prevPoints == null)
                    {
                        // First slice before start - sample points from the YZ slice
                        points = SamplePointsFromYZSlice(mainForm, height, depth, x + 1, volumeLabels, material.ID);
                    }
                    else
                    {
                        // Use previous slice's points with updated X coordinate
                        points = prevPoints.Select(p => new AnnotationPoint
                        {
                            X = x,
                            Y = p.Y,
                            Z = p.Z,
                            Label = p.Label
                        }).ToList();
                    }

                    if (points.Count == 0)
                    {
                        Logger.Log($"[SegmentationPropagator] No points found for material '{material.Name}' in slice X={x + 1}, stopping backward propagation");
                        break;
                    }

                    // Convert material points to SAM-expected format
                    List<AnnotationPoint> samPoints = BuildMixedPrompts(points, material.Name);

                    // Process the slice using SAM 2.1
                    using (Bitmap mask = segmenter.ProcessYZSlice(x, baseImage, samPoints, material.Name))
                    {
                        // Apply the mask to the volume
                        int pixelsSegmented = ApplyMaskToYZVolume(mask, height, depth, x, material.ID, volumeLabels, threshold);
                        Logger.Log($"[SegmentationPropagator] Segmented {pixelsSegmented} pixels for material '{material.Name}' in slice X={x}");

                        // If nothing was segmented, track but continue for a few more slices
                        if (pixelsSegmented == 0)
                        {
                            stepsWithoutSegmentation++;
                            Logger.Log($"[SegmentationPropagator] No pixels segmented in slice X={x}, steps without segmentation: {stepsWithoutSegmentation}");

                            if (stepsWithoutSegmentation >= maxStepsWithoutSegmentation)
                            {
                                Logger.Log($"[SegmentationPropagator] Reached {maxStepsWithoutSegmentation} steps without segmentation, stopping backward propagation");
                                break;
                            }
                        }
                        else
                        {
                            // Reset counter when we get successful segmentation
                            stepsWithoutSegmentation = 0;
                        }

                        // Update points for next iteration based on this slice's segmentation
                        List<AnnotationPoint> newPoints = SamplePointsFromYZSegmentation(mainForm, mask, x, material.ID, threshold);

                        // If we have meaningful new points, use them, otherwise keep using previous points
                        if (newPoints.Count >= 5)
                        {
                            prevPoints = newPoints;
                        }
                        // If very few points, augment with some from previous slice
                        else if (newPoints.Count > 0 && prevPoints != null)
                        {
                            // Keep some previous points but update their X coordinate
                            var augPoints = prevPoints.Take(Math.Min(10, prevPoints.Count))
                                .Select(p => new AnnotationPoint
                                {
                                    X = x,
                                    Y = p.Y,
                                    Z = p.Z,
                                    Label = p.Label
                                }).ToList();

                            // Add the new points
                            augPoints.AddRange(newPoints);
                            prevPoints = augPoints;
                        }
                    }
                }
            }
        }

        #endregion

        #region Original SAM Propagation Methods

        /// <summary>
        /// Propagates segmentation in the XY direction using original SAM models
        /// </summary>
        private static byte[,,] PropagateXYDirection(CTMemorySegmenter segmenter, MainForm mainForm,
                                           int width, int height, int depth, int threshold)
        {
            // Find a segmented slice in XY direction
            int startZ = FindSegmentedXYSlice(mainForm, width, height, depth);
            if (startZ == -1)
            {
                Logger.Log("[SegmentationPropagator] No segmented XY slice found to start propagation");
                return null;
            }

            Logger.Log($"[SegmentationPropagator] Found segmented XY slice at Z={startZ}");

            // Create a new volume to store results
            byte[,,] resultVolume = new byte[width, height, depth];

            // First, copy the existing segmentation
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        resultVolume[x, y, z] = mainForm.volumeLabels[x, y, z];
                    }
                }
            }

            // Get all materials in the segmented slice
            var materials = GetMaterialsInXYSlice(mainForm, width, height, startZ);
            if (materials.Count == 0)
            {
                Logger.Log("[SegmentationPropagator] No materials found in the segmented XY slice");
                return null;
            }

            Logger.Log($"[SegmentationPropagator] Found {materials.Count} materials in XY slice: {string.Join(", ", materials.Select(m => m.Name))}");

            // For each material, propagate forward and/or backward as needed
            foreach (var material in materials)
            {
                Logger.Log($"[SegmentationPropagator] Propagating material '{material.Name}' (ID: {material.ID})");

                // Check if we're at boundaries and only propagate in valid directions
                bool canPropagateForward = startZ < depth - 1;
                bool canPropagateBackward = startZ > 0;

                if (canPropagateForward)
                {
                    Logger.Log($"[SegmentationPropagator] Propagating forward from Z={startZ}");
                    PropagateXYForward(segmenter, mainForm, width, height, depth, startZ, material, resultVolume, threshold);
                }
                else
                {
                    Logger.Log($"[SegmentationPropagator] At last slice Z={startZ}, skipping forward propagation");
                }

                if (canPropagateBackward)
                {
                    Logger.Log($"[SegmentationPropagator] Propagating backward from Z={startZ}");
                    PropagateXYBackward(segmenter, mainForm, width, height, depth, startZ, material, resultVolume, threshold);
                }
                else
                {
                    Logger.Log($"[SegmentationPropagator] At first slice Z={startZ}, skipping backward propagation");
                }
            }

            return resultVolume;
        }

        /// <summary>
        /// Propagates original SAM segmentation in forward direction (increasing Z) for XY slices
        /// </summary>
        private static void PropagateXYForward(CTMemorySegmenter segmenter, MainForm mainForm,
                                     int width, int height, int depth,
                                     int startZ, Material material, byte[,,] volumeLabels, int threshold)
        {
            // Safety check - don't try to propagate from the last slice
            if (startZ >= depth - 1)
            {
                Logger.Log($"[SegmentationPropagator] Cannot propagate forward from Z={startZ} (last slice)");
                return;
            }

            Logger.Log($"[SegmentationPropagator] Propagating material '{material.Name}' forward from Z={startZ}");

            // Keep track of the previous slice's points for consistency
            List<AnnotationPoint> prevPoints = null;
            int maxStepsWithoutSegmentation = 3; // Allow up to 3 slices with no segmentation before stopping
            int stepsWithoutSegmentation = 0;

            for (int z = startZ + 1; z < depth; z++)
            {
                Logger.Log($"[SegmentationPropagator] Processing forward slice Z={z}");

                // Generate base image for current slice
                using (Bitmap baseImage = GenerateXYBitmap(mainForm, z, width, height))
                {
                    // Create annotation points from previous slice's segmentation
                    List<AnnotationPoint> points;

                    if (prevPoints == null)
                    {
                        // First slice after start - sample points from the segmented slice
                        points = SamplePointsFromVolume(mainForm, width, height, z - 1, volumeLabels, material.ID);
                    }
                    else
                    {
                        // Use previous slice's points with updated Z coordinate
                        points = prevPoints.Select(p => new AnnotationPoint
                        {
                            X = p.X,
                            Y = p.Y,
                            Z = z,
                            Label = p.Label  // Preserve the original material label
                        }).ToList();
                    }

                    if (points.Count == 0)
                    {
                        Logger.Log($"[SegmentationPropagator] No points found for material '{material.Name}' in slice Z={z - 1}, stopping forward propagation");
                        break;
                    }

                    // Convert material-labeled points to the SAM-expected format (Foreground/Exterior)
                    List<AnnotationPoint> samPoints = BuildMixedPrompts(points, material.Name);

                    // Process the slice using SAM
                    using (Bitmap mask = segmenter.ProcessXYSlice(z, baseImage, samPoints, material.Name))
                    {
                        // Apply the mask to the volume
                        int pixelsSegmented = ApplyMaskToVolume(mask, width, height, z, material.ID, volumeLabels, threshold);
                        Logger.Log($"[SegmentationPropagator] Segmented {pixelsSegmented} pixels for material '{material.Name}' in slice Z={z}");

                        // If nothing was segmented, track but continue for a few more slices
                        if (pixelsSegmented == 0)
                        {
                            stepsWithoutSegmentation++;
                            Logger.Log($"[SegmentationPropagator] No pixels segmented in slice Z={z}, steps without segmentation: {stepsWithoutSegmentation}");

                            if (stepsWithoutSegmentation >= maxStepsWithoutSegmentation)
                            {
                                Logger.Log($"[SegmentationPropagator] Reached {maxStepsWithoutSegmentation} steps without segmentation, stopping forward propagation");
                                break;
                            }
                        }
                        else
                        {
                            // Reset counter when we get successful segmentation
                            stepsWithoutSegmentation = 0;
                        }

                        // Update points for next iteration
                        List<AnnotationPoint> newPoints = SamplePointsFromNewSegmentation(mainForm, mask, z, material.ID, threshold);

                        // If we have meaningful new points, use them, otherwise keep using previous points
                        if (newPoints.Count >= 5)
                        {
                            prevPoints = newPoints;
                        }
                        // If very few points, augment with some from previous slice
                        else if (newPoints.Count > 0 && prevPoints != null)
                        {
                            // Keep some previous points but update their Z coordinate
                            var augPoints = prevPoints.Take(Math.Min(10, prevPoints.Count))
                                .Select(p => new AnnotationPoint
                                {
                                    X = p.X,
                                    Y = p.Y,
                                    Z = z,
                                    Label = p.Label
                                }).ToList();

                            // Add the new points
                            augPoints.AddRange(newPoints);
                            prevPoints = augPoints;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Propagates original SAM segmentation in backward direction (decreasing Z) for XY slices
        /// </summary>
        private static void PropagateXYBackward(CTMemorySegmenter segmenter, MainForm mainForm,
                                      int width, int height, int depth,
                                      int startZ, Material material, byte[,,] volumeLabels, int threshold)
        {
            // Similar implementation to PropagateXYForward but going backward
            // Safety check - don't try to propagate from the first slice
            if (startZ <= 0)
            {
                Logger.Log($"[SegmentationPropagator] Cannot propagate backward from Z={startZ} (first slice)");
                return;
            }

            Logger.Log($"[SegmentationPropagator] Propagating material '{material.Name}' backward from Z={startZ}");

            // Keep track of the previous slice's points for consistency
            List<AnnotationPoint> prevPoints = null;
            int maxStepsWithoutSegmentation = 3; // Allow up to 3 slices with no segmentation before stopping
            int stepsWithoutSegmentation = 0;

            for (int z = startZ - 1; z >= 0; z--)
            {
                Logger.Log($"[SegmentationPropagator] Processing backward slice Z={z}");

                // Generate base image for current slice
                using (Bitmap baseImage = GenerateXYBitmap(mainForm, z, width, height))
                {
                    // Create annotation points from previous slice's segmentation
                    List<AnnotationPoint> points;

                    if (prevPoints == null)
                    {
                        // First slice before start - sample points from the segmented slice
                        points = SamplePointsFromVolume(mainForm, width, height, z + 1, volumeLabels, material.ID);
                    }
                    else
                    {
                        // Use previous slice's points with updated Z coordinate
                        points = prevPoints.Select(p => new AnnotationPoint
                        {
                            X = p.X,
                            Y = p.Y,
                            Z = z,
                            Label = p.Label  // Preserve the original material label
                        }).ToList();
                    }

                    if (points.Count == 0)
                    {
                        Logger.Log($"[SegmentationPropagator] No points found for material '{material.Name}' in slice Z={z + 1}, stopping backward propagation");
                        break;
                    }

                    // Convert material-labeled points to the SAM-expected format (Foreground/Exterior)
                    List<AnnotationPoint> samPoints = BuildMixedPrompts(points, material.Name);

                    // Process the slice using SAM
                    using (Bitmap mask = segmenter.ProcessXYSlice(z, baseImage, samPoints, material.Name))
                    {
                        // Apply the mask to the volume
                        int pixelsSegmented = ApplyMaskToVolume(mask, width, height, z, material.ID, volumeLabels, threshold);
                        Logger.Log($"[SegmentationPropagator] Segmented {pixelsSegmented} pixels for material '{material.Name}' in slice Z={z}");

                        // If nothing was segmented, track but continue for a few more slices
                        if (pixelsSegmented == 0)
                        {
                            stepsWithoutSegmentation++;
                            Logger.Log($"[SegmentationPropagator] No pixels segmented in slice Z={z}, steps without segmentation: {stepsWithoutSegmentation}");

                            if (stepsWithoutSegmentation >= maxStepsWithoutSegmentation)
                            {
                                Logger.Log($"[SegmentationPropagator] Reached {maxStepsWithoutSegmentation} steps without segmentation, stopping backward propagation");
                                break;
                            }
                        }
                        else
                        {
                            // Reset counter when we get successful segmentation
                            stepsWithoutSegmentation = 0;
                        }

                        // Update points for next iteration
                        List<AnnotationPoint> newPoints = SamplePointsFromNewSegmentation(mainForm, mask, z, material.ID, threshold);

                        // If we have meaningful new points, use them, otherwise keep using previous points
                        if (newPoints.Count >= 5)
                        {
                            prevPoints = newPoints;
                        }
                        // If very few points, augment with some from previous slice
                        else if (newPoints.Count > 0 && prevPoints != null)
                        {
                            // Keep some previous points but update their Z coordinate
                            var augPoints = prevPoints.Take(Math.Min(10, prevPoints.Count))
                                .Select(p => new AnnotationPoint
                                {
                                    X = p.X,
                                    Y = p.Y,
                                    Z = z,
                                    Label = p.Label
                                }).ToList();

                            // Add the new points
                            augPoints.AddRange(newPoints);
                            prevPoints = augPoints;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Propagates segmentation in the XZ direction using original SAM models
        /// </summary>
        private static byte[,,] PropagateXZDirection(CTMemorySegmenter segmenter, MainForm mainForm,
                                           int width, int height, int depth, int threshold)
        {
            // Find a segmented slice in XZ direction
            int startY = FindSegmentedXZSlice(mainForm, width, height, depth);
            if (startY == -1)
            {
                Logger.Log("[SegmentationPropagator] No segmented XZ slice found to start propagation");
                return null;
            }

            Logger.Log($"[SegmentationPropagator] Found segmented XZ slice at Y={startY}");

            // Create a new volume to store results
            byte[,,] resultVolume = new byte[width, height, depth];

            // First, copy the existing segmentation
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        resultVolume[x, y, z] = mainForm.volumeLabels[x, y, z];
                    }
                }
            }

            // Get all materials in the segmented slice
            var materials = GetMaterialsInXZSlice(mainForm, width, depth, startY);
            if (materials.Count == 0)
            {
                Logger.Log("[SegmentationPropagator] No materials found in the segmented XZ slice");
                return null;
            }

            Logger.Log($"[SegmentationPropagator] Found {materials.Count} materials in XZ slice: {string.Join(", ", materials.Select(m => m.Name))}");

            // For each material, propagate forward and backward
            foreach (var material in materials)
            {
                Logger.Log($"[SegmentationPropagator] Propagating material '{material.Name}' (ID: {material.ID})");

                // Check if we're at boundaries and only propagate in valid directions
                bool canPropagateForward = startY < height - 1;
                bool canPropagateBackward = startY > 0;

                if (canPropagateForward)
                {
                    Logger.Log($"[SegmentationPropagator] Propagating forward from Y={startY}");
                    PropagateXZForward(segmenter, mainForm, width, height, depth, startY, material, resultVolume, threshold);
                }
                else
                {
                    Logger.Log($"[SegmentationPropagator] At last slice Y={startY}, skipping forward propagation");
                }

                if (canPropagateBackward)
                {
                    Logger.Log($"[SegmentationPropagator] Propagating backward from Y={startY}");
                    PropagateXZBackward(segmenter, mainForm, width, height, depth, startY, material, resultVolume, threshold);
                }
                else
                {
                    Logger.Log($"[SegmentationPropagator] At first slice Y={startY}, skipping backward propagation");
                }
            }

            return resultVolume;
        }

        /// <summary>
        /// Propagates original SAM segmentation in forward direction (increasing Y) for XZ slices
        /// </summary>
        private static void PropagateXZForward(CTMemorySegmenter segmenter, MainForm mainForm,
                                     int width, int height, int depth,
                                     int startY, Material material, byte[,,] volumeLabels, int threshold)
        {
            // Implementation similar to PropagateXYForward but for XZ view
            // Safety check - don't try to propagate from the last slice
            if (startY >= height - 1)
            {
                Logger.Log($"[SegmentationPropagator] Cannot propagate forward from Y={startY} (last slice)");
                return;
            }

            Logger.Log($"[SegmentationPropagator] Propagating material '{material.Name}' forward from Y={startY}");

            // Keep track of the previous slice's points for consistency
            List<AnnotationPoint> prevPoints = null;
            int maxStepsWithoutSegmentation = 3; // Allow up to 3 slices with no segmentation before stopping
            int stepsWithoutSegmentation = 0;

            for (int y = startY + 1; y < height; y++)
            {
                Logger.Log($"[SegmentationPropagator] Processing forward slice Y={y}");

                // Generate base image for current slice
                using (Bitmap baseImage = GenerateXZBitmap(mainForm, y, width, depth))
                {
                    // Create annotation points from previous slice's segmentation
                    List<AnnotationPoint> points;

                    if (prevPoints == null)
                    {
                        // First slice after start - sample points from the XZ slice
                        points = SamplePointsFromXZSlice(mainForm, width, depth, y - 1, volumeLabels, material.ID);
                    }
                    else
                    {
                        // Use previous slice's points with updated Y coordinate
                        points = prevPoints.Select(p => new AnnotationPoint
                        {
                            X = p.X,
                            Y = y,
                            Z = p.Z,
                            Label = p.Label
                        }).ToList();
                    }

                    if (points.Count == 0)
                    {
                        Logger.Log($"[SegmentationPropagator] No points found for material '{material.Name}' in slice Y={y - 1}, stopping forward propagation");
                        break;
                    }

                    // Convert material points to SAM-expected format
                    List<AnnotationPoint> samPoints = BuildMixedPrompts(points, material.Name);

                    // Process the slice using SAM
                    using(Bitmap mask = segmenter.ProcessXZSlice(y, baseImage, samPoints, material.Name))
                    {
                        
                        // Apply the mask to the volume
                        int pixelsSegmented = ApplyMaskToXZVolume(mask, width, depth, y, material.ID, volumeLabels, threshold);
                        Logger.Log($"[SegmentationPropagator] Segmented {pixelsSegmented} pixels for material '{material.Name}' in slice Y={y}");

                        // If nothing was segmented, track but continue for a few more slices
                        if (pixelsSegmented == 0)
                        {
                            stepsWithoutSegmentation++;
                            Logger.Log($"[SegmentationPropagator] No pixels segmented in slice Y={y}, steps without segmentation: {stepsWithoutSegmentation}");

                            if (stepsWithoutSegmentation >= maxStepsWithoutSegmentation)
                            {
                                Logger.Log($"[SegmentationPropagator] Reached {maxStepsWithoutSegmentation} steps without segmentation, stopping forward propagation");
                                break;
                            }
                        }
                        else
                        {
                            // Reset counter when we get successful segmentation
                            stepsWithoutSegmentation = 0;
                        }

                        // Update points for next iteration based on this slice's segmentation
                        List<AnnotationPoint> newPoints = SamplePointsFromXZSegmentation(mainForm, mask, y, material.ID, threshold);

                        // If we have meaningful new points, use them, otherwise keep using previous points
                        if (newPoints.Count >= 5)
                        {
                            prevPoints = newPoints;
                        }
                        // If very few points, augment with some from previous slice
                        else if (newPoints.Count > 0 && prevPoints != null)
                        {
                            // Keep some previous points but update their Y coordinate
                            var augPoints = prevPoints.Take(Math.Min(10, prevPoints.Count))
                                .Select(p => new AnnotationPoint
                                {
                                    X = p.X,
                                    Y = y,
                                    Z = p.Z,
                                    Label = p.Label
                                }).ToList();

                            // Add the new points
                            augPoints.AddRange(newPoints);
                            prevPoints = augPoints;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Propagates original SAM segmentation in backward direction (decreasing Y) for XZ slices
        /// </summary>
        private static void PropagateXZBackward(CTMemorySegmenter segmenter, MainForm mainForm,
                                      int width, int height, int depth,
                                      int startY, Material material, byte[,,] volumeLabels, int threshold)
        {
            // Safety check - don't try to propagate from the first slice
            if (startY <= 0)
            {
                Logger.Log($"[SegmentationPropagator] Cannot propagate backward from Y={startY} (first slice)");
                return;
            }

            Logger.Log($"[SegmentationPropagator] Propagating material '{material.Name}' backward from Y={startY}");

            // Keep track of the previous slice's points for consistency
            List<AnnotationPoint> prevPoints = null;
            int maxStepsWithoutSegmentation = 3; // Allow up to 3 slices with no segmentation before stopping
            int stepsWithoutSegmentation = 0;

            for (int y = startY - 1; y >= 0; y--)
            {
                Logger.Log($"[SegmentationPropagator] Processing backward slice Y={y}");

                // Generate base image for current slice
                using (Bitmap baseImage = GenerateXZBitmap(mainForm, y, width, depth))
                {
                    // Create annotation points from previous slice's segmentation
                    List<AnnotationPoint> points;

                    if (prevPoints == null)
                    {
                        // First slice before start - sample points from the XZ slice
                        points = SamplePointsFromXZSlice(mainForm, width, depth, y + 1, volumeLabels, material.ID);
                    }
                    else
                    {
                        // Use previous slice's points with updated Y coordinate
                        points = prevPoints.Select(p => new AnnotationPoint
                        {
                            X = p.X,
                            Y = y,
                            Z = p.Z,
                            Label = p.Label
                        }).ToList();
                    }

                    if (points.Count == 0)
                    {
                        Logger.Log($"[SegmentationPropagator] No points found for material '{material.Name}' in slice Y={y + 1}, stopping backward propagation");
                        break;
                    }

                    // Convert material points to SAM-expected format
                    List<AnnotationPoint> samPoints = BuildMixedPrompts(points, material.Name);

                    // Process the slice using SAM
                    using (Bitmap mask = segmenter.ProcessXZSlice(y, baseImage, samPoints, material.Name))
                    {
                        // Apply the mask to the volume
                        int pixelsSegmented = ApplyMaskToXZVolume(mask, width, depth, y, material.ID, volumeLabels, threshold);
                        Logger.Log($"[SegmentationPropagator] Segmented {pixelsSegmented} pixels for material '{material.Name}' in slice Y={y}");

                        // If nothing was segmented, track but continue for a few more slices
                        if (pixelsSegmented == 0)
                        {
                            stepsWithoutSegmentation++;
                            Logger.Log($"[SegmentationPropagator] No pixels segmented in slice Y={y}, steps without segmentation: {stepsWithoutSegmentation}");

                            if (stepsWithoutSegmentation >= maxStepsWithoutSegmentation)
                            {
                                Logger.Log($"[SegmentationPropagator] Reached {maxStepsWithoutSegmentation} steps without segmentation, stopping backward propagation");
                                break;
                            }
                        }
                        else
                        {
                            // Reset counter when we get successful segmentation
                            stepsWithoutSegmentation = 0;
                        }

                        // Update points for next iteration based on this slice's segmentation
                        List<AnnotationPoint> newPoints = SamplePointsFromXZSegmentation(mainForm, mask, y, material.ID, threshold);

                        // If we have meaningful new points, use them, otherwise keep using previous points
                        if (newPoints.Count >= 5)
                        {
                            prevPoints = newPoints;
                        }
                        // If very few points, augment with some from previous slice
                        else if (newPoints.Count > 0 && prevPoints != null)
                        {
                            // Keep some previous points but update their Y coordinate
                            var augPoints = prevPoints.Take(Math.Min(10, prevPoints.Count))
                                .Select(p => new AnnotationPoint
                                {
                                    X = p.X,
                                    Y = y,
                                    Z = p.Z,
                                    Label = p.Label
                                }).ToList();

                            // Add the new points
                            augPoints.AddRange(newPoints);
                            prevPoints = augPoints;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Propagates segmentation in the YZ direction using original SAM models
        /// </summary>
        private static byte[,,] PropagateYZDirection(CTMemorySegmenter segmenter, MainForm mainForm,
                                           int width, int height, int depth, int threshold)
        {
            // Find a segmented slice in YZ direction
            int startX = FindSegmentedYZSlice(mainForm, width, height, depth);
            if (startX == -1)
            {
                Logger.Log("[SegmentationPropagator] No segmented YZ slice found to start propagation");
                return null;
            }

            Logger.Log($"[SegmentationPropagator] Found segmented YZ slice at X={startX}");

            // Create a new volume to store results
            byte[,,] resultVolume = new byte[width, height, depth];

            // First, copy the existing segmentation
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        resultVolume[x, y, z] = mainForm.volumeLabels[x, y, z];
                    }
                }
            }

            // Get all materials in the segmented slice
            var materials = GetMaterialsInYZSlice(mainForm, height, depth, startX);
            if (materials.Count == 0)
            {
                Logger.Log("[SegmentationPropagator] No materials found in the segmented YZ slice");
                return null;
            }

            Logger.Log($"[SegmentationPropagator] Found {materials.Count} materials in YZ slice: {string.Join(", ", materials.Select(m => m.Name))}");

            // For each material, propagate forward and backward
            foreach (var material in materials)
            {
                Logger.Log($"[SegmentationPropagator] Propagating material '{material.Name}' (ID: {material.ID})");

                // Check if we're at boundaries and only propagate in valid directions
                bool canPropagateForward = startX < width - 1;
                bool canPropagateBackward = startX > 0;

                if (canPropagateForward)
                {
                    Logger.Log($"[SegmentationPropagator] Propagating forward from X={startX}");
                    PropagateYZForward(segmenter, mainForm, width, height, depth, startX, material, resultVolume, threshold);
                }
                else
                {
                    Logger.Log($"[SegmentationPropagator] At last slice X={startX}, skipping forward propagation");
                }

                if (canPropagateBackward)
                {
                    Logger.Log($"[SegmentationPropagator] Propagating backward from X={startX}");
                    PropagateYZBackward(segmenter, mainForm, width, height, depth, startX, material, resultVolume, threshold);
                }
                else
                {
                    Logger.Log($"[SegmentationPropagator] At first slice X={startX}, skipping backward propagation");
                }
            }

            return resultVolume;
        }

        /// <summary>
        /// Propagates original SAM segmentation in forward direction (increasing X) for YZ slices
        /// </summary>
        private static void PropagateYZForward(CTMemorySegmenter segmenter, MainForm mainForm,
                                     int width, int height, int depth,
                                     int startX, Material material, byte[,,] volumeLabels, int threshold)
        {
            // Safety check - don't try to propagate from the last slice
            if (startX >= width - 1)
            {
                Logger.Log($"[SegmentationPropagator] Cannot propagate forward from X={startX} (last slice)");
                return;
            }

            Logger.Log($"[SegmentationPropagator] Propagating material '{material.Name}' forward from X={startX}");

            // Keep track of the previous slice's points for consistency
            List<AnnotationPoint> prevPoints = null;
            int maxStepsWithoutSegmentation = 3; // Allow up to 3 slices with no segmentation before stopping
            int stepsWithoutSegmentation = 0;

            for (int x = startX + 1; x < width; x++)
            {
                Logger.Log($"[SegmentationPropagator] Processing forward slice X={x}");

                // Generate base image for current slice
                using (Bitmap baseImage = GenerateYZBitmap(mainForm, x, height, depth))
                {
                    // Create annotation points from previous slice's segmentation
                    List<AnnotationPoint> points;

                    if (prevPoints == null)
                    {
                        // First slice after start - sample points from the YZ slice
                        points = SamplePointsFromYZSlice(mainForm, height, depth, x - 1, volumeLabels, material.ID);
                    }
                    else
                    {
                        // Use previous slice's points with updated X coordinate
                        points = prevPoints.Select(p => new AnnotationPoint
                        {
                            X = x,
                            Y = p.Y,
                            Z = p.Z,
                            Label = p.Label
                        }).ToList();
                    }

                    if (points.Count == 0)
                    {
                        Logger.Log($"[SegmentationPropagator] No points found for material '{material.Name}' in slice X={x - 1}, stopping forward propagation");
                        break;
                    }

                    // Convert material points to SAM-expected format
                    List<AnnotationPoint> samPoints = BuildMixedPrompts(points, material.Name);

                    // Process the slice using SAM
                    using (Bitmap mask = segmenter.ProcessYZSlice(x, baseImage, samPoints, material.Name))
                    {
                        // Apply the mask to the volume
                        int pixelsSegmented = ApplyMaskToYZVolume(mask, height, depth, x, material.ID, volumeLabels, threshold);
                        Logger.Log($"[SegmentationPropagator] Segmented {pixelsSegmented} pixels for material '{material.Name}' in slice X={x}");

                        // If nothing was segmented, track but continue for a few more slices
                        if (pixelsSegmented == 0)
                        {
                            stepsWithoutSegmentation++;
                            Logger.Log($"[SegmentationPropagator] No pixels segmented in slice X={x}, steps without segmentation: {stepsWithoutSegmentation}");

                            if (stepsWithoutSegmentation >= maxStepsWithoutSegmentation)
                            {
                                Logger.Log($"[SegmentationPropagator] Reached {maxStepsWithoutSegmentation} steps without segmentation, stopping forward propagation");
                                break;
                            }
                        }
                        else
                        {
                            // Reset counter when we get successful segmentation
                            stepsWithoutSegmentation = 0;
                        }

                        // Update points for next iteration based on this slice's segmentation
                        List<AnnotationPoint> newPoints = SamplePointsFromYZSegmentation(mainForm, mask, x, material.ID, threshold);

                        // If we have meaningful new points, use them, otherwise keep using previous points
                        if (newPoints.Count >= 5)
                        {
                            prevPoints = newPoints;
                        }
                        // If very few points, augment with some from previous slice
                        else if (newPoints.Count > 0 && prevPoints != null)
                        {
                            // Keep some previous points but update their X coordinate
                            var augPoints = prevPoints.Take(Math.Min(10, prevPoints.Count))
                                .Select(p => new AnnotationPoint
                                {
                                    X = x,
                                    Y = p.Y,
                                    Z = p.Z,
                                    Label = p.Label
                                }).ToList();

                            // Add the new points
                            augPoints.AddRange(newPoints);
                            prevPoints = augPoints;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Propagates original SAM segmentation in backward direction (decreasing X) for YZ slices
        /// </summary>
        private static void PropagateYZBackward(CTMemorySegmenter segmenter, MainForm mainForm,
                                      int width, int height, int depth,
                                      int startX, Material material, byte[,,] volumeLabels, int threshold)
        {
            // Safety check - don't try to propagate from the first slice
            if (startX <= 0)
            {
                Logger.Log($"[SegmentationPropagator] Cannot propagate backward from X={startX} (first slice)");
                return;
            }

            Logger.Log($"[SegmentationPropagator] Propagating material '{material.Name}' backward from X={startX}");

            // Keep track of the previous slice's points for consistency
            List<AnnotationPoint> prevPoints = null;
            int maxStepsWithoutSegmentation = 3; // Allow up to 3 slices with no segmentation before stopping
            int stepsWithoutSegmentation = 0;

            for (int x = startX - 1; x >= 0; x--)
            {
                Logger.Log($"[SegmentationPropagator] Processing backward slice X={x}");

                // Generate base image for current slice
                using (Bitmap baseImage = GenerateYZBitmap(mainForm, x, height, depth))
                {
                    // Create annotation points from previous slice's segmentation
                    List<AnnotationPoint> points;

                    if (prevPoints == null)
                    {
                        // First slice before start - sample points from the YZ slice
                        points = SamplePointsFromYZSlice(mainForm, height, depth, x + 1, volumeLabels, material.ID);
                    }
                    else
                    {
                        // Use previous slice's points with updated X coordinate
                        points = prevPoints.Select(p => new AnnotationPoint
                        {
                            X = x,
                            Y = p.Y,
                            Z = p.Z,
                            Label = p.Label
                        }).ToList();
                    }

                    if (points.Count == 0)
                    {
                        Logger.Log($"[SegmentationPropagator] No points found for material '{material.Name}' in slice X={x + 1}, stopping backward propagation");
                        break;
                    }

                    // Convert material points to SAM-expected format
                    List<AnnotationPoint> samPoints = BuildMixedPrompts(points, material.Name);

                    // Process the slice using SAM
                    using (Bitmap mask = segmenter.ProcessYZSlice(x, baseImage, samPoints, material.Name))
                    {
                        // Apply the mask to the volume
                        int pixelsSegmented = ApplyMaskToYZVolume(mask, height, depth, x, material.ID, volumeLabels, threshold);
                        Logger.Log($"[SegmentationPropagator] Segmented {pixelsSegmented} pixels for material '{material.Name}' in slice X={x}");

                        // If nothing was segmented, track but continue for a few more slices
                        if (pixelsSegmented == 0)
                        {
                            stepsWithoutSegmentation++;
                            Logger.Log($"[SegmentationPropagator] No pixels segmented in slice X={x}, steps without segmentation: {stepsWithoutSegmentation}");

                            if (stepsWithoutSegmentation >= maxStepsWithoutSegmentation)
                            {
                                Logger.Log($"[SegmentationPropagator] Reached {maxStepsWithoutSegmentation} steps without segmentation, stopping backward propagation");
                                break;
                            }
                        }
                        else
                        {
                            // Reset counter when we get successful segmentation
                            stepsWithoutSegmentation = 0;
                        }

                        // Update points for next iteration based on this slice's segmentation
                        List<AnnotationPoint> newPoints = SamplePointsFromYZSegmentation(mainForm, mask, x, material.ID, threshold);

                        // If we have meaningful new points, use them, otherwise keep using previous points
                        if (newPoints.Count >= 5)
                        {
                            prevPoints = newPoints;
                        }
                        // If very few points, augment with some from previous slice
                        else if (newPoints.Count > 0 && prevPoints != null)
                        {
                            // Keep some previous points but update their X coordinate
                            var augPoints = prevPoints.Take(Math.Min(10, prevPoints.Count))
                                .Select(p => new AnnotationPoint
                                {
                                    X = x,
                                    Y = p.Y,
                                    Z = p.Z,
                                    Label = p.Label
                                }).ToList();

                            // Add the new points
                            augPoints.AddRange(newPoints);
                            prevPoints = augPoints;
                        }
                    }
                }
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Generates a bitmap for an XY slice at the given Z coordinate
        /// </summary>
        private static Bitmap GenerateXYBitmap(MainForm mainForm, int z, int width, int height)
        {
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte gVal = mainForm.volumeData[x, y, z];
                    bmp.SetPixel(x, y, Color.FromArgb(gVal, gVal, gVal));
                }
            }
            return bmp;
        }

        /// <summary>
        /// Generates a bitmap for an XZ slice at the given Y coordinate
        /// </summary>
        private static Bitmap GenerateXZBitmap(MainForm mainForm, int y, int width, int depth)
        {
            Bitmap bmp = new Bitmap(width, depth, PixelFormat.Format24bppRgb);
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte gVal = mainForm.volumeData[x, y, z];
                    bmp.SetPixel(x, z, Color.FromArgb(gVal, gVal, gVal));
                }
            }
            return bmp;
        }

        /// <summary>
        /// Generates a bitmap for a YZ slice at the given X coordinate
        /// </summary>
        private static Bitmap GenerateYZBitmap(MainForm mainForm, int x, int height, int depth)
        {
            Bitmap bmp = new Bitmap(depth, height, PixelFormat.Format24bppRgb);
            for (int y = 0; y < height; y++)
            {
                for (int z = 0; z < depth; z++)
                {
                    byte gVal = mainForm.volumeData[x, y, z];
                    bmp.SetPixel(z, y, Color.FromArgb(gVal, gVal, gVal));
                }
            }
            return bmp;
        }

        /// <summary>
        /// Finds a segmented XY slice to start propagation from
        /// </summary>
        private static int FindSegmentedXYSlice(MainForm mainForm, int width, int height, int depth)
        {
            // Try current slice first
            int currentZ = mainForm.CurrentSlice;
            if (IsXYSliceSegmented(mainForm, width, height, currentZ))
                return currentZ;

            // Search from the current slice outward
            for (int offset = 1; offset < depth; offset++)
            {
                // Check forward
                int checkZ = currentZ + offset;
                if (checkZ < depth && IsXYSliceSegmented(mainForm, width, height, checkZ))
                    return checkZ;

                // Check backward
                checkZ = currentZ - offset;
                if (checkZ >= 0 && IsXYSliceSegmented(mainForm, width, height, checkZ))
                    return checkZ;
            }

            return -1; // No segmented slice found
        }

        /// <summary>
        /// Finds a segmented XZ slice to start propagation from
        /// </summary>
        private static int FindSegmentedXZSlice(MainForm mainForm, int width, int height, int depth)
        {
            // Try current XZ slice first
            int currentY = mainForm.XzSliceY;
            if (IsXZSliceSegmented(mainForm, width, depth, currentY))
                return currentY;

            // Search from the current slice outward
            for (int offset = 1; offset < height; offset++)
            {
                // Check forward
                int checkY = currentY + offset;
                if (checkY < height && IsXZSliceSegmented(mainForm, width, depth, checkY))
                    return checkY;

                // Check backward
                checkY = currentY - offset;
                if (checkY >= 0 && IsXZSliceSegmented(mainForm, width, depth, checkY))
                    return checkY;
            }

            return -1; // No segmented slice found
        }

        /// <summary>
        /// Finds a segmented YZ slice to start propagation from
        /// </summary>
        private static int FindSegmentedYZSlice(MainForm mainForm, int width, int height, int depth)
        {
            // Try current YZ slice first
            int currentX = mainForm.YzSliceX;
            if (IsYZSliceSegmented(mainForm, height, depth, currentX))
                return currentX;

            // Search from the current slice outward
            for (int offset = 1; offset < width; offset++)
            {
                // Check forward
                int checkX = currentX + offset;
                if (checkX < width && IsYZSliceSegmented(mainForm, height, depth, checkX))
                    return checkX;

                // Check backward
                checkX = currentX - offset;
                if (checkX >= 0 && IsYZSliceSegmented(mainForm, height, depth, checkX))
                    return checkX;
            }

            return -1; // No segmented slice found
        }

        /// <summary>
        /// Checks if an XY slice has any segmentation
        /// </summary>
        private static bool IsXYSliceSegmented(MainForm mainForm, int width, int height, int z)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte label = mainForm.volumeLabels[x, y, z];
                    if (label > 0) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Checks if an XZ slice has any segmentation
        /// </summary>
        private static bool IsXZSliceSegmented(MainForm mainForm, int width, int depth, int y)
        {
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte label = mainForm.volumeLabels[x, y, z];
                    if (label > 0) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Checks if a YZ slice has any segmentation
        /// </summary>
        private static bool IsYZSliceSegmented(MainForm mainForm, int height, int depth, int x)
        {
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    byte label = mainForm.volumeLabels[x, y, z];
                    if (label > 0) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Gets all materials present in an XY slice
        /// </summary>
        private static List<Material> GetMaterialsInXYSlice(MainForm mainForm, int width, int height, int z)
        {
            HashSet<byte> materialIDs = new HashSet<byte>();
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte label = mainForm.volumeLabels[x, y, z];
                    if (label > 0) materialIDs.Add(label);
                }
            }

            // Convert material IDs to Material objects
            return mainForm.Materials.Where(m => materialIDs.Contains(m.ID) && !m.IsExterior).ToList();
        }

        /// <summary>
        /// Gets all materials present in an XZ slice
        /// </summary>
        private static List<Material> GetMaterialsInXZSlice(MainForm mainForm, int width, int depth, int y)
        {
            HashSet<byte> materialIDs = new HashSet<byte>();
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte label = mainForm.volumeLabels[x, y, z];
                    if (label > 0) materialIDs.Add(label);
                }
            }

            // Convert material IDs to Material objects
            return mainForm.Materials.Where(m => materialIDs.Contains(m.ID) && !m.IsExterior).ToList();
        }

        /// <summary>
        /// Gets all materials present in a YZ slice
        /// </summary>
        private static List<Material> GetMaterialsInYZSlice(MainForm mainForm, int height, int depth, int x)
        {
            HashSet<byte> materialIDs = new HashSet<byte>();
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    byte label = mainForm.volumeLabels[x, y, z];
                    if (label > 0) materialIDs.Add(label);
                }
            }

            // Convert material IDs to Material objects
            return mainForm.Materials.Where(m => materialIDs.Contains(m.ID) && !m.IsExterior).ToList();
        }

        /// <summary>
        /// Samples annotation points from a segmented XY slice
        /// </summary>
        private static List<AnnotationPoint> SamplePointsFromVolume(
            MainForm mainForm, int width, int height, int z, byte[,,] volumeLabels, byte materialID)
        {
            List<AnnotationPoint> points = new List<AnnotationPoint>();
            // Find points for this material
            List<Point> matPoints = new List<Point>();
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (volumeLabels[x, y, z] == materialID)
                    {
                        matPoints.Add(new Point(x, y));
                    }
                }
            }

            // Get material name
            string materialName = "Unknown";
            Material material = mainForm.Materials.FirstOrDefault(m => m.ID == materialID);
            if (material != null)
            {
                materialName = material.Name;
            }

            // Sample points uniformly across the segmentation
            if (matPoints.Count > 0)
            {
                int numSamples = Math.Min(20, matPoints.Count);
                int step = matPoints.Count / numSamples;
                for (int i = 0; i < matPoints.Count; i += step)
                {
                    Point p = matPoints[i];
                    points.Add(new AnnotationPoint
                    {
                        X = p.X,
                        Y = p.Y,
                        Z = z,
                        Label = materialName
                    });
                }
            }

            return points;
        }

        /// <summary>
        /// Samples annotation points from a segmented XZ slice
        /// </summary>
        private static List<AnnotationPoint> SamplePointsFromXZSlice(
            MainForm mainForm, int width, int depth, int y, byte[,,] volumeLabels, byte materialID)
        {
            List<AnnotationPoint> points = new List<AnnotationPoint>();
            // Find points for this material
            List<Point> matPoints = new List<Point>();
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (volumeLabels[x, y, z] == materialID)
                    {
                        matPoints.Add(new Point(x, z));
                    }
                }
            }

            // Get material name
            string materialName = "Unknown";
            Material material = mainForm.Materials.FirstOrDefault(m => m.ID == materialID);
            if (material != null)
            {
                materialName = material.Name;
            }

            // Sample points uniformly across the segmentation
            if (matPoints.Count > 0)
            {
                int numSamples = Math.Min(20, matPoints.Count);
                int step = matPoints.Count / numSamples;
                for (int i = 0; i < matPoints.Count; i += step)
                {
                    Point p = matPoints[i];
                    points.Add(new AnnotationPoint
                    {
                        X = p.X,
                        Y = y,
                        Z = p.Y, // p.Y holds Z in this context
                        Label = materialName
                    });
                }
            }

            return points;
        }

        /// <summary>
        /// Samples annotation points from a segmented YZ slice
        /// </summary>
        private static List<AnnotationPoint> SamplePointsFromYZSlice(
            MainForm mainForm, int height, int depth, int x, byte[,,] volumeLabels, byte materialID)
        {
            List<AnnotationPoint> points = new List<AnnotationPoint>();
            // Find points for this material
            List<Point> matPoints = new List<Point>();
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    if (volumeLabels[x, y, z] == materialID)
                    {
                        matPoints.Add(new Point(z, y)); // Note: we store z in the Point.X
                    }
                }
            }

            // Get material name
            string materialName = "Unknown";
            Material material = mainForm.Materials.FirstOrDefault(m => m.ID == materialID);
            if (material != null)
            {
                materialName = material.Name;
            }

            // Sample points uniformly across the segmentation
            if (matPoints.Count > 0)
            {
                int numSamples = Math.Min(20, matPoints.Count);
                int step = matPoints.Count / numSamples;
                for (int i = 0; i < matPoints.Count; i += step)
                {
                    Point p = matPoints[i];
                    points.Add(new AnnotationPoint
                    {
                        X = x,
                        Y = p.Y,
                        Z = p.X, // p.X holds Z in this context
                        Label = materialName
                    });
                }
            }

            return points;
        }

        /// <summary>
        /// Samples annotation points from newly segmented XY slice
        /// </summary>
        private static List<AnnotationPoint> SamplePointsFromNewSegmentation(
            MainForm mainForm, Bitmap mask, int z, byte materialID, int threshold)
        {
            List<AnnotationPoint> points = new List<AnnotationPoint>();
            // Find white pixels in the mask
            List<Point> maskPoints = new List<Point>();
            for (int y = 0; y < mask.Height; y++)
            {
                for (int x = 0; x < mask.Width; x++)
                {
                    if (mask.GetPixel(x, y).R > threshold)
                    {
                        maskPoints.Add(new Point(x, y));
                    }
                }
            }

            // Get material name
            string materialName = "Unknown";
            Material material = mainForm.Materials.FirstOrDefault(m => m.ID == materialID);
            if (material != null)
            {
                materialName = material.Name;
            }

            // Sample points from mask
            if (maskPoints.Count > 0)
            {
                int numSamples = Math.Min(10, maskPoints.Count);
                int step = maskPoints.Count / numSamples;
                for (int i = 0; i < maskPoints.Count; i += step)
                {
                    Point p = maskPoints[i];
                    points.Add(new AnnotationPoint
                    {
                        X = p.X,
                        Y = p.Y,
                        Z = z,
                        Label = materialName
                    });
                }
            }

            return points;
        }

        /// <summary>
        /// Samples annotation points from newly segmented XZ slice
        /// </summary>
        private static List<AnnotationPoint> SamplePointsFromXZSegmentation(
            MainForm mainForm, Bitmap mask, int y, byte materialID, int threshold)
        {
            List<AnnotationPoint> points = new List<AnnotationPoint>();
            // Find white pixels in the mask
            List<Point> maskPoints = new List<Point>();
            for (int z = 0; z < mask.Height; z++)
            {
                for (int x = 0; x < mask.Width; x++)
                {
                    if (mask.GetPixel(x, z).R > threshold)
                    {
                        maskPoints.Add(new Point(x, z));
                    }
                }
            }

            // Get material name
            string materialName = "Unknown";
            Material material = mainForm.Materials.FirstOrDefault(m => m.ID == materialID);
            if (material != null)
            {
                materialName = material.Name;
            }

            // Sample points from mask
            if (maskPoints.Count > 0)
            {
                int numSamples = Math.Min(10, maskPoints.Count);
                int step = maskPoints.Count / numSamples;
                for (int i = 0; i < maskPoints.Count; i += step)
                {
                    Point p = maskPoints[i];
                    points.Add(new AnnotationPoint
                    {
                        X = p.X,
                        Y = y,
                        Z = p.Y, // p.Y holds Z in this context
                        Label = materialName
                    });
                }
            }

            return points;
        }

        /// <summary>
        /// Samples annotation points from newly segmented YZ slice
        /// </summary>
        private static List<AnnotationPoint> SamplePointsFromYZSegmentation(
            MainForm mainForm, Bitmap mask, int x, byte materialID, int threshold)
        {
            List<AnnotationPoint> points = new List<AnnotationPoint>();
            // Find white pixels in the mask - YZ view has Z on the horizontal axis and Y on the vertical
            List<Point> maskPoints = new List<Point>();
            for (int y = 0; y < mask.Height; y++)
            {
                for (int z = 0; z < mask.Width; z++)
                {
                    if (mask.GetPixel(z, y).R > threshold)
                    {
                        maskPoints.Add(new Point(z, y)); // z is stored in the X component
                    }
                }
            }

            // Get material name
            string materialName = "Unknown";
            Material material = mainForm.Materials.FirstOrDefault(m => m.ID == materialID);
            if (material != null)
            {
                materialName = material.Name;
            }

            // Sample points from mask
            if (maskPoints.Count > 0)
            {
                int numSamples = Math.Min(10, maskPoints.Count);
                int step = maskPoints.Count / numSamples;
                for (int i = 0; i < maskPoints.Count; i += step)
                {
                    Point p = maskPoints[i];
                    points.Add(new AnnotationPoint
                    {
                        X = x,
                        Y = p.Y,
                        Z = p.X, // p.X holds Z in this context
                        Label = materialName
                    });
                }
            }

            return points;
        }

        /// <summary>
        /// Applies a binary mask to a 3D volume for XY view
        /// </summary>
        private static int ApplyMaskToVolume(
            Bitmap mask, int width, int height, int z,
            byte materialID, byte[,,] volumeLabels, int threshold)
        {
            int pixelsSegmented = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (x < mask.Width && y < mask.Height && mask.GetPixel(x, y).R > threshold)
                    {
                        volumeLabels[x, y, z] = materialID;
                        pixelsSegmented++;
                    }
                }
            }
            return pixelsSegmented;
        }

        /// <summary>
        /// Applies a binary mask to a 3D volume for XZ view
        /// </summary>
        private static int ApplyMaskToXZVolume(
            Bitmap mask, int width, int depth, int y,
            byte materialID, byte[,,] volumeLabels, int threshold)
        {
            int pixelsSegmented = 0;
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (x < mask.Width && z < mask.Height && mask.GetPixel(x, z).R > threshold)
                    {
                        volumeLabels[x, y, z] = materialID;
                        pixelsSegmented++;
                    }
                }
            }
            return pixelsSegmented;
        }

        /// <summary>
        /// Applies a binary mask to a 3D volume for YZ view
        /// </summary>
        private static int ApplyMaskToYZVolume(
            Bitmap mask, int height, int depth, int x,
            byte materialID, byte[,,] volumeLabels, int threshold)
        {
            int pixelsSegmented = 0;
            for (int y = 0; y < height; y++)
            {
                for (int z = 0; z < depth; z++)
                {
                    if (z < mask.Width && y < mask.Height && mask.GetPixel(z, y).R > threshold)
                    {
                        volumeLabels[x, y, z] = materialID;
                        pixelsSegmented++;
                    }
                }
            }
            return pixelsSegmented;
        }

        /// <summary>
        /// Builds mixed prompts for SAM (converting material points to SAM-expected format)
        /// </summary>
        private static List<AnnotationPoint> BuildMixedPrompts(
            List<AnnotationPoint> slicePoints, string targetMaterialName)
        {
            // Create a new list to avoid modifying the input
            List<AnnotationPoint> samPoints = new List<AnnotationPoint>();

            // Convert each point to the appropriate label format for SAM
            foreach (var point in slicePoints)
            {
                bool isPositive = point.Label.Equals(targetMaterialName, StringComparison.OrdinalIgnoreCase);
                samPoints.Add(new AnnotationPoint
                {
                    X = point.X,
                    Y = point.Y,
                    Z = point.Z,
                    Label = isPositive ? "Foreground" : "Exterior"
                });
            }

            return samPoints;
        }

        /// <summary>
        /// Applies fusion to results from multiple directions
        /// </summary>
        private static byte[,,] ApplyFusion(
            Dictionary<string, byte[,,]> directionResults,
            int width, int height, int depth,
            string fusionAlgorithm,
            MainForm mainForm)
        {
            // Create a new volume to store the fused result
            byte[,,] fusedVolume = new byte[width, height, depth];

            // Copy the existing segmentation as a baseline
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        fusedVolume[x, y, z] = mainForm.volumeLabels[x, y, z];
                    }
                }
            }

            // Apply the appropriate fusion algorithm
            switch (fusionAlgorithm)
            {
                case "Majority Vote":
                    ApplyMajorityVoteFusion(directionResults, fusedVolume, width, height, depth);
                    break;
                case "Maximum Material":
                    ApplyMaximumMaterialFusion(directionResults, fusedVolume, width, height, depth, mainForm);
                    break;
                case "Direction Priority (XY->XZ->YZ)":
                    ApplyDirectionPriorityFusion(directionResults, fusedVolume, width, height, depth);
                    break;
                default:
                    Logger.Log($"[SegmentationPropagator] Unknown fusion algorithm: {fusionAlgorithm}. Using Majority Vote.");
                    ApplyMajorityVoteFusion(directionResults, fusedVolume, width, height, depth);
                    break;
            }

            return fusedVolume;
        }

        /// <summary>
        /// Applies majority vote fusion to results from multiple directions
        /// </summary>
        private static void ApplyMajorityVoteFusion(
            Dictionary<string, byte[,,]> directionResults,
            byte[,,] fusedVolume,
            int width, int height, int depth)
        {
            // For each voxel, count materials from different directions
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        Dictionary<byte, int> votes = new Dictionary<byte, int>();

                        // Count votes from each direction
                        foreach (var result in directionResults.Values)
                        {
                            byte material = result[x, y, z];
                            if (material > 0) // Only count non-exterior
                            {
                                if (!votes.ContainsKey(material))
                                    votes[material] = 0;
                                votes[material]++;
                            }
                        }

                        // Find the material with the most votes
                        if (votes.Count > 0)
                        {
                            byte bestMaterial = votes.OrderByDescending(v => v.Value).First().Key;
                            fusedVolume[x, y, z] = bestMaterial;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Applies maximum material fusion to results from multiple directions
        /// </summary>
        private static void ApplyMaximumMaterialFusion(
            Dictionary<string, byte[,,]> directionResults,
            byte[,,] fusedVolume,
            int width, int height, int depth,
            MainForm mainForm)
        {
            // For each voxel, use the material with the highest ID
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        byte maxMaterial = 0;

                        // Find the highest material ID from all directions
                        foreach (var result in directionResults.Values)
                        {
                            byte material = result[x, y, z];
                            if (material > maxMaterial)
                                maxMaterial = material;
                        }

                        // Apply the highest material ID
                        if (maxMaterial > 0)
                            fusedVolume[x, y, z] = maxMaterial;
                    }
                }
            }
        }

        /// <summary>
        /// Applies direction priority fusion to results from multiple directions
        /// </summary>
        private static void ApplyDirectionPriorityFusion(
            Dictionary<string, byte[,,]> directionResults,
            byte[,,] fusedVolume,
            int width, int height, int depth)
        {
            // Define the priority order
            string[] priorityOrder = { "XY", "XZ", "YZ" };

            // For each voxel, check directions in priority order
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        // Check each direction in priority order
                        foreach (string direction in priorityOrder)
                        {
                            if (directionResults.ContainsKey(direction))
                            {
                                byte material = directionResults[direction][x, y, z];
                                if (material > 0)
                                {
                                    fusedVolume[x, y, z] = material;
                                    break; // Use the first non-zero material found
                                }
                            }
                        }
                    }
                }
            }
        }

        #endregion
    }
}
