using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenTK;

namespace CTS.Misc
{
    public class ScanToVolume
    {
        private List<Vector3> vertices;
        private List<int> indices;
        private List<float> densityValues;
        private byte[,,] volumeData;
        private float minDensity;
        private float maxDensity;
        private double pixelSize;
        private int width, height, depth;
        private Vector3 meshMin, meshMax;

        /// <summary>
        /// Creates a new instance of the ScanToVolume class
        /// </summary>
        public ScanToVolume()
        {
        }

        /// <summary>
        /// Converts a mesh to a volumetric representation
        /// </summary>
        /// <param name="vertices">Mesh vertices</param>
        /// <param name="indices">Mesh triangle indices</param>
        /// <param name="densityValues">Density values associated with vertices</param>
        /// <param name="resolution">Resolution in voxels per unit</param>
        /// <param name="pixelSize">Physical size of each voxel in meters</param>
        /// <returns>True if successful</returns>
        public bool ConvertMeshToVolume(List<Vector3> vertices, List<int> indices, List<float> densityValues,
                                      float resolution, double pixelSize)
        {
            if (vertices == null || indices == null || vertices.Count == 0 || indices.Count == 0)
            {
                Console.WriteLine("Invalid mesh data provided");
                return false;
            }

            this.vertices = new List<Vector3>(vertices);
            this.indices = new List<int>(indices);
            this.densityValues = densityValues != null ? new List<float>(densityValues) : null;
            this.pixelSize = pixelSize;

            CalculateMeshBounds();
            CalculateVolumeDimensions(resolution);

            // Find density range if available
            if (this.densityValues != null && this.densityValues.Count > 0)
            {
                minDensity = this.densityValues.Min();
                maxDensity = this.densityValues.Max();

                // If min and max are the same, create a small range to avoid division by zero
                if (Math.Abs(maxDensity - minDensity) < 0.001f)
                {
                    maxDensity = minDensity + 1.0f;
                }
            }
            else
            {
                minDensity = 0f;
                maxDensity = 1f;
            }

            // Create the volume data array
            volumeData = new byte[width, height, depth];

            // Voxelize the mesh
            VoxelizeMesh();

            return true;
        }

        /// <summary>
        /// Exports the volume as a stack of 2D grayscale images
        /// </summary>
        /// <param name="outputFolder">Folder to save the images</param>
        /// <param name="filePrefix">Prefix for image filenames</param>
        /// <returns>True if successful</returns>
        public bool ExportVolumeToImages(string outputFolder, string filePrefix = "slice_")
        {
            if (volumeData == null)
            {
                Console.WriteLine("No volume data to export");
                return false;
            }

            // Create output directory if it doesn't exist
            if (!Directory.Exists(outputFolder))
            {
                Directory.CreateDirectory(outputFolder);
            }

            // Create and save a metadata file with pixel size information
            string metadataPath = Path.Combine(outputFolder, "metadata.txt");
            using (StreamWriter writer = new StreamWriter(metadataPath))
            {
                writer.WriteLine($"Width: {width}");
                writer.WriteLine($"Height: {height}");
                writer.WriteLine($"Depth: {depth}");
                writer.WriteLine($"PixelSize: {pixelSize}");
                writer.WriteLine($"MinDensity: {minDensity}");
                writer.WriteLine($"MaxDensity: {maxDensity}");
                writer.WriteLine($"ExportDate: {DateTime.Now}");
            }

            // Export each slice as a BMP file
            Parallel.For(0, depth, z =>
            {
                string filePath = Path.Combine(outputFolder, $"{filePrefix}{z:D5}.bmp");
                SaveSliceAsBitmap(z, filePath);
            });

            return true;
        }

        /// <summary>
        /// Gets the volumetric data
        /// </summary>
        /// <returns>3D array of byte values representing the volume</returns>
        public byte[,,] GetVolumeData()
        {
            return volumeData;
        }

        /// <summary>
        /// Gets the dimensions of the volume
        /// </summary>
        /// <param name="width">Width of the volume</param>
        /// <param name="height">Height of the volume</param>
        /// <param name="depth">Depth of the volume</param>
        public void GetVolumeDimensions(out int width, out int height, out int depth)
        {
            width = this.width;
            height = this.height;
            depth = this.depth;
        }

