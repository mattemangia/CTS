using System;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTSegmenter
{
    /// <summary>
    /// Static class providing 3D segmentation propagation functionality using SAM2
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
            string imageEncoderPath = Path.Combine(modelFolder, "image_encoder_hiera_t.onnx");
            string promptEncoderPath = Path.Combine(modelFolder, "prompt_encoder_hiera_t.onnx");
            string maskDecoderPath = Path.Combine(modelFolder, "mask_decoder_hiera_t.onnx");
            string memoryAttentionPath = Path.Combine(modelFolder, "memory_attention_hiera_t.onnx");
            string memoryEncoderPath = Path.Combine(modelFolder, "memory_encoder_hiera_t.onnx");
            string mlpPath = Path.Combine(modelFolder, "mlp_hiera_t.onnx");

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
                settings.EnableMlp))
            {
                segmenter.UseSelectiveHoleFilling = settings.UseSelectiveHoleFilling;
                segmenter.MaskThreshold = threshold;

                // Process XY direction if selected
                if (directions.HasFlag(SAMForm.SegmentationDirection.XY))
                {
                    byte[,,] xyResult = PropagateXYDirection(segmenter, mainForm, width, height, depth, threshold);
                    if (xyResult != null)
                    {
                        directionResults["XY"] = xyResult;
                        Logger.Log("[SegmentationPropagator] XY direction propagation completed");
                    }
                }

                // Process XZ direction if selected
                if (directions.HasFlag(SAMForm.SegmentationDirection.XZ))
                {
                    byte[,,] xzResult = PropagateXZDirection(segmenter, mainForm, width, height, depth, threshold);
                    if (xzResult != null)
                    {
                        directionResults["XZ"] = xzResult;
                        Logger.Log("[SegmentationPropagator] XZ direction propagation completed");
                    }
                }

                // Process YZ direction if selected
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

            // Apply fusion if multiple directions were propagated
            if (directionResults.Count > 1)
            {
                Logger.Log($"[SegmentationPropagator] Applying fusion for {directionResults.Count} directions using {settings.FusionAlgorithm}");
                return ApplyFusion(directionResults, width, height, depth, settings.FusionAlgorithm);
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

        #region XY Direction Propagation

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

        // Find an XY slice that has been segmented
        private static int FindSegmentedXYSlice(MainForm mainForm, int width, int height, int depth)
        {
            // First try the current slice
            int currentZ = mainForm.CurrentSlice;
            if (IsXYSliceSegmented(mainForm, width, height, currentZ))
            {
                return currentZ;
            }

            // If current slice isn't segmented, scan through all slices
            for (int z = 0; z < depth; z++)
            {
                if (IsXYSliceSegmented(mainForm, width, height, z))
                {
                    return z;
                }
            }

            return -1; // No segmented slice found
        }

        // Check if an XY slice has any segmentation
        private static bool IsXYSliceSegmented(MainForm mainForm, int width, int height, int z)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (mainForm.volumeLabels[x, y, z] > 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // Get materials used in a particular XY slice
        private static List<Material> GetMaterialsInXYSlice(MainForm mainForm, int width, int height, int z)
        {
            HashSet<byte> materialIds = new HashSet<byte>();

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte label = mainForm.volumeLabels[x, y, z];
                    if (label > 0)
                    {
                        materialIds.Add(label);
                    }
                }
            }

            List<Material> materials = new List<Material>();
            foreach (byte id in materialIds)
            {
                Material material = mainForm.Materials.FirstOrDefault(m => m.ID == id);
                if (material != null)
                {
                    materials.Add(material);
                }
            }

            return materials;
        }

        // Propagate material segmentation in XY direction forward (increasing z)
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

                    // Process the slice using SAM2
                    using (Bitmap mask = segmenter.ProcessXYSlice(z, baseImage, samPoints, null, null))
                    {
                        // Apply the mask to the volume
                        int pixelsSegmented = ApplyMaskToVolume(mask, width, height, z, material.ID, volumeLabels, threshold);
                        Logger.Log($"[SegmentationPropagator] Segmented {pixelsSegmented} pixels for material '{material.Name}' in slice Z={z}");

                        // If nothing was segmented, stop propagation for this material
                        if (pixelsSegmented == 0)
                        {
                            Logger.Log($"[SegmentationPropagator] No pixels segmented in slice Z={z}, stopping forward propagation");
                            break;
                        }

                        // Update points for next iteration
                        prevPoints = SamplePointsFromNewSegmentation(mainForm, mask, z, material.ID, threshold);
                    }
                }
            }
        }

        // Propagate material segmentation in XY direction backward (decreasing z)
        private static void PropagateXYBackward(CTMemorySegmenter segmenter, MainForm mainForm,
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
                            Label = p.Label
                        }).ToList();
                    }

                    if (points.Count == 0)
                    {
                        Logger.Log($"[SegmentationPropagator] No points found for material '{material.Name}' in slice Z={z + 1}, stopping backward propagation");
                        break;
                    }

                    // Convert material points to SAM-expected format
                    List<AnnotationPoint> samPoints = BuildMixedPrompts(points, material.Name);

                    // Process the slice using SAM2
                    using (Bitmap mask = segmenter.ProcessXYSlice(z, baseImage, samPoints, null, null))
                    {
                        // Apply the mask to the volume
                        int pixelsSegmented = ApplyMaskToVolume(mask, width, height, z, material.ID, volumeLabels, threshold);
                        Logger.Log($"[SegmentationPropagator] Segmented {pixelsSegmented} pixels for material '{material.Name}' in slice Z={z}");

                        // If nothing was segmented, stop propagation for this material
                        if (pixelsSegmented == 0)
                        {
                            Logger.Log($"[SegmentationPropagator] No pixels segmented in slice Z={z}, stopping backward propagation");
                            break;
                        }

                        // Update points for next iteration based on this slice's segmentation
                        prevPoints = SamplePointsFromNewSegmentation(mainForm, mask, z, material.ID, threshold);
                    }
                }
            }
        }

        #endregion

        #region XZ Direction Propagation

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

        // Helper methods for XZ direction
        private static int FindSegmentedXZSlice(MainForm mainForm, int width, int height, int depth)
        {
            // Try current XZ slice first
            int currentY = mainForm.XzSliceY;
            if (IsXZSliceSegmented(mainForm, width, depth, currentY))
                return currentY;

            // Scan all slices
            for (int y = 0; y < height; y++)
            {
                if (IsXZSliceSegmented(mainForm, width, depth, y))
                    return y;
            }
            return -1;
        }

        private static bool IsXZSliceSegmented(MainForm mainForm, int width, int depth, int y)
        {
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (mainForm.volumeLabels[x, y, z] > 0)
                        return true;
                }
            }
            return false;
        }

        private static List<Material> GetMaterialsInXZSlice(MainForm mainForm, int width, int depth, int y)
        {
            HashSet<byte> materialIds = new HashSet<byte>();

            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte label = mainForm.volumeLabels[x, y, z];
                    if (label > 0)
                        materialIds.Add(label);
                }
            }

            return materialIds.Select(id => mainForm.Materials.FirstOrDefault(m => m.ID == id))
                             .Where(m => m != null)
                             .ToList();
        }

        private static void PropagateXZForward(CTMemorySegmenter segmenter, MainForm mainForm,
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

                    // Process the slice using SAM2
                    using (Bitmap mask = segmenter.ProcessXZSlice(y, baseImage, samPoints, null, null))
                    {
                        // Apply the mask to the volume
                        int pixelsSegmented = ApplyMaskToXZVolume(mask, width, depth, y, material.ID, volumeLabels, threshold);
                        Logger.Log($"[SegmentationPropagator] Segmented {pixelsSegmented} pixels for material '{material.Name}' in slice Y={y}");

                        // If nothing was segmented, stop propagation for this material
                        if (pixelsSegmented == 0)
                        {
                            Logger.Log($"[SegmentationPropagator] No pixels segmented in slice Y={y}, stopping forward propagation");
                            break;
                        }

                        // Update points for next iteration based on this slice's segmentation
                        prevPoints = SamplePointsFromXZSegmentation(mainForm, mask, y, material.ID, threshold);
                    }
                }
            }
        }

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

                    // Process the slice using SAM2
                    using (Bitmap mask = segmenter.ProcessXZSlice(y, baseImage, samPoints, null, null))
                    {
                        // Apply the mask to the volume
                        int pixelsSegmented = ApplyMaskToXZVolume(mask, width, depth, y, material.ID, volumeLabels, threshold);
                        Logger.Log($"[SegmentationPropagator] Segmented {pixelsSegmented} pixels for material '{material.Name}' in slice Y={y}");

                        // If nothing was segmented, stop propagation for this material
                        if (pixelsSegmented == 0)
                        {
                            Logger.Log($"[SegmentationPropagator] No pixels segmented in slice Y={y}, stopping backward propagation");
                            break;
                        }

                        // Update points for next iteration based on this slice's segmentation
                        prevPoints = SamplePointsFromXZSegmentation(mainForm, mask, y, material.ID, threshold);
                    }
                }
            }
        }

        // Sample points from a volume slice in XZ plane
        private static List<AnnotationPoint> SamplePointsFromXZSlice(
    MainForm mainForm,
    int width, int depth, int sliceY,
    byte[,,] volumeLabels, byte materialId)
        {
            List<AnnotationPoint> points = new List<AnnotationPoint>();
            List<Point> foregroundPoints = new List<Point>();
            List<Point> backgroundPoints = new List<Point>();

            // Find the target material name from its ID
            Material targetMaterial = mainForm.Materials.FirstOrDefault(m => m.ID == materialId);
            if (targetMaterial == null)
            {
                Logger.Log($"[SamplePointsFromXZSlice] Error: Material with ID {materialId} not found");
                return points;
            }

            string targetMaterialName = targetMaterial.Name;
            Logger.Log($"[SamplePointsFromXZSlice] Sampling for material '{targetMaterialName}' (ID: {materialId})");

            // Sample points from the XZ slice
            for (int z = 0; z < depth; z += 10)  // Sample every 10 pixels
            {
                for (int x = 0; x < width; x += 10)  // Sample every 10 pixels
                {
                    if (volumeLabels[x, sliceY, z] == materialId)
                    {
                        foregroundPoints.Add(new Point(x, z)); // For XZ, x is x and z is the second coordinate
                    }
                    else if (volumeLabels[x, sliceY, z] > 0)  // Other material
                    {
                        backgroundPoints.Add(new Point(x, z));
                    }
                }
            }

            // Randomly select a subset of points if we have too many
            Random random = new Random();
            int maxPointsPerClass = 20;  // Adjust as needed

            if (foregroundPoints.Count > 0)
            {
                foreach (var p in foregroundPoints.OrderBy(x => random.Next()).Take(Math.Min(maxPointsPerClass, foregroundPoints.Count)))
                {
                    points.Add(new AnnotationPoint
                    {
                        X = p.X,     // X stays X
                        Y = sliceY,  // Y is fixed for this XZ slice
                        Z = p.Y,     // In our Point, Y holds the Z value
                        Label = targetMaterialName
                    });
                }
            }

            if (backgroundPoints.Count > 0)
            {
                foreach (var p in backgroundPoints.OrderBy(x => random.Next()).Take(Math.Min(maxPointsPerClass, backgroundPoints.Count)))
                {
                    // Look up the material name for this background point
                    byte backgroundId = volumeLabels[p.X, sliceY, p.Y];
                    Material backgroundMaterial = mainForm.Materials.FirstOrDefault(m => m.ID == backgroundId);
                    string backgroundName = backgroundMaterial?.Name ?? "Exterior";

                    points.Add(new AnnotationPoint
                    {
                        X = p.X,
                        Y = sliceY,
                        Z = p.Y,     // In our Point, Y holds the Z value
                        Label = backgroundName
                    });
                }
            }

            return points;
        }


        // Sample points from a newly segmented XZ mask
        private static List<AnnotationPoint> SamplePointsFromXZSegmentation(
    MainForm mainForm,
    Bitmap mask, int sliceY, byte materialId, int threshold)
        {
            List<AnnotationPoint> points = new List<AnnotationPoint>();
            List<Point> foregroundPoints = new List<Point>();
            List<Point> backgroundPoints = new List<Point>();

            // Find the target material name from its ID
            Material targetMaterial = mainForm.Materials.FirstOrDefault(m => m.ID == materialId);
            if (targetMaterial == null)
            {
                Logger.Log($"[SamplePointsFromXZSegmentation] Error: Material with ID {materialId} not found");
                return points;
            }

            string targetMaterialName = targetMaterial.Name;

            // Sample from the mask
            for (int z = 0; z < mask.Height; z += 10)
            {
                for (int x = 0; x < mask.Width; x += 10)
                {
                    if (mask.GetPixel(x, z).R > threshold)
                    {
                        foregroundPoints.Add(new Point(x, z));
                    }
                    else
                    {
                        backgroundPoints.Add(new Point(x, z));
                    }
                }
            }

            // Randomly select a subset of points
            Random random = new Random();
            int maxPointsPerClass = 20;

            if (foregroundPoints.Count > 0)
            {
                foreach (var p in foregroundPoints.OrderBy(x => random.Next()).Take(Math.Min(maxPointsPerClass, foregroundPoints.Count)))
                {
                    points.Add(new AnnotationPoint
                    {
                        X = p.X,
                        Y = sliceY,
                        Z = p.Y,     // In our Point, Y holds the Z value
                        Label = targetMaterialName
                    });
                }
            }

            if (backgroundPoints.Count > 0)
            {
                foreach (var p in backgroundPoints.OrderBy(x => random.Next()).Take(Math.Min(maxPointsPerClass, backgroundPoints.Count)))
                {
                    points.Add(new AnnotationPoint
                    {
                        X = p.X,
                        Y = sliceY,
                        Z = p.Y,     // In our Point, Y holds the Z value
                        Label = "Exterior"
                    });
                }
            }

            return points;
        }
        // Apply an XZ mask to the volume
        private static int ApplyMaskToXZVolume(Bitmap mask, int width, int depth, int sliceY,
                                            byte materialId, byte[,,] volumeLabels, int threshold)
        {
            int pixelsSegmented = 0;

            for (int z = 0; z < depth && z < mask.Height; z++)
            {
                for (int x = 0; x < width && x < mask.Width; x++)
                {
                    if (mask.GetPixel(x, z).R > threshold)
                    {
                        if (volumeLabels[x, sliceY, z] == 0)  // Only overwrite if empty
                        {
                            volumeLabels[x, sliceY, z] = materialId;
                            pixelsSegmented++;
                        }
                    }
                }
            }

            return pixelsSegmented;
        }

        #endregion

        #region YZ Direction Propagation

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

        private static int FindSegmentedYZSlice(MainForm mainForm, int width, int height, int depth)
        {
            // Try current YZ slice first
            int currentX = mainForm.YzSliceX;
            if (IsYZSliceSegmented(mainForm, height, depth, currentX))
                return currentX;

            // Scan all slices
            for (int x = 0; x < width; x++)
            {
                if (IsYZSliceSegmented(mainForm, height, depth, x))
                    return x;
            }
            return -1;
        }

        private static bool IsYZSliceSegmented(MainForm mainForm, int height, int depth, int x)
        {
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    if (mainForm.volumeLabels[x, y, z] > 0)
                        return true;
                }
            }
            return false;
        }

        private static List<Material> GetMaterialsInYZSlice(MainForm mainForm, int height, int depth, int x)
        {
            HashSet<byte> materialIds = new HashSet<byte>();

            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    byte label = mainForm.volumeLabels[x, y, z];
                    if (label > 0)
                        materialIds.Add(label);
                }
            }

            return materialIds.Select(id => mainForm.Materials.FirstOrDefault(m => m.ID == id))
                             .Where(m => m != null)
                             .ToList();
        }

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

                    // Process the slice using SAM2
                    using (Bitmap mask = segmenter.ProcessYZSlice(x, baseImage, samPoints, null, null))
                    {
                        // Apply the mask to the volume
                        int pixelsSegmented = ApplyMaskToYZVolume(mask, height, depth, x, material.ID, volumeLabels, threshold);
                        Logger.Log($"[SegmentationPropagator] Segmented {pixelsSegmented} pixels for material '{material.Name}' in slice X={x}");

                        // If nothing was segmented, stop propagation for this material
                        if (pixelsSegmented == 0)
                        {
                            Logger.Log($"[SegmentationPropagator] No pixels segmented in slice X={x}, stopping forward propagation");
                            break;
                        }

                        // Update points for next iteration based on this slice's segmentation
                        prevPoints = SamplePointsFromYZSegmentation(mainForm, mask, x, material.ID, threshold);
                    }
                }
            }
        }

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

                    // Process the slice using SAM2
                    using (Bitmap mask = segmenter.ProcessYZSlice(x, baseImage, samPoints, null, null))
                    {
                        // Apply the mask to the volume
                        int pixelsSegmented = ApplyMaskToYZVolume(mask, height, depth, x, material.ID, volumeLabels, threshold);
                        Logger.Log($"[SegmentationPropagator] Segmented {pixelsSegmented} pixels for material '{material.Name}' in slice X={x}");

                        // If nothing was segmented, stop propagation for this material
                        if (pixelsSegmented == 0)
                        {
                            Logger.Log($"[SegmentationPropagator] No pixels segmented in slice X={x}, stopping backward propagation");
                            break;
                        }

                        // Update points for next iteration based on this slice's segmentation
                        prevPoints = SamplePointsFromYZSegmentation(mainForm, mask, x, material.ID, threshold);
                    }
                }
            }
        }

        // Sample points from a volume slice in YZ plane
        private static List<AnnotationPoint> SamplePointsFromYZSlice(
    MainForm mainForm,
    int height, int depth, int sliceX,
    byte[,,] volumeLabels, byte materialId)
        {
            List<AnnotationPoint> points = new List<AnnotationPoint>();
            List<Point> foregroundPoints = new List<Point>();
            List<Point> backgroundPoints = new List<Point>();

            // Find the target material name from its ID
            Material targetMaterial = mainForm.Materials.FirstOrDefault(m => m.ID == materialId);
            if (targetMaterial == null)
            {
                Logger.Log($"[SamplePointsFromYZSlice] Error: Material with ID {materialId} not found");
                return points;
            }

            string targetMaterialName = targetMaterial.Name;
            Logger.Log($"[SamplePointsFromYZSlice] Sampling for material '{targetMaterialName}' (ID: {materialId})");

            // Sample points from the YZ slice
            for (int z = 0; z < depth; z += 10)  // Sample every 10 pixels
            {
                for (int y = 0; y < height; y += 10)  // Sample every 10 pixels
                {
                    if (volumeLabels[sliceX, y, z] == materialId)
                    {
                        foregroundPoints.Add(new Point(z, y)); // For YZ bitmap, z is x-coordinate, y is y-coordinate
                    }
                    else if (volumeLabels[sliceX, y, z] > 0)  // Other material
                    {
                        backgroundPoints.Add(new Point(z, y));
                    }
                }
            }

            // Randomly select a subset of points if we have too many
            Random random = new Random();
            int maxPointsPerClass = 20;  // Adjust as needed

            if (foregroundPoints.Count > 0)
            {
                foreach (var p in foregroundPoints.OrderBy(x => random.Next()).Take(Math.Min(maxPointsPerClass, foregroundPoints.Count)))
                {
                    points.Add(new AnnotationPoint
                    {
                        X = sliceX,
                        Y = p.Y,     // Y stays Y
                        Z = p.X,     // In our Point, X holds the Z value for YZ bitmap
                        Label = targetMaterialName
                    });
                }
            }

            if (backgroundPoints.Count > 0)
            {
                foreach (var p in backgroundPoints.OrderBy(x => random.Next()).Take(Math.Min(maxPointsPerClass, backgroundPoints.Count)))
                {
                    // Look up the material name for this background point
                    byte backgroundId = volumeLabels[sliceX, p.Y, p.X];
                    Material backgroundMaterial = mainForm.Materials.FirstOrDefault(m => m.ID == backgroundId);
                    string backgroundName = backgroundMaterial?.Name ?? "Exterior";

                    points.Add(new AnnotationPoint
                    {
                        X = sliceX,
                        Y = p.Y,
                        Z = p.X,     // In our Point, X holds the Z value for YZ bitmap
                        Label = backgroundName
                    });
                }
            }

            return points;
        }

        // Sample points from a newly segmented YZ mask
        private static List<AnnotationPoint> SamplePointsFromYZSegmentation(
    MainForm mainForm,
    Bitmap mask, int sliceX, byte materialId, int threshold)
        {
            List<AnnotationPoint> points = new List<AnnotationPoint>();
            List<Point> foregroundPoints = new List<Point>();
            List<Point> backgroundPoints = new List<Point>();

            // Find the target material name from its ID
            Material targetMaterial = mainForm.Materials.FirstOrDefault(m => m.ID == materialId);
            if (targetMaterial == null)
            {
                Logger.Log($"[SamplePointsFromYZSegmentation] Error: Material with ID {materialId} not found");
                return points;
            }

            string targetMaterialName = targetMaterial.Name;

            // Sample from the mask
            for (int y = 0; y < mask.Height; y += 10)
            {
                for (int z = 0; z < mask.Width; z += 10)
                {
                    if (mask.GetPixel(z, y).R > threshold)
                    {
                        foregroundPoints.Add(new Point(z, y));
                    }
                    else
                    {
                        backgroundPoints.Add(new Point(z, y));
                    }
                }
            }

            // Randomly select a subset of points
            Random random = new Random();
            int maxPointsPerClass = 20;

            if (foregroundPoints.Count > 0)
            {
                foreach (var p in foregroundPoints.OrderBy(x => random.Next()).Take(Math.Min(maxPointsPerClass, foregroundPoints.Count)))
                {
                    points.Add(new AnnotationPoint
                    {
                        X = sliceX,
                        Y = p.Y,
                        Z = p.X,     // In our Point, X holds the Z value for YZ bitmap
                        Label = targetMaterialName
                    });
                }
            }

            if (backgroundPoints.Count > 0)
            {
                foreach (var p in backgroundPoints.OrderBy(x => random.Next()).Take(Math.Min(maxPointsPerClass, backgroundPoints.Count)))
                {
                    points.Add(new AnnotationPoint
                    {
                        X = sliceX,
                        Y = p.Y,
                        Z = p.X,     // In our Point, X holds the Z value for YZ bitmap
                        Label = "Exterior"
                    });
                }
            }

            return points;
        }

        // Apply a YZ mask to the volume
        private static int ApplyMaskToYZVolume(Bitmap mask, int height, int depth, int sliceX,
                                            byte materialId, byte[,,] volumeLabels, int threshold)
        {
            int pixelsSegmented = 0;

            // In YZ bitmap: z is width, y is height
            for (int y = 0; y < height && y < mask.Height; y++)
            {
                for (int z = 0; z < depth && z < mask.Width; z++)
                {
                    if (mask.GetPixel(z, y).R > threshold)
                    {
                        if (volumeLabels[sliceX, y, z] == 0)  // Only overwrite if empty
                        {
                            volumeLabels[sliceX, y, z] = materialId;
                            pixelsSegmented++;
                        }
                    }
                }
            }

            return pixelsSegmented;
        }

        #endregion

        #region Common Helper Methods

        // Sample annotation points from the volume for a specific material
        private static List<AnnotationPoint> SamplePointsFromVolume(
    MainForm mainForm,
    int width, int height, int sliceIndex,
    byte[,,] volumeLabels, byte materialId)
        {
            List<AnnotationPoint> points = new List<AnnotationPoint>();
            List<Point> foregroundPoints = new List<Point>();
            List<Point> backgroundPoints = new List<Point>();

            // Find the target material name from its ID
            Material targetMaterial = mainForm.Materials.FirstOrDefault(m => m.ID == materialId);
            if (targetMaterial == null)
            {
                Logger.Log($"[SamplePointsFromVolume] Error: Material with ID {materialId} not found");
                return points;
            }

            string targetMaterialName = targetMaterial.Name;
            Logger.Log($"[SamplePointsFromVolume] Sampling for material '{targetMaterialName}' (ID: {materialId})");

            // Sample points from the volume
            for (int y = 0; y < height; y += 10)  // Sample every 10 pixels
            {
                for (int x = 0; x < width; x += 10)  // Sample every 10 pixels
                {
                    if (volumeLabels[x, y, sliceIndex] == materialId)
                    {
                        foregroundPoints.Add(new Point(x, y));
                    }
                    else if (volumeLabels[x, y, sliceIndex] > 0)  // Other material
                    {
                        backgroundPoints.Add(new Point(x, y));
                    }
                }
            }

            // Randomly select a subset of points if we have too many
            Random random = new Random();
            int maxPointsPerClass = 20;  // Adjust as needed

            if (foregroundPoints.Count > 0)
            {
                foreach (var p in foregroundPoints.OrderBy(x => random.Next()).Take(Math.Min(maxPointsPerClass, foregroundPoints.Count)))
                {
                    points.Add(new AnnotationPoint
                    {
                        X = p.X,
                        Y = p.Y,
                        Z = sliceIndex,
                        Label = targetMaterialName  // Use the actual material name
                    });
                }
            }

            if (backgroundPoints.Count > 0)
            {
                // For background points, try to find actual material names
                foreach (var p in backgroundPoints.OrderBy(x => random.Next()).Take(Math.Min(maxPointsPerClass, backgroundPoints.Count)))
                {
                    // Look up the material name for this background point
                    byte backgroundId = volumeLabels[p.X, p.Y, sliceIndex];
                    Material backgroundMaterial = mainForm.Materials.FirstOrDefault(m => m.ID == backgroundId);
                    string backgroundName = backgroundMaterial?.Name ?? "Exterior";

                    points.Add(new AnnotationPoint
                    {
                        X = p.X,
                        Y = p.Y,
                        Z = sliceIndex,
                        Label = backgroundName  // Use the actual background material name
                    });
                }
            }

            return points;
        }

        // Sample points from a newly segmented mask for next iteration
        private static List<AnnotationPoint> SamplePointsFromNewSegmentation(
     MainForm mainForm,
     Bitmap mask, int sliceIndex, byte materialId, int threshold)
        {
            List<AnnotationPoint> points = new List<AnnotationPoint>();
            List<Point> foregroundPoints = new List<Point>();
            List<Point> backgroundPoints = new List<Point>();

            // Find the target material name from its ID
            Material targetMaterial = mainForm.Materials.FirstOrDefault(m => m.ID == materialId);
            if (targetMaterial == null)
            {
                Logger.Log($"[SamplePointsFromNewSegmentation] Error: Material with ID {materialId} not found");
                return points;
            }

            string targetMaterialName = targetMaterial.Name;

            // Sample from the mask
            for (int y = 0; y < mask.Height; y += 10)
            {
                for (int x = 0; x < mask.Width; x += 10)
                {
                    if (mask.GetPixel(x, y).R > threshold)
                    {
                        foregroundPoints.Add(new Point(x, y));
                    }
                    else
                    {
                        backgroundPoints.Add(new Point(x, y));
                    }
                }
            }

            // Randomly select a subset of points
            Random random = new Random();
            int maxPointsPerClass = 20;

            if (foregroundPoints.Count > 0)
            {
                foreach (var p in foregroundPoints.OrderBy(x => random.Next()).Take(Math.Min(maxPointsPerClass, foregroundPoints.Count)))
                {
                    points.Add(new AnnotationPoint
                    {
                        X = p.X,
                        Y = p.Y,
                        Z = sliceIndex,
                        Label = targetMaterialName  // Use the material name
                    });
                }
            }

            if (backgroundPoints.Count > 0)
            {
                foreach (var p in backgroundPoints.OrderBy(x => random.Next()).Take(Math.Min(maxPointsPerClass, backgroundPoints.Count)))
                {
                    points.Add(new AnnotationPoint
                    {
                        X = p.X,
                        Y = p.Y,
                        Z = sliceIndex,
                        Label = "Exterior"  // Use Exterior for negative points from mask
                    });
                }
            }

            return points;
        }

        // Apply a segmentation mask to the volume
        private static int ApplyMaskToVolume(Bitmap mask, int width, int height, int sliceIndex,
                                          byte materialId, byte[,,] volumeLabels, int threshold)
        {
            int pixelsSegmented = 0;

            for (int y = 0; y < height && y < mask.Height; y++)
            {
                for (int x = 0; x < width && x < mask.Width; x++)
                {
                    if (mask.GetPixel(x, y).R > threshold)
                    {
                        if (volumeLabels[x, y, sliceIndex] == 0)  // Only overwrite if empty
                        {
                            volumeLabels[x, y, sliceIndex] = materialId;
                            pixelsSegmented++;
                        }
                    }
                }
            }

            return pixelsSegmented;
        }

        // Generates a grayscale XY slice image from MainForm.volumeData.
        private static Bitmap GenerateXYBitmap(MainForm mainForm, int sliceIndex, int width, int height)
        {
            Bitmap bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte val = mainForm.volumeData[x, y, sliceIndex];
                    bmp.SetPixel(x, y, Color.FromArgb(val, val, val));
                }
            }
            return bmp;
        }

        // Generates a grayscale XZ projection (at fixed Y) from MainForm.volumeData.
        private static Bitmap GenerateXZBitmap(MainForm mainForm, int fixedY, int width, int depth)
        {
            Bitmap bmp = new Bitmap(width, depth, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte val = mainForm.volumeData[x, fixedY, z];
                    bmp.SetPixel(x, z, Color.FromArgb(val, val, val));
                }
            }
            return bmp;
        }

        // Generate a grayscale YZ bitmap at a fixed X index.
        private static Bitmap GenerateYZBitmap(MainForm mainForm, int fixedX, int height, int depth)
        {
            // For YZ, the resulting image has width=depth, height=height.
            Bitmap bmp = new Bitmap(depth, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            for (int y = 0; y < height; y++)
            {
                for (int z = 0; z < depth; z++)
                {
                    byte val = mainForm.volumeData[fixedX, y, z];
                    bmp.SetPixel(z, y, Color.FromArgb(val, val, val));
                }
            }
            return bmp;
        }

        // Apply fusion to the results from multiple directions
        private static byte[,,] ApplyFusion(Dictionary<string, byte[,,]> directionResults,
                                          int width, int height, int depth, string fusionAlgorithm)
        {
            Logger.Log($"[SegmentationPropagator] Applying {fusionAlgorithm} to merge results from multiple directions");

            byte[,,] fusedVolume = new byte[width, height, depth];

            // For each voxel position
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        // Collect all labels from different directions for this voxel
                        Dictionary<byte, int> labelCounts = new Dictionary<byte, int>();

                        foreach (var directionVolume in directionResults.Values)
                        {
                            byte label = directionVolume[x, y, z];
                            if (label > 0)  // Only count non-zero labels
                            {
                                if (!labelCounts.ContainsKey(label))
                                    labelCounts[label] = 0;
                                labelCounts[label]++;
                            }
                        }

                        // Apply fusion algorithm
                        if (labelCounts.Count > 0)
                        {
                            switch (fusionAlgorithm)
                            {
                                case "Majority Voting Fusion":
                                    // Pick the most frequent label
                                    fusedVolume[x, y, z] = labelCounts.OrderByDescending(kvp => kvp.Value).First().Key;
                                    break;

                                case "Weighted Averaging Fusion":
                                    // Weight by confidence (number of directions)
                                    fusedVolume[x, y, z] = labelCounts.OrderByDescending(kvp => kvp.Value).First().Key;
                                    break;

                                default:
                                    // Default to majority voting
                                    fusedVolume[x, y, z] = labelCounts.OrderByDescending(kvp => kvp.Value).First().Key;
                                    break;
                            }
                        }
                    }
                }
            }

            return fusedVolume;
        }
        private static List<AnnotationPoint> BuildMixedPrompts(
    IEnumerable<AnnotationPoint> slicePoints,
    string targetMaterialName)
        {
            Logger.Log($"Building prompts for material: {targetMaterialName}");

            // Create a new list for our processed points
            List<AnnotationPoint> finalList = new List<AnnotationPoint>();

            // Process each point in the slice
            foreach (var pt in slicePoints)
            {
                AnnotationPoint newPoint = new AnnotationPoint
                {
                    ID = pt.ID,
                    X = pt.X,
                    Y = pt.Y,
                    Z = pt.Z,
                    Type = pt.Type
                };

                // Points belonging to the target material are marked as positive prompts
                // All other points are negative prompts
                if (pt.Label.Equals(targetMaterialName, StringComparison.OrdinalIgnoreCase))
                {
                    newPoint.Label = "Foreground"; // SAM2 expects "Foreground" for positive points
                }
                else
                {
                    newPoint.Label = "Exterior"; // SAM2 expects "Exterior" for negative points
                }

                finalList.Add(newPoint);
            }

            // Log counts for debugging
            int positiveCount = finalList.Count(p => p.Label == "Foreground");
            int negativeCount = finalList.Count(p => p.Label == "Exterior");
            Logger.Log($"Generated {finalList.Count} total prompts: {positiveCount} positive, {negativeCount} negative");

            return finalList;
        }


        #endregion
    }

}
