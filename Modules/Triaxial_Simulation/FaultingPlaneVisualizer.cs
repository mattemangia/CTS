using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Windows.Forms;

namespace CTS
{
    /// <summary>
    /// Extension class to handle 3D visualization of faulting planes
    /// </summary>
    public class FaultingPlaneVisualizer
    {
        // Rendering parameters
        private float _rotationX = 30;
        private float _rotationY = 30;
        private float _zoom = 1.0f;
        private PointF _pan = new PointF(0, 0);
        private bool _isDragging = false;
        private Point _lastMousePosition;
        private bool _showVolume = true;

        // Volume data
        private int _width;
        private int _height;
        private int _depth;
        private byte _materialId;
        private ILabelVolumeData _labelData;
        private double[,,] _damageData;
        private float[,,] _densityData;

        // Cached data for rendering
        private List<Vector3> _vertices = new List<Vector3>();
        private List<Vector3> _normals = new List<Vector3>();
        private List<int> _indices = new List<int>();
        private List<Color> _colors = new List<Color>();
        private bool _meshGenerated = false;

        // Constants
        private const float CRACK_THRESHOLD = 0.7f; // Damage threshold for identifying cracks

        /// <summary>
        /// Constructor
        /// </summary>
        public FaultingPlaneVisualizer(int width, int height, int depth, byte materialId)
        {
            _width = width;
            _height = height;
            _depth = depth;
            _materialId = materialId;
        }

        /// <summary>
        /// Set the visualization data
        /// </summary>
        public void SetData(ILabelVolumeData labels, double[,,] damage, float[,,] density = null)
        {
            _labelData = labels;
            _damageData = damage;
            _densityData = density;
            _meshGenerated = false; // Reset mesh generation flag
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
        /// Enable or disable volume rendering
        /// </summary>
        public void SetShowVolume(bool showVolume)
        {
            _showVolume = showVolume;
        }

        /// <summary>
        /// Create a visualization of the faulting planes
        /// </summary>
        public Bitmap CreateVisualization(int width, int height)
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

                // Draw the 3D visualization
                DrawFaultingPlanes(g, new Rectangle(0, 0, width, height));

                // Draw controls and info
                DrawControls(g, width, height);
            }

            return bmp;
        }

        /// <summary>
        /// Draw faulting planes visualization
        /// </summary>
        private void DrawFaultingPlanes(Graphics g, Rectangle rect)
        {
            // Check if we have valid data
            if (_labelData == null || _damageData == null)
            {
                DrawPlaceholder(g, rect);
                return;
            }

            // Generate mesh if not already done
            if (!_meshGenerated)
            {
                GenerateMesh();
            }

            // Calculate center of the rendering area
            int centerX = rect.X + rect.Width / 2;
            int centerY = rect.Y + rect.Height / 2;

            // Calculate rendering scale
            float scale = Math.Min(rect.Width, rect.Height) * 0.4f * _zoom / Math.Max(Math.Max(_width, _height), _depth);

            // Draw the volume if enabled
            if (_showVolume)
            {
                DrawVolume(g, centerX, centerY, scale);
            }

            // Draw the faulting planes (cracks)
            DrawCracks(g, centerX, centerY, scale);

            // Draw bounding box
            DrawBoundingBox(g, centerX, centerY, scale);
        }

        /// <summary>
        /// Draw placeholder when no data is available
        /// </summary>
        private void DrawPlaceholder(Graphics g, Rectangle rect)
        {
            using (Font font = new Font("Segoe UI", 12))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            using (StringFormat sf = new StringFormat() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString("No simulation data available\nRun simulation to visualize faulting planes",
                            font, textBrush, rect, sf);
            }
        }

