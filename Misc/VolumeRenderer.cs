using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading;
using System.Threading.Tasks;
using Krypton.Toolkit;
using System.Windows.Forms;
using System.Linq;
using System.Numerics;

namespace CTSegmenter
{
    /// <summary>
    /// Handles efficient 3D rendering of volumetric data as a wireframe
    /// </summary>
    public class VolumeRenderer
    {
        private float[,,] densityVolume;
        private int width, height, depth;
        private double pixelSize;
        private byte materialID;
        // For color mapping
        private float minDensity, maxDensity;
        private bool renderFullVolume;
        // Transformation parameters
        private float rotationX, rotationY;
        private float zoom;
        private PointF pan;

        // For efficient rendering
        private bool needsUpdate = true;
        private Bitmap cachedRender;
        private readonly object renderLock = new object();

        // For wireframe rendering
        private int skipFactor = 2;
        private int lineThickness = 1;

        // For performance optimization
        private List<(int, int, float)> sortedEdges;
        private bool edgesSorted = false;
        private float minZoom = 0.5f;

        // Wireframe data structure
        private List<Point3D> vertices;
        private List<(int, int)> edges;


        // Simple 3D point class
        private class Point3D
        {
            public float X, Y, Z;
            public float Density;

            public Point3D(float x, float y, float z, float density)
            {
                X = x;
                Y = y;
                Z = z;
                Density = density;
            }
        }
        public VolumeRenderer(float[,,] densityVolume, int width, int height, int depth, double pixelSize)
    : this(densityVolume, width, height, depth, pixelSize, 0, true) // Default to ID 0 and full volume
        {
        }
        public VolumeRenderer(float[,,] densityVolume, int width, int height, int depth, double pixelSize, byte materialID, bool renderFullVolume = true)
        {
            this.densityVolume = densityVolume;
            this.width = width;
            this.height = height;
            this.depth = depth;
            this.pixelSize = pixelSize;
            this.materialID = materialID;
            this.lineThickness = 1;
            this.renderFullVolume = renderFullVolume;

            // Debug - check material ID validity
            if (materialID == 0)
            {
                Logger.Log("[VolumeRenderer] WARNING: Initializing with materialID 0 (Exterior), this may be incorrect!");
            }

            // Calculate min/max density for color mapping
            CalculateDensityRange();

            // Generate an optimized wireframe representation of the volume
            GenerateVolumeRepresentation();

            // Set initial transformation - CHANGED: Lowered initial zoom to 1.0f for better initial view
            SetTransformation(30, 30, 1.0f, new PointF(0, 0));

            Logger.Log($"[VolumeRenderer] Initialized with wireframe for material ID {materialID}, rendering mode: {(renderFullVolume ? "Full Volume" : "Boundary Only")}");
        }

        /// <summary>
        /// Updates transformation parameters and marks that rendering needs to be updated
        /// </summary>
        public void SetTransformation(float rotationX, float rotationY, float zoom, PointF pan)
        {
            this.rotationX = rotationX;
            this.rotationY = rotationY;
            this.zoom = Math.Max(minZoom, zoom);
            this.pan = pan;

            lock (renderLock)
            {
                needsUpdate = true;
                edgesSorted = false;
            }
        }
        public PointF ProjectToScreen(float x, float y, float z, int screenWidth, int screenHeight)
        {
            // Normalize coordinates to [0,1]
            float nx = x / (float)width;
            float ny = y / (float)height;
            float nz = z / (float)depth;

            // Center and scale to [-0.5, 0.5]
            nx = nx - 0.5f;
            ny = ny - 0.5f;
            nz = nz - 0.5f;

            // Conversion to radians
            float rotXRad = rotationX * (float)Math.PI / 180;
            float rotYRad = rotationY * (float)Math.PI / 180;

            // Apply X rotation (around X axis)
            float ny1 = ny * (float)Math.Cos(rotXRad) - nz * (float)Math.Sin(rotXRad);
            float nz1 = ny * (float)Math.Sin(rotXRad) + nz * (float)Math.Cos(rotXRad);

            // Apply Y rotation (around Y axis)
            float nx1 = nx * (float)Math.Cos(rotYRad) + nz1 * (float)Math.Sin(rotYRad);
            float nz2 = -nx * (float)Math.Sin(rotYRad) + nz1 * (float)Math.Cos(rotYRad);

            // Scale by maximum dimension for aspect ratio preservation
            float scale = Math.Max(Math.Max(width, height), depth) * zoom;

            // Project to screen space, apply pan, and center
            float screenX = nx1 * scale + pan.X + screenWidth * 0.5f;
            float screenY = ny1 * scale + pan.Y + screenHeight * 0.5f;

            return new PointF(screenX, screenY);
        }

        /// <summary>
        /// Calculates the min and max density values in the volume for color mapping
        /// </summary>
        private void CalculateDensityRange()
        {
            minDensity = float.MaxValue;
            maxDensity = float.MinValue;

            // Find min/max density values in parallel for performance
            object lockObj = new object();

            Parallel.For(0, depth, z =>
            {
                float localMin = float.MaxValue;
                float localMax = float.MinValue;

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        float density = densityVolume[x, y, z];
                        if (density > 0)
                        {
                            localMin = Math.Min(localMin, density);
                            localMax = Math.Max(localMax, density);
                        }
                    }
                }

                // Update global min/max with local results
                if (localMin < float.MaxValue || localMax > float.MinValue)
                {
                    lock (lockObj)
                    {
                        minDensity = Math.Min(minDensity, localMin);
                        maxDensity = Math.Max(maxDensity, localMax);
                    }
                }
            });

            // Handle case where no valid density was found
            if (minDensity == float.MaxValue || minDensity >= maxDensity)
            {
                minDensity = 0;
                maxDensity = 1000;
            }

