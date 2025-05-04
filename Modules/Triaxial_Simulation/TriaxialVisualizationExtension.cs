using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading.Tasks;

namespace CTS
{
    /// <summary>
    /// Extension class for generating triaxial simulation visualizations
    /// showing pressure fields, vectors, and material volume.
    /// </summary>
    public class TriaxialVisualizationExtension
    {
        // Color maps for pressure visualization
        private static readonly Color[] PressureColorMap = new Color[]
        {
            Color.FromArgb(0, 0, 128),      // Dark Blue
            Color.FromArgb(0, 0, 255),      // Blue
            Color.FromArgb(0, 128, 255),    // Light Blue
            Color.FromArgb(0, 255, 255),    // Cyan
            Color.FromArgb(0, 255, 0),      // Green
            Color.FromArgb(255, 255, 0),    // Yellow
            Color.FromArgb(255, 128, 0),    // Orange
            Color.FromArgb(255, 0, 0),      // Red
            Color.FromArgb(128, 0, 0)       // Dark Red
        };

        // Color map for density visualization
        private static readonly Color[] DensityColorMap = new Color[]
        {
            Color.FromArgb(100, 100, 255),  // Light Blue (low density)
            Color.FromArgb(100, 180, 255),  // Medium Blue
            Color.FromArgb(100, 255, 255),  // Cyan
            Color.FromArgb(100, 255, 180),  // Teal
            Color.FromArgb(100, 255, 100),  // Green
            Color.FromArgb(180, 255, 100),  // Lime
            Color.FromArgb(255, 255, 100),  // Yellow
            Color.FromArgb(255, 180, 100),  // Orange
            Color.FromArgb(255, 100, 100)   // Red (high density)
        };

        private static readonly Color[] GradientColorMap = new Color[]
        {
            Color.FromArgb(32, 32, 255),    // Light Blue
            Color.FromArgb(128, 128, 255),  // Medium Blue
            Color.FromArgb(192, 192, 255),  // Pale Blue
            Color.FromArgb(255, 255, 255),  // White (neutral)
            Color.FromArgb(255, 192, 192),  // Pale Red
            Color.FromArgb(255, 128, 128),  // Medium Red
            Color.FromArgb(255, 32, 32)     // Bright Red
        };

        // Cached visualization images
        private Bitmap _volumeImage;
        private Bitmap _confiningPressureImage;
        private Bitmap _pressureGradientImage;
        private float _lastRotationX, _lastRotationY, _lastZoom;
        private PointF _lastPan;
        private bool _visualizationDirty = true;

        // Reference data
        private float[,,] _densityVolume;
        private byte[,,] _volumeLabels;
        private byte _materialID;
        private int _width, _height, _depth;
        private float _pixelSize;
        private double _confiningPressure;
        private double _axialPressure;
        private StressAxis _pressureAxis;

        // Statistics for density coloring
        private float _minDensity = float.MaxValue;
        private float _maxDensity = float.MinValue;
        private float _avgDensity = 0;
        private bool _densityStatsCalculated = false;

        /// <summary>
        /// Initializes a new instance of the TriaxialVisualizationExtension class.
        /// </summary>
        public TriaxialVisualizationExtension(
            int width, int height, int depth, float pixelSize,
            byte[,,] volumeLabels, float[,,] densityVolume, byte materialID)
        {
            _width = width;
            _height = height;
            _depth = depth;
            _pixelSize = pixelSize;
            _volumeLabels = volumeLabels;
            _densityVolume = densityVolume;
            _materialID = materialID;
            _confiningPressure = 0.0;
            _axialPressure = 0.0;
            _pressureAxis = StressAxis.Z;
        }

        /// <summary>
        /// Set the pressure parameters for visualization.
        /// </summary>
        public void SetPressureParameters(double confiningPressure, double axialPressure, StressAxis axis)
        {
            if (_confiningPressure != confiningPressure ||
                _axialPressure != axialPressure ||
                _pressureAxis != axis)
            {
                _confiningPressure = confiningPressure;
                _axialPressure = axialPressure;
                _pressureAxis = axis;
                _visualizationDirty = true;
            }
        }

