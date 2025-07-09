//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;

namespace CTS
{
    public class VolumeRenderer
    {
        private readonly float[,,] densityVolume;
        private readonly int width, height, depth;
        private readonly double pixelSize;
        private readonly byte materialID;
        private float rotationX, rotationY;
        private float zoom = 1.0f;
        private float defaultZoom;
        private PointF pan = new PointF(0, 0);
        private float minDensity, maxDensity;
        private bool uniformDensity = false;
        private bool renderFullVolume;

        // Pre-computed boundary cells for performance
        private bool[,,] boundaryCache;
        private bool boundaryCacheValid = false;

        // Lock for thread safety
        private readonly object syncLock = new object();

        // Main constructor
        public VolumeRenderer(float[,,] densityVolume, int width, int height, int depth, double pixelSize, byte materialID, bool renderFullVolume = true)
        {
            this.densityVolume = densityVolume;
            this.width = width;
            this.height = height;
            this.depth = depth;
            this.pixelSize = pixelSize;
            this.materialID = materialID;
            this.renderFullVolume = renderFullVolume;

            // Calculate default zoom based on volume size
            CalculateDefaultZoom();

            // Calculate density range for color mapping
            CalculateDensityRange();

            // Set default view
            this.rotationX = 30;
            this.rotationY = 30;
            this.zoom = defaultZoom;

            // Initialize boundary cache
            boundaryCache = new bool[width, height, depth];
        }

        // Compatibility constructor
        public VolumeRenderer(float[,,] densityVolume, int width, int height, int depth, double pixelSize)
            : this(densityVolume, width, height, depth, pixelSize, 0, true)
        {
        }

        // Calculate appropriate default zoom based on volume size
        private void CalculateDefaultZoom()
        {
            // Get the maximum dimension
            float maxDimension = Math.Max(Math.Max(width, height), depth);

            // Scale inversely with volume size - larger volumes get smaller zoom
            defaultZoom = 500.0f / maxDimension;

            Logger.Log($"[VolumeRenderer] Volume size: {width}x{height}x{depth}, default zoom: {defaultZoom}");
        }

        // Calculate density range for color mapping - OPTIMIZED with parallel processing
        private void CalculateDensityRange()
        {
            float localMin = float.MaxValue;
            float localMax = float.MinValue;

            // Calculate volume size
            long volumeSize = (long)width * height * depth;

            // Use sparse sampling for large volumes
            int step = volumeSize > 100_000_000 ? 32 :
                      volumeSize > 10_000_000 ? 16 :
                      volumeSize > 1_000_000 ? 8 : 4;

            // Use parallel processing with thread-safe min/max updates
            object lockObj = new object();

            // Sample the volume in parallel to find min/max density
            Parallel.For(0, depth / step + 1, z =>
            {
                float threadMin = float.MaxValue;
                float threadMax = float.MinValue;

                int zCoord = z * step;
                if (zCoord >= depth) return;

                for (int y = 0; y < height; y += step)
                {
                    if (y >= height) continue;

                    for (int x = 0; x < width; x += step)
                    {
                        if (x >= width) continue;

                        float density = densityVolume[x, y, zCoord];
                        if (density > 0)
                        {
                            threadMin = Math.Min(threadMin, density);
                            threadMax = Math.Max(threadMax, density);
                        }
                    }
                }

                // Update global min/max with thread-safe locking
                if (threadMin != float.MaxValue || threadMax != float.MinValue)
                {
                    lock (lockObj)
                    {
                        localMin = Math.Min(localMin, threadMin);
                        localMax = Math.Max(localMax, threadMax);
                    }
                }
            });

            // Handle edge cases
            if (localMin == float.MaxValue || localMax == float.MinValue)
            {
                localMin = 0;
                localMax = 1000;
            }

            // Check if density is fairly uniform
            uniformDensity = (localMax - localMin < 100);

            // Store results
            minDensity = localMin;
            maxDensity = localMax;

            Logger.Log($"[VolumeRenderer] Density range: {minDensity} to {maxDensity}, Uniform: {uniformDensity}");
        }

        // Set view transformation
        public void SetTransformation(float rotationX, float rotationY, float zoom, PointF pan)
        {
            lock (syncLock)
            {
                this.rotationX = rotationX;
                this.rotationY = rotationY;

                // Apply zoom relative to default zoom for this volume
                this.zoom = zoom * defaultZoom;

                this.pan = pan;

                // Invalidate boundary cache when view changes
                boundaryCacheValid = false;
            }
        }

        // Project 3D point to screen - THREAD SAFE
        public PointF ProjectToScreen(float x, float y, float z, int screenWidth, int screenHeight)
        {
            // Normalize coordinates to center of volume
            float nx = x - width / 2.0f;
            float ny = y - height / 2.0f;
            float nz = z - depth / 2.0f;

            // Make local copies to avoid race conditions
            float localRotationX, localRotationY, localZoom;
            PointF localPan;

            lock (syncLock)
            {
                localRotationX = this.rotationX;
                localRotationY = this.rotationY;
                localZoom = this.zoom;
                localPan = this.pan;
            }

            // Apply rotations
            float angleXRad = localRotationX * (float)Math.PI / 180;
            float angleYRad = localRotationY * (float)Math.PI / 180;

            // Rotate around Y axis
            float nx1 = nx * (float)Math.Cos(angleYRad) + nz * (float)Math.Sin(angleYRad);
            float nz1 = -nx * (float)Math.Sin(angleYRad) + nz * (float)Math.Cos(angleYRad);

            // Rotate around X axis
            float ny1 = ny * (float)Math.Cos(angleXRad) - nz1 * (float)Math.Sin(angleXRad);
            float nz2 = ny * (float)Math.Sin(angleXRad) + nz1 * (float)Math.Cos(angleXRad);

            // Apply zoom directly - no extra scaling factors
            float screenX = nx1 * localZoom + screenWidth / 2.0f + localPan.X;
            float screenY = ny1 * localZoom + screenHeight / 2.0f + localPan.Y;

            return new PointF(screenX, screenY);
        }

        // Get color based on density
        private Color GetColorForDensity(float density)
        {
            // If density is uniform, use coral color as default
            if (uniformDensity)
            {
                return Color.LightCoral;
            }

            // Normalize density to 0-1 range
            float normalizedDensity = (density - minDensity) / (maxDensity - minDensity);
            normalizedDensity = Math.Max(0, Math.Min(1, normalizedDensity));

            // Create color gradient - blue to green to coral
            if (normalizedDensity < 0.5f)
            {
                // Blue to green - with clamping to valid range
                int r = Math.Min(255, (int)(normalizedDensity * 2 * 100));
                int g = Math.Min(255, (int)(normalizedDensity * 2 * 255));
                int b = Math.Min(255, (int)((1 - normalizedDensity * 2) * 155 + 100)); // FIXED: reduced to avoid overflow
                return Color.FromArgb(r, g, b);
            }
            else
            {
                // Green to coral - with clamping to valid range
                float t = (normalizedDensity - 0.5f) * 2;
                int r = Math.Min(255, (int)(100 + t * (240 - 100)));
                int g = Math.Min(255, (int)(255 - t * (255 - 128)));
                int b = Math.Min(255, (int)(100 + t * (128 - 100)));
                return Color.FromArgb(r, g, b);
            }
        }