            Logger.Log($"[VolumeRenderer] Density range: {minDensity} to {maxDensity} kg/m³");
        }

        /// <summary>
        /// Adjusts the skip factor based on volume size for better performance
        /// </summary>
        private void AdjustSkipFactor()
        {
            int maxDimension = Math.Max(Math.Max(width, height), depth);

            // Use larger skip factors for better performance
            if (maxDimension > 512)
                skipFactor = 8;
            else if (maxDimension > 256)
                skipFactor = 6;
            else if (maxDimension > 128)
                skipFactor = 4;
            else
                skipFactor = 2;

            Logger.Log($"[VolumeRenderer] Using skip factor {skipFactor} for optimized wireframe");
        }

        /// <summary>
        /// Generates an optimized wireframe representation of the volume with support for both full volume and boundary-only rendering
        /// </summary>
        private void GenerateVolumeRepresentation()
        {
            // Adjust skip factor based on volume size
            AdjustSkipFactor();

            vertices = new List<Point3D>();
            edges = new List<(int, int)>();

            // Dictionary to track vertex indices
            Dictionary<(int, int, int), int> vertexIndices = new Dictionary<(int, int, int), int>();

            // Calculate total voxel count for adaptive sampling
            int totalVoxels = width * height * depth;
            bool isLargeVolume = totalVoxels > 16_000_000; // Special handling for volumes > 16M voxels

            Logger.Log($"[VolumeRenderer] Processing volume with {totalVoxels} voxels");

            // For extremely large volumes, implement aggressive subsampling
            int subSampleRate = isLargeVolume ?
                Math.Max(4, (int)Math.Log(totalVoxels / 1_000_000, 2)) : 1;

            // Use parallel processing with partitioning for material identification
            bool[,,] isMaterialVoxel = new bool[width, height, depth];
            int materialVoxelCount = 0;

            Logger.Log("[VolumeRenderer] Identifying material voxels with subsample rate: " + subSampleRate);

            // Use spatial partitioning for large volumes
            int blockSize = Math.Max(16, width / 16); // Use larger blocks for better cache efficiency
            int blocksX = (width + blockSize - 1) / blockSize;
            int blocksY = (height + blockSize - 1) / blockSize;
            int blocksZ = (depth + blockSize - 1) / blockSize;

            // Process in blocks for better cache locality
            Parallel.For(0, blocksZ, blockZ =>
            {
                int zStart = blockZ * blockSize;
                int zEnd = Math.Min(zStart + blockSize, depth);

                for (int blockY = 0; blockY < blocksY; blockY++)
                {
                    int yStart = blockY * blockSize;
                    int yEnd = Math.Min(yStart + blockSize, height);

                    for (int blockX = 0; blockX < blocksX; blockX++)
                    {
                        int xStart = blockX * blockSize;
                        int xEnd = Math.Min(xStart + blockSize, width);

                        // Process voxels within this block
                        int localCount = 0;
                        for (int z = zStart; z < zEnd; z += subSampleRate)
                        {
                            for (int y = yStart; y < yEnd; y += subSampleRate)
                            {
                                for (int x = xStart; x < xEnd; x += subSampleRate)
                                {
                                    if (densityVolume[x, y, z] > 0)
                                    {
                                        isMaterialVoxel[x, y, z] = true;
                                        localCount++;
                                    }
                                }
                            }
                        }

                        if (localCount > 0)
                        {
                            Interlocked.Add(ref materialVoxelCount, localCount);
                        }
                    }
                }
            });

            Logger.Log($"[VolumeRenderer] Found {materialVoxelCount} material voxels");

            if (materialVoxelCount == 0)
            {
                Logger.Log("[VolumeRenderer] Warning: No material voxels found");
                vertices.Add(new Point3D(width / 2, height / 2, depth / 2, 0));
                return;
            }

            // For boundary calculation, only process if needed and use a more efficient approach
            bool[,,] isBoundary = null;
            int boundaryVoxelCount = 0;

            if (!renderFullVolume)
            {
                // Only allocate the array if we need it
                isBoundary = new bool[width, height, depth];

                // Compute boundaries with optimized kernel approach
                Parallel.For(0, blocksZ, blockZ =>
                {
                    int zStart = blockZ * blockSize;
                    int zEnd = Math.Min(zStart + blockSize, depth);

                    for (int blockY = 0; blockY < blocksY; blockY++)
                    {
                        int yStart = blockY * blockSize;
                        int yEnd = Math.Min(yStart + blockSize, height);

                        for (int blockX = 0; blockX < blocksX; blockX++)
                        {
                            int xStart = blockX * blockSize;
                            int xEnd = Math.Min(xStart + blockSize, width);

                            int localBoundaryCount = 0;

                            // Process voxels within this block - use skip factor for large volumes
                            for (int z = zStart; z < zEnd; z += subSampleRate)
                            {
                                for (int y = yStart; y < yEnd; y += subSampleRate)
                                {
                                    for (int x = xStart; x < xEnd; x += subSampleRate)
                                    {
                                        // Only check material voxels
                                        if (!isMaterialVoxel[x, y, z])
                                            continue;

                                        // Check if this material voxel has any non-material neighbors
                                        // Use inline check instead of separate function calls for speed
                                        bool hasBoundary = false;

                                        // For extra large volumes, simplify boundary check
                                        if (isLargeVolume)
                                        {
                                            // Simplified boundary detection for large volumes
                                            // Check only 2 directions instead of 6 for extreme performance
                                            hasBoundary =
                                                (x > 0 && !isMaterialVoxel[x - 1, y, z]) ||
                                                (z > 0 && !isMaterialVoxel[x, y, z - 1]);
                                        }
                                        else
                                        {
                                            // Regular 6-connected boundary check for normal volumes
                                            if (x > 0 && !isMaterialVoxel[x - 1, y, z]) hasBoundary = true;
                                            else if (x < width - 1 && !isMaterialVoxel[x + 1, y, z]) hasBoundary = true;
                                            else if (y > 0 && !isMaterialVoxel[x, y - 1, z]) hasBoundary = true;
                                            else if (y < height - 1 && !isMaterialVoxel[x, y + 1, z]) hasBoundary = true;
                                            else if (z > 0 && !isMaterialVoxel[x, y, z - 1]) hasBoundary = true;
                                            else if (z < depth - 1 && !isMaterialVoxel[x, y, z + 1]) hasBoundary = true;
                                        }

                                        if (hasBoundary)
                                        {
                                            isBoundary[x, y, z] = true;
                                            localBoundaryCount++;
                                        }
                                    }
                                }
                            }

                            if (localBoundaryCount > 0)
                            {
                                Interlocked.Add(ref boundaryVoxelCount, localBoundaryCount);
                            }
                        }
                    }
                });

                Logger.Log($"[VolumeRenderer] Found {boundaryVoxelCount} boundary voxels");
            }

            bool showInterior = renderFullVolume || boundaryVoxelCount < 100;

            // Estimate target vertex count based on volume size and complexity
            int targetVertexCount = isLargeVolume ?
                Math.Min(5000, materialVoxelCount / 100) :
                Math.Min(10000, materialVoxelCount / 20);

            Logger.Log($"[VolumeRenderer] Target vertex count: {targetVertexCount}");

            // Adjust sampling step to meet target vertex count
            int samplingStep = Math.Max(skipFactor, (int)Math.Sqrt(materialVoxelCount / targetVertexCount));

            // For very large volumes, use progressive mesh generation to avoid memory issues
            int vertexBatchSize = isLargeVolume ? 1000 : 10000;
            int currentBatch = 0;

            Logger.Log($"[VolumeRenderer] Using sampling step: {samplingStep}");

            // Generate vertices with adaptive sampling
            for (int z = 0; z < depth; z += samplingStep)
            {
                for (int y = 0; y < height; y += samplingStep)
                {
                    for (int x = 0; x < width; x += samplingStep)
                    {
                        bool isVertex = false;
                        float density = 0;

                        // Check if any voxel in this sample cell is material
                        for (int dz = 0; dz < samplingStep && z + dz < depth; dz += subSampleRate)
                        {
                            for (int dy = 0; dy < samplingStep && y + dy < height; dy += subSampleRate)
                            {
                                for (int dx = 0; dx < samplingStep && x + dx < width; dx += subSampleRate)
                                {
                                    int nx = x + dx;
                                    int ny = y + dy;
                                    int nz = z + dz;

                                    if (nx < width && ny < height && nz < depth)
                                    {
                                        if (renderFullVolume)
                                        {
                                            // For full volume, include all material voxels
                                            if (isMaterialVoxel[nx, ny, nz])
                                            {
                                                isVertex = true;
                                                density = Math.Max(density, densityVolume[nx, ny, nz]);
                                            }
                                        }
                                        else
                                        {
                                            // For boundary-only, check if it's a boundary or small object
                                            if (isBoundary[nx, ny, nz] || (showInterior && isMaterialVoxel[nx, ny, nz]))
                                            {
                                                isVertex = true;
                                                density = Math.Max(density, densityVolume[nx, ny, nz]);
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        if (isVertex && density > 0)
                        {
                            // Add vertex
                            int vertexIndex = vertices.Count;
                            vertices.Add(new Point3D(x, y, z, density));
                            vertexIndices[(x, y, z)] = vertexIndex;

                            // Check if we need to process edges for this batch
                            currentBatch++;
                            if (isLargeVolume && currentBatch >= vertexBatchSize)
                            {
                                ProcessEdgesIncrementally(vertexIndices, samplingStep, isLargeVolume);
                                currentBatch = 0;
                            }
                        }
                    }
                }
            }

            // Process remaining edges if not processed in batches
            if (!isLargeVolume || currentBatch > 0)
            {
                ProcessEdgesIncrementally(vertexIndices, samplingStep, isLargeVolume);
            }

            Logger.Log($"[VolumeRenderer] Created wireframe with {vertices.Count} vertices and {edges.Count} edges");

            // Apply LOD based on volume size
            if (isLargeVolume && vertices.Count > targetVertexCount * 1.5)
            {
                ApplyLOD(targetVertexCount);
            }
        }

        // Helper method to process edges incrementally to avoid memory spikes
        private void ProcessEdgesIncrementally(Dictionary<(int, int, int), int> vertexIndices, int samplingStep, bool isLargeVolume)
        {
            // Simplified, directed edge creation for large volumes
            HashSet<(int, int)> processedEdges = new HashSet<(int, int)>();

            foreach (var vertexEntry in vertexIndices)
            {
                var pos = vertexEntry.Key;
                int idx = vertexEntry.Value;
                int x = pos.Item1;
                int y = pos.Item2;
                int z = pos.Item3;

                // Connect only in positive directions to avoid duplicate edges
                if (vertexIndices.TryGetValue((x + samplingStep, y, z), out int xNeighbor))
                {
                    // Ensure we haven't processed this edge pair already
                    if (!processedEdges.Contains((xNeighbor, idx)))
                    {
                        edges.Add((idx, xNeighbor));
                        processedEdges.Add((idx, xNeighbor));
                    }
                }

                if (vertexIndices.TryGetValue((x, y + samplingStep, z), out int yNeighbor))
                {
                    if (!processedEdges.Contains((yNeighbor, idx)))
                    {
                        edges.Add((idx, yNeighbor));
                        processedEdges.Add((idx, yNeighbor));
                    }
                }

                if (vertexIndices.TryGetValue((x, y, z + samplingStep), out int zNeighbor))
                {
                    if (!processedEdges.Contains((zNeighbor, idx)))
                    {
                        edges.Add((idx, zNeighbor));
                        processedEdges.Add((idx, zNeighbor));
                    }
                }

                // For large volumes, skip diagonals to reduce edge count
                if (isLargeVolume)
                    continue;

                // Add diagonals for better structure visualization in smaller volumes
                if (vertexIndices.TryGetValue((x + samplingStep, y + samplingStep, z), out int xyNeighbor))
                {
                    if (!processedEdges.Contains((xyNeighbor, idx)))
                    {
                        edges.Add((idx, xyNeighbor));
                        processedEdges.Add((idx, xyNeighbor));
                    }
                }

                if (vertices.Count < 2000) // Add more connections for small volumes
                {
                    if (vertexIndices.TryGetValue((x + samplingStep, y, z + samplingStep), out int xzNeighbor))
                    {
                        edges.Add((idx, xzNeighbor));
                    }

                    if (vertexIndices.TryGetValue((x, y + samplingStep, z + samplingStep), out int yzNeighbor))
                    {
                        edges.Add((idx, yzNeighbor));
                    }
                }
            }
        }

        // Apply Level of Detail simplification
        private void ApplyLOD(int targetVertexCount)
        {
            if (vertices.Count <= targetVertexCount)
                return;

            Logger.Log($"[VolumeRenderer] Applying LOD to reduce from {vertices.Count} to ~{targetVertexCount} vertices");

            // Use importance sampling based on density and position
            var verticesWithImportance = new List<(int index, float importance)>(vertices.Count);

            // Calculate center of the volume
            float centerX = width / 2.0f;
            float centerY = height / 2.0f;
            float centerZ = depth / 2.0f;

            for (int i = 0; i < vertices.Count; i++)
            {
                Point3D v = vertices[i];

                // Calculate distance from center (normalized)
                float dx = (v.X - centerX) / width;
                float dy = (v.Y - centerY) / height;
                float dz = (v.Z - centerZ) / depth;
                float distFromCenter = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);

                // Calculate importance based on density and distance from center
                // This prioritizes keeping high-density vertices and boundary vertices
                float densityFactor = (v.Density - minDensity) / (maxDensity - minDensity);
                float importance = densityFactor * 0.7f + (1.0f - distFromCenter) * 0.3f;

                verticesWithImportance.Add((i, importance));
            }

            // Sort by importance (descending)
            verticesWithImportance.Sort((a, b) => b.importance.CompareTo(a.importance));

            // Keep only the most important vertices
            var keptVertices = verticesWithImportance.Take(targetVertexCount).Select(v => v.index).ToHashSet();

            // Create new vertex and edge lists
            var newVertices = new List<Point3D>(targetVertexCount);
            var newIndexMap = new Dictionary<int, int>(targetVertexCount);

            // Build the new vertex list and index mapping
            foreach (var vIdx in keptVertices)
            {
                newIndexMap[vIdx] = newVertices.Count;
                newVertices.Add(vertices[vIdx]);
            }

            // Filter edges to only include vertices we're keeping
            var newEdges = new List<(int, int)>(edges.Count / 2);
            foreach (var edge in edges)
            {
                if (keptVertices.Contains(edge.Item1) && keptVertices.Contains(edge.Item2))
                {
                    newEdges.Add((newIndexMap[edge.Item1], newIndexMap[edge.Item2]));
                }
            }

            // Replace the original data
            vertices = newVertices;
            edges = newEdges;

            Logger.Log($"[VolumeRenderer] LOD applied, reduced to {vertices.Count} vertices and {edges.Count} edges");
        }
        /// <summary>
        /// Renders the volume to the graphics context
        /// </summary>
        public void Render(Graphics g, int viewWidth, int viewHeight)
        {
            // Check if we need to update the cached render
            bool needRendering = needsUpdate || cachedRender == null ||
                                 cachedRender.Width != viewWidth ||
                                 cachedRender.Height != viewHeight;

            if (needRendering)
            {
                lock (renderLock)
                {
                    if (needRendering) // Double check inside lock
                    {
                        // Performance monitoring
                        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                        // Create or recreate cached bitmap if needed
                        if (cachedRender == null || cachedRender.Width != viewWidth || cachedRender.Height != viewHeight)
                        {
                            cachedRender?.Dispose();
                            cachedRender = new Bitmap(viewWidth, viewHeight);
                        }

                        // Render to the bitmap
                        RenderToImage(cachedRender, viewWidth, viewHeight);
                        needsUpdate = false;

                        stopwatch.Stop();
                        if (stopwatch.ElapsedMilliseconds > 100)
                        {
                            Logger.Log($"[VolumeRenderer] Rendering took {stopwatch.ElapsedMilliseconds}ms");
                        }
                    }
                }
            }

            // Draw cached image
            g.DrawImage(cachedRender, 0, 0);

            // Draw legend
            DrawColorLegend(g, viewWidth, viewHeight);
        }
        /// <summary>
        /// Renders the wireframe to a bitmap using LockBits for performance
        /// </summary>
        private void RenderToImage(Bitmap bitmap, int viewWidth, int viewHeight)
        {
            // Lock the bitmap for direct pixel manipulation
            BitmapData bitmapData = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);

            try
            {
                unsafe
                {
                    byte* ptr = (byte*)bitmapData.Scan0;
                    int stride = bitmapData.Stride;
                    int bytesPerPixel = 4; // 32bpp = 4 bytes per pixel

                    // Fill with black using standard memory operations
                    int byteCount = stride * bitmap.Height;
                    byte[] blackBytes = new byte[byteCount];
                    System.Runtime.InteropServices.Marshal.Copy(blackBytes, 0, (IntPtr)ptr, byteCount);

                    // Pre-processing phase: Prepare for rendering
                    PreProcessRenderData(viewWidth, viewHeight);

                    // Early exit if no vertices to render
                    if (vertices.Count == 0)
                        return;

                    // Fast rendering with view frustum culling and level-of-detail
                    RenderWireframeOptimized(ptr, stride, bytesPerPixel, viewWidth, viewHeight);
                }
            }
            finally
            {
                // Unlock the bitmap
                bitmap.UnlockBits(bitmapData);
            }
        }
        private void PreProcessRenderData(int viewWidth, int viewHeight)
        {
            // Only perform Z-sorting if needed
            if (!edgesSorted || sortedEdges == null)
            {
                // Center of volume
                float volumeCenterX = width / 2.0f;
                float volumeCenterY = height / 2.0f;
                float volumeCenterZ = depth / 2.0f;

                // Calculate transformation matrices
                float angleY = rotationY * (float)Math.PI / 180.0f;
                float cosY = (float)Math.Cos(angleY);
                float sinY = (float)Math.Sin(angleY);

                float angleX = rotationX * (float)Math.PI / 180.0f;
                float cosX = (float)Math.Cos(angleX);
                float sinX = (float)Math.Sin(angleX);

                // Reset and initialize sorted edges list
                sortedEdges = new List<(int, int, float)>(edges.Count);

                // For large volumes, limit the number of edges to process
                int edgeStep = edges.Count > 50000 ? edges.Count / 20000 : 1;

                for (int i = 0; i < edges.Count; i += edgeStep)
                {
                    var edge = edges[i];
                    Point3D v1 = vertices[edge.Item1];
                    Point3D v2 = vertices[edge.Item2];

                    // Skip edges with zero density vertices 
                    if ((v1.Density <= 0 && v2.Density <= 0))
                        continue;

                    // Translate points relative to volume center
                    float v1x = v1.X - volumeCenterX;
                    float v1y = v1.Y - volumeCenterY;
                    float v1z = v1.Z - volumeCenterZ;

                    float v2x = v2.X - volumeCenterX;
                    float v2y = v2.Y - volumeCenterY;
                    float v2z = v2.Z - volumeCenterZ;

                    // Apply Y-axis rotation
                    float v1xr = v1x * cosY + v1z * sinY;
                    float v1zr = -v1x * sinY + v1z * cosY;

                    float v2xr = v2x * cosY + v2z * sinY;
                    float v2zr = -v2x * sinY + v2z * cosY;

                    // Apply X-axis rotation
                    float v1yr = v1y * cosX - v1zr * sinX;
                    v1zr = v1y * sinX + v1zr * cosX;

                    float v2yr = v2y * cosX - v2zr * sinX;
                    v2zr = v2y * sinX + v2zr * cosX;

                    // Calculate average Z for depth sorting
                    float avgZ = (v1zr + v2zr) / 2.0f;

                    // Add to sorted edges
                    sortedEdges.Add((edge.Item1, edge.Item2, avgZ));
                }

                // Sort by Z-depth (back-to-front rendering)
                sortedEdges.Sort((a, b) => a.Item3.CompareTo(b.Item3));
                edgesSorted = true;
            }
        }
        private void QuickSortEdges(List<(int, int, float)> edgeList, int left, int right)
        {
            if (left < right)
            {
                int pivotIndex = Partition(edgeList, left, right);

                // Only recurse if the partitions are large enough
                if (pivotIndex - left > 1000)
                    QuickSortEdges(edgeList, left, pivotIndex - 1);
                else if (pivotIndex - left > 1)
                    edgeList.Sort(left, pivotIndex - left, Comparer<(int, int, float)>.Create((a, b) => a.Item3.CompareTo(b.Item3)));

                if (right - pivotIndex > 1000)
                    QuickSortEdges(edgeList, pivotIndex + 1, right);
                else if (right - pivotIndex > 1)
                    edgeList.Sort(pivotIndex + 1, right - pivotIndex, Comparer<(int, int, float)>.Create((a, b) => a.Item3.CompareTo(b.Item3)));
            }
        }

        private int Partition(List<(int, int, float)> edgeList, int left, int right)
        {
            // Use median-of-three for pivot selection
            int mid = left + (right - left) / 2;
            if (edgeList[right].Item3 < edgeList[left].Item3)
                Swap(edgeList, left, right);
            if (edgeList[mid].Item3 < edgeList[left].Item3)
                Swap(edgeList, mid, left);
            if (edgeList[right].Item3 < edgeList[mid].Item3)
                Swap(edgeList, mid, right);

            var pivot = edgeList[mid].Item3;
            Swap(edgeList, mid, right - 1);

            int i = left;
            int j = right - 1;

            while (true)
            {
                while (edgeList[++i].Item3 < pivot) { }
                while (edgeList[--j].Item3 > pivot) { }

                if (i >= j)
                    break;

                Swap(edgeList, i, j);
            }

            Swap(edgeList, i, right - 1);
            return i;
        }

        private void Swap(List<(int, int, float)> list, int i, int j)
        {
            var temp = list[i];
            list[i] = list[j];
            list[j] = temp;
        }

        // Optimized wireframe rendering with adaptive level-of-detail
        private unsafe void RenderWireframeOptimized(byte* basePtr, int stride, int bytesPerPixel, int viewWidth, int viewHeight)
        {
            // Center of view
            float centerX = viewWidth / 2.0f;
            float centerY = viewHeight / 2.0f;

            // Center of volume
            float volumeCenterX = width / 2.0f;
            float volumeCenterY = height / 2.0f;
            float volumeCenterZ = depth / 2.0f;

            // View configuration
            float viewDistance = Math.Max(Math.Max(width, height), depth) * 2.0f;
            float focalLength = viewDistance * 0.75f;

            // Pre-calculate rotation matrices
            float angleY = rotationY * (float)Math.PI / 180.0f;
            float cosY = (float)Math.Cos(angleY);
            float sinY = (float)Math.Sin(angleY);

            float angleX = rotationX * (float)Math.PI / 180.0f;
            float cosX = (float)Math.Cos(angleX);
            float sinX = (float)Math.Sin(angleX);

            // Determine LOD level based on zoom and data size
            int edgeSkip = zoom < 0.8f ? 3 : zoom < 1.5f ? 2 : 1;

            // Don't skip edges for smaller meshes - we need to see them
            if (edges.Count < 1000)
                edgeSkip = 1;

            // For large models, still maintain a minimum number of edges
            int minEdgesToRender = 500;
            if (edges.Count / edgeSkip < minEdgesToRender)
                edgeSkip = Math.Max(1, edges.Count / minEdgesToRender);

            // Calculate actual line thickness based on zoom
            int lineThickness = zoom > 3.0f ? 2 : 1;

            // Draw edges
            for (int i = 0; i < sortedEdges.Count; i += edgeSkip)
            {
                var sortedEdge = sortedEdges[i];
                Point3D v1 = vertices[sortedEdge.Item1];
                Point3D v2 = vertices[sortedEdge.Item2];

                // Skip if both vertices have zero density
                if (v1.Density <= 0 && v2.Density <= 0) continue;

                // Translate points relative to volume center
                float v1x = v1.X - volumeCenterX;
                float v1y = v1.Y - volumeCenterY;
                float v1z = v1.Z - volumeCenterZ;

                float v2x = v2.X - volumeCenterX;
                float v2y = v2.Y - volumeCenterY;
                float v2z = v2.Z - volumeCenterZ;

                // Apply Y-axis rotation
                float v1xr = v1x * cosY + v1z * sinY;
                float v1zr = -v1x * sinY + v1z * cosY;

                float v2xr = v2x * cosY + v2z * sinY;
                float v2zr = -v2x * sinY + v2z * cosY;

                // Apply X-axis rotation
                float v1yr = v1y * cosX - v1zr * sinX;
                v1zr = v1y * sinX + v1zr * cosX;

                float v2yr = v2y * cosX - v2zr * sinX;
                v2zr = v2y * sinX + v2zr * cosX;

                // Apply perspective projection
                float z1 = viewDistance + v1zr;
                float z2 = viewDistance + v2zr;

                if (z1 <= 0 || z2 <= 0) continue; // Behind the camera

                // Apply projection with focal length
                float p1x = (v1xr * focalLength / z1) * zoom;
                float p1y = (v1yr * focalLength / z1) * zoom;
                float p2x = (v2xr * focalLength / z2) * zoom;
                float p2y = (v2yr * focalLength / z2) * zoom;

                // Map to screen coordinates with panning
                int x1 = (int)(centerX + p1x + pan.X);
                int y1 = (int)(centerY + p1y + pan.Y);
                int x2 = (int)(centerX + p2x + pan.X);
                int y2 = (int)(centerY + p2y + pan.Y);

                // Simple viewport clipping
                if ((x1 < -100 && x2 < -100) || (x1 > viewWidth + 100 && x2 > viewWidth + 100) ||
                    (y1 < -100 && y2 < -100) || (y1 > viewHeight + 100 && y2 > viewHeight + 100))
                    continue;

                // Draw line - use the stable line drawing implementation
                DrawBresenhamLine(basePtr, stride, bytesPerPixel, viewWidth, viewHeight,
                          x1, y1, x2, y2, v1.Density, v2.Density);
            }

            // Render vertices if zoomed in
            if (zoom > 2.0f)
            {
                int vertexSkip = zoom < 3.0f ? 3 : 1;

                // Don't skip vertices for smaller meshes
                if (vertices.Count < 500)
                    vertexSkip = 1;

                for (int i = 0; i < vertices.Count; i += vertexSkip)
                {
                    var vertex = vertices[i];

                    // Skip vertices with zero density
                    if (vertex.Density <= 0) continue;

                    // Translate point relative to volume center
                    float vx = vertex.X - volumeCenterX;
                    float vy = vertex.Y - volumeCenterY;
                    float vz = vertex.Z - volumeCenterZ;

                    // Apply Y-axis rotation
                    float vxr = vx * cosY + vz * sinY;
                    float vzr = -vx * sinY + vz * cosY;

                    // Apply X-axis rotation
                    float vyr = vy * cosX - vzr * sinX;
                    vzr = vy * sinX + vzr * cosX;

                    // Apply perspective projection
                    float z = viewDistance + vzr;
                    if (z <= 0) continue; // Behind the camera

                    // Calculate projected coordinates
                    float px = (vxr * focalLength / z) * zoom;
                    float py = (vyr * focalLength / z) * zoom;

                    // Map to screen coordinates with panning
                    int x = (int)(centerX + px + pan.X);
                    int y = (int)(centerY + py + pan.Y);

                    // Skip if outside viewport with margin
                    if (x < -10 || x >= viewWidth + 10 || y < -10 || y >= viewHeight + 10)
                        continue;

                    // Draw vertex as a single pixel
                    if (x >= 0 && x < viewWidth && y >= 0 && y < viewHeight)
                    {
                        Color color = GetColorForDensity(vertex.Density);
                        byte* pixel = basePtr + (y * stride) + (x * bytesPerPixel);
                        pixel[0] = color.B;
                        pixel[1] = color.G;
                        pixel[2] = color.R;
                        pixel[3] = 255; // Alpha
                    }
                }
            }
        }
        private unsafe void DrawBresenhamLine(byte* basePtr, int stride, int bytesPerPixel,
                            int width, int height, int x1, int y1, int x2, int y2,
                            float density1, float density2)
        {
            // Calculate deltas
            int dx = Math.Abs(x2 - x1);
            int dy = Math.Abs(y2 - y1);
            int sx = x1 < x2 ? 1 : -1;
            int sy = y1 < y2 ? 1 : -1;
            int err = dx - dy;

            // Calculate total length for interpolation
            float totalLength = (float)Math.Sqrt(dx * dx + dy * dy);
            int startX = x1;
            int startY = y1;
            float currentLength = 0;

            while (true)
            {
                // Skip if outside the viewport
                if (x1 >= 0 && x1 < width && y1 >= 0 && y1 < height)
                {
                    // Calculate distance for interpolation
                    int currentDx = x1 - startX;
                    int currentDy = y1 - startY;
                    currentLength = (float)Math.Sqrt(currentDx * currentDx + currentDy * currentDy);

                    // Calculate interpolation factor
                    float t = totalLength > 0 ? currentLength / totalLength : 0;

                    // Interpolate density
                    float density = density1 * (1 - t) + density2 * t;

                    // Map to color
                    Color color = GetColorForDensity(density);

                    // Draw pixel
                    byte* pixel = basePtr + (y1 * stride) + (x1 * bytesPerPixel);
                    pixel[0] = color.B;
                    pixel[1] = color.G;
                    pixel[2] = color.R;
                    pixel[3] = 255; // Alpha
                }

                // Check if we've reached the end point
                if (x1 == x2 && y1 == y2)
                    break;

                // Calculate next position
                int e2 = 2 * err;
                if (e2 > -dy)
                {
                    err -= dy;
                    x1 += sx;
                }
                if (e2 < dx)
                {
                    err += dx;
                    y1 += sy;
                }
            }
        }
        private unsafe void RenderVerticesOptimized(byte* basePtr, int stride, int bytesPerPixel,
                                        int viewWidth, int viewHeight,
                                        float centerX, float centerY,
                                        float volumeCenterX, float volumeCenterY, float volumeCenterZ,
                                        float viewDistance, float focalLength,
                                        float cosX, float sinX, float cosY, float sinY)
        {
            // Skip based on zoom level and vertex count
            int vertexSkip = 1;
            if (vertices.Count > 5000)
                vertexSkip = (int)(vertices.Count / 2000);

            // Only render a representative sample of vertices for large datasets
            if (vertices.Count > 10000)
            {
                // Use importance-based sampling
                int sampleCount = Math.Min(2000, vertices.Count / 4);
                var toRender = new List<int>();

                // Pick vertices based on density (higher density = more likely to be rendered)
                Random rnd = new Random(42); // Fixed seed for consistency

                for (int i = 0; i < vertices.Count; i += vertexSkip)
                {
                    var v = vertices[i];
                    float normalizedDensity = (v.Density - minDensity) / (maxDensity - minDensity);

                    // Higher density vertices have higher chance of being included
                    if (rnd.NextDouble() < normalizedDensity * 0.8 + 0.2)
                    {
                        toRender.Add(i);
                        if (toRender.Count >= sampleCount)
                            break;
                    }
                }

                // Render the selected vertices in parallel
                Parallel.ForEach(toRender, i =>
                {
                    RenderSingleVertex(basePtr, stride, bytesPerPixel, viewWidth, viewHeight,
                                      vertices[i], centerX, centerY, volumeCenterX, volumeCenterY, volumeCenterZ,
                                      viewDistance, focalLength, cosX, sinX, cosY, sinY);
                });
            }
            else
            {
                // For smaller datasets, just render all vertices with skipping
                Parallel.For(0, vertices.Count, i =>
                {
                    if (i % vertexSkip != 0) return;

                    RenderSingleVertex(basePtr, stride, bytesPerPixel, viewWidth, viewHeight,
                                      vertices[i], centerX, centerY, volumeCenterX, volumeCenterY, volumeCenterZ,
                                      viewDistance, focalLength, cosX, sinX, cosY, sinY);
                });
            }
        }
        private unsafe void RenderSingleVertex(byte* basePtr, int stride, int bytesPerPixel,
                                     int viewWidth, int viewHeight,
                                     Point3D vertex, float centerX, float centerY,
                                     float volumeCenterX, float volumeCenterY, float volumeCenterZ,
                                     float viewDistance, float focalLength,
                                     float cosX, float sinX, float cosY, float sinY)
        {
            // Skip vertices with zero density
            if (vertex.Density <= 0) return;

            // Translate point relative to volume center
            float vx = vertex.X - volumeCenterX;
            float vy = vertex.Y - volumeCenterY;
            float vz = vertex.Z - volumeCenterZ;

            // Apply Y-axis rotation
            float vxr = vx * cosY + vz * sinY;
            float vzr = -vx * sinY + vz * cosY;

            // Apply X-axis rotation
            float vyr = vy * cosX - vzr * sinX;
            vzr = vy * sinX + vzr * cosX;

            // Apply perspective projection
            float z = viewDistance + vzr;
            if (z <= 0) return; // Behind the camera

            // Calculate projected coordinates
            float px = (vxr * focalLength / z) * zoom;
            float py = (vyr * focalLength / z) * zoom;

            // Map to screen coordinates with panning
            int x = (int)(centerX + px + pan.X);
            int y = (int)(centerY + py + pan.Y);

            // Skip if outside viewport
            if (x < 0 || x >= viewWidth || y < 0 || y >= viewHeight)
                return;

            // Draw vertex point with fade based on z-distance
            Color color = GetColorForDensity(vertex.Density);

            // Size depends on zoom
            int dotSize = zoom > 4.0f ? 2 : 1;

            // Direct pixel manipulation for speed
            byte* pixel = basePtr + (y * stride) + (x * bytesPerPixel);

            // Draw a small point
            if (dotSize == 1)
            {
                pixel[0] = color.B;
                pixel[1] = color.G;
                pixel[2] = color.R;
                pixel[3] = 255; // Alpha
            }
            else
            {
                // Draw a 2x2 dot for larger size
                if (x + 1 < viewWidth)
                {
                    byte* rightPixel = pixel + bytesPerPixel;
                    rightPixel[0] = color.B;
                    rightPixel[1] = color.G;
                    rightPixel[2] = color.R;
                    rightPixel[3] = 255;
                }

                if (y + 1 < viewHeight)
                {
                    byte* bottomPixel = pixel + stride;
                    bottomPixel[0] = color.B;
                    bottomPixel[1] = color.G;
                    bottomPixel[2] = color.R;
                    bottomPixel[3] = 255;

                    if (x + 1 < viewWidth)
                    {
                        byte* bottomRightPixel = bottomPixel + bytesPerPixel;
                        bottomRightPixel[0] = color.B;
                        bottomRightPixel[1] = color.G;
                        bottomRightPixel[2] = color.R;
                        bottomRightPixel[3] = 255;
                    }
                }
            }
        }
        private unsafe void DrawFastLineClipped(byte* basePtr, int stride, int bytesPerPixel,
                             int width, int height, int x1, int y1, int x2, int y2,
                             float density1, float density2, int thickness)
        {
            // Cohen-Sutherland line clipping
            const int INSIDE = 0; // 0000
            const int LEFT = 1;   // 0001
            const int RIGHT = 2;  // 0010
            const int BOTTOM = 4; // 0100
            const int TOP = 8;    // 1000

            // Calculate outcodes for both endpoints
            int outcode1 = ComputeOutCode(x1, y1, width, height);
            int outcode2 = ComputeOutCode(x2, y2, width, height);
            bool accept = false;

            while (true)
            {
                if ((outcode1 | outcode2) == 0)
                {
                    // Both endpoints inside viewport
                    accept = true;
                    break;
                }
                else if ((outcode1 & outcode2) != 0)
                {
                    // Both endpoints outside same region
                    break;
                }
                else
                {
                    // Line needs clipping - at least one endpoint outside
                    int outcodeOut = outcode1 != 0 ? outcode1 : outcode2;

                    int x, y;

                    // Find intersection point
                    if ((outcodeOut & TOP) != 0)
                    {
                        // Top
                        x = x1 + (x2 - x1) * (0 - y1) / (y2 - y1);
                        y = 0;
                    }
                    else if ((outcodeOut & BOTTOM) != 0)
                    {
                        // Bottom
                        x = x1 + (x2 - x1) * (height - 1 - y1) / (y2 - y1);
                        y = height - 1;
                    }
                    else if ((outcodeOut & RIGHT) != 0)
                    {
                        // Right
                        y = y1 + (y2 - y1) * (width - 1 - x1) / (x2 - x1);
                        x = width - 1;
                    }
                    else
                    {
                        // Left
                        y = y1 + (y2 - y1) * (0 - x1) / (x2 - x1);
                        x = 0;
                    }

                    // Replace outside point
                    if (outcodeOut == outcode1)
                    {
                        x1 = x;
                        y1 = y;
                        outcode1 = ComputeOutCode(x1, y1, width, height);
                    }
                    else
                    {
                        x2 = x;
                        y2 = y;
                        outcode2 = ComputeOutCode(x2, y2, width, height);
                    }
                }
            }

            if (!accept) return;

            // Now draw the clipped line using optimized Bresenham
            int dx = Math.Abs(x2 - x1);
            int dy = Math.Abs(y2 - y1);

            // Fast path for horizontal or vertical lines
            if (dx == 0 || dy == 0)
            {
                if (dx == 0) // Vertical line
                {
                    int startY = Math.Min(y1, y2);
                    int endY = Math.Max(y1, y2);

                    for (int y = startY; y <= endY; y++)
                    {
                        // Calculate interpolation factor
                        float t = (y - y1) / (float)(y2 - y1);
                        if (float.IsNaN(t)) t = 0;

                        // Interpolate density
                        float density = density1 * (1 - t) + density2 * t;
                        Color color = GetColorForDensity(density);

                        // Draw a single point or thicker line
                        if (thickness <= 1)
                        {
                            byte* pixel = basePtr + (y * stride) + (x1 * bytesPerPixel);
                            pixel[0] = color.B;
                            pixel[1] = color.G;
                            pixel[2] = color.R;
                            pixel[3] = 255; // Alpha
                        }
                        else
                        {
                            // Draw a thicker line
                            for (int tx = Math.Max(0, x1 - thickness / 2); tx <= Math.Min(width - 1, x1 + thickness / 2); tx++)
                            {
                                byte* pixel = basePtr + (y * stride) + (tx * bytesPerPixel);
                                pixel[0] = color.B;
                                pixel[1] = color.G;
                                pixel[2] = color.R;
                                pixel[3] = 255; // Alpha
                            }
                        }
                    }
                    return;
                }
                else // Horizontal line
                {
                    int startX = Math.Min(x1, x2);
                    int endX = Math.Max(x1, x2);

                    for (int x = startX; x <= endX; x++)
                    {
                        // Calculate interpolation factor
                        float t = (x - x1) / (float)(x2 - x1);
                        if (float.IsNaN(t)) t = 0;

                        // Interpolate density
                        float density = density1 * (1 - t) + density2 * t;
                        Color color = GetColorForDensity(density);

                        // Draw a single point or thicker line
                        if (thickness <= 1)
                        {
                            byte* pixel = basePtr + (y1 * stride) + (x * bytesPerPixel);
                            pixel[0] = color.B;
                            pixel[1] = color.G;
                            pixel[2] = color.R;
                            pixel[3] = 255; // Alpha
                        }
                        else
                        {
                            // Draw a thicker line
                            for (int ty = Math.Max(0, y1 - thickness / 2); ty <= Math.Min(height - 1, y1 + thickness / 2); ty++)
                            {
                                byte* pixel = basePtr + (ty * stride) + (x * bytesPerPixel);
                                pixel[0] = color.B;
                                pixel[1] = color.G;
                                pixel[2] = color.R;
                                pixel[3] = 255; // Alpha
                            }
                        }
                    }
                    return;
                }
            }

            // For long lines, use optimized rendering approach
            if (dx > 100 || dy > 100)
            {
                // Optimized algorithm for long lines
                int steps = Math.Max(dx, dy) / 2; // Sample fewer points for long lines
                steps = Math.Max(10, Math.Min(50, steps)); // At least 10 points, at most 50

                float xStep = (float)(x2 - x1) / steps;
                float yStep = (float)(y2 - y1) / steps;

                for (int i = 0; i <= steps; i++)
                {
                    float t = (float)i / steps;
                    int x = (int)(x1 + xStep * i + 0.5f);
                    int y = (int)(y1 + yStep * i + 0.5f);

                    if (x >= 0 && x < width && y >= 0 && y < height)
                    {
                        // Interpolate density
                        float density = density1 * (1 - t) + density2 * t;
                        Color color = GetColorForDensity(density);

                        // Draw pixel
                        byte* pixel = basePtr + (y * stride) + (x * bytesPerPixel);
                        pixel[0] = color.B;
                        pixel[1] = color.G;
                        pixel[2] = color.R;
                        pixel[3] = 255; // Alpha
                    }
                }
                return;
            }

            // Standard Bresenham for shorter lines
            int sx = x1 < x2 ? 1 : -1;
            int sy = y1 < y2 ? 1 : -1;
            int err = dx - dy;

            float totalDistance = (float)Math.Sqrt(dx * dx + dy * dy);
            float currentDistance = 0;
            int origX1 = x1;
            int origY1 = y1;

            while (true)
            {
                // Calculate distance traveled for interpolation
                float t;
                if (totalDistance > 0)
                {
                    int dX = x1 - origX1;
                    int dY = y1 - origY1;
                    currentDistance = (float)Math.Sqrt(dX * dX + dY * dY);
                    t = currentDistance / totalDistance;
                }
                else
                {
                    t = 0;
                }

                // Calculate interpolated density
                float density = density1 * (1 - t) + density2 * t;
                Color color = GetColorForDensity(density);

                // Draw a single point
                byte* pixel = basePtr + (y1 * stride) + (x1 * bytesPerPixel);
                pixel[0] = color.B;
                pixel[1] = color.G;
                pixel[2] = color.R;
                pixel[3] = 255; // Alpha

                // For thick lines, draw additional pixels
                if (thickness > 1)
                {
                    int halfThick = thickness / 2;
                    for (int ty = -halfThick; ty <= halfThick; ty++)
                    {
                        for (int tx = -halfThick; tx <= halfThick; tx++)
                        {
                            if (tx == 0 && ty == 0) continue; // Already drawn

                            int px = x1 + tx;
                            int py = y1 + ty;

                            if (px >= 0 && px < width && py >= 0 && py < height)
                            {
                                byte* thickPixel = basePtr + (py * stride) + (px * bytesPerPixel);
                                thickPixel[0] = color.B;
                                thickPixel[1] = color.G;
                                thickPixel[2] = color.R;
                                thickPixel[3] = 255; // Alpha
                            }
                        }
                    }
                }

                if (x1 == x2 && y1 == y2) break;

                int e2 = 2 * err;
                if (e2 > -dy)
                {
                    err -= dy;
                    x1 += sx;
                }
                if (e2 < dx)
                {
                    err += dx;
                    y1 += sy;
                }
            }
        }
        private int ComputeOutCode(int x, int y, int width, int height)
        {
            int code = 0;

            if (y < 0)           // top
                code |= 8;
            else if (y >= height) // bottom
                code |= 4;

            if (x < 0)           // left
                code |= 1;
            else if (x >= width)  // right
                code |= 2;

            return code;
        }
        /// <summary>
        /// Fast line drawing optimized for performance
        /// </summary>
        private unsafe void DrawFastLine(byte* basePtr, int stride, int bytesPerPixel,
                             int width, int height, int x1, int y1, int x2, int y2,
                             float density1, float density2)
        {
            // Quick clip check
            if ((x1 < 0 && x2 < 0) || (x1 >= width && x2 >= width) ||
                (y1 < 0 && y2 < 0) || (y1 >= height && y2 >= height))
                return;

            // For very long lines, sample fewer points
            int dx = Math.Abs(x2 - x1);
            int dy = Math.Abs(y2 - y1);
            int length = Math.Max(dx, dy);

            // Use faster algorithm for long lines by sampling fewer points
            if (length > 100)
            {
                int stepCount = Math.Max(20, length / 5);
                float step = 1.0f / stepCount;

                for (float t = 0; t <= 1.0f; t += step)
                {
                    int x = (int)Math.Round(x1 + t * (x2 - x1));
                    int y = (int)Math.Round(y1 + t * (y2 - y1));

                    // Skip if outside viewport
                    if (x < 0 || x >= width || y < 0 || y >= height)
                        continue;

                    // Calculate interpolated density
                    float density = density1 * (1 - t) + density2 * t;

                    // Map density to color
                    Color color = GetColorForDensity(density);

                    // Draw a single point
                    byte* pixel = basePtr + (y * stride) + (x * bytesPerPixel);
                    pixel[0] = color.B;
                    pixel[1] = color.G;
                    pixel[2] = color.R;
                    pixel[3] = 255; // Alpha
                }
                return;
            }

            // Use Bresenham's algorithm for short to medium lines
            int sx = x1 < x2 ? 1 : -1;
            int sy = y1 < y2 ? 1 : -1;
            int err = dx - dy;

            float totalDistance = (float)Math.Sqrt(dx * dx + dy * dy);
            float currentDistance = 0;

            int origX1 = x1;
            int origY1 = y1;

            while (true)
            {
                // Skip if outside viewport (faster than clipping)
                if (x1 >= 0 && x1 < width && y1 >= 0 && y1 < height)
                {
                    // Calculate distance traveled for interpolation
                    float t;
                    if (totalDistance > 0)
                    {
                        int dX = x1 - origX1;
                        int dY = y1 - origY1;
                        currentDistance = (float)Math.Sqrt(dX * dX + dY * dY);
                        t = currentDistance / totalDistance;
                    }
                    else
                    {
                        t = 0;
                    }

                    // Calculate interpolated density
                    float density = density1 * (1 - t) + density2 * t;

                    // Map density to color
                    Color color = GetColorForDensity(density);

                    // Draw a single point
                    byte* pixel = basePtr + (y1 * stride) + (x1 * bytesPerPixel);
                    pixel[0] = color.B;
                    pixel[1] = color.G;
                    pixel[2] = color.R;
                    pixel[3] = 255; // Alpha
                }

                if (x1 == x2 && y1 == y2) break;

                int e2 = 2 * err;
                if (e2 > -dy)
                {
                    err -= dy;
                    x1 += sx;
                }
                if (e2 < dx)
                {
                    err += dx;
                    y1 += sy;
                }
            }
        }

        /// <summary>
        /// Draws a single point (vertex) with optimized bounds checking
        /// </summary>
        private unsafe void DrawPoint(byte* basePtr, int stride, int bytesPerPixel,
                             int width, int height, int x, int y, int size, Color color)
        {
            // Fast path for size 1 (most common case)
            if (size <= 1)
            {
                if (x >= 0 && x < width && y >= 0 && y < height)
                {
                    byte* pixel = basePtr + (y * stride) + (x * bytesPerPixel);
                    pixel[0] = color.B;
                    pixel[1] = color.G;
                    pixel[2] = color.R;
                    pixel[3] = 255; // Alpha
                }
                return;
            }

            // Limit checking for larger sizes
            int xStart = Math.Max(0, x - size);
            int xEnd = Math.Min(width - 1, x + size);
            int yStart = Math.Max(0, y - size);
            int yEnd = Math.Min(height - 1, y + size);

            for (int py = yStart; py <= yEnd; py++)
            {
                byte* row = basePtr + (py * stride);

                for (int px = xStart; px <= xEnd; px++)
                {
                    byte* pixel = row + (px * bytesPerPixel);
                    pixel[0] = color.B;
                    pixel[1] = color.G;
                    pixel[2] = color.R;
                    pixel[3] = 255; // Alpha
                }
            }
        }

        /// <summary>
        /// Maps a density value to a color using a blue-to-red color scale with optimized bounds checking
        /// </summary>
        private Color GetColorForDensity(float density)
        {
            // Handle edge cases to avoid division by zero or other errors
            if (maxDensity <= minDensity)
            {
                return Color.Blue;
            }

            // Normalize density to [0, 1]
            float normalizedDensity = (density - minDensity) / (maxDensity - minDensity);

            // Handle NaN and Infinity
            if (float.IsNaN(normalizedDensity) || float.IsInfinity(normalizedDensity))
            {
                normalizedDensity = 0;
            }

            // Clamp to valid range
            normalizedDensity = Math.Max(0, Math.Min(1, normalizedDensity));

            // Create a smooth color gradient from blue to red with explicit bounds checking
            int r = Math.Max(0, Math.Min(255, (int)(normalizedDensity * 255)));
            int g = Math.Max(0, Math.Min(255, (int)(normalizedDensity < 0.5 ?
                normalizedDensity * 2 * 255 : (1 - normalizedDensity) * 2 * 255)));
            int b = Math.Max(0, Math.Min(255, (int)((1 - normalizedDensity) * 255)));

            return Color.FromArgb(r, g, b);
        }

        /// <summary>
        /// Draws a color legend for the density scale
        /// </summary>
        private void DrawColorLegend(Graphics g, int viewWidth, int viewHeight)
        {
            int legendWidth = 20;
            int legendHeight = 150;
            int legendX = viewWidth - legendWidth - 40;
            int legendY = 10;

            // Draw gradient bar
            Rectangle gradientRect = new Rectangle(legendX, legendY, legendWidth, legendHeight);
            using (LinearGradientBrush brush = new LinearGradientBrush(
                gradientRect, Color.Blue, Color.Red, LinearGradientMode.Vertical))
            {
                ColorBlend blend = new ColorBlend(3);
                blend.Colors = new Color[] { Color.Blue, Color.Green, Color.Red };
                blend.Positions = new float[] { 0.0f, 0.5f, 1.0f };
                brush.InterpolationColors = blend;

                g.FillRectangle(brush, gradientRect);
                g.DrawRectangle(Pens.White, gradientRect);
            }

            // Draw density labels
            using (Font font = new Font("Arial", 8))
            using (SolidBrush brush = new SolidBrush(Color.White))
            using (StringFormat format = new StringFormat { Alignment = StringAlignment.Far })
            {
                g.DrawString($"{maxDensity:F1}", font, brush,
                    legendX - 5, legendY, format);
                g.DrawString($"{(minDensity + maxDensity) / 2:F1}", font, brush,
                    legendX - 5, legendY + legendHeight / 2, format);
                g.DrawString($"{minDensity:F1}", font, brush,
                    legendX - 5, legendY + legendHeight, format);

                // Draw unit
                g.DrawString("kg/m³", font, brush,
                    legendX + legendWidth / 2, legendY + legendHeight + 10);
            }
        }
    }
}