        /// <summary>
        /// Gets the pixel size of the volume
        /// </summary>
        /// <returns>Pixel size in meters</returns>
        public double GetPixelSize()
        {
            return pixelSize;
        }

        /// <summary>
        /// Calculates the bounds of the mesh
        /// </summary>
        private void CalculateMeshBounds()
        {
            if (vertices.Count == 0)
                return;

            meshMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            meshMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            foreach (Vector3 vertex in vertices)
            {
                meshMin.X = Math.Min(meshMin.X, vertex.X);
                meshMin.Y = Math.Min(meshMin.Y, vertex.Y);
                meshMin.Z = Math.Min(meshMin.Z, vertex.Z);

                meshMax.X = Math.Max(meshMax.X, vertex.X);
                meshMax.Y = Math.Max(meshMax.Y, vertex.Y);
                meshMax.Z = Math.Max(meshMax.Z, vertex.Z);
            }

            // Add a small padding to ensure mesh is fully inside the volume
            const float padding = 0.01f;
            meshMin -= new Vector3(padding, padding, padding);
            meshMax += new Vector3(padding, padding, padding);
        }

        /// <summary>
        /// Calculates the dimensions of the volume based on mesh bounds and resolution
        /// </summary>
        /// <param name="resolution">Resolution in voxels per unit</param>
        private void CalculateVolumeDimensions(float resolution)
        {
            Vector3 size = meshMax - meshMin;

            // Calculate dimensions based on resolution and adjusted pixel size
            width = Math.Max(1, (int)(size.X * resolution));
            height = Math.Max(1, (int)(size.Y * resolution));
            depth = Math.Max(1, (int)(size.Z * resolution));

            // Update pixel size to maintain physical size of the model
            // pixelSize is in meters per voxel
            // We keep the original pixelSize and adjust the voxel count instead
        }