        /// <summary>
        /// Generate mesh data for visualization
        /// </summary>
        private void GenerateMesh()
        {
            // Clear existing mesh data
            _vertices.Clear();
            _normals.Clear();
            _indices.Clear();
            _colors.Clear();

            // Find all voxels with damage above threshold (cracks)
            List<Vector3> crackVoxels = new List<Vector3>();

            for (int z = 0; z < _depth; z++)
            {
                for (int y = 0; y < _height; y++)
                {
                    for (int x = 0; x < _width; x++)
                    {
                        // Check if this voxel is part of the material and damaged
                        if (_labelData[x, y, z] == _materialId && _damageData[x, y, z] >= CRACK_THRESHOLD)
                        {
                            crackVoxels.Add(new Vector3(x, y, z));
                        }
                    }
                }
            }

            // If no cracks found, nothing to render
            if (crackVoxels.Count == 0)
            {
                _meshGenerated = true;
                return;
            }

            // Generate cube faces for each crack voxel
            foreach (Vector3 voxel in crackVoxels)
            {
                int x = (int)voxel.X;
                int y = (int)voxel.Y;
                int z = (int)voxel.Z;

                // Only draw faces that are adjacent to non-crack voxels
                // Front face (positive Z)
                if (z + 1 >= _depth || _labelData[x, y, z + 1] != _materialId || _damageData[x, y, z + 1] < CRACK_THRESHOLD)
                {
                    AddCubeFace(x, y, z, CubeFace.Front, GetDamageColor((float)_damageData[x, y, z]));
                }

                // Back face (negative Z)
                if (z - 1 < 0 || _labelData[x, y, z - 1] != _materialId || _damageData[x, y, z - 1] < CRACK_THRESHOLD)
                {
                    AddCubeFace(x, y, z, CubeFace.Back, GetDamageColor((float)_damageData[x, y, z]));
                }

                // Right face (positive X)
                if (x + 1 >= _width || _labelData[x + 1, y, z] != _materialId || _damageData[x + 1, y, z] < CRACK_THRESHOLD)
                {
                    AddCubeFace(x, y, z, CubeFace.Right, GetDamageColor((float)_damageData[x, y, z]));
                }

                // Left face (negative X)
                if (x - 1 < 0 || _labelData[x - 1, y, z] != _materialId || _damageData[x - 1, y, z] < CRACK_THRESHOLD)
                {
                    AddCubeFace(x, y, z, CubeFace.Left, GetDamageColor((float)_damageData[x, y, z]));
                }

                // Top face (positive Y)
                if (y + 1 >= _height || _labelData[x, y + 1, z] != _materialId || _damageData[x, y + 1, z] < CRACK_THRESHOLD)
                {
                    AddCubeFace(x, y, z, CubeFace.Top, GetDamageColor((float)_damageData[x, y, z]));
                }

                // Bottom face (negative Y)
                if (y - 1 < 0 || _labelData[x, y - 1, z] != _materialId || _damageData[x, y - 1, z] < CRACK_THRESHOLD)
                {
                    AddCubeFace(x, y, z, CubeFace.Bottom, GetDamageColor((float)_damageData[x, y, z]));
                }
            }

            _meshGenerated = true;
        }

        /// <summary>
        /// Cube face enum
        /// </summary>
        private enum CubeFace
        {
            Front,
            Back,
            Left,
            Right,
            Top,
            Bottom
        }

