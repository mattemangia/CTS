using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading;
using System.Threading.Tasks;
using Krypton.Toolkit;
using System.Windows.Forms;

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

            // First identify material voxels (those with non-zero density)
            bool[,,] isMaterialVoxel = new bool[width, height, depth];
            int materialVoxelCount = 0;

            Logger.Log("[VolumeRenderer] Identifying material voxels for wireframe...");

            // Mark voxels that have density (these are the material voxels)
            Parallel.For(0, depth, z =>
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (densityVolume[x, y, z] > 0)
                        {
                            isMaterialVoxel[x, y, z] = true;
                            Interlocked.Increment(ref materialVoxelCount);
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

            // If we're only rendering boundaries, identify them
            bool[,,] isBoundary = new bool[width, height, depth];
            int boundaryVoxelCount = 0;

            if (!renderFullVolume)
            {
                Parallel.For(0, depth, z =>
                {
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            // Only check material voxels
                            if (!isMaterialVoxel[x, y, z])
                                continue;

                            // Check if this material voxel has any non-material neighbors
                            bool isBoundaryVoxel = false;

                            // Check 6-connected neighbors
                            if (x > 0 && !isMaterialVoxel[x - 1, y, z]) isBoundaryVoxel = true;
                            else if (x < width - 1 && !isMaterialVoxel[x + 1, y, z]) isBoundaryVoxel = true;
                            else if (y > 0 && !isMaterialVoxel[x, y - 1, z]) isBoundaryVoxel = true;
                            else if (y < height - 1 && !isMaterialVoxel[x, y + 1, z]) isBoundaryVoxel = true;
                            else if (z > 0 && !isMaterialVoxel[x, y, z - 1]) isBoundaryVoxel = true;
                            else if (z < depth - 1 && !isMaterialVoxel[x, y, z + 1]) isBoundaryVoxel = true;

                            if (isBoundaryVoxel)
                            {
                                isBoundary[x, y, z] = true;
                                Interlocked.Increment(ref boundaryVoxelCount);
                            }
                        }
                    }
                });

                Logger.Log($"[VolumeRenderer] Found {boundaryVoxelCount} boundary voxels");
            }

            bool showInterior = renderFullVolume || boundaryVoxelCount < 100;
            if (renderFullVolume)
            {
                Logger.Log("[VolumeRenderer] Generating full volume representation");
            }
            else if (showInterior)
            {
                Logger.Log("[VolumeRenderer] Small object detected - including interior voxels");
            }

            // Create vertices based on rendering mode
            for (int z = 0; z < depth; z += skipFactor)
            {
                for (int y = 0; y < height; y += skipFactor)
                {
                    for (int x = 0; x < width; x += skipFactor)
                    {
                        bool isVertex = false;
                        float density = 0;

                        // Check current position and nearby positions
                        for (int dz = 0; dz < skipFactor && z + dz < depth; dz++)
                        {
                            for (int dy = 0; dy < skipFactor && y + dy < height; dy++)
                            {
                                for (int dx = 0; dx < skipFactor && x + dx < width; dx++)
                                {
                                    int nx = x + dx;
                                    int ny = y + dy;
                                    int nz = z + dz;

                                    if (nx < width && ny < height && nz < depth)
                                    {
                                        if (renderFullVolume)
                                        {
                                            // If full volume, include all material voxels
                                            if (isMaterialVoxel[nx, ny, nz])
                                            {
                                                isVertex = true;
                                                density = Math.Max(density, densityVolume[nx, ny, nz]);
                                            }
                                        }
                                        else
                                        {
                                            // If boundary-only, check if it's a boundary or small object
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
                        }
                    }
                }
            }

            Logger.Log($"[VolumeRenderer] Created {vertices.Count} vertices");

            // If very few vertices, decrease skip factor and try again
            if (vertices.Count < 10 && skipFactor > 1)
            {
                skipFactor = Math.Max(1, skipFactor / 2);
                Logger.Log($"[VolumeRenderer] Too few vertices - reducing skip factor to {skipFactor} and retrying");
                GenerateVolumeRepresentation();
                return;
            }

            // Connect vertices to form edges - different strategies based on mode
            foreach (var vertexEntry in vertexIndices)
            {
                var pos = vertexEntry.Key;
                int idx = vertexEntry.Value;
                int x = pos.Item1;
                int y = pos.Item2;
                int z = pos.Item3;

                // Connect to immediate neighbors in all directions
                if (vertexIndices.TryGetValue((x + skipFactor, y, z), out int xNeighbor))
                {
                    edges.Add((idx, xNeighbor));
                }

                if (vertexIndices.TryGetValue((x, y + skipFactor, z), out int yNeighbor))
                {
                    edges.Add((idx, yNeighbor));
                }

                if (vertexIndices.TryGetValue((x, y, z + skipFactor), out int zNeighbor))
                {
                    edges.Add((idx, zNeighbor));
                }

                // For full volume rendering or small objects, add more connections
                if (renderFullVolume || vertices.Count < 100)
                {
                    if (vertexIndices.TryGetValue((x + skipFactor, y + skipFactor, z), out int xyNeighbor))
                    {
                        edges.Add((idx, xyNeighbor));
                    }

                    if (vertexIndices.TryGetValue((x + skipFactor, y, z + skipFactor), out int xzNeighbor))
                    {
                        edges.Add((idx, xzNeighbor));
                    }

                    if (vertexIndices.TryGetValue((x, y + skipFactor, z + skipFactor), out int yzNeighbor))
                    {
                        edges.Add((idx, yzNeighbor));
                    }

                    // Add diagonal corner for more complete structure in full volume mode
                    if (renderFullVolume && vertexIndices.TryGetValue((x + skipFactor, y + skipFactor, z + skipFactor), out int xyzNeighbor))
                    {
                        edges.Add((idx, xyzNeighbor));
                    }
                }
                // For larger objects in boundary mode, add diagonal edges selectively to reduce clutter
                else if (x % (skipFactor * 2) == 0 && y % (skipFactor * 2) == 0)
                {
                    if (vertexIndices.TryGetValue((x + skipFactor, y + skipFactor, z), out int xyNeighbor))
                    {
                        edges.Add((idx, xyNeighbor));
                    }
                }
            }

            string modeText = renderFullVolume ? "full volume" : "boundary-only";
            Logger.Log($"[VolumeRenderer] Created wireframe with {vertices.Count} vertices and {edges.Count} edges in {modeText} mode");
        }

        /// <summary>
        /// Renders the volume to the graphics context
        /// </summary>
        public void Render(Graphics g, int viewWidth, int viewHeight)
        {
            // Check if we need to update the cached render
            if (needsUpdate || cachedRender == null ||
                cachedRender.Width != viewWidth || cachedRender.Height != viewHeight)
            {
                lock (renderLock)
                {
                    // Create or recreate cached bitmap if needed
                    if (cachedRender == null || cachedRender.Width != viewWidth || cachedRender.Height != viewHeight)
                    {
                        cachedRender?.Dispose();
                        cachedRender = new Bitmap(viewWidth, viewHeight);
                    }

                    RenderToImage(cachedRender);
                    needsUpdate = false;
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
        private void RenderToImage(Bitmap bitmap)
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

                    // Fill with black using faster memory operations
                    int byteCount = stride * bitmap.Height;
                    byte[] blackBytes = new byte[byteCount];
                    System.Runtime.InteropServices.Marshal.Copy(blackBytes, 0, (IntPtr)ptr, byteCount);

                    // Sort edges by Z-order for better depth visualization
                    if (!edgesSorted || sortedEdges == null)
                    {
                        sortedEdges = new List<(int, int, float)>(edges.Count);

                        foreach (var edge in edges)
                        {
                            Point3D v1 = vertices[edge.Item1];
                            Point3D v2 = vertices[edge.Item2];
                            float avgZ = (v1.Z + v2.Z) / 2.0f;
                            sortedEdges.Add((edge.Item1, edge.Item2, avgZ));
                        }

                        // Sort by Z-depth (render back-to-front)
                        sortedEdges.Sort((a, b) => a.Item3.CompareTo(b.Item3));
                        edgesSorted = true;
                    }

                    // Center of view
                    float centerX = bitmap.Width / 2.0f;
                    float centerY = bitmap.Height / 2.0f;

                    // Center of volume
                    float volumeCenterX = width / 2.0f;
                    float volumeCenterY = height / 2.0f;
                    float volumeCenterZ = depth / 2.0f;

                    // FIXED: Increased view distance to reduce fisheye effect
                    float viewDistance = Math.Max(Math.Max(width, height), depth) * 1.5f;

                    // Pre-calculate rotation matrices for better performance
                    float angleY = rotationY * (float)Math.PI / 180.0f;
                    float cosY = (float)Math.Cos(angleY);
                    float sinY = (float)Math.Sin(angleY);

                    float angleX = rotationX * (float)Math.PI / 180.0f;
                    float cosX = (float)Math.Cos(angleX);
                    float sinX = (float)Math.Sin(angleX);

                    // FIXED: Focal length now independent of zoom to prevent fisheye effect
                    float focalLength = viewDistance;

                    // Draw the edges in sorted order (back to front)
                    // For performance, only draw a subset of edges when zoomed out
                    int edgeSkip = zoom < 0.8f ? 2 : 1;

                    for (int i = 0; i < sortedEdges.Count; i += edgeSkip)
                    {
                        var sortedEdge = sortedEdges[i];
                        Point3D v1 = vertices[sortedEdge.Item1];
                        Point3D v2 = vertices[sortedEdge.Item2];

                        // Get density values for color
                        float density1 = v1.Density;
                        float density2 = v2.Density;

                        // Skip if both vertices have zero density
                        if (density1 <= 0 && density2 <= 0) continue;

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

                        // Apply perspective projection with improved scaling
                        float z1 = viewDistance + v1zr;
                        float z2 = viewDistance + v2zr;

                        if (z1 <= 0 || z2 <= 0) continue; // Behind the camera

                        // FIXED: Perspective projection with better focal length and zoom application
                        float p1x = (v1xr * focalLength / z1) * zoom;
                        float p1y = (v1yr * focalLength / z1) * zoom;
                        float p2x = (v2xr * focalLength / z2) * zoom;
                        float p2y = (v2yr * focalLength / z2) * zoom;

                        // Map to screen coordinates with panning
                        int x1 = (int)(centerX + p1x + pan.X);
                        int y1 = (int)(centerY + p1y + pan.Y);

                        int x2 = (int)(centerX + p2x + pan.X);
                        int y2 = (int)(centerY + p2y + pan.Y);

                        // Draw line between vertices - optimized line drawing
                        DrawFastLine(ptr, stride, bytesPerPixel, bitmap.Width, bitmap.Height,
                                  x1, y1, x2, y2, density1, density2);
                    }

                    // For performance, only draw vertices when zoomed in enough
                    if (zoom > 1.5f)
                    {
                        // Draw vertices as small dots for better visualization
                        int vertexSkip = zoom < 2.0f ? 3 : 1;

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

                            // FIXED: Projection with better focal length and zoom application
                            float px = (vxr * focalLength / z) * zoom;
                            float py = (vyr * focalLength / z) * zoom;

                            // Map to screen coordinates with panning
                            int x = (int)(centerX + px + pan.X);
                            int y = (int)(centerY + py + pan.Y);

                            // Draw vertex point - use a smaller size for better performance
                            Color color = GetColorForDensity(vertex.Density);
                            DrawPoint(ptr, stride, bytesPerPixel, bitmap.Width, bitmap.Height,
                                      x, y, 1, color);
                        }
                    }
                }
            }
            finally
            {
                // Unlock the bitmap
                bitmap.UnlockBits(bitmapData);
            }
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