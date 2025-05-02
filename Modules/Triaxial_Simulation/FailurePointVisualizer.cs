using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CTSegmenter
{
    /// <summary>
    /// Extension class to create 3D visualizations of failure points
    /// </summary>
    public class FailurePointVisualizer
    {
        // Visual parameters
        private readonly Color[] _stressColorMap = new Color[]
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

        private readonly Color[] _strainColorMap = new Color[]
        {
            Color.FromArgb(0, 0, 64),       // Very Dark Blue
            Color.FromArgb(0, 0, 128),      // Dark Blue
            Color.FromArgb(0, 0, 255),      // Blue
            Color.FromArgb(0, 128, 255),    // Light Blue
            Color.FromArgb(0, 255, 255),    // Cyan
            Color.FromArgb(0, 255, 128),    // Turquoise
            Color.FromArgb(0, 255, 0),      // Green
            Color.FromArgb(128, 255, 0),    // Lime
            Color.FromArgb(255, 255, 0)     // Yellow
        };

        private readonly Color[] _damageColorMap = new Color[]
        {
            Color.FromArgb(0, 255, 0),      // Green (no damage)
            Color.FromArgb(128, 255, 0),    // Lime
            Color.FromArgb(255, 255, 0),    // Yellow
            Color.FromArgb(255, 192, 0),    // Gold
            Color.FromArgb(255, 128, 0),    // Orange
            Color.FromArgb(255, 64, 0),     // Bright Red
            Color.FromArgb(255, 0, 0),      // Red
            Color.FromArgb(192, 0, 0),      // Dark Red
            Color.FromArgb(128, 0, 0)       // Very Dark Red (high damage)
        };

        // Rendering parameters
        private float _rotationX = 30;
        private float _rotationY = 30;
        private float _zoom = 1.0f;
        private PointF _pan = new PointF(0, 0);

        // Volume data
        private int _width;
        private int _height;
        private int _depth;
        private byte _materialId;
        private double[,,] _damageData;
        private ILabelVolumeData _labelData;
        private float[,,] _stressData;
        private float[,,] _strainData;

        // Failure point
        private bool _failureDetected;
        private Point3D _failurePoint = new Point3D(0, 0, 0);

        /// <summary>
        /// Simple 3D point structure
        /// </summary>
        public struct Point3D
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
        }

        /// <summary>
        /// Color map modes for visualization
        /// </summary>
        public enum ColorMapMode
        {
            Stress,
            Strain,
            Damage
        }

        /// <summary>
        /// Constructor
        /// </summary>
        public FailurePointVisualizer(int width, int height, int depth, byte materialId)
        {
            _width = width;
            _height = height;
            _depth = depth;
            _materialId = materialId;
        }

        /// <summary>
        /// Set the visualization data
        /// </summary>
        public void SetData(ILabelVolumeData labels, double[,,] damage, float[,,] stress = null, float[,,] strain = null)
        {
            _labelData = labels;
            _damageData = damage;
            _stressData = stress;
            _strainData = strain;
        }

        /// <summary>
        /// Set failure point data
        /// </summary>
        public void SetFailurePoint(bool detected, Point3D failurePoint)
        {
            _failureDetected = detected;
            _failurePoint = failurePoint;
        }

        /// <summary>
        /// Set view parameters
        /// </summary>
        public void SetViewParameters(float rotationX, float rotationY, float zoom, PointF pan)
        {
            _rotationX = rotationX;
            _rotationY = rotationY;
            _zoom = zoom;
            _pan = pan;
        }

        /// <summary>
        /// Create a failure point visualization with orthogonal views
        /// </summary>
        public Bitmap CreateVisualization(int width, int height, ColorMapMode colorMode)
        {
            // Create bitmap
            Bitmap bmp = new Bitmap(Math.Max(1, width), Math.Max(1, height));

            using (Graphics g = Graphics.FromImage(bmp))
            {
                // Setup high quality rendering
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                // Fill background
                g.Clear(Color.FromArgb(20, 20, 20));

                // Divide into four quadrants
                int quadWidth = width / 2;
                int quadHeight = height / 2;

                // Draw orthogonal views
                DrawTopView(g, new Rectangle(0, 0, quadWidth, quadHeight), colorMode);
                DrawFrontView(g, new Rectangle(quadWidth, 0, quadWidth, quadHeight), colorMode);
                DrawSideView(g, new Rectangle(0, quadHeight, quadWidth, quadHeight), colorMode);
                DrawPerspectiveView(g, new Rectangle(quadWidth, quadHeight, quadWidth, quadHeight), colorMode);

                // Draw color legend
                DrawColorLegend(g, width - 150, 20, 130, 200, colorMode);
            }

            return bmp;
        }

        /// <summary>
        /// Draw top view (XY plane)
        /// </summary>
        private void DrawTopView(Graphics g, Rectangle rect, ColorMapMode colorMode)
        {
            // Draw background
            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(40, 40, 40)))
            {
                g.FillRectangle(bgBrush, rect);
            }

            // Draw border
            using (Pen borderPen = new Pen(Color.FromArgb(100, 100, 100)))
            {
                g.DrawRectangle(borderPen, rect);
            }

            // Draw title
            using (Font titleFont = new Font("Segoe UI", 10, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            using (StringFormat sf = new StringFormat() { Alignment = StringAlignment.Center })
            {
                g.DrawString("Top View (XY)", titleFont, textBrush,
                            new Rectangle(rect.X, rect.Y + 5, rect.Width, 20), sf);
            }

            // Render the actual view
            if (_labelData != null && _damageData != null)
            {
                // Calculate slice position (use the middle of the volume, or failure point if detected)
                int sliceZ = _failureDetected ? _failurePoint.Z : _depth / 2;

                // Draw the slice
                DrawSlice(g, rect, SliceDirection.Z, sliceZ, colorMode);

                // Mark the slice position
                using (Font posFont = new Font("Segoe UI", 8))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    g.DrawString($"Z = {sliceZ}", posFont, textBrush, rect.X + 10, rect.Y + 25);
                }
            }
            else
            {
                // Draw placeholder text
                using (Font font = new Font("Segoe UI", 9))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                using (StringFormat sf = new StringFormat() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawString("No data available", font, textBrush, rect, sf);
                }
            }
        }

        /// <summary>
        /// Draw front view (XZ plane)
        /// </summary>
        private void DrawFrontView(Graphics g, Rectangle rect, ColorMapMode colorMode)
        {
            // Draw background
            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(40, 40, 40)))
            {
                g.FillRectangle(bgBrush, rect);
            }

            // Draw border
            using (Pen borderPen = new Pen(Color.FromArgb(100, 100, 100)))
            {
                g.DrawRectangle(borderPen, rect);
            }

            // Draw title
            using (Font titleFont = new Font("Segoe UI", 10, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            using (StringFormat sf = new StringFormat() { Alignment = StringAlignment.Center })
            {
                g.DrawString("Front View (XZ)", titleFont, textBrush,
                            new Rectangle(rect.X, rect.Y + 5, rect.Width, 20), sf);
            }

            // Render the actual view
            if (_labelData != null && _damageData != null)
            {
                // Calculate slice position (use the middle of the volume, or failure point if detected)
                int sliceY = _failureDetected ? _failurePoint.Y : _height / 2;

                // Draw the slice
                DrawSlice(g, rect, SliceDirection.Y, sliceY, colorMode);

                // Mark the slice position
                using (Font posFont = new Font("Segoe UI", 8))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    g.DrawString($"Y = {sliceY}", posFont, textBrush, rect.X + 10, rect.Y + 25);
                }
            }
            else
            {
                // Draw placeholder text
                using (Font font = new Font("Segoe UI", 9))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                using (StringFormat sf = new StringFormat() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawString("No data available", font, textBrush, rect, sf);
                }
            }
        }

        /// <summary>
        /// Draw side view (YZ plane)
        /// </summary>
        private void DrawSideView(Graphics g, Rectangle rect, ColorMapMode colorMode)
        {
            // Draw background
            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(40, 40, 40)))
            {
                g.FillRectangle(bgBrush, rect);
            }

            // Draw border
            using (Pen borderPen = new Pen(Color.FromArgb(100, 100, 100)))
            {
                g.DrawRectangle(borderPen, rect);
            }

            // Draw title
            using (Font titleFont = new Font("Segoe UI", 10, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            using (StringFormat sf = new StringFormat() { Alignment = StringAlignment.Center })
            {
                g.DrawString("Side View (YZ)", titleFont, textBrush,
                            new Rectangle(rect.X, rect.Y + 5, rect.Width, 20), sf);
            }

            // Render the actual view
            if (_labelData != null && _damageData != null)
            {
                // Calculate slice position (use the middle of the volume, or failure point if detected)
                int sliceX = _failureDetected ? _failurePoint.X : _width / 2;

                // Draw the slice
                DrawSlice(g, rect, SliceDirection.X, sliceX, colorMode);

                // Mark the slice position
                using (Font posFont = new Font("Segoe UI", 8))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    g.DrawString($"X = {sliceX}", posFont, textBrush, rect.X + 10, rect.Y + 25);
                }
            }
            else
            {
                // Draw placeholder text
                using (Font font = new Font("Segoe UI", 9))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                using (StringFormat sf = new StringFormat() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawString("No data available", font, textBrush, rect, sf);
                }
            }
        }

        /// <summary>
        /// Draw perspective 3D view
        /// </summary>
        private void DrawPerspectiveView(Graphics g, Rectangle rect, ColorMapMode colorMode)
        {
            // Draw background
            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(30, 30, 30)))
            {
                g.FillRectangle(bgBrush, rect);
            }

            // Draw border
            using (Pen borderPen = new Pen(Color.FromArgb(100, 100, 100)))
            {
                g.DrawRectangle(borderPen, rect);
            }

            // Draw title
            using (Font titleFont = new Font("Segoe UI", 10, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            using (StringFormat sf = new StringFormat() { Alignment = StringAlignment.Center })
            {
                g.DrawString("3D Perspective", titleFont, textBrush,
                            new Rectangle(rect.X, rect.Y + 5, rect.Width, 20), sf);
            }

            // Render the 3D view
            if (_labelData != null && _damageData != null)
            {
                Draw3DRendering(g, rect, colorMode);
            }
            else
            {
                // Draw placeholder text
                using (Font font = new Font("Segoe UI", 9))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                using (StringFormat sf = new StringFormat() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawString("No data available", font, textBrush, rect, sf);
                }
            }
        }

        /// <summary>
        /// Slice direction enum
        /// </summary>
        private enum SliceDirection
        {
            X,  // YZ plane
            Y,  // XZ plane
            Z   // XY plane
        }

        /// <summary>
        /// Draw a slice through the volume
        /// </summary>
        private void DrawSlice(Graphics g, Rectangle rect, SliceDirection direction, int sliceIndex, ColorMapMode colorMode)
        {
            // Calculate dimensions based on slice direction
            int dim1, dim2;
            switch (direction)
            {
                case SliceDirection.X:
                    dim1 = _height;
                    dim2 = _depth;
                    break;
                case SliceDirection.Y:
                    dim1 = _width;
                    dim2 = _depth;
                    break;
                case SliceDirection.Z:
                default:
                    dim1 = _width;
                    dim2 = _height;
                    break;
            }

            // Calculate scale to fit rectangle
            float scale1 = (float)(rect.Width - 60) / dim1;
            float scale2 = (float)(rect.Height - 60) / dim2;
            float scale = Math.Min(scale1, scale2);

            // Offset to center the slice in the rectangle
            int offsetX = rect.X + 30 + (int)((rect.Width - 60 - dim1 * scale) / 2);
            int offsetY = rect.Y + 30 + (int)((rect.Height - 60 - dim2 * scale) / 2);

            // Draw each voxel in the slice
            for (int i = 0; i < dim1; i++)
            {
                for (int j = 0; j < dim2; j++)
                {
                    // Get voxel coordinates
                    int x, y, z;
                    switch (direction)
                    {
                        case SliceDirection.X:
                            x = sliceIndex;
                            y = i;
                            z = j;
                            break;
                        case SliceDirection.Y:
                            x = i;
                            y = sliceIndex;
                            z = j;
                            break;
                        case SliceDirection.Z:
                        default:
                            x = i;
                            y = j;
                            z = sliceIndex;
                            break;
                    }

                    // Skip voxels outside the volume bounds
                    if (x < 0 || x >= _width || y < 0 || y >= _height || z < 0 || z >= _depth)
                        continue;

                    // Check if this voxel is part of the material
                    if (_labelData[x, y, z] == _materialId)
                    {
                        // Get color based on the selected mode
                        Color voxelColor = GetVoxelColor(x, y, z, colorMode);

                        // Calculate screen position
                        float screenX, screenY;
                        switch (direction)
                        {
                            case SliceDirection.X:
                                screenX = offsetX + i * scale;
                                screenY = offsetY + j * scale;
                                break;
                            case SliceDirection.Y:
                                screenX = offsetX + i * scale;
                                screenY = offsetY + j * scale;
                                break;
                            case SliceDirection.Z:
                            default:
                                screenX = offsetX + i * scale;
                                screenY = offsetY + j * scale;
                                break;
                        }

                        // Draw the voxel
                        using (SolidBrush brush = new SolidBrush(voxelColor))
                        {
                            g.FillRectangle(brush, screenX, screenY, scale, scale);
                        }

                        // If this is the failure point, mark it
                        if (_failureDetected && x == _failurePoint.X && y == _failurePoint.Y && z == _failurePoint.Z)
                        {
                            // Draw a clear marker
                            using (Pen markerPen = new Pen(Color.White, 2))
                            {
                                g.DrawEllipse(markerPen, screenX - scale, screenY - scale, scale * 3, scale * 3);
                            }

                            // Add a label
                            using (Font markerFont = new Font("Segoe UI", 8, FontStyle.Bold))
                            using (SolidBrush textBrush = new SolidBrush(Color.White))
                            {
                                g.DrawString("Failure", markerFont, textBrush, screenX + scale * 2, screenY);
                            }
                        }
                    }
                }
            }

            // Draw coordinate axes
            using (Pen axisPen = new Pen(Color.White, 1))
            {
                // X axis (horizontal)
                g.DrawLine(axisPen, offsetX, offsetY + dim2 * scale + 5, offsetX + dim1 * scale, offsetY + dim2 * scale + 5);

                // Y axis (vertical)
                g.DrawLine(axisPen, offsetX - 5, offsetY, offsetX - 5, offsetY + dim2 * scale);

                // Labels
                using (Font axisFont = new Font("Segoe UI", 8))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    switch (direction)
                    {
                        case SliceDirection.X:
                            g.DrawString("Y", axisFont, textBrush, offsetX + dim1 * scale + 5, offsetY + dim2 * scale);
                            g.DrawString("Z", axisFont, textBrush, offsetX - 15, offsetY - 5);
                            break;
                        case SliceDirection.Y:
                            g.DrawString("X", axisFont, textBrush, offsetX + dim1 * scale + 5, offsetY + dim2 * scale);
                            g.DrawString("Z", axisFont, textBrush, offsetX - 15, offsetY - 5);
                            break;
                        case SliceDirection.Z:
                        default:
                            g.DrawString("X", axisFont, textBrush, offsetX + dim1 * scale + 5, offsetY + dim2 * scale);
                            g.DrawString("Y", axisFont, textBrush, offsetX - 15, offsetY - 5);
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Draw a simplified 3D rendering of the volume
        /// </summary>
        private void Draw3DRendering(Graphics g, Rectangle rect, ColorMapMode colorMode)
        {
            // Calculate center of the rendering area
            int centerX = rect.X + rect.Width / 2;
            int centerY = rect.Y + rect.Height / 2;

            // Calculate rendering scale
            float scale = Math.Min(rect.Width, rect.Height) * 0.4f * _zoom / Math.Max(Math.Max(_width, _height), _depth);

            // Create rotation matrices
            Matrix rotX = new Matrix();
            rotX.Rotate(_rotationX);
            Matrix rotY = new Matrix();
            rotY.Rotate(_rotationY);

            // Create bounding box corners
            Point3D[] corners = new Point3D[8];
            corners[0] = new Point3D(0, 0, 0);
            corners[1] = new Point3D(_width, 0, 0);
            corners[2] = new Point3D(_width, _height, 0);
            corners[3] = new Point3D(0, _height, 0);
            corners[4] = new Point3D(0, 0, _depth);
            corners[5] = new Point3D(_width, 0, _depth);
            corners[6] = new Point3D(_width, _height, _depth);
            corners[7] = new Point3D(0, _height, _depth);

            // Project corners to 2D
            PointF[] projectedCorners = new PointF[8];
            for (int i = 0; i < 8; i++)
            {
                projectedCorners[i] = Project3DPoint(corners[i], centerX, centerY, scale);
            }

            // Draw bounding box
            using (Pen boxPen = new Pen(Color.FromArgb(100, 255, 255, 255), 1))
            {
                // Draw edges
                // Bottom face
                g.DrawLine(boxPen, projectedCorners[0], projectedCorners[1]);
                g.DrawLine(boxPen, projectedCorners[1], projectedCorners[2]);
                g.DrawLine(boxPen, projectedCorners[2], projectedCorners[3]);
                g.DrawLine(boxPen, projectedCorners[3], projectedCorners[0]);

                // Top face
                g.DrawLine(boxPen, projectedCorners[4], projectedCorners[5]);
                g.DrawLine(boxPen, projectedCorners[5], projectedCorners[6]);
                g.DrawLine(boxPen, projectedCorners[6], projectedCorners[7]);
                g.DrawLine(boxPen, projectedCorners[7], projectedCorners[4]);

                // Connecting edges
                g.DrawLine(boxPen, projectedCorners[0], projectedCorners[4]);
                g.DrawLine(boxPen, projectedCorners[1], projectedCorners[5]);
                g.DrawLine(boxPen, projectedCorners[2], projectedCorners[6]);
                g.DrawLine(boxPen, projectedCorners[3], projectedCorners[7]);
            }

            // Add axis labels
            using (Font axisFont = new Font("Segoe UI", 8))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                g.DrawString("X", axisFont, textBrush, projectedCorners[1].X + 5, projectedCorners[1].Y);
                g.DrawString("Y", axisFont, textBrush, projectedCorners[3].X - 15, projectedCorners[3].Y);
                g.DrawString("Z", axisFont, textBrush, projectedCorners[4].X, projectedCorners[4].Y - 15);
            }

            // Draw volume with dot density visualization (simplified)
            int dotSkip = Math.Max(1, (_width * _height * _depth) / 10000); // Limit number of dots

            for (int z = 0; z < _depth; z += dotSkip)
            {
                for (int y = 0; y < _height; y += dotSkip)
                {
                    for (int x = 0; x < _width; x += dotSkip)
                    {
                        // Skip voxels outside the volume or not part of the material
                        if (x >= _width || y >= _height || z >= _depth || _labelData[x, y, z] != _materialId)
                            continue;

                        // Get voxel color
                        Color voxelColor = GetVoxelColor(x, y, z, colorMode);

                        // Calculate point size based on damage (more damaged = larger)
                        float pointSize = 2f;
                        if (_damageData != null)
                        {
                            pointSize = 2f + (float)(_damageData[x, y, z] * 4f);
                        }

                        // Project point
                        PointF screenPt = Project3DPoint(new Point3D(x, y, z), centerX, centerY, scale);

                        // Draw the point
                        using (SolidBrush pointBrush = new SolidBrush(voxelColor))
                        {
                            g.FillEllipse(pointBrush,
                                         screenPt.X - pointSize / 2,
                                         screenPt.Y - pointSize / 2,
                                         pointSize, pointSize);
                        }
                    }
                }
            }

            // If failure is detected, highlight the failure point
            if (_failureDetected)
            {
                // Project failure point
                PointF failurePt = Project3DPoint(_failurePoint, centerX, centerY, scale);

                // Draw a pulsing circle around the failure point
                using (Pen failurePen = new Pen(Color.Red, 2))
                {
                    g.DrawEllipse(failurePen, failurePt.X - 10, failurePt.Y - 10, 20, 20);
                }

                // Draw label
                using (Font labelFont = new Font("Segoe UI", 9, FontStyle.Bold))
                using (SolidBrush textBrush = new SolidBrush(Color.Red))
                {
                    g.DrawString("Failure Point", labelFont, textBrush, failurePt.X + 12, failurePt.Y - 5);
                }
            }

            // Add interaction instructions
            using (Font instrFont = new Font("Segoe UI", 8))
            using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(200, 200, 200)))
            {
                g.DrawString("Drag to rotate | Right-click to pan | Scroll to zoom",
                            instrFont, textBrush, rect.X + 10, rect.Y + rect.Height - 20);
            }
        }

        /// <summary>
        /// Project a 3D point to 2D screen coordinates
        /// </summary>
        private PointF Project3DPoint(Point3D p3d, int centerX, int centerY, float scale)
        {
            // Center the point at the origin
            float x = p3d.X - _width / 2f;
            float y = p3d.Y - _height / 2f;
            float z = p3d.Z - _depth / 2f;

            // Apply rotations
            double radX = _rotationX * Math.PI / 180.0;
            double radY = _rotationY * Math.PI / 180.0;

            // Rotate around X
            float tempY = (float)(y * Math.Cos(radX) - z * Math.Sin(radX));
            float tempZ = (float)(y * Math.Sin(radX) + z * Math.Cos(radX));
            y = tempY;
            z = tempZ;

            // Rotate around Y
            float tempX = (float)(x * Math.Cos(radY) + z * Math.Sin(radY));
            tempZ = (float)(-x * Math.Sin(radY) + z * Math.Cos(radY));
            x = tempX;
            z = tempZ;

            // Scale and project to screen
            float screenX = centerX + x * scale + _pan.X;
            float screenY = centerY + y * scale + _pan.Y;

            return new PointF(screenX, screenY);
        }

        /// <summary>
        /// Get color for a voxel based on the selected color mapping
        /// </summary>
        private Color GetVoxelColor(int x, int y, int z, ColorMapMode colorMode)
        {
            switch (colorMode)
            {
                case ColorMapMode.Strain:
                    return GetStrainColor(x, y, z);
                case ColorMapMode.Damage:
                    return GetDamageColor(x, y, z);
                case ColorMapMode.Stress:
                default:
                    return GetStressColor(x, y, z);
            }
        }

        /// <summary>
        /// Get color based on stress value
        /// </summary>
        private Color GetStressColor(int x, int y, int z)
        {
            if (_stressData != null)
            {
                // Use actual stress data
                float stress = _stressData[x, y, z];
                float normalizedStress = Saturate(stress / 100.0f); // Normalize to 0-1 range, assuming 100 MPa max
                return GetInterpolatedColor(_stressColorMap, normalizedStress);
            }
            else
            {
                // Use simple gradient based on position
                float normalizedPos = (float)z / _depth;
                return GetInterpolatedColor(_stressColorMap, normalizedPos);
            }
        }

        /// <summary>
        /// Get color based on strain value
        /// </summary>
        private Color GetStrainColor(int x, int y, int z)
        {
            if (_strainData != null)
            {
                // Use actual strain data
                float strain = _strainData[x, y, z];
                float normalizedStrain = Saturate(strain / 0.2f); // Normalize to 0-1 range, assuming 20% max strain
                return GetInterpolatedColor(_strainColorMap, normalizedStrain);
            }
            else
            {
                // Use simple gradient based on position
                float normalizedPos = (float)y / _height;
                return GetInterpolatedColor(_strainColorMap, normalizedPos);
            }
        }

        /// <summary>
        /// Get color based on damage value
        /// </summary>
        private Color GetDamageColor(int x, int y, int z)
        {
            if (_damageData != null)
            {
                // Use actual damage data
                double damage = _damageData[x, y, z];
                float normalizedDamage = Saturate((float)damage);
                return GetInterpolatedColor(_damageColorMap, normalizedDamage);
            }
            else
            {
                // Use simple gradient based on position
                float normalizedPos = (float)x / _width;
                return GetInterpolatedColor(_damageColorMap, normalizedPos);
            }
        }

        /// <summary>
        /// Interpolate between colors in a color map
        /// </summary>
        private Color GetInterpolatedColor(Color[] colorMap, float t)
        {
            if (colorMap.Length == 0) return Color.White;
            if (colorMap.Length == 1) return colorMap[0];

            t = Saturate(t);
            float scaledT = t * (colorMap.Length - 1);
            int idx = (int)scaledT;
            float frac = scaledT - idx;

            if (idx >= colorMap.Length - 1)
                return colorMap[colorMap.Length - 1];

            Color c1 = colorMap[idx];
            Color c2 = colorMap[idx + 1];

            return InterpolateColor(c1, c2, frac);
        }

        /// <summary>
        /// Interpolate between two colors
        /// </summary>
        private Color InterpolateColor(Color c1, Color c2, float t)
        {
            t = Saturate(t);

            int r = (int)(c1.R * (1 - t) + c2.R * t);
            int g = (int)(c1.G * (1 - t) + c2.G * t);
            int b = (int)(c1.B * (1 - t) + c2.B * t);
            int a = (int)(c1.A * (1 - t) + c2.A * t);

            return Color.FromArgb(a, r, g, b);
        }

        /// <summary>
        /// Clamp value to 0-1 range
        /// </summary>
        private float Saturate(float value)
        {
            return Math.Max(0, Math.Min(1, value));
        }

        /// <summary>
        /// Draw a color legend for the current color map mode
        /// </summary>
        private void DrawColorLegend(Graphics g, int x, int y, int width, int height, ColorMapMode colorMode)
        {
            // Draw background
            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(40, 40, 40)))
            {
                g.FillRectangle(bgBrush, x, y, width, height);
            }

            // Draw border
            using (Pen borderPen = new Pen(Color.FromArgb(100, 100, 100)))
            {
                g.DrawRectangle(borderPen, x, y, width, height);
            }

            // Draw title
            using (Font titleFont = new Font("Segoe UI", 9, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            using (StringFormat sf = new StringFormat() { Alignment = StringAlignment.Center })
            {
                string title;
                switch (colorMode)
                {
                    case ColorMapMode.Strain:
                        title = "Strain";
                        break;
                    case ColorMapMode.Damage:
                        title = "Damage";
                        break;
                    case ColorMapMode.Stress:
                    default:
                        title = "Stress (MPa)";
                        break;
                }

                g.DrawString(title, titleFont, textBrush, new Rectangle(x, y + 5, width, 20), sf);
            }

            // Draw color gradient
            int gradientHeight = height - 60;
            int gradientWidth = 30;
            int gradientX = x + (width - gradientWidth) / 2;
            int gradientY = y + 30;

            // Choose the right color map
            Color[] colorMap;
            switch (colorMode)
            {
                case ColorMapMode.Strain:
                    colorMap = _strainColorMap;
                    break;
                case ColorMapMode.Damage:
                    colorMap = _damageColorMap;
                    break;
                case ColorMapMode.Stress:
                default:
                    colorMap = _stressColorMap;
                    break;
            }

            // Draw the gradient
            using (LinearGradientBrush lgb = new LinearGradientBrush(
                new Point(gradientX, gradientY),
                new Point(gradientX, gradientY + gradientHeight),
                Color.Red, Color.Blue))
            {
                // Create a color blend with all the colors in the map
                ColorBlend blend = new ColorBlend(colorMap.Length);
                blend.Colors = colorMap;

                // Generate positions evenly distributed
                float[] positions = new float[colorMap.Length];
                for (int i = 0; i < colorMap.Length; i++)
                {
                    positions[i] = (float)i / (colorMap.Length - 1);
                }
                blend.Positions = positions;

                lgb.InterpolationColors = blend;

                g.FillRectangle(lgb, gradientX, gradientY, gradientWidth, gradientHeight);
            }

            // Draw border around gradient
            using (Pen borderPen = new Pen(Color.White))
            {
                g.DrawRectangle(borderPen, gradientX, gradientY, gradientWidth, gradientHeight);
            }

            // Draw tick marks and labels
            using (Font labelFont = new Font("Segoe UI", 8))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            using (StringFormat sf = new StringFormat() { Alignment = StringAlignment.Far })
            {
                // Generate labels based on selected mode
                string[] labels;

                switch (colorMode)
                {
                    case ColorMapMode.Strain:
                        labels = new string[] { "0.20", "0.15", "0.10", "0.05", "0.00" };
                        break;
                    case ColorMapMode.Damage:
                        labels = new string[] { "1.0", "0.75", "0.5", "0.25", "0.0" };
                        break;
                    case ColorMapMode.Stress:
                    default:
                        labels = new string[] { "100", "75", "50", "25", "0" };
                        break;
                }

                // Draw tick marks and labels
                for (int i = 0; i < 5; i++)
                {
                    int tickY = gradientY + (i * gradientHeight / 4);
                    g.DrawLine(Pens.White, gradientX - 3, tickY, gradientX, tickY);
                    g.DrawString(labels[i], labelFont, textBrush,
                                 new Rectangle(x, tickY - 7, gradientX - 5, 15), sf);
                }
            }
        }
    }
}