        /// <summary>
        /// Add a cube face to the mesh
        /// </summary>
        private void AddCubeFace(int x, int y, int z, CubeFace face, Color color)
        {
            // Calculate vertex position offset
            Vector3 offset = new Vector3(
                -_width / 2.0f + x + 0.5f,
                -_height / 2.0f + y + 0.5f,
                -_depth / 2.0f + z + 0.5f
            );

            // Get vertices and indices for this face
            Vector3[] vertices;
            int[] indices;
            Vector3 normal;

            // Define vertices for each face (corners of a unit cube centered at origin)
            float h = 0.5f; // Half-size

            switch (face)
            {
                case CubeFace.Front:
                    vertices = new Vector3[] {
                        new Vector3(-h, -h, h),
                        new Vector3(h, -h, h),
                        new Vector3(h, h, h),
                        new Vector3(-h, h, h)
                    };
                    normal = new Vector3(0, 0, 1);
                    break;

                case CubeFace.Back:
                    vertices = new Vector3[] {
                        new Vector3(h, -h, -h),
                        new Vector3(-h, -h, -h),
                        new Vector3(-h, h, -h),
                        new Vector3(h, h, -h)
                    };
                    normal = new Vector3(0, 0, -1);
                    break;

                case CubeFace.Left:
                    vertices = new Vector3[] {
                        new Vector3(-h, -h, -h),
                        new Vector3(-h, -h, h),
                        new Vector3(-h, h, h),
                        new Vector3(-h, h, -h)
                    };
                    normal = new Vector3(-1, 0, 0);
                    break;

                case CubeFace.Right:
                    vertices = new Vector3[] {
                        new Vector3(h, -h, h),
                        new Vector3(h, -h, -h),
                        new Vector3(h, h, -h),
                        new Vector3(h, h, h)
                    };
                    normal = new Vector3(1, 0, 0);
                    break;

                case CubeFace.Top:
                    vertices = new Vector3[] {
                        new Vector3(-h, h, h),
                        new Vector3(h, h, h),
                        new Vector3(h, h, -h),
                        new Vector3(-h, h, -h)
                    };
                    normal = new Vector3(0, 1, 0);
                    break;

                case CubeFace.Bottom:
                default:
                    vertices = new Vector3[] {
                        new Vector3(-h, -h, -h),
                        new Vector3(h, -h, -h),
                        new Vector3(h, -h, h),
                        new Vector3(-h, -h, h)
                    };
                    normal = new Vector3(0, -1, 0);
                    break;
            }

            // Apply offset to vertices
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] += offset;
            }

            // Create indices for two triangles (face is a quad composed of two triangles)
            int baseIndex = _vertices.Count;
            indices = new int[] {
                baseIndex, baseIndex + 1, baseIndex + 2,
                baseIndex, baseIndex + 2, baseIndex + 3
            };

            // Add to the mesh
            _vertices.AddRange(vertices);
            for (int i = 0; i < vertices.Length; i++)
            {
                _normals.Add(normal);
                _colors.Add(color);
            }
            _indices.AddRange(indices);
        }

        /// <summary>
        /// Draw volume with semi-transparent rendering
        /// </summary>
        private void DrawVolume(Graphics g, int centerX, int centerY, float scale)
        {
            // For simplicity, just draw a semi-transparent bounding box for the volume
            // In a full implementation, this would use volume rendering

            // Get volume bounds
            Vector3 min = new Vector3(-_width / 2.0f, -_height / 2.0f, -_depth / 2.0f);
            Vector3 max = new Vector3(_width / 2.0f, _height / 2.0f, _depth / 2.0f);

            // Define corners
            Vector3[] corners = new Vector3[] {
                new Vector3(min.X, min.Y, min.Z),
                new Vector3(max.X, min.Y, min.Z),
                new Vector3(max.X, max.Y, min.Z),
                new Vector3(min.X, max.Y, min.Z),
                new Vector3(min.X, min.Y, max.Z),
                new Vector3(max.X, min.Y, max.Z),
                new Vector3(max.X, max.Y, max.Z),
                new Vector3(min.X, max.Y, max.Z)
            };

            // Project corners to 2D
            PointF[] projectedCorners = new PointF[8];
            for (int i = 0; i < 8; i++)
            {
                projectedCorners[i] = Project3DPoint(corners[i], centerX, centerY, scale);
            }

            // Draw volume faces with semi-transparency
            using (SolidBrush faceBrush = new SolidBrush(Color.FromArgb(30, 100, 150, 200)))
            {
                // Only draw visible faces based on current rotation
                // This is a simplified approach - a full implementation would use depth sorting

                // Back face
                g.FillPolygon(faceBrush, new PointF[] {
                    projectedCorners[0], projectedCorners[1], projectedCorners[2], projectedCorners[3]
                });

                // Left face
                g.FillPolygon(faceBrush, new PointF[] {
                    projectedCorners[0], projectedCorners[3], projectedCorners[7], projectedCorners[4]
                });

                // Bottom face
                g.FillPolygon(faceBrush, new PointF[] {
                    projectedCorners[0], projectedCorners[1], projectedCorners[5], projectedCorners[4]
                });

                // Right face
                g.FillPolygon(faceBrush, new PointF[] {
                    projectedCorners[1], projectedCorners[2], projectedCorners[6], projectedCorners[5]
                });

                // Top face
                g.FillPolygon(faceBrush, new PointF[] {
                    projectedCorners[3], projectedCorners[2], projectedCorners[6], projectedCorners[7]
                });

                // Front face
                g.FillPolygon(faceBrush, new PointF[] {
                    projectedCorners[4], projectedCorners[5], projectedCorners[6], projectedCorners[7]
                });
            }
        }

        /// <summary>
        /// Draw cracks (faulting planes)
        /// </summary>
        private void DrawCracks(Graphics g, int centerX, int centerY, float scale)
        {
            // Early exit if no mesh data
            if (_vertices.Count == 0 || _indices.Count == 0)
                return;

            // Draw each triangle in the mesh
            for (int i = 0; i < _indices.Count; i += 3)
            {
                // Get triangle indices
                int i1 = _indices[i];
                int i2 = _indices[i + 1];
                int i3 = _indices[i + 2];

                // Get vertices
                Vector3 v1 = _vertices[i1];
                Vector3 v2 = _vertices[i2];
                Vector3 v3 = _vertices[i3];

                // Project to 2D
                PointF p1 = Project3DPoint(v1, centerX, centerY, scale);
                PointF p2 = Project3DPoint(v2, centerX, centerY, scale);
                PointF p3 = Project3DPoint(v3, centerX, centerY, scale);

                // Get the color
                Color color = _colors[i1]; // Same color for all vertices of face

                // Draw the triangle
                using (SolidBrush brush = new SolidBrush(color))
                {
                    g.FillPolygon(brush, new PointF[] { p1, p2, p3 });
                }

                // Draw outline
                using (Pen pen = new Pen(Color.FromArgb(50, 50, 50), 1))
                {
                    g.DrawPolygon(pen, new PointF[] { p1, p2, p3 });
                }
            }
        }

        /// <summary>
        /// Draw bounding box
        /// </summary>
        private void DrawBoundingBox(Graphics g, int centerX, int centerY, float scale)
        {
            // Get volume bounds
            Vector3 min = new Vector3(-_width / 2.0f, -_height / 2.0f, -_depth / 2.0f);
            Vector3 max = new Vector3(_width / 2.0f, _height / 2.0f, _depth / 2.0f);

            // Define corners
            Vector3[] corners = new Vector3[] {
                new Vector3(min.X, min.Y, min.Z),
                new Vector3(max.X, min.Y, min.Z),
                new Vector3(max.X, max.Y, min.Z),
                new Vector3(min.X, max.Y, min.Z),
                new Vector3(min.X, min.Y, max.Z),
                new Vector3(max.X, min.Y, max.Z),
                new Vector3(max.X, max.Y, max.Z),
                new Vector3(min.X, max.Y, max.Z)
            };

            // Project corners to 2D
            PointF[] projectedCorners = new PointF[8];
            for (int i = 0; i < 8; i++)
            {
                projectedCorners[i] = Project3DPoint(corners[i], centerX, centerY, scale);
            }

            // Draw edges
            using (Pen boxPen = new Pen(Color.FromArgb(200, 255, 255, 255), 1))
            {
                // Bottom square
                g.DrawLine(boxPen, projectedCorners[0], projectedCorners[1]);
                g.DrawLine(boxPen, projectedCorners[1], projectedCorners[2]);
                g.DrawLine(boxPen, projectedCorners[2], projectedCorners[3]);
                g.DrawLine(boxPen, projectedCorners[3], projectedCorners[0]);

                // Top square
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
        }

        /// <summary>
        /// Draw UI controls and information
        /// </summary>
        private void DrawControls(Graphics g, int width, int height)
        {
            // Draw interaction instructions
            using (Font instrFont = new Font("Segoe UI", 8))
            using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(200, 200, 200)))
            {
                g.DrawString("Drag to rotate | Right-click to pan | Scroll to zoom | Toggle 'Show Volume' to see only cracks",
                            instrFont, textBrush, 10, height - 20);
            }

            // Draw stats on crack visualization
            using (Font statsFont = new Font("Segoe UI", 9))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                int faceCount = _indices.Count / 3;
                int crackVoxelCount = _vertices.Count / 4; // 4 vertices per face

                string statsText = $"Visualization: {(_showVolume ? "Volume + Cracks" : "Cracks Only")}";
                statsText += $"\nCrack Voxels: {crackVoxelCount} | Faces: {faceCount}";
                statsText += $"\nThreshold: Damage > {CRACK_THRESHOLD:F2}";

                g.DrawString(statsText, statsFont, textBrush, 10, 10);
            }

            // Draw color legend
            DrawDamageLegend(g, width - 150, 10, 130, 200);
        }

        /// <summary>
        /// Draw damage color legend
        /// </summary>
        private void DrawDamageLegend(Graphics g, int x, int y, int width, int height)
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
                g.DrawString("Damage Level", titleFont, textBrush,
                            new Rectangle(x, y + 5, width, 20), sf);
            }

            // Draw color gradient
            int gradientHeight = height - 60;
            int gradientWidth = 30;
            int gradientX = x + (width - gradientWidth) / 2;
            int gradientY = y + 30;

            // Create gradient brush
            using (LinearGradientBrush lgb = new LinearGradientBrush(
                new Point(gradientX, gradientY),
                new Point(gradientX, gradientY + gradientHeight),
                Color.Red, Color.Green))
            {
                // Define color stops
                ColorBlend blend = new ColorBlend(3);
                blend.Colors = new Color[] {
                    Color.Red, Color.Yellow, Color.Green
                };
                blend.Positions = new float[] { 0.0f, 0.5f, 1.0f };
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
                // Draw threshold marker
                int thresholdY = gradientY + (int)(gradientHeight * (1 - CRACK_THRESHOLD));
                using (Pen thresholdPen = new Pen(Color.White, 2))
                {
                    g.DrawLine(thresholdPen, gradientX - 5, thresholdY, gradientX + gradientWidth + 5, thresholdY);
                }

                // Draw labels
                g.DrawString("1.0 (High)", labelFont, textBrush, new Rectangle(x, gradientY - 7, gradientX - 5, 15), sf);
                g.DrawString("0.5", labelFont, textBrush, new Rectangle(x, gradientY + gradientHeight / 2 - 7, gradientX - 5, 15), sf);
                g.DrawString("0.0 (None)", labelFont, textBrush, new Rectangle(x, gradientY + gradientHeight - 7, gradientX - 5, 15), sf);

                // Draw threshold label
                g.DrawString($"Threshold: {CRACK_THRESHOLD:F2}", labelFont, textBrush,
                            new Rectangle(x, thresholdY - 7, gradientX - 5, 15), sf);
            }
        }

        /// <summary>
        /// Project a 3D point to 2D screen coordinates
        /// </summary>
        private PointF Project3DPoint(Vector3 point, int centerX, int centerY, float scale)
        {
            // Apply rotations
            double radX = _rotationX * Math.PI / 180.0;
            double radY = _rotationY * Math.PI / 180.0;

            // Rotate around X
            float tempY = (float)(point.Y * Math.Cos(radX) - point.Z * Math.Sin(radX));
            float tempZ = (float)(point.Y * Math.Sin(radX) + point.Z * Math.Cos(radX));
            float y = tempY;
            float z = tempZ;

            // Rotate around Y
            float tempX = (float)(point.X * Math.Cos(radY) + z * Math.Sin(radY));
            tempZ = (float)(-point.X * Math.Sin(radY) + z * Math.Cos(radY));
            float x = tempX;
            z = tempZ;

            // Scale and project to screen
            float screenX = centerX + x * scale + _pan.X;
            float screenY = centerY + y * scale + _pan.Y;

            return new PointF(screenX, screenY);
        }

        /// <summary>
        /// Get color based on damage value
        /// </summary>
        private Color GetDamageColor(float damage)
        {
            // Damage colors range from green (low damage) to red (high damage)
            float t = Math.Max(0, Math.Min(1, damage));

            if (t < 0.5f)
            {
                // Green to yellow
                t = t * 2;
                return Color.FromArgb(255,
                                    (int)(255 * t),
                                    255,
                                    0);
            }
            else
            {
                // Yellow to red
                t = (t - 0.5f) * 2;
                return Color.FromArgb(255,
                                    255,
                                    (int)(255 * (1 - t)),
                                    0);
            }
        }

        /// <summary>
        /// Handle mouse events for interactive rotation, panning and zooming
        /// </summary>
        public void HandleMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
            {
                _isDragging = true;
                _lastMousePosition = e.Location;
            }
        }

        public void HandleMouseMove(MouseEventArgs e)
        {
            if (_isDragging)
            {
                int dx = e.X - _lastMousePosition.X;
                int dy = e.Y - _lastMousePosition.Y;

                if (e.Button == MouseButtons.Left)
                {
                    // Rotate
                    _rotationY += dx * 0.5f;
                    _rotationX += dy * 0.5f;

                    // Limit rotation angles
                    _rotationX = Math.Max(-90, Math.Min(90, _rotationX));
                }
                else if (e.Button == MouseButtons.Right)
                {
                    // Pan
                    _pan.X += dx;
                    _pan.Y += dy;
                }

                _lastMousePosition = e.Location;
            }
        }

        public void HandleMouseUp(MouseEventArgs e)
        {
            _isDragging = false;
        }

        public void HandleMouseWheel(MouseEventArgs e)
        {
            // Zoom in/out with more responsive adjustment
            float zoomFactor = 1.2f;
            if (e.Delta > 0)
            {
                _zoom *= zoomFactor;
            }
            else
            {
                _zoom /= zoomFactor;
            }

            // Limit zoom range
            _zoom = Math.Max(0.1f, Math.Min(50.0f, _zoom));
        }
    }
}