        /// <summary>
        /// Voxelizes the mesh using a 3D scan-line algorithm
        /// </summary>
        private void VoxelizeMesh()
        {
            // Initialize volume data to empty (black)
            Parallel.For(0, depth, z =>
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        volumeData[x, y, z] = 0;
                    }
                }
            });

            // Process each triangle in the mesh
            for (int i = 0; i < indices.Count; i += 3)
            {
                if (i + 2 >= indices.Count)
                    break;

                int index1 = indices[i];
                int index2 = indices[i + 1];
                int index3 = indices[i + 2];

                if (index1 >= vertices.Count || index2 >= vertices.Count || index3 >= vertices.Count)
                    continue;

                Vector3 v1 = vertices[index1];
                Vector3 v2 = vertices[index2];
                Vector3 v3 = vertices[index3];

                // Voxelize this triangle
                VoxelizeTriangle(v1, v2, v3, index1, index2, index3);
            }

            // Now we have a shell of the mesh, fill the interior
            FillVolumeInterior();
        }

        /// <summary>
        /// Voxelizes a single triangle
        /// </summary>
        /// <param name="v1">First vertex</param>
        /// <param name="v2">Second vertex</param>
        /// <param name="v3">Third vertex</param>
        /// <param name="index1">Index of first vertex</param>
        /// <param name="index2">Index of second vertex</param>
        /// <param name="index3">Index of third vertex</param>
        private void VoxelizeTriangle(Vector3 v1, Vector3 v2, Vector3 v3, int index1, int index2, int index3)
        {
            // Calculate the bounding box of the triangle in voxel coordinates
            Vector3 min = new Vector3(
                Math.Min(v1.X, Math.Min(v2.X, v3.X)),
                Math.Min(v1.Y, Math.Min(v2.Y, v3.Y)),
                Math.Min(v1.Z, Math.Min(v2.Z, v3.Z))
            );

            Vector3 max = new Vector3(
                Math.Max(v1.X, Math.Max(v2.X, v3.X)),
                Math.Max(v1.Y, Math.Max(v2.Y, v3.Y)),
                Math.Max(v1.Z, Math.Max(v2.Z, v3.Z))
            );

            // Convert to voxel coordinates
            int minX = Math.Max(0, (int)((min.X - meshMin.X) / (meshMax.X - meshMin.X) * width));
            int minY = Math.Max(0, (int)((min.Y - meshMin.Y) / (meshMax.Y - meshMin.Y) * height));
            int minZ = Math.Max(0, (int)((min.Z - meshMin.Z) / (meshMax.Z - meshMin.Z) * depth));

            int maxX = Math.Min(width - 1, (int)((max.X - meshMin.X) / (meshMax.X - meshMin.X) * width));
            int maxY = Math.Min(height - 1, (int)((max.Y - meshMin.Y) / (meshMax.Y - meshMin.Y) * height));
            int maxZ = Math.Min(depth - 1, (int)((max.Z - meshMin.Z) / (meshMax.Z - meshMin.Z) * depth));

            // Calculate triangle normal
            Vector3 edge1 = v2 - v1;
            Vector3 edge2 = v3 - v1;
            Vector3 normal = Vector3.Cross(edge1, edge2);
            normal.Normalize();

            // Calculate average density if available
            byte density = 255; // Default to white
            if (densityValues != null &&
                index1 < densityValues.Count &&
                index2 < densityValues.Count &&
                index3 < densityValues.Count)
            {
                float avgDensity = (densityValues[index1] + densityValues[index2] + densityValues[index3]) / 3.0f;
                float normalizedDensity = (avgDensity - minDensity) / (maxDensity - minDensity);
                normalizedDensity = Math.Max(0.0f, Math.Min(1.0f, normalizedDensity));
                density = (byte)(normalizedDensity * 255);
            }

            // Iterate through potential voxels
            for (int z = minZ; z <= maxZ; z++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        // Convert voxel center to mesh coordinates
                        Vector3 voxelCenter = new Vector3(
                            meshMin.X + (x + 0.5f) * (meshMax.X - meshMin.X) / width,
                            meshMin.Y + (y + 0.5f) * (meshMax.Y - meshMin.Y) / height,
                            meshMin.Z + (z + 0.5f) * (meshMax.Z - meshMin.Z) / depth
                        );

                        // Check if the voxel intersects the triangle
                        if (PointInTriangle(voxelCenter, v1, v2, v3, normal))
                        {
                            volumeData[x, y, z] = density;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Checks if a point is inside a triangle
        /// </summary>
        /// <param name="p">Point to check</param>
        /// <param name="v1">First triangle vertex</param>
        /// <param name="v2">Second triangle vertex</param>
        /// <param name="v3">Third triangle vertex</param>
        /// <param name="normal">Triangle normal</param>
        /// <returns>True if the point is inside the triangle</returns>
        private bool PointInTriangle(Vector3 p, Vector3 v1, Vector3 v2, Vector3 v3, Vector3 normal)
        {
            // Project to 2D based on the largest normal component
            int i1, i2;
            if (Math.Abs(normal.X) > Math.Abs(normal.Y) && Math.Abs(normal.X) > Math.Abs(normal.Z))
            {
                // Project onto YZ plane
                i1 = 1; i2 = 2;
            }
            else if (Math.Abs(normal.Y) > Math.Abs(normal.Z))
            {
                // Project onto XZ plane
                i1 = 0; i2 = 2;
            }
            else
            {
                // Project onto XY plane
                i1 = 0; i2 = 1;
            }

            // Get projected coordinates
            Vector2 point = new Vector2(GetComponent(p, i1), GetComponent(p, i2));
            Vector2 a = new Vector2(GetComponent(v1, i1), GetComponent(v1, i2));
            Vector2 b = new Vector2(GetComponent(v2, i1), GetComponent(v2, i2));
            Vector2 c = new Vector2(GetComponent(v3, i1), GetComponent(v3, i2));

            // Barycentric coordinates test
            float area = 0.5f * ((b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y));
            float s = ((a.Y - c.Y) * (point.X - c.X) + (c.X - a.X) * (point.Y - c.Y)) / (2 * area);
            float t = ((a.Y - b.Y) * (point.X - b.X) + (b.X - a.X) * (point.Y - b.Y)) / (2 * area);

            return s >= 0 && t >= 0 && (s + t) <= 1;
        }

        /// <summary>
        /// Gets a specific component of a Vector3
        /// </summary>
        /// <param name="v">Vector</param>
        /// <param name="i">Component index (0=X, 1=Y, 2=Z)</param>
        /// <returns>The component value</returns>
        private float GetComponent(Vector3 v, int i)
        {
            switch (i)
            {
                case 0: return v.X;
                case 1: return v.Y;
                case 2: return v.Z;
                default: return 0;
            }
        }

        /// <summary>
        /// Fills the interior of the mesh using a flood-fill approach
        /// </summary>
        private void FillVolumeInterior()
        {
            // Create a temporary copy of the volume data
            byte[,,] filledVolume = new byte[width, height, depth];

            // Copy the shell
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        filledVolume[x, y, z] = volumeData[x, y, z];
                    }
                }
            }

            // For each slice, perform a 2D flood fill from the boundaries
            Parallel.For(0, depth, z =>
            {
                bool[,] visited = new bool[width, height];
                Queue<Point> queue = new Queue<Point>();

                // Start from the boundaries
                for (int x = 0; x < width; x++)
                {
                    EnqueueIfEmpty(queue, visited, x, 0, z, filledVolume);
                    EnqueueIfEmpty(queue, visited, x, height - 1, z, filledVolume);
                }

                for (int y = 1; y < height - 1; y++)
                {
                    EnqueueIfEmpty(queue, visited, 0, y, z, filledVolume);
                    EnqueueIfEmpty(queue, visited, width - 1, y, z, filledVolume);
                }

                // Perform BFS flood fill - marking outside voxels
                while (queue.Count > 0)
                {
                    Point p = queue.Dequeue();

                    // Check neighbors (4-connected)
                    EnqueueIfEmpty(queue, visited, p.X + 1, p.Y, z, filledVolume);
                    EnqueueIfEmpty(queue, visited, p.X - 1, p.Y, z, filledVolume);
                    EnqueueIfEmpty(queue, visited, p.X, p.Y + 1, z, filledVolume);
                    EnqueueIfEmpty(queue, visited, p.X, p.Y - 1, z, filledVolume);
                }

                // Mark unvisited voxels as filled with average density
                byte fillValue = CalculateAverageDensity();
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (!visited[x, y] && filledVolume[x, y, z] == 0)
                        {
                            filledVolume[x, y, z] = fillValue;
                        }
                    }
                }
            });

            // Replace the original volume with the filled one
            volumeData = filledVolume;
        }

        /// <summary>
        /// Helper method for flood fill to enqueue empty voxels
        /// </summary>
        private void EnqueueIfEmpty(Queue<Point> queue, bool[,] visited, int x, int y, int z, byte[,,] volume)
        {
            if (x >= 0 && x < width && y >= 0 && y < height && !visited[x, y] && volume[x, y, z] == 0)
            {
                visited[x, y] = true;
                queue.Enqueue(new Point(x, y));
            }
        }

        /// <summary>
        /// Calculates the average density value for filling
        /// </summary>
        /// <returns>Average density value</returns>
        private byte CalculateAverageDensity()
        {
            if (densityValues == null || densityValues.Count == 0)
                return 128; // Default gray

            float avgDensity = densityValues.Average();
            float normalizedDensity = (avgDensity - minDensity) / (maxDensity - minDensity);
            normalizedDensity = Math.Max(0.0f, Math.Min(1.0f, normalizedDensity));
            return (byte)(normalizedDensity * 255);
        }

        /// <summary>
        /// Saves a single slice of the volume as a bitmap
        /// </summary>
        /// <param name="z">Z-index of the slice</param>
        /// <param name="filePath">Output file path</param>
        private void SaveSliceAsBitmap(int z, string filePath)
        {
            // Create a new bitmap for the slice
            using (Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format8bppIndexed))
            {
                // Set up a grayscale palette
                ColorPalette palette = bitmap.Palette;
                for (int i = 0; i < 256; i++)
                {
                    palette.Entries[i] = Color.FromArgb(i, i, i);
                }
                bitmap.Palette = palette;

                // Lock the bitmap data for direct manipulation
                BitmapData bitmapData = bitmap.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format8bppIndexed);

                // Copy data from volume to bitmap
                unsafe
                {
                    byte* ptr = (byte*)bitmapData.Scan0;
                    int stride = bitmapData.Stride;

                    for (int y = 0; y < height; y++)
                    {
                        byte* row = ptr + (y * stride);

                        for (int x = 0; x < width; x++)
                        {
                            row[x] = volumeData[x, y, z];
                        }
                    }
                }

                // Unlock the bitmap
                bitmap.UnlockBits(bitmapData);

                // Save the bitmap
                bitmap.Save(filePath, ImageFormat.Bmp);
            }
        }
    }
}