        /// <summary>
        /// Set the view transformation parameters.
        /// </summary>
        public void SetViewTransformation(float rotationX, float rotationY, float zoom, PointF pan)
        {
            if (_lastRotationX != rotationX ||
                _lastRotationY != rotationY ||
                _lastZoom != zoom ||
                _lastPan != pan)
            {
                _lastRotationX = rotationX;
                _lastRotationY = rotationY;
                _lastZoom = zoom;
                _lastPan = pan;
                _visualizationDirty = true;
            }
        }

        /// <summary>
        /// Renders the triaxial visualization to the given graphics context.
        /// </summary>
        public void Render(Graphics g, int width, int height)
        {
            // Ensure visualizations are created with appropriate dimensions
            EnsureVisualizationsCreated(width, height);

            // Calculate optimal aspect ratio for the visualization panels
            // We'll determine if we should use horizontal or vertical layout based on the container dimensions
            bool useHorizontalLayout = width > height * 1.2;

            int padding = 10;
            int headerHeight = 24; // Height of the label header
            int footerHeight = 20; // Height of the footer text
            int legendHeight = 30; // Height of the color legend

            int availableWidth = width - (useHorizontalLayout ? padding * 4 : padding * 2);
            int availableHeight = height - padding * 2 - legendHeight;

            int imageWidth, imageHeight, xOffset, yOffset;

            if (useHorizontalLayout)
            {
                // Horizontal layout (side by side)
                imageWidth = availableWidth / 3;
                imageHeight = availableHeight - headerHeight - footerHeight;
                xOffset = imageWidth + padding;
                yOffset = 0;
            }
            else
            {
                // Vertical layout (stacked)
                imageWidth = availableWidth;
                imageHeight = (availableHeight - headerHeight * 3 - footerHeight * 3) / 3;
                xOffset = 0;
                yOffset = imageHeight + headerHeight + footerHeight + padding;
            }

            // Draw the volume visualization
            DrawImageWithLabel(g, _volumeImage, padding, padding,
                imageWidth, imageHeight, "Material Volume");

            // Draw the confining pressure visualization
            DrawImageWithLabel(g, _confiningPressureImage,
                useHorizontalLayout ? padding + xOffset : padding,
                useHorizontalLayout ? padding : padding + yOffset,
                imageWidth, imageHeight, "Confining Pressure");

            // Draw the pressure gradient visualization
            DrawImageWithLabel(g, _pressureGradientImage,
                useHorizontalLayout ? padding + xOffset * 2 : padding,
                useHorizontalLayout ? padding : padding + yOffset * 2,
                imageWidth, imageHeight, "Pressure Gradient");

            // Draw color legend at the bottom
            int legendY = height - legendHeight - padding;
            DrawPressureLegend(g, padding, legendY, width - padding * 2, legendHeight);
        }

        /// <summary>
        /// Force regeneration of the visualizations.
        /// </summary>
        public void Invalidate()
        {
            _visualizationDirty = true;
            DisposeImages();
        }

        /// <summary>
        /// Clean up resources.
        /// </summary>
        public void Dispose()
        {
            DisposeImages();
        }

        #region Private Helper Methods

        private void DisposeImages()
        {
            _volumeImage?.Dispose();
            _confiningPressureImage?.Dispose();
            _pressureGradientImage?.Dispose();
            _volumeImage = null;
            _confiningPressureImage = null;
            _pressureGradientImage = null;
        }

