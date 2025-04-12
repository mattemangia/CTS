using System;
using System.Collections.Generic;
using System.IO;
using SharpDX;
using CTSegmenter;

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
            bool showSlices)
        {
            Logger.Log("[VoxelMeshExporter] Starting export...");

            var vertices = new List<Vector3>();
            var indices = new List<int>();
            int vertIndex = 0;

            for (int z = 0; z < grayVol.Depth; z++)
            {
                for (int y = 0; y < grayVol.Height; y++)
                {
                    for (int x = 0; x < grayVol.Width; x++)
                    {
                        // Apply orthoslice clipping
                        if (showSlices && (x > sliceX || y > sliceY || z > sliceZ))
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
                            if (label > 0 && labelVisibility[label])
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

            SaveAsObj(outputPath, vertices, indices);
            Logger.Log("[VoxelMeshExporter] Export completed: " + outputPath);
        }

        private static void AddCube(Vector3 pos, List<Vector3> verts, List<int> inds, ref int vIdx)
        {
            float size = 1f;
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