        // Render the volume
        public void Render(Graphics g, int viewWidth, int viewHeight, bool isInteracting = false)
        {
            g.Clear(Color.Black);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Always show outline box for reference
            DrawBoundingBox(g, viewWidth, viewHeight);

            // Calculate volume size
            long volumeSize = (long)width * height * depth;

            // If user is actively interacting, use a faster approach
            if (isInteracting)
            {
                RenderFastPreview(g, viewWidth, viewHeight, volumeSize);
            }
            else
            {
                // Determine step size based on volume size and rendering mode
                int baseStep = DetermineOptimalStepSize(volumeSize);

                // Use finer step if full rendering is requested
                int step = renderFullVolume ? Math.Max(baseStep / 2, 4) : baseStep;

                // Precompute boundary cells if needed
                PrecomputeBoundaryCells();

                // Direct rendering of surface boundary cells
                RenderSmartWireframe(g, viewWidth, viewHeight, step);

                // Draw density colorbar if not uniform
                if (!uniformDensity)
                {
                    DrawDensityColorbar(g, viewWidth, viewHeight);
                }
            }
        }
        private int DetermineOptimalStepSize(long volumeSize)
        {
            // For extremely large volumes (like high-res micro-CT), use larger steps
            if (volumeSize > 1_000_000_000) return 32;      // > 1 billion voxels
            if (volumeSize > 500_000_000) return 24;        // > 500 million voxels
            if (volumeSize > 100_000_000) return 16;        // > 100 million voxels
            if (volumeSize > 10_000_000) return 12;         // > 10 million voxels
            if (volumeSize > 1_000_000) return 8;           // > 1 million voxels
            return 4;                                        // Default for smaller volumes
        }
        private void RenderFastPreview(Graphics g, int viewWidth, int viewHeight, long volumeSize)
        {
            // Determine super aggressive step size for interactive rendering
            int step = DetermineInteractiveStepSize(volumeSize);

            // Draw a simplified wireframe version with large steps
            using (Pen pen = new Pen(Color.White, 1))
            {
                // Calculate view-dependent sort keys
                float cosY = (float)Math.Cos(rotationY * Math.PI / 180);
                float sinY = (float)Math.Sin(rotationY * Math.PI / 180);
                float cosX = (float)Math.Cos(rotationX * Math.PI / 180);
                float sinX = (float)Math.Sin(rotationX * Math.PI / 180);

                // Center of volume
                float centerX = width / 2.0f;
                float centerY = height / 2.0f;
                float centerZ = depth / 2.0f;

                // Just draw a simple grid of voxels for speed
                for (int z = 0; z < depth; z += step * 2)
                {
                    for (int y = 0; y < height; y += step * 2)
                    {
                        PointF? lastPoint = null;

                        for (int x = 0; x < width; x += step)
                        {
                            if (x >= width || y >= height || z >= depth) continue;

                            float density = 0;
                            try
                            {
                                density = densityVolume[x, y, z];
                            }
                            catch
                            {
                                continue; // Skip any out of range
                            }

                            if (density <= 0)
                            {
                                lastPoint = null;
                                continue;
                            }

                            PointF currentPoint = ProjectToScreen(x, y, z, viewWidth, viewHeight);

                            if (lastPoint.HasValue)
                            {
                                // Set color based on density
                                pen.Color = GetColorForDensity(density);
                                g.DrawLine(pen, lastPoint.Value, currentPoint);
                            }

                            lastPoint = currentPoint;
                        }
                    }
                }
            }

            // Draw a "Fast Preview" indicator
            using (Font font = new Font("Arial", 10, FontStyle.Bold))
            using (SolidBrush brush = new SolidBrush(Color.Yellow))
            using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(128, 0, 0, 0)))
            {
                string message = "FAST PREVIEW MODE - Release mouse for full quality";
                SizeF textSize = g.MeasureString(message, font);
                float textX = (viewWidth - textSize.Width) / 2;
                float textY = viewHeight - textSize.Height - 10;

                // Draw shadow for better visibility
                g.FillRectangle(shadowBrush, textX - 5, textY - 2, textSize.Width + 10, textSize.Height + 4);
                g.DrawString(message, font, brush, textX, textY);
            }
        }

        // Determine step size for interactive mode
        private int DetermineInteractiveStepSize(long volumeSize)
        {
            // Use even larger steps during interaction for responsiveness
            if (volumeSize > 1_000_000_000) return 64;      // > 1 billion voxels
            if (volumeSize > 500_000_000) return 48;        // > 500 million voxels
            if (volumeSize > 100_000_000) return 32;        // > 100 million voxels
            if (volumeSize > 10_000_000) return 24;         // > 10 million voxels
            if (volumeSize > 1_000_000) return 16;          // > 1 million voxels
            return 8;                                        // Default for smaller volumes
        }
        // OPTIMIZED: Precompute boundary cells
        private void PrecomputeBoundaryCells()
        {
            if (boundaryCacheValid)
                return;

            // Compute boundary cells in parallel
            Parallel.For(0, depth, z =>
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        boundaryCache[x, y, z] = false;

                        // Only check cells with density
                        if (densityVolume[x, y, z] <= 0)
                            continue;

                        // Check 6-connected neighbors
                        if (x > 0 && densityVolume[x - 1, y, z] <= 0)
                            boundaryCache[x, y, z] = true;
                        else if (x < width - 1 && densityVolume[x + 1, y, z] <= 0)
                            boundaryCache[x, y, z] = true;
                        else if (y > 0 && densityVolume[x, y - 1, z] <= 0)
                            boundaryCache[x, y, z] = true;
                        else if (y < height - 1 && densityVolume[x, y + 1, z] <= 0)
                            boundaryCache[x, y, z] = true;
                        else if (z > 0 && densityVolume[x, y, z - 1] <= 0)
                            boundaryCache[x, y, z] = true;
                        else if (z < depth - 1 && densityVolume[x, y, z + 1] <= 0)
                            boundaryCache[x, y, z] = true;
                    }
                }
            });

            boundaryCacheValid = true;
        }

        // OPTIMIZED: Check if a voxel is on the boundary
        private bool IsBoundaryVoxel(int x, int y, int z)
        {
            // Use cached values if available
            if (boundaryCacheValid)
                return boundaryCache[x, y, z];

            // Otherwise compute on the fly
            if (densityVolume[x, y, z] <= 0)
                return false;

            // Check 6-connected neighbors
            if (x > 0 && densityVolume[x - 1, y, z] <= 0) return true;
            if (x < width - 1 && densityVolume[x + 1, y, z] <= 0) return true;
            if (y > 0 && densityVolume[x, y - 1, z] <= 0) return true;
            if (y < height - 1 && densityVolume[x, y + 1, z] <= 0) return true;
            if (z > 0 && densityVolume[x, y, z - 1] <= 0) return true;
            if (z < depth - 1 && densityVolume[x, y, z + 1] <= 0) return true;

            return false;
        }

        // OPTIMIZED: Render smart wireframe with parallel processing
        private void RenderSmartWireframe(Graphics g, int viewWidth, int viewHeight, int step)
        {
            // Calculate view-dependent sort keys
            float cosY = (float)Math.Cos(rotationY * Math.PI / 180);
            float sinY = (float)Math.Sin(rotationY * Math.PI / 180);
            float cosX = (float)Math.Cos(rotationX * Math.PI / 180);
            float sinX = (float)Math.Sin(rotationX * Math.PI / 180);

            // Center of volume
            float centerX = width / 2.0f;
            float centerY = height / 2.0f;
            float centerZ = depth / 2.0f;

            // Use thread-safe collection for drawing operations
            var drawOps = new ConcurrentBag<(PointF, PointF, Color, float)>();

            // Process X, Y, Z directions in parallel
            Parallel.Invoke(
                // X-direction scan
                () => ScanXDirection(drawOps, step, viewWidth, viewHeight, centerX, centerY, centerZ, cosX, sinX, cosY, sinY),

                // Y-direction scan
                () => ScanYDirection(drawOps, step, viewWidth, viewHeight, centerX, centerY, centerZ, cosX, sinX, cosY, sinY),

                // Z-direction scan
                () => ScanZDirection(drawOps, step, viewWidth, viewHeight, centerX, centerY, centerZ, cosX, sinX, cosY, sinY)
            );

            // Add diagonal connections if needed
            if (renderFullVolume)
            {
                AddDiagonalConnections(drawOps, step, viewWidth, viewHeight, centerX, centerY, centerZ, cosX, sinX, cosY, sinY);
            }

            // Sort draw operations by depth (back-to-front)
            var sortedOps = new List<(PointF, PointF, Color, float)>(drawOps);
            sortedOps.Sort((a, b) => a.Item4.CompareTo(b.Item4));

            // Draw all lines
            using (Pen pen = new Pen(Color.White, 1))
            {
                foreach (var op in sortedOps)
                {
                    pen.Color = op.Item3;
                    g.DrawLine(pen, op.Item1, op.Item2);
                }
            }
        }

        // OPTIMIZED: Process X-direction scan
        private void ScanXDirection(ConcurrentBag<(PointF, PointF, Color, float)> drawOps, int step,
            int viewWidth, int viewHeight, float centerX, float centerY, float centerZ,
            float cosX, float sinX, float cosY, float sinY)
        {
            Parallel.For(0, depth / step + 1, zIndex =>
            {
                int z = zIndex * step;
                if (z >= depth) return;

                for (int y = 0; y < height; y += step)
                {
                    if (y >= height) continue;

                    PointF? lastPoint = null;
                    float lastDensity = 0;
                    bool lastIsBoundary = false;

                    for (int x = 0; x < width; x += step)
                    {
                        if (x >= width) continue;

                        float density = densityVolume[x, y, z];
                        if (density <= 0)
                        {
                            lastPoint = null;
                            continue;
                        }

                        // Check if this is a boundary cell
                        bool isBoundary = IsBoundaryVoxel(x, y, z);

                        // For interior lines, use larger steps unless full rendering
                        if (!isBoundary && !renderFullVolume && !lastIsBoundary && x % (step * 2) != 0)
                            continue;

                        PointF currentPoint = ProjectToScreen(x, y, z, viewWidth, viewHeight);

                        // Connect points with density
                        if (lastPoint.HasValue)
                        {
                            // Calculate depth for sorting
                            float midX = (x + (x - step)) / 2 - centerX;
                            float midY = y - centerY;
                            float midZ = z - centerZ;

                            float rotX = midX * cosY + midZ * sinY;
                            float rotZ = -midX * sinY + midZ * cosY;
                            float depth = midY * sinX + rotZ * cosX;

                            // Use average density for line color
                            float avgDensity = (density + lastDensity) / 2;
                            Color lineColor = GetColorForDensity(avgDensity);

                            // Add to draw operations
                            drawOps.Add((lastPoint.Value, currentPoint, lineColor, depth));
                        }

                        lastPoint = currentPoint;
                        lastDensity = density;
                        lastIsBoundary = isBoundary;
                    }
                }
            });
        }

        // OPTIMIZED: Process Y-direction scan
        private void ScanYDirection(ConcurrentBag<(PointF, PointF, Color, float)> drawOps, int step,
            int viewWidth, int viewHeight, float centerX, float centerY, float centerZ,
            float cosX, float sinX, float cosY, float sinY)
        {
            Parallel.For(0, depth / step + 1, zIndex =>
            {
                int z = zIndex * step;
                if (z >= depth) return;

                for (int x = 0; x < width; x += step)
                {
                    if (x >= width) continue;

                    PointF? lastPoint = null;
                    float lastDensity = 0;
                    bool lastIsBoundary = false;

                    for (int y = 0; y < height; y += step)
                    {
                        if (y >= height) continue;

                        float density = densityVolume[x, y, z];
                        if (density <= 0)
                        {
                            lastPoint = null;
                            continue;
                        }

                        // Check if this is a boundary cell
                        bool isBoundary = IsBoundaryVoxel(x, y, z);

                        // For interior cells, use larger steps
                        if (!isBoundary && !renderFullVolume && !lastIsBoundary && y % (step * 2) != 0)
                            continue;

                        PointF currentPoint = ProjectToScreen(x, y, z, viewWidth, viewHeight);

                        if (lastPoint.HasValue)
                        {
                            // Calculate depth for sorting
                            float midX = x - centerX;
                            float midY = (y + (y - step)) / 2 - centerY;
                            float midZ = z - centerZ;

                            float rotX = midX * cosY + midZ * sinY;
                            float rotZ = -midX * sinY + midZ * cosY;
                            float depth = midY * sinX + rotZ * cosX;

                            float avgDensity = (density + lastDensity) / 2;
                            Color lineColor = GetColorForDensity(avgDensity);

                            drawOps.Add((lastPoint.Value, currentPoint, lineColor, depth));
                        }

                        lastPoint = currentPoint;
                        lastDensity = density;
                        lastIsBoundary = isBoundary;
                    }
                }
            });
        }

        // OPTIMIZED: Process Z-direction scan
        private void ScanZDirection(ConcurrentBag<(PointF, PointF, Color, float)> drawOps, int step,
            int viewWidth, int viewHeight, float centerX, float centerY, float centerZ,
            float cosX, float sinX, float cosY, float sinY)
        {
            Parallel.For(0, height / step + 1, yIndex =>
            {
                int y = yIndex * step;
                if (y >= height) return;

                for (int x = 0; x < width; x += step * 2)
                {
                    if (x >= width) continue;

                    PointF? lastPoint = null;
                    float lastDensity = 0;
                    bool lastIsBoundary = false;

                    for (int z = 0; z < depth; z += step)
                    {
                        if (z >= depth) continue;

                        float density = densityVolume[x, y, z];
                        if (density <= 0)
                        {
                            lastPoint = null;
                            continue;
                        }

                        // Check if this is a boundary cell
                        bool isBoundary = IsBoundaryVoxel(x, y, z);

                        // For interior cells, use larger steps
                        if (!isBoundary && !renderFullVolume && !lastIsBoundary && z % (step * 2) != 0)
                            continue;

                        PointF currentPoint = ProjectToScreen(x, y, z, viewWidth, viewHeight);

                        if (lastPoint.HasValue)
                        {
                            // Calculate depth for sorting
                            float midX = x - centerX;
                            float midY = y - centerY;
                            float midZ = (z + (z - step)) / 2 - centerZ;

                            float rotX = midX * cosY + midZ * sinY;
                            float rotZ = -midX * sinY + midZ * cosY;
                            float depth = midY * sinX + rotZ * cosX;

                            float avgDensity = (density + lastDensity) / 2;
                            Color lineColor = GetColorForDensity(avgDensity);

                            drawOps.Add((lastPoint.Value, currentPoint, lineColor, depth));
                        }

                        lastPoint = currentPoint;
                        lastDensity = density;
                        lastIsBoundary = isBoundary;
                    }
                }
            });
        }

        // OPTIMIZED: Add diagonal connections for better curved surfaces
        private void AddDiagonalConnections(ConcurrentBag<(PointF, PointF, Color, float)> drawOps, int step,
            int viewWidth, int viewHeight, float centerX, float centerY, float centerZ,
            float cosX, float sinX, float cosY, float sinY)
        {
            // Using parallel processing
            Parallel.For(step, depth - step, z =>
            {
                for (int y = step; y < height - step; y += step * 2)
                {
                    for (int x = step; x < width - step; x += step * 2)
                    {
                        if (x >= width || y >= height || z >= depth) continue;

                        float density = densityVolume[x, y, z];
                        if (density <= 0) continue;

                        // Only add diagonals on boundary cells
                        if (!IsBoundaryVoxel(x, y, z)) continue;

                        PointF p0 = ProjectToScreen(x, y, z, viewWidth, viewHeight);

                        // XY diagonal - only if all corners have density
                        if (x + step < width && y + step < height &&
                            densityVolume[x + step, y + step, z] > 0 &&
                            densityVolume[x + step, y, z] > 0 &&
                            densityVolume[x, y + step, z] > 0)
                        {
                            PointF p1 = ProjectToScreen(x + step, y + step, z, viewWidth, viewHeight);

                            // Calculate depth
                            float midX = x + step / 2 - centerX;
                            float midY = y + step / 2 - centerY;
                            float midZ = z - centerZ;

                            float rotX = midX * cosY + midZ * sinY;
                            float rotZ = -midX * sinY + midZ * cosY;
                            float depth = midY * sinX + rotZ * cosX;

                            Color lineColor = GetColorForDensity(density);
                            drawOps.Add((p0, p1, lineColor, depth));
                        }

                        // XZ diagonal - only if all corners have density
                        if (x + step < width && z + step < depth &&
                            densityVolume[x + step, y, z + step] > 0 &&
                            densityVolume[x + step, y, z] > 0 &&
                            densityVolume[x, y, z + step] > 0)
                        {
                            PointF p1 = ProjectToScreen(x + step, y, z + step, viewWidth, viewHeight);

                            // Calculate depth
                            float midX = x + step / 2 - centerX;
                            float midY = y - centerY;
                            float midZ = z + step / 2 - centerZ;

                            float rotX = midX * cosY + midZ * sinY;
                            float rotZ = -midX * sinY + midZ * cosY;
                            float depth = midY * sinX + rotZ * cosX;

                            Color lineColor = GetColorForDensity(density);
                            drawOps.Add((p0, p1, lineColor, depth));
                        }

                        // YZ diagonal - only if all corners have density
                        if (y + step < height && z + step < depth &&
                            densityVolume[x, y + step, z + step] > 0 &&
                            densityVolume[x, y + step, z] > 0 &&
                            densityVolume[x, y, z + step] > 0)
                        {
                            PointF p1 = ProjectToScreen(x, y + step, z + step, viewWidth, viewHeight);

                            // Calculate depth
                            float midX = x - centerX;
                            float midY = y + step / 2 - centerY;
                            float midZ = z + step / 2 - centerZ;

                            float rotX = midX * cosY + midZ * sinY;
                            float rotZ = -midX * sinY + midZ * cosY;
                            float depth = midY * sinX + rotZ * cosX;

                            Color lineColor = GetColorForDensity(density);
                            drawOps.Add((p0, p1, lineColor, depth));
                        }
                    }
                }
            });
        }

        // Draw density colorbar
        private void DrawDensityColorbar(Graphics g, int viewWidth, int viewHeight)
        {
            int barWidth = 20;
            int barHeight = 150;
            int barX = viewWidth - barWidth - 20;
            int barY = 20;

            // Draw gradient rectangle
            Rectangle barRect = new Rectangle(barX, barY, barWidth, barHeight);

            using (LinearGradientBrush gradientBrush = new LinearGradientBrush(
                barRect, Color.Blue, Color.LightCoral, LinearGradientMode.Vertical))
            {
                // Create smooth gradient
                ColorBlend colorBlend = new ColorBlend(3);
                colorBlend.Colors = new Color[] { Color.Blue, Color.Green, Color.LightCoral };
                colorBlend.Positions = new float[] { 0.0f, 0.5f, 1.0f };
                gradientBrush.InterpolationColors = colorBlend;

                // Draw gradient bar
                g.FillRectangle(gradientBrush, barRect);
                g.DrawRectangle(Pens.White, barRect);
            }

            // Draw labels
            using (Font labelFont = new Font("Arial", 8))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            using (StringFormat rightAlign = new StringFormat() { Alignment = StringAlignment.Far })
            {
                // Max density
                g.DrawString($"{maxDensity:F0}", labelFont, textBrush,
                    barX - 5, barY, rightAlign);

                // Middle density
                g.DrawString($"{(minDensity + maxDensity) / 2:F0}", labelFont, textBrush,
                    barX - 5, barY + barHeight / 2, rightAlign);

                // Min density
                g.DrawString($"{minDensity:F0}", labelFont, textBrush,
                    barX - 5, barY + barHeight, rightAlign);

                // Density unit
                g.DrawString("kg/m³", labelFont, textBrush,
                    barX + barWidth / 2, barY + barHeight + 10);
            }
        }

        // Always draw bounding box for reference
        private void DrawBoundingBox(Graphics g, int viewWidth, int viewHeight)
        {
            // Get corners of the volume
            PointF[] corners = new PointF[8];

            corners[0] = ProjectToScreen(0, 0, 0, viewWidth, viewHeight);
            corners[1] = ProjectToScreen(width, 0, 0, viewWidth, viewHeight);
            corners[2] = ProjectToScreen(0, height, 0, viewWidth, viewHeight);
            corners[3] = ProjectToScreen(width, height, 0, viewWidth, viewHeight);
            corners[4] = ProjectToScreen(0, 0, depth, viewWidth, viewHeight);
            corners[5] = ProjectToScreen(width, 0, depth, viewWidth, viewHeight);
            corners[6] = ProjectToScreen(0, height, depth, viewWidth, viewHeight);
            corners[7] = ProjectToScreen(width, height, depth, viewWidth, viewHeight);

            using (Pen boxPen = new Pen(Color.DarkGray, 1))
            {
                // Draw bottom face
                g.DrawLine(boxPen, corners[0], corners[1]);
                g.DrawLine(boxPen, corners[1], corners[3]);
                g.DrawLine(boxPen, corners[3], corners[2]);
                g.DrawLine(boxPen, corners[2], corners[0]);

                // Draw top face
                g.DrawLine(boxPen, corners[4], corners[5]);
                g.DrawLine(boxPen, corners[5], corners[7]);
                g.DrawLine(boxPen, corners[7], corners[6]);
                g.DrawLine(boxPen, corners[6], corners[4]);

                // Draw connecting edges
                g.DrawLine(boxPen, corners[0], corners[4]);
                g.DrawLine(boxPen, corners[1], corners[5]);
                g.DrawLine(boxPen, corners[2], corners[6]);
                g.DrawLine(boxPen, corners[3], corners[7]);
            }
        }
    }
}