        private void EnsureVisualizationsCreated(int width, int height)
        {
            if (_visualizationDirty || _volumeImage == null ||
                _confiningPressureImage == null || _pressureGradientImage == null)
            {
                DisposeImages();

                // Calculate dimensions based on aspect ratio
                bool useHorizontalLayout = width > height * 1.2;
                int padding = 10;
                int headerHeight = 24;
                int footerHeight = 20;
                int legendHeight = 30;

                int availableWidth = width - (useHorizontalLayout ? padding * 4 : padding * 2);
                int availableHeight = height - padding * 2 - legendHeight;

                int imageWidth, imageHeight;

                if (useHorizontalLayout)
                {
                    // Horizontal layout (side by side)
                    imageWidth = availableWidth / 3;
                    imageHeight = availableHeight - headerHeight - footerHeight;
                }
                else
                {
                    // Vertical layout (stacked)
                    imageWidth = availableWidth;
                    imageHeight = (availableHeight - headerHeight * 3 - footerHeight * 3) / 3;
                }

                // Ensure minimum dimensions
                imageWidth = Math.Max(imageWidth, 50);
                imageHeight = Math.Max(imageHeight, 50);

                // Calculate density statistics for coloring
                if (!_densityStatsCalculated)
                {
                    CalculateDensityStatistics();
                }

                // Create all visualizations
                _volumeImage = CreateVolumeVisualization(imageWidth, imageHeight);
                _confiningPressureImage = CreateConfiningPressureVisualization(imageWidth, imageHeight);
                _pressureGradientImage = CreatePressureGradientVisualization(imageWidth, imageHeight);

                _visualizationDirty = false;
            }
        }

        private void CalculateDensityStatistics()
        {
            // Calculate min, max, and average density values for the selected material
            _minDensity = float.MaxValue;
            _maxDensity = float.MinValue;
            float totalDensity = 0;
            int count = 0;

            for (int z = 0; z < _depth; z++)
            {
                for (int y = 0; y < _height; y++)
                {
                    for (int x = 0; x < _width; x++)
                    {
                        if (_volumeLabels[x, y, z] == _materialID)
                        {
                            float density = _densityVolume[x, y, z];
                            _minDensity = Math.Min(_minDensity, density);
                            _maxDensity = Math.Max(_maxDensity, density);
                            totalDensity += density;
                            count++;
                        }
                    }
                }
            }

            if (count > 0)
            {
                _avgDensity = totalDensity / count;
            }
            else
            {
                _minDensity = 0;
                _maxDensity = 3000;
                _avgDensity = 1500;
            }

            _densityStatsCalculated = true;
        }

        private void DrawImageWithLabel(Graphics g, Image image, int x, int y, int width, int height, string label)
        {
            if (image == null) return;

            // Draw the image
            g.DrawImage(image, x, y, width, height);

            // Draw label background
            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
            {
                g.FillRectangle(bgBrush, x, y, width, 24);
            }

            // Draw the label
            using (Font font = new Font("Segoe UI", 10, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                StringFormat sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString(label, font, textBrush, new Rectangle(x, y, width, 24), sf);
            }
        }

        private Bitmap CreateVolumeVisualization(int width, int height)
        {
            Bitmap bmp = new Bitmap(Math.Max(1, width), Math.Max(1, height));
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(20, 20, 20));

                // Create a render using 3D projection
                Matrix3DProjection projector = new Matrix3DProjection(
                    _width, _height, _depth, _lastRotationX, _lastRotationY, _lastZoom, _lastPan);

                // Set up rendering parameters
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                // Draw volume (material only)
                DrawVolumeWithMaterial(g, projector, width, height);

                // Add title
                string volumeInfo = $"Material: {_materialID} - Density: {_minDensity:F0} to {_maxDensity:F0} kg/m³";
                using (Font font = new Font("Segoe UI", 8))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    g.DrawString(volumeInfo, font, brush, 10, height - 20);
                }
            }

