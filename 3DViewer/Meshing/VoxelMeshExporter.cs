using System;
using System.Collections.Generic;
using System.IO;
using SharpDX;

namespace CTSegmenter.SharpDXIntegration
{
    public static class VoxelMeshExporter
    {
        public static void ExportVisibleVoxels(
            string outputPath,
            ChunkedVolume grayVol,
            ChunkedLabelVolume labelVol,
            int minThreshold,
            int maxThreshold,
            bool showGrayscale,
            bool[] labelVisibility,
            int sliceX, int sliceY, int sliceZ,
            bool showSlices,
            // Add cutting plane parameters
            bool cutXEnabled = false,
            bool cutYEnabled = false,
            bool cutZEnabled = false,
            float cutXDirection = 1.0f,
            float cutYDirection = 1.0f,
            float cutZDirection = 1.0f,
            float cutXPosition = 0.5f,
            float cutYPosition = 0.5f,
            float cutZPosition = 0.5f,
            bool applyPlaneCut = true,
            Action<int> progressCallback = null)
        {
            Logger.Log("[VoxelMeshExporter] Starting export...");

            var vertices = new List<Vector3>();
            var indices = new List<int>();
            int vertIndex = 0;

            // Calculate cut positions in voxel coordinates
            int cutXPos = (int)(cutXPosition * grayVol.Width);
            int cutYPos = (int)(cutYPosition * grayVol.Height);
            int cutZPos = (int)(cutZPosition * grayVol.Depth);

            // Total number of voxels to process for progress reporting
            long totalVoxels = (long)grayVol.Width * grayVol.Height * grayVol.Depth;
            long processedVoxels = 0;
            int lastReportedProgress = 0;

            for (int z = 0; z < grayVol.Depth; z++)
            {
                for (int y = 0; y < grayVol.Height; y++)
                {
                    for (int x = 0; x < grayVol.Width; x++)
                    {
                        // Update progress every 5% (more efficient than updating every voxel)
                        processedVoxels++;
                        int currentProgress = (int)((processedVoxels * 100) / totalVoxels);
                        if (currentProgress >= lastReportedProgress + 5)
                        {
                            progressCallback?.Invoke(currentProgress);
                            lastReportedProgress = currentProgress;
                        }

                        // Apply orthoslice clipping
                        if (showSlices && (x > sliceX || y > sliceY || z > sliceZ))
                            continue;

                        // Apply cutting plane check if enabled and user wants to apply cuts
                        if (applyPlaneCut && IsCutByPlane(x, y, z,
                                                         cutXEnabled, cutYEnabled, cutZEnabled,
                                                         cutXDirection, cutYDirection, cutZDirection,
                                                         cutXPos, cutYPos, cutZPos))
                            continue;

                        bool include = false;

                        byte gVal = grayVol[x, y, z];
                        if (showGrayscale && gVal >= minThreshold && gVal <= maxThreshold)
                        {
                            include = true;
                        }

                        if (labelVol != null)
                        {
                            byte label = labelVol[x, y, z];
                            if (label > 0 && label < labelVisibility.Length && labelVisibility[label])
                            {
                                include = true;
                            }
                        }

                        if (!include) continue;

                        // Add voxel as cube
                        AddCube(new Vector3(x, y, z), vertices, indices, ref vertIndex);
                    }
                }
            }

            // Ensure 100% progress is reported at the end
            progressCallback?.Invoke(100);

            SaveAsObj(outputPath, vertices, indices);
            Logger.Log("[VoxelMeshExporter] Export completed: " + outputPath);
        }

        private static bool IsCutByPlane(int x, int y, int z,
                                         bool cutXEnabled, bool cutYEnabled, bool cutZEnabled,
                                         float cutXDirection, float cutYDirection, float cutZDirection,
                                         int cutXPosition, int cutYPosition, int cutZPosition)
        {
            // Check X cutting plane
            if (cutXEnabled)
            {
                if (cutXDirection > 0) // Forward cut
                {
                    if (x > cutXPosition) return true;
                }
                else // Backward cut
                {
                    if (x < cutXPosition) return true;
                }
            }

            // Check Y cutting plane
            if (cutYEnabled)
            {
                if (cutYDirection > 0) // Forward cut
                {
                    if (y > cutYPosition) return true;
                }
                else // Backward cut
                {
                    if (y < cutYPosition) return true;
                }
            }

            // Check Z cutting plane
            if (cutZEnabled)
            {
                if (cutZDirection > 0) // Forward cut
                {
                    if (z > cutZPosition) return true;
                }
                else // Backward cut
                {
                    if (z < cutZPosition) return true;
                }
            }

            return false; // Not cut by any plane
        }

        private static void AddCube(Vector3 pos, List<Vector3> verts, List<int> inds, ref int vIdx)
        {
            var p = pos;
            var cubeVerts = new Vector3[]
            {
                p + new Vector3(0, 0, 0),
                p + new Vector3(1, 0, 0),
                p + new Vector3(1, 1, 0),
                p + new Vector3(0, 1, 0),
                p + new Vector3(0, 0, 1),
                p + new Vector3(1, 0, 1),
                p + new Vector3(1, 1, 1),
                p + new Vector3(0, 1, 1)
            };
            verts.AddRange(cubeVerts);

            var cubeInds = new int[]
            {
                0,1,2, 0,2,3,
                4,6,5, 4,7,6,
                0,4,5, 0,5,1,
                1,5,6, 1,6,2,
                2,6,7, 2,7,3,
                3,7,4, 3,4,0
            };

            foreach (var idx in cubeInds)
                inds.Add(vIdx + idx);

            vIdx += 8;
        }

        private static void SaveAsObj(string path, List<Vector3> verts, List<int> inds)
        {
            using (var sw = new StreamWriter(path))
            {
                for (int i = 0; i < verts.Count; i++)
                {
                    var v = verts[i];
                    sw.WriteLine($"v {v.X} {v.Y} {v.Z}");
                }
                for (int i = 0; i < inds.Count; i += 3)
                {
                    int i1 = inds[i] + 1;
                    int i2 = inds[i + 1] + 1;
                    int i3 = inds[i + 2] + 1;
                    sw.WriteLine($"f {i1} {i2} {i3}");
                }
            }
        }
    }
}