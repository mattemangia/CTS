// Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace CTS.D3D11
{
    // A simple struct to hold triangle data for STL export
    public struct Triangle
    {
        public Vector3 Normal;
        public Vector3 V1, V2, V3;
    }

    public class VolumeExporter
    {
        public enum ExportQuality
        {
            Low,    // 1/4 resolution
            Medium, // 1/2 resolution
            High    // Full resolution
        }

        private readonly IGrayscaleVolumeData grayscaleData;
        private readonly ILabelVolumeData labelData;
        private readonly RenderParameters renderParams;
        private readonly bool[] visibleMaterials = new bool[256];

        private readonly int volWidth;
        private readonly int volHeight;
        private readonly int volDepth;
        private readonly Vector3 volDimensions;

        public VolumeExporter(IGrayscaleVolumeData grayscaleData, ILabelVolumeData labelData, List<Material> materials, RenderParameters renderParams)
        {
            this.grayscaleData = grayscaleData;
            this.labelData = labelData;
            this.renderParams = renderParams;

            this.volWidth = grayscaleData.Width;
            this.volHeight = grayscaleData.Height;
            this.volDepth = grayscaleData.Depth;
            this.volDimensions = new Vector3(volWidth, volHeight, volDepth);

            // Pre-calculate which materials are visible to speed up checks
            for (int i = 0; i < materials.Count; i++)
            {
                // Exclude exterior (ID 0) and invisible materials
                if (i > 0 && materials[i].IsVisible)
                {
                    visibleMaterials[i] = true;
                }
            }
        }

        public async Task ExportToStlAsync(string filePath, ExportQuality quality)
        {
            int step;
            switch (quality)
            {
                case ExportQuality.Low: step = 4; break;
                case ExportQuality.Medium: step = 2; break;
                case ExportQuality.High: step = 1; break;
                default: step = 1; break;
            }

            // A thread-safe bag to collect triangles from all parallel tasks
            var triangles = new ConcurrentBag<Triangle>();

            // Process the volume in parallel slices along the Z-axis for better cache coherency
            await Task.Run(() =>
            {
                Parallel.For(0, (volDepth / step), z_chunk =>
                {
                    int z = z_chunk * step;
                    for (int y = 0; y < volHeight - step; y += step)
                    {
                        for (int x = 0; x < volWidth - step; x += step)
                        {
                            // If the current voxel is visible, check its neighbors to see if we need to draw faces
                            if (IsVoxelVisible(x, y, z))
                            {
                                GenerateVoxelFaces(x, y, z, step, triangles);
                            }
                        }
                    }
                });
            });

            WriteStlFile(filePath, triangles.ToList());
        }

        private bool IsVoxelVisible(int x, int y, int z)
        {
            // 1. Check volume bounds
            if (x < 0 || y < 0 || z < 0 || x >= volWidth || y >= volHeight || z >= volDepth)
            {
                return false;
            }

            // 2. Check clipping planes
            var worldPos = new Vector3(x, y, z);
            if (renderParams.ClippingPlanes != null)
            {
                foreach (var plane in renderParams.ClippingPlanes)
                {
                    // This logic mirrors the HLSL shader's clipping check
                    float dist = Vector3.Dot(worldPos - volDimensions * 0.5f, new Vector3(plane.X, plane.Y, plane.Z)) - plane.W;
                    if (dist > 0)
                    {
                        return false; // This voxel is clipped away
                    }
                }
            }

            // 3. Check material/label visibility
            byte label = labelData.GetVoxel(x, y, z);
            if (label > 0 && label < visibleMaterials.Length && visibleMaterials[label])
            {
                return true; // A visible material is present
            }

            // 4. If no visible material, check if grayscale data should be shown and is within threshold
            if (renderParams.ShowGrayscale > 0.5f)
            {
                byte grayValue = grayscaleData.GetVoxel(x, y, z);
                if (grayValue >= renderParams.Threshold.X && grayValue <= renderParams.Threshold.Y)
                {
                    return true;
                }
            }

            return false;
        }

        private void GenerateVoxelFaces(int x, int y, int z, int step, ConcurrentBag<Triangle> triangles)
        {
            var p0 = new Vector3(x, y, z);
            var p1 = new Vector3(x + step, y, z);
            var p2 = new Vector3(x + step, y + step, z);
            var p3 = new Vector3(x, y + step, z);
            var p4 = new Vector3(x, y, z + step);
            var p5 = new Vector3(x + step, y, z + step);
            var p6 = new Vector3(x + step, y + step, z + step);
            var p7 = new Vector3(x, y + step, z + step);

            // Check 6 neighbours and add faces if the neighbour is not visible

            // -X face (left)
            if (!IsVoxelVisible(x - step, y, z))
            {
                var n = new Vector3(-1, 0, 0);
                triangles.Add(new Triangle { Normal = n, V1 = p0, V2 = p7, V3 = p4 });
                triangles.Add(new Triangle { Normal = n, V1 = p0, V2 = p3, V3 = p7 });
            }
            // +X face (right)
            if (!IsVoxelVisible(x + step, y, z))
            {
                var n = new Vector3(1, 0, 0);
                triangles.Add(new Triangle { Normal = n, V1 = p1, V2 = p5, V3 = p6 });
                triangles.Add(new Triangle { Normal = n, V1 = p1, V2 = p6, V3 = p2 });
            }
            // -Y face (bottom)
            if (!IsVoxelVisible(x, y - step, z))
            {
                var n = new Vector3(0, -1, 0);
                triangles.Add(new Triangle { Normal = n, V1 = p0, V2 = p1, V3 = p5 });
                triangles.Add(new Triangle { Normal = n, V1 = p0, V2 = p5, V3 = p4 });
            }
            // +Y face (top)
            if (!IsVoxelVisible(x, y + step, z))
            {
                var n = new Vector3(0, 1, 0);
                triangles.Add(new Triangle { Normal = n, V1 = p3, V2 = p6, V3 = p7 });
                triangles.Add(new Triangle { Normal = n, V1 = p3, V2 = p2, V3 = p6 });
            }
            // -Z face (back)
            if (!IsVoxelVisible(x, y, z - step))
            {
                var n = new Vector3(0, 0, -1);
                triangles.Add(new Triangle { Normal = n, V1 = p0, V2 = p2, V3 = p1 });
                triangles.Add(new Triangle { Normal = n, V1 = p0, V2 = p3, V3 = p2 });
            }
            // +Z face (front)
            if (!IsVoxelVisible(x, y, z + step))
            {
                var n = new Vector3(0, 0, 1);
                triangles.Add(new Triangle { Normal = n, V1 = p4, V2 = p5, V3 = p6 });
                triangles.Add(new Triangle { Normal = n, V1 = p4, V2 = p6, V3 = p7 });
            }
        }

        private static void WriteStlFile(string filePath, List<Triangle> triangles)
        {
            using (var writer = new BinaryWriter(File.Open(filePath, FileMode.Create)))
            {
                // 80-byte header (can be empty)
                writer.Write(new byte[80]);
                // 4-byte uint for the number of triangles
                writer.Write((uint)triangles.Count);

                foreach (var tri in triangles)
                {
                    // 12 bytes for Normal vector
                    writer.Write(tri.Normal.X);
                    writer.Write(tri.Normal.Y);
                    writer.Write(tri.Normal.Z);

                    // 12 bytes for Vertex 1
                    writer.Write(tri.V1.X);
                    writer.Write(tri.V1.Y);
                    writer.Write(tri.V1.Z);

                    // 12 bytes for Vertex 2
                    writer.Write(tri.V2.X);
                    writer.Write(tri.V2.Y);
                    writer.Write(tri.V2.Z);

                    // 12 bytes for Vertex 3
                    writer.Write(tri.V3.X);
                    writer.Write(tri.V3.Y);
                    writer.Write(tri.V3.Z);

                    // 2-byte attribute byte count (usually 0)
                    writer.Write((ushort)0);
                }
            }
        }
    }
}