            return bmp;
        }

        private Bitmap CreateConfiningPressureVisualization(int width, int height)
        {
            Bitmap bmp = new Bitmap(Math.Max(1, width), Math.Max(1, height));
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(20, 20, 20));

                // Create a render using 3D projection
                Matrix3DProjection projector = new Matrix3DProjection(
                    _width, _height, _depth, _lastRotationX, _lastRotationY, _lastZoom, _lastPan);

                // Set up rendering parameters
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                // Draw volume with confining pressure vectors
                DrawVolumeWithConfiningPressure(g, projector, width, height);

                // Add pressure info
                string pressureInfo = $"Confining Pressure: {_confiningPressure:F1} MPa";
                using (Font font = new Font("Segoe UI", 8))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    g.DrawString(pressureInfo, font, brush, 10, height - 20);
                }
            }

            return bmp;
        }

        private Bitmap CreatePressureGradientVisualization(int width, int height)
        {
            Bitmap bmp = new Bitmap(Math.Max(1, width), Math.Max(1, height));
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(20, 20, 20));

                // Create a render using 3D projection
                Matrix3DProjection projector = new Matrix3DProjection(
                    _width, _height, _depth, _lastRotationX, _lastRotationY, _lastZoom, _lastPan);

                // Set up rendering parameters
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                // Draw volume with axial pressure gradient
                DrawVolumeWithPressureGradient(g, projector, width, height);

                // Add pressure info
                string pressureInfo = $"Axial Pressure: {_axialPressure:F1} MPa (Axis: {_pressureAxis})";
                using (Font font = new Font("Segoe UI", 8))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    g.DrawString(pressureInfo, font, brush, 10, height - 20);
                }
            }

            return bmp;
        }

        private void DrawVolumeWithMaterial(Graphics g, Matrix3DProjection projector, int width, int height)
        {
            // Create bounds to only render the actual material volume
            int minX = _width, minY = _height, minZ = _depth;
            int maxX = 0, maxY = 0, maxZ = 0;

            // Find the bounds of the material
            for (int z = 0; z < _depth; z++)
            {
                for (int y = 0; y < _height; y++)
                {
                    for (int x = 0; x < _width; x++)
                    {
                        if (_volumeLabels[x, y, z] == _materialID)
                        {
                            minX = Math.Min(minX, x);
                            minY = Math.Min(minY, y);
                            minZ = Math.Min(minZ, z);
                            maxX = Math.Max(maxX, x);
                            maxY = Math.Max(maxY, y);
                            maxZ = Math.Max(maxZ, z);
                        }
                    }
                }
            }

            // Ensure we have valid bounds (material exists)
            if (minX > maxX || minY > maxY || minZ > maxZ)
                return;

            // Downsample factor to avoid too many points (adjust as needed)
            int downsample = Math.Max(1, Math.Min(Math.Min(_width, _height), _depth) / 40);

            // Draw material points with density-based coloring
            for (int z = minZ; z <= maxZ; z += downsample)
            {
                for (int y = minY; y <= maxY; y += downsample)
                {
                    for (int x = minX; x <= maxX; x += downsample)
                    {
                        if (_volumeLabels[x, y, z] == _materialID)
                        {
                            // Get density value and map to point size and color
                            float density = _densityVolume[x, y, z];

                            // Normalize density for coloring (range 0-1)
                            float normalizedDensity = 0.5f; // Default middle value
                            if (_maxDensity > _minDensity)
                            {
                                normalizedDensity = (density - _minDensity) / (_maxDensity - _minDensity);
                            }

                            // Map to point size (smaller points for low density, larger for high density)
                            int pointSize = (int)(2 + normalizedDensity * 6);

                            // Map to color from density color map
                            Color pointColor = GetDensityColor(normalizedDensity);

                            // Project point to screen coordinates
                            PointF screenPt = projector.Project(x, y, z, width, height);

                            // Draw point with color based on density
                            using (SolidBrush pointBrush = new SolidBrush(pointColor))
                            {
                                g.FillEllipse(pointBrush,
                                    screenPt.X - pointSize / 2,
                                    screenPt.Y - pointSize / 2,
                                    pointSize, pointSize);
                            }
                        }
                    }
                }
            }

            // Draw volume bounding box
            using (Pen boundingBoxPen = new Pen(Color.FromArgb(100, 255, 255, 255)))
            {
                // Define the corners of the bounding box
                PointF[] corners = new PointF[8];
                corners[0] = projector.Project(minX, minY, minZ, width, height);
                corners[1] = projector.Project(maxX, minY, minZ, width, height);
                corners[2] = projector.Project(maxX, maxY, minZ, width, height);
                corners[3] = projector.Project(minX, maxY, minZ, width, height);
                corners[4] = projector.Project(minX, minY, maxZ, width, height);
                corners[5] = projector.Project(maxX, minY, maxZ, width, height);
                corners[6] = projector.Project(maxX, maxY, maxZ, width, height);
                corners[7] = projector.Project(minX, maxY, maxZ, width, height);

                // Draw the edges of the box
                // Bottom face
                g.DrawLine(boundingBoxPen, corners[0], corners[1]);
                g.DrawLine(boundingBoxPen, corners[1], corners[2]);
                g.DrawLine(boundingBoxPen, corners[2], corners[3]);
                g.DrawLine(boundingBoxPen, corners[3], corners[0]);

                // Top face
                g.DrawLine(boundingBoxPen, corners[4], corners[5]);
                g.DrawLine(boundingBoxPen, corners[5], corners[6]);
                g.DrawLine(boundingBoxPen, corners[6], corners[7]);
                g.DrawLine(boundingBoxPen, corners[7], corners[4]);

                // Connecting edges
                g.DrawLine(boundingBoxPen, corners[0], corners[4]);
                g.DrawLine(boundingBoxPen, corners[1], corners[5]);
                g.DrawLine(boundingBoxPen, corners[2], corners[6]);
                g.DrawLine(boundingBoxPen, corners[3], corners[7]);
            }
        }

        private void DrawVolumeWithConfiningPressure(Graphics g, Matrix3DProjection projector, int width, int height)
        {
            // First draw the basic material volume
            DrawVolumeWithMaterial(g, projector, width, height);

            // Create bounds to only render the actual material volume
            int minX = _width, minY = _height, minZ = _depth;
            int maxX = 0, maxY = 0, maxZ = 0;

            // Find the bounds of the material
            for (int z = 0; z < _depth; z++)
            {
                for (int y = 0; y < _height; y++)
                {
                    for (int x = 0; x < _width; x++)
                    {
                        if (_volumeLabels[x, y, z] == _materialID)
                        {
                            minX = Math.Min(minX, x);
                            minY = Math.Min(minY, y);
                            minZ = Math.Min(minZ, z);
                            maxX = Math.Max(maxX, x);
                            maxY = Math.Max(maxY, y);
                            maxZ = Math.Max(maxZ, z);
                        }
                    }
                }
            }

            // Ensure we have valid bounds (material exists)
            if (minX > maxX || minY > maxY || minZ > maxZ)
                return;

            // Calculate reduced sampling to show vectors (more sparely distributed)
            int vectorSample = Math.Max(3, Math.Min(Math.Min(_width, _height), _depth) / 12);

            // Get pressure color index
            int pressureColorIndex = GetPressureColorIndex(_confiningPressure, 0, 200);

            // Draw pressure vectors at the faces of the volume
            using (Pen vectorPen = new Pen(PressureColorMap[pressureColorIndex], 1.5f))
            {
                // Scale factor for vector length
                float vectorScale = 10.0f * (float)Math.Sqrt(_confiningPressure / 100.0);

                // Draw X-face vectors
                DrawPressureVectorsOnFace(g, projector, width, height, minX, minY, minZ, maxX, maxY, maxZ,
                    0, vectorSample, vectorSample, -vectorScale, 0, 0, vectorPen, true);
                DrawPressureVectorsOnFace(g, projector, width, height, minX, minY, minZ, maxX, maxY, maxZ,
                    1, vectorSample, vectorSample, vectorScale, 0, 0, vectorPen, true);

                // Draw Y-face vectors
                DrawPressureVectorsOnFace(g, projector, width, height, minX, minY, minZ, maxX, maxY, maxZ,
                    vectorSample, 0, vectorSample, 0, -vectorScale, 0, vectorPen, true);
                DrawPressureVectorsOnFace(g, projector, width, height, minX, minY, minZ, maxX, maxY, maxZ,
                    vectorSample, 1, vectorSample, 0, vectorScale, 0, vectorPen, true);

                // Draw Z-face vectors
                DrawPressureVectorsOnFace(g, projector, width, height, minX, minY, minZ, maxX, maxY, maxZ,
                    vectorSample, vectorSample, 0, 0, 0, -vectorScale, vectorPen, true);
                DrawPressureVectorsOnFace(g, projector, width, height, minX, minY, minZ, maxX, maxY, maxZ,
                    vectorSample, vectorSample, 1, 0, 0, vectorScale, vectorPen, true);
            }
        }

        private void DrawVolumeWithPressureGradient(Graphics g, Matrix3DProjection projector, int width, int height)
        {
            // First draw the basic material volume
            DrawVolumeWithMaterial(g, projector, width, height);

            // Create bounds to only render the actual material volume
            int minX = _width, minY = _height, minZ = _depth;
            int maxX = 0, maxY = 0, maxZ = 0;

            // Find the bounds of the material
            for (int z = 0; z < _depth; z++)
            {
                for (int y = 0; y < _height; y++)
                {
                    for (int x = 0; x < _width; x++)
                    {
                        if (_volumeLabels[x, y, z] == _materialID)
                        {
                            minX = Math.Min(minX, x);
                            minY = Math.Min(minY, y);
                            minZ = Math.Min(minZ, z);
                            maxX = Math.Max(maxX, x);
                            maxY = Math.Max(maxY, y);
                            maxZ = Math.Max(maxZ, z);
                        }
                    }
                }
            }

            // Ensure we have valid bounds (material exists)
            if (minX > maxX || minY > maxY || minZ > maxZ)
                return;

            // Calculate gradient intensity
            double gradientMagnitude = _axialPressure - _confiningPressure;
            if (gradientMagnitude <= 0)
                return; // No gradient to show

            // Calculate reduced sampling for gradient vectors
            int vectorSample = Math.Max(4, Math.Min(Math.Min(_width, _height), _depth) / 10);

            // Calculate midpoints to place pressure gradient along the right axis
            int midX = (minX + maxX) / 2;
            int midY = (minY + maxY) / 2;
            int midZ = (minZ + maxZ) / 2;

            // Get pressure color index for gradient
            int pressureColorIndex = GetPressureColorIndex(_axialPressure, 0, 200);

            // Scale factor for vector length
            float vectorScale = 15.0f * (float)Math.Sqrt(gradientMagnitude / 100.0);

            // Draw pressure gradient vectors
            Pen gradientPen = new Pen(PressureColorMap[pressureColorIndex], 2.0f);
            gradientPen.CustomEndCap = new AdjustableArrowCap(4, 5);

            switch (_pressureAxis)
            {
                case StressAxis.X:
                    // Draw gradient vectors along X-axis
                    for (int z = minZ; z <= maxZ; z += vectorSample)
                    {
                        for (int y = minY; y <= maxY; y += vectorSample)
                        {
                            // Check if this position is within the material
                            if (IsPointInMaterial(midX, y, z))
                            {
                                PointF start = projector.Project(minX, y, z, width, height);
                                // Vectors point from min to max along X
                                float dx = vectorScale;
                                PointF end = projector.Project(minX + dx, y, z, width, height);

                                g.DrawLine(gradientPen, start, end);
                            }
                        }
                    }
                    break;

                case StressAxis.Y:
                    // Draw gradient vectors along Y-axis
                    for (int z = minZ; z <= maxZ; z += vectorSample)
                    {
                        for (int x = minX; x <= maxX; x += vectorSample)
                        {
                            // Check if this position is within the material
                            if (IsPointInMaterial(x, midY, z))
                            {
                                PointF start = projector.Project(x, minY, z, width, height);
                                // Vectors point from min to max along Y
                                float dy = vectorScale;
                                PointF end = projector.Project(x, minY + dy, z, width, height);

                                g.DrawLine(gradientPen, start, end);
                            }
                        }
                    }
                    break;

                case StressAxis.Z:
                default:
                    // Draw gradient vectors along Z-axis
                    for (int y = minY; y <= maxY; y += vectorSample)
                    {
                        for (int x = minX; x <= maxX; x += vectorSample)
                        {
                            // Check if this position is within the material
                            if (IsPointInMaterial(x, y, midZ))
                            {
                                PointF start = projector.Project(x, y, minZ, width, height);
                                // Vectors point from min to max along Z
                                float dz = vectorScale;
                                PointF end = projector.Project(x, y, minZ + dz, width, height);

                                g.DrawLine(gradientPen, start, end);
                            }
                        }
                    }
                    break;
            }

            gradientPen.Dispose();
        }

        private void DrawPressureVectorsOnFace(
            Graphics g, Matrix3DProjection projector, int width, int height,
            int minX, int minY, int minZ, int maxX, int maxY, int maxZ,
            int xSample, int ySample, int zSample,
            float vx, float vy, float vz,
            Pen vectorPen, bool useArrowheads)
        {
            if (useArrowheads)
            {
                vectorPen.CustomEndCap = new AdjustableArrowCap(3, 4);
            }

            // Track which points were drawn to avoid duplicates
            HashSet<Point3D> drawnPoints = new HashSet<Point3D>();

            // X face (xVal is either minX or maxX)
            if (xSample == 0 || xSample == 1)
            {
                int xVal = xSample == 0 ? minX : maxX;
                for (int z = minZ; z <= maxZ; z += zSample)
                {
                    for (int y = minY; y <= maxY; y += ySample)
                    {
                        if (IsPointInMaterial(xVal, y, z))
                        {
                            Point3D point = new Point3D(xVal, y, z);
                            if (!drawnPoints.Contains(point))
                            {
                                PointF start = projector.Project(xVal, y, z, width, height);
                                PointF end = projector.Project(xVal + vx, y + vy, z + vz, width, height);
                                g.DrawLine(vectorPen, start, end);
                                drawnPoints.Add(point);
                            }
                        }
                    }
                }
            }

            // Y face (yVal is either minY or maxY)
            if (ySample == 0 || ySample == 1)
            {
                int yVal = ySample == 0 ? minY : maxY;
                for (int z = minZ; z <= maxZ; z += zSample)
                {
                    for (int x = minX; x <= maxX; x += xSample)
                    {
                        if (IsPointInMaterial(x, yVal, z))
                        {
                            Point3D point = new Point3D(x, yVal, z);
                            if (!drawnPoints.Contains(point))
                            {
                                PointF start = projector.Project(x, yVal, z, width, height);
                                PointF end = projector.Project(x + vx, yVal + vy, z + vz, width, height);
                                g.DrawLine(vectorPen, start, end);
                                drawnPoints.Add(point);
                            }
                        }
                    }
                }
            }

            // Z face (zVal is either minZ or maxZ)
            if (zSample == 0 || zSample == 1)
            {
                int zVal = zSample == 0 ? minZ : maxZ;
                for (int y = minY; y <= maxY; y += ySample)
                {
                    for (int x = minX; x <= maxX; x += xSample)
                    {
                        if (IsPointInMaterial(x, y, zVal))
                        {
                            Point3D point = new Point3D(x, y, zVal);
                            if (!drawnPoints.Contains(point))
                            {
                                PointF start = projector.Project(x, y, zVal, width, height);
                                PointF end = projector.Project(x + vx, y + vy, zVal + vz, width, height);
                                g.DrawLine(vectorPen, start, end);
                                drawnPoints.Add(point);
                            }
                        }
                    }
                }
            }
        }

        private void DrawPressureLegend(Graphics g, int x, int y, int width, int height)
        {
            // Draw two color legends side by side
            int halfWidth = width / 2;
            int padding = 5;

            // 1. Draw pressure color legend
            using (Font font = new Font("Segoe UI", 8))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                g.DrawString("Pressure (MPa):", font, textBrush, x, y-15);
            }

            int legendY = y;
            int segments = PressureColorMap.Length;
            int segmentWidth = (halfWidth - padding * 2) / segments;

            // Draw gradient bars
            for (int i = 0; i < segments; i++)
            {
                using (SolidBrush brush = new SolidBrush(PressureColorMap[i]))
                {
                    g.FillRectangle(brush, x + padding + i * segmentWidth, legendY, segmentWidth, height - 20);
                }
            }

            // Draw border
            using (Pen pen = new Pen(Color.White))
            {
                g.DrawRectangle(pen, x + padding, legendY, segmentWidth * segments, height - 20);
            }

            // Draw min and max pressure values
            using (Font font = new Font("Segoe UI", 8))
            using (SolidBrush brush = new SolidBrush(Color.White))
            {
                g.DrawString("0", font, brush, x + padding, legendY + height - 15);
                g.DrawString("200", font, brush, x + padding + segmentWidth * segments - 20, legendY + height - 15);
            }

            // 2. Draw density color legend
            using (Font font = new Font("Segoe UI", 8))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                g.DrawString("Density (kg/m³):", font, textBrush, x + halfWidth, y-15);
            }

            segments = DensityColorMap.Length;
            segmentWidth = (halfWidth - padding * 2) / segments;

            // Draw gradient bars
            for (int i = 0; i < segments; i++)
            {
                using (SolidBrush brush = new SolidBrush(DensityColorMap[i]))
                {
                    g.FillRectangle(brush, x + halfWidth + padding + i * segmentWidth, legendY, segmentWidth, height - 20);
                }
            }

            // Draw border
            using (Pen pen = new Pen(Color.White))
            {
                g.DrawRectangle(pen, x + halfWidth + padding, legendY, segmentWidth * segments, height - 20);
            }

            // Draw min and max density values
            using (Font font = new Font("Segoe UI", 8))
            using (SolidBrush brush = new SolidBrush(Color.White))
            {
                g.DrawString($"{_minDensity:F0}", font, brush, x + halfWidth + padding, legendY + height - 15);
                g.DrawString($"{_maxDensity:F0}", font, brush,
                    x + halfWidth + padding + segmentWidth * segments - 30, legendY + height - 15);
            }
        }

        private int GetPressureColorIndex(double pressure, double minPressure, double maxPressure)
        {
            // Normalize pressure to 0-1 range
            double normalizedPressure = Math.Min(1.0, Math.Max(0.0,
                (pressure - minPressure) / (maxPressure - minPressure)));

            // Map to color index
            int colorIndex = (int)(normalizedPressure * (PressureColorMap.Length - 1));
            return colorIndex;
        }

        private Color GetDensityColor(float normalizedDensity)
        {
            // Ensure normalized density is in range 0-1
            normalizedDensity = Math.Max(0f, Math.Min(1f, normalizedDensity));

            // Find the corresponding color in the density color map
            float indexFloat = normalizedDensity * (DensityColorMap.Length - 1);
            int index = (int)indexFloat;

            // If exactly at a color index, return that color
            if (index == indexFloat)
                return DensityColorMap[index];

            // Otherwise, interpolate between colors
            if (index >= DensityColorMap.Length - 1)
                return DensityColorMap[DensityColorMap.Length - 1];

            Color c1 = DensityColorMap[index];
            Color c2 = DensityColorMap[index + 1];
            float t = indexFloat - index; // Fractional part

            // Linear interpolation between colors
            int r = (int)(c1.R * (1 - t) + c2.R * t);
            int g = (int)(c1.G * (1 - t) + c2.G * t);
            int b = (int)(c1.B * (1 - t) + c2.B * t);
            int a = (int)(c1.A * (1 - t) + c2.A * t);

            return Color.FromArgb(a, r, g, b);
        }

        private bool IsPointInMaterial(int x, int y, int z)
        {
            if (x < 0 || x >= _width || y < 0 || y >= _height || z < 0 || z >= _depth)
                return false;

            return _volumeLabels[x, y, z] == _materialID;
        }

        #endregion
        /// <summary>
        /// Simple 3D point structure for tracking.
        /// </summary>
        private struct Point3D : IEquatable<Point3D>
        {
            public int X { get; }
            public int Y { get; }
            public int Z { get; }

            public Point3D(int x, int y, int z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public bool Equals(Point3D other)
            {
                return X == other.X && Y == other.Y && Z == other.Z;
            }

            public override bool Equals(object obj)
            {
                if (obj is Point3D point)
                    return Equals(point);
                return false;
            }

            public override int GetHashCode()
            {
                return (X * 73856093) ^ (Y * 19349663) ^ (Z * 83492791);
            }
        }
    }
}