using Krypton.Toolkit;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;

namespace CTS
{
    /// <summary>
    /// Interface for density visualization across different simulation types
    /// </summary>
    public interface IDensityVisualizable
    {
        void RenderDensityDistribution(Graphics g, int width, int height);

        float MinimumDensity { get; }
        float MaximumDensity { get; }
        float AverageDensity { get; }
        Dictionary<Triangle, float> TriangleDensities { get; }
        Material Material { get; }
        IReadOnlyList<Triangle> MeshTriangles { get; }
    }

    /// <summary>
    /// Form for visualizing inhomogeneous density distribution in simulations
    /// </summary>
    public partial class DensityVisualizationForm : KryptonForm
    {
        // Simulation to visualize
        private readonly IDensityVisualizable _simulation;

        private readonly bool _isTriaxial;
        private readonly string _simulationType;

        // UI Elements
        private Panel _visualizationPanel;

        private KryptonGroupBox _controlsGroupBox;
        private KryptonButton _exportButton;
        private KryptonButton _closeButton;
        private KryptonCheckBox _showWireframeCheckBox;
        private KryptonTrackBar _opacityTrackBar;
        private KryptonButton _resetViewButton;
        private KryptonGroupBox _statisticsGroupBox;

        // View controls
        private float _rotationX = 0.5f;

        private float _rotationY = 0.5f;
        private float _zoomLevel = 1.0f;
        private PointF _panOffset = new PointF(0, 0);
        private bool _isRotating = false;
        private bool _isPanning = false;
        private Point _lastMousePosition;
        private bool _autoRotate = false;
        private System.Windows.Forms.Timer _rotationTimer;

        // Constructor overloads for both simulation types
        public DensityVisualizationForm(InhomogeneousAcousticSimulation simulation)
        {
            _simulation = simulation;
            _isTriaxial = false;
            _simulationType = "Acoustic";
            InitializeComponent();
        }

        public DensityVisualizationForm(InhomogeneousTriaxialSimulation simulation)
        {
            _simulation = (IDensityVisualizable)simulation;
            _isTriaxial = true;
            _simulationType = "Triaxial";
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            // Basic form settings
            this.Text = $"Inhomogeneous Density Visualization - {_simulationType}";
            this.Size = new Size(1000, 700);
            this.MinimumSize = new Size(800, 600);
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = true;
            this.MinimizeBox = true;

            // Set up main layout
            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.FromArgb(45, 45, 48)
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 75F));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));

            // Create the visualization panel
            _visualizationPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                Margin = new Padding(5)
            };
            _visualizationPanel.Paint += VisualizationPanel_Paint;
            _visualizationPanel.MouseDown += VisualizationPanel_MouseDown;
            _visualizationPanel.MouseMove += VisualizationPanel_MouseMove;
            _visualizationPanel.MouseUp += VisualizationPanel_MouseUp;
            _visualizationPanel.MouseWheel += VisualizationPanel_MouseWheel;

            // Create controls panel
            Panel controlsPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(45, 45, 48),
                Margin = new Padding(5)
            };

            // Controls group box
            _controlsGroupBox = new KryptonGroupBox
            {
                Text = "Visualization Controls",
                Dock = DockStyle.Top,
                Height = 280,
                Name = "Controls"
            };

            // Show wireframe checkbox
            _showWireframeCheckBox = new KryptonCheckBox
            {
                Text = "Show Wireframe",
                Checked = true,
                Location = new Point(15, 30)
            };
            _showWireframeCheckBox.CheckedChanged += (s, e) => _visualizationPanel.Invalidate();

            // Opacity track bar label
            KryptonLabel opacityLabel = new KryptonLabel
            {
                Text = "Opacity:",
                Location = new Point(15, 60),
                Size = new Size(150, 20)
            };

            // Opacity track bar
            _opacityTrackBar = new KryptonTrackBar
            {
                Location = new Point(15, 85),
                Size = new Size(180, 30),
                Minimum = 10,
                Maximum = 100,
                Value = 80,
                SmallChange = 5,
                LargeChange = 20
            };
            _opacityTrackBar.ValueChanged += (s, e) => _visualizationPanel.Invalidate();

            // Auto rotate checkbox
            KryptonCheckBox autoRotateCheckBox = new KryptonCheckBox
            {
                Text = "Auto Rotate",
                Checked = false,
                Location = new Point(15, 120)
            };
            autoRotateCheckBox.CheckedChanged += (s, e) =>
            {
                _autoRotate = autoRotateCheckBox.Checked;
                if (_autoRotate && _rotationTimer == null)
                {
                    _rotationTimer = new System.Windows.Forms.Timer
                    {
                        Interval = 50
                    };
                    _rotationTimer.Tick += (_, __) =>
                    {
                        _rotationY += 0.01f;
                        _visualizationPanel.Invalidate();
                    };
                }

                if (_rotationTimer != null)
                {
                    _rotationTimer.Enabled = _autoRotate;
                }
            };

            // Reset view button
            _resetViewButton = new KryptonButton
            {
                Text = "Reset View",
                Location = new Point(15, 155),
                Size = new Size(180, 30)
            };
            _resetViewButton.Click += (s, e) =>
            {
                _rotationX = 0.5f;
                _rotationY = 0.5f;
                _zoomLevel = 1.0f;
                _panOffset = new PointF(0, 0);
                _visualizationPanel.Invalidate();
            };

            // Export button
            _exportButton = new KryptonButton
            {
                Text = "Export as Image",
                Location = new Point(15, 195),
                Size = new Size(180, 30)
            };
            _exportButton.Click += ExportButton_Click;

            // Close button
            _closeButton = new KryptonButton
            {
                Text = "Close",
                Location = new Point(15, 235),
                Size = new Size(180, 30)
            };
            _closeButton.Click += (s, e) => this.Close();

            // Add controls to the group box
            _controlsGroupBox.Panel.Controls.Add(_showWireframeCheckBox);
            _controlsGroupBox.Panel.Controls.Add(opacityLabel);
            _controlsGroupBox.Panel.Controls.Add(_opacityTrackBar);
            _controlsGroupBox.Panel.Controls.Add(autoRotateCheckBox);
            _controlsGroupBox.Panel.Controls.Add(_resetViewButton);
            _controlsGroupBox.Panel.Controls.Add(_exportButton);
            _controlsGroupBox.Panel.Controls.Add(_closeButton);

            // Statistics group box
            _statisticsGroupBox = new KryptonGroupBox
            {
                Text = "Density Statistics",
                Dock = DockStyle.Top,
                Height = 280,
                Name = "Statistics"
            };

            // Create the statistics labels
            KryptonLabel materialLabel = new KryptonLabel
            {
                Text = $"Material: {_simulation.Material.Name}",
                Location = new Point(15, 30),
                Size = new Size(200, 25)
            };

            KryptonLabel minDensityLabel = new KryptonLabel
            {
                Text = $"Minimum Density: {_simulation.MinimumDensity:F1} kg/m³",
                Location = new Point(15, 60),
                Size = new Size(200, 25)
            };

            KryptonLabel maxDensityLabel = new KryptonLabel
            {
                Text = $"Maximum Density: {_simulation.MaximumDensity:F1} kg/m³",
                Location = new Point(15, 90),
                Size = new Size(200, 25)
            };

            KryptonLabel avgDensityLabel = new KryptonLabel
            {
                Text = $"Average Density: {_simulation.AverageDensity:F1} kg/m³",
                Location = new Point(15, 120),
                Size = new Size(200, 25)
            };

            KryptonLabel triangleCountLabel = new KryptonLabel
            {
                Text = $"Triangles: {_simulation.MeshTriangles.Count}",
                Location = new Point(15, 150),
                Size = new Size(200, 25)
            };

            KryptonLabel densityPointsLabel = new KryptonLabel
            {
                Text = $"Density Points: {_simulation.TriangleDensities.Count}",
                Location = new Point(15, 180),
                Size = new Size(200, 25)
            };

            // Simulation-specific stats
            KryptonLabel simulationLabel = new KryptonLabel
            {
                Text = $"Simulation Type: {_simulationType}",
                Location = new Point(15, 210),
                Size = new Size(200, 25)
            };

            // Add labels to stats group box
            _statisticsGroupBox.Panel.Controls.Add(materialLabel);
            _statisticsGroupBox.Panel.Controls.Add(minDensityLabel);
            _statisticsGroupBox.Panel.Controls.Add(maxDensityLabel);
            _statisticsGroupBox.Panel.Controls.Add(avgDensityLabel);
            _statisticsGroupBox.Panel.Controls.Add(triangleCountLabel);
            _statisticsGroupBox.Panel.Controls.Add(densityPointsLabel);
            _statisticsGroupBox.Panel.Controls.Add(simulationLabel);

            // Density legend panel
            Panel legendPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(45, 45, 48)
            };

            // Create the legend
            CreateDensityLegend(legendPanel);

            // Add group boxes to controls panel
            controlsPanel.Controls.Add(legendPanel);
            controlsPanel.Controls.Add(_statisticsGroupBox);
            controlsPanel.Controls.Add(_controlsGroupBox);

            // Add panels to main layout
            mainLayout.Controls.Add(_visualizationPanel, 0, 0);
            mainLayout.Controls.Add(controlsPanel, 1, 0);

            // Add main layout to form
            this.Controls.Add(mainLayout);

            // Set up resize event to redraw
            this.Resize += (s, e) => _visualizationPanel.Invalidate();

            // Add tooltips for controls
            ToolTip toolTip = new ToolTip();
            toolTip.SetToolTip(_visualizationPanel, "Left-click + Drag: Rotate\nRight-click + Drag: Pan\nMouse Wheel: Zoom");
            toolTip.SetToolTip(_resetViewButton, "Reset the view to default rotation, position, and zoom");
            toolTip.SetToolTip(_exportButton, "Export the current view as a PNG image");
            toolTip.SetToolTip(_showWireframeCheckBox, "Show triangle outlines");
            toolTip.SetToolTip(_opacityTrackBar, "Adjust the opacity of the triangles");
        }

        private void CreateDensityLegend(Panel panel)
        {
            // Legend picture box
            PictureBox legendBox = new PictureBox
            {
                Size = new Size(30, 200),
                Location = new Point(70, 20),
                BorderStyle = BorderStyle.FixedSingle
            };

            // Create legend bitmap
            Bitmap legendBitmap = new Bitmap(30, 200);
            using (Graphics g = Graphics.FromImage(legendBitmap))
            {
                // Create gradient from blue to red
                using (LinearGradientBrush brush = new LinearGradientBrush(
                    new Rectangle(0, 0, 30, 200),
                    Color.Blue, Color.Red, LinearGradientMode.Vertical))
                {
                    ColorBlend blend = new ColorBlend(5);
                    blend.Colors = new Color[] {
                        Color.Blue,
                        Color.Cyan,
                        Color.Green,
                        Color.Yellow,
                        Color.Red
                    };
                    blend.Positions = new float[] { 0.0f, 0.25f, 0.5f, 0.75f, 1.0f };
                    brush.InterpolationColors = blend;

                    g.FillRectangle(brush, 0, 0, 30, 200);
                }
            }

            legendBox.Image = legendBitmap;

            // Add labels
            Label legendTitle = new Label
            {
                Text = "Density (kg/m³)",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(40, 0)
            };

            Label maxLabel = new Label
            {
                Text = $"{_simulation.MaximumDensity:F0}",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(105, 20)
            };

            Label avgLabel = new Label
            {
                Text = $"{_simulation.AverageDensity:F0}",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(105, 120)
            };

            Label minLabel = new Label
            {
                Text = $"{_simulation.MinimumDensity:F0}",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(105, 220)
            };

            // Add to panel
            panel.Controls.Add(legendTitle);
            panel.Controls.Add(legendBox);
            panel.Controls.Add(maxLabel);
            panel.Controls.Add(avgLabel);
            panel.Controls.Add(minLabel);

            // Help text
            Label helpLabel = new Label
            {
                Text = "Navigation Help:\n" +
                      "• Left-click + drag: Rotate\n" +
                      "• Right-click + drag: Pan\n" +
                      "• Mouse wheel: Zoom\n" +
                      "• Reset View: Default position",
                ForeColor = Color.LightGray,
                AutoSize = true,
                Location = new Point(20, 250)
            };

            panel.Controls.Add(helpLabel);
        }

        private void VisualizationPanel_Paint(object sender, PaintEventArgs e)
        {
            if (_simulation != null)
            {
                // Enable high quality rendering
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

                // Create offscreen buffer for better performance
                using (Bitmap buffer = new Bitmap(_visualizationPanel.Width, _visualizationPanel.Height))
                using (Graphics bufferGraphics = Graphics.FromImage(buffer))
                {
                    bufferGraphics.SmoothingMode = SmoothingMode.AntiAlias;
                    bufferGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

                    // Clear background
                    bufferGraphics.Clear(Color.Black);

                    // Set up custom rendering with our view parameters
                    RenderDensityDistribution(bufferGraphics, _visualizationPanel.Width, _visualizationPanel.Height);

                    // Draw the buffer to the screen
                    e.Graphics.DrawImage(buffer, 0, 0);
                }
            }
            else
            {
                // Draw an error message if no simulation is available
                e.Graphics.Clear(Color.Black);
                using (Font font = new Font("Arial", 12))
                using (SolidBrush brush = new SolidBrush(Color.Red))
                {
                    e.Graphics.DrawString("No simulation data available", font, brush, 20, 20);
                }
            }
        }

        private void RenderDensityDistribution(Graphics g, int width, int height)
        {
            // Set up projection parameters
            float scale = Math.Min(width, height) / 200.0f * _zoomLevel;
            float centerX = width / 2.0f + _panOffset.X;
            float centerY = height / 2.0f + _panOffset.Y;

            // Get opacity from trackbar
            int opacity = _opacityTrackBar.Value * 255 / 100;

            // Find maximum coordinate for normalization
            float maxCoord = FindMaxCoordinate();

            // Prepare triangles with depth sorting info
            List<TriangleRenderInfo> trianglesToDraw = new List<TriangleRenderInfo>();

            foreach (var tri in _simulation.MeshTriangles)
            {
                // Average Z for depth sorting
                float avgZ = (tri.V1.Z + tri.V2.Z + tri.V3.Z) / 3.0f;

                // Get density if available
                float density = (float)_simulation.Material.Density; // Default
                if (_simulation.TriangleDensities.TryGetValue(tri, out float triDensity))
                {
                    density = triDensity;
                }

                // Normalize density to 0-1 range for color mapping
                float normalizedDensity = (_simulation.MaximumDensity <= _simulation.MinimumDensity) ? 0.5f :
                    (density - _simulation.MinimumDensity) / (_simulation.MaximumDensity - _simulation.MinimumDensity);

                // Get color based on density
                Color triColor = GetHeatMapColor(normalizedDensity, 0, 1);

                trianglesToDraw.Add(new TriangleRenderInfo
                {
                    Triangle = tri,
                    Depth = avgZ,
                    Color = triColor
                });
            }

            // Sort back to front for proper rendering
            trianglesToDraw.Sort((a, b) => -a.Depth.CompareTo(b.Depth));

            // Draw triangles
            foreach (var triInfo in trianglesToDraw)
            {
                // Project vertices
                PointF p1 = ProjectVertex(triInfo.Triangle.V1, centerX, centerY, scale, maxCoord);
                PointF p2 = ProjectVertex(triInfo.Triangle.V2, centerX, centerY, scale, maxCoord);
                PointF p3 = ProjectVertex(triInfo.Triangle.V3, centerX, centerY, scale, maxCoord);

                // Create triangle points array
                PointF[] points = { p1, p2, p3 };

                // Fill triangle with density-based color
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(opacity, triInfo.Color)))
                {
                    g.FillPolygon(brush, points);
                }

                // Draw wireframe if enabled
                if (_showWireframeCheckBox.Checked)
                {
                    using (Pen pen = new Pen(Color.FromArgb(100, Color.Black), 1))
                    {
                        g.DrawPolygon(pen, points);
                    }
                }
            }

            // Draw info overlay
            using (Font font = new Font("Arial", 10))
            using (SolidBrush brush = new SolidBrush(Color.White))
            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(128, 0, 0, 0)))
            {
                string infoText = $"Triangles: {trianglesToDraw.Count} | Zoom: {_zoomLevel:F1}x | Rotation: {_rotationX:F1}, {_rotationY:F1}";
                SizeF textSize = g.MeasureString(infoText, font);

                // Background for info text
                g.FillRectangle(bgBrush, 10, height - 30, textSize.Width + 10, textSize.Height + 5);
                g.DrawString(infoText, font, brush, 15, height - 28);

                // Title
                string title = $"{_simulationType} Density Distribution - {_simulation.Material.Name}";
                SizeF titleSize = g.MeasureString(title, new Font("Arial", 14, FontStyle.Bold));
                g.FillRectangle(bgBrush, 10, 10, titleSize.Width + 10, titleSize.Height + 5);
                g.DrawString(title, new Font("Arial", 14, FontStyle.Bold), brush, 15, 12);
            }
        }

        private float FindMaxCoordinate()
        {
            float maxCoord = 0;
            foreach (var tri in _simulation.MeshTriangles)
            {
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V1.X));
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V1.Y));
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V1.Z));
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V2.X));
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V2.Y));
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V2.Z));
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V3.X));
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V3.Y));
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V3.Z));
            }
            return maxCoord > 0 ? maxCoord : 1.0f;
        }

        private PointF ProjectVertex(Vector3 vertex, float centerX, float centerY, float scale, float maxCoord)
        {
            // Normalize coordinates to -0.5 to 0.5 range
            float nx = vertex.X / maxCoord - 0.5f;
            float ny = vertex.Y / maxCoord - 0.5f;
            float nz = vertex.Z / maxCoord - 0.5f;

            // Apply rotation around Y axis first
            float cosY = (float)Math.Cos(_rotationY);
            float sinY = (float)Math.Sin(_rotationY);
            float tx = nx * cosY + nz * sinY;
            float ty = ny;
            float tz = -nx * sinY + nz * cosY;

            // Then apply rotation around X axis
            float cosX = (float)Math.Cos(_rotationX);
            float sinX = (float)Math.Sin(_rotationX);
            float rx = tx;
            float ry = ty * cosX - tz * sinX;
            float rz = ty * sinX + tz * cosX;

            // Simple perspective projection
            float perspective = 1.5f + rz;
            float projX = centerX + rx * scale * 150 / perspective;
            float projY = centerY + ry * scale * 150 / perspective;

            return new PointF(projX, projY);
        }

        private Color GetHeatMapColor(float value, float min, float max)
        {
            // Normalize value to 0-1 range
            float normalized = Math.Max(0, Math.Min(1, (value - min) / (max - min)));

            // Create a heatmap gradient: blue -> cyan -> green -> yellow -> red
            if (normalized < 0.25f)
            {
                // Blue to cyan
                float t = normalized / 0.25f;
                return Color.FromArgb(
                    0,
                    (int)(255 * t),
                    255);
            }
            else if (normalized < 0.5f)
            {
                // Cyan to green
                float t = (normalized - 0.25f) / 0.25f;
                return Color.FromArgb(
                    0,
                    255,
                    (int)(255 * (1 - t)));
            }
            else if (normalized < 0.75f)
            {
                // Green to yellow
                float t = (normalized - 0.5f) / 0.25f;
                return Color.FromArgb(
                    (int)(255 * t),
                    255,
                    0);
            }
            else
            {
                // Yellow to red
                float t = (normalized - 0.75f) / 0.25f;
                return Color.FromArgb(
                    255,
                    (int)(255 * (1 - t)),
                    0);
            }
        }

        private void VisualizationPanel_MouseDown(object sender, MouseEventArgs e)
        {
            _lastMousePosition = e.Location;

            if (e.Button == MouseButtons.Left)
            {
                _isRotating = true;
                // Turn off auto-rotation when user manually rotates
                if (_autoRotate && _rotationTimer != null)
                {
                    _autoRotate = false;
                    _rotationTimer.Enabled = false;
                    if (_controlsGroupBox.Panel.Controls.Find("autoRotateCheckBox", true).FirstOrDefault() is KryptonCheckBox checkBox)
                    {
                        checkBox.Checked = false;
                    }
                }
            }
            else if (e.Button == MouseButtons.Right)
            {
                _isPanning = true;
            }
        }

        private void VisualizationPanel_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isRotating)
            {
                float deltaX = (e.X - _lastMousePosition.X) * 0.01f;
                float deltaY = (e.Y - _lastMousePosition.Y) * 0.01f;

                _rotationY += deltaX;
                _rotationX += deltaY;

                // Limit vertical rotation to avoid flipping
                _rotationX = Math.Max(Math.Min(_rotationX, (float)Math.PI / 2), -(float)Math.PI / 2);

                _visualizationPanel.Invalidate();
            }
            else if (_isPanning)
            {
                float deltaX = (e.X - _lastMousePosition.X);
                float deltaY = (e.Y - _lastMousePosition.Y);

                _panOffset.X += deltaX;
                _panOffset.Y += deltaY;

                _visualizationPanel.Invalidate();
            }

            _lastMousePosition = e.Location;
        }

        private void VisualizationPanel_MouseUp(object sender, MouseEventArgs e)
        {
            _isRotating = false;
            _isPanning = false;
        }

        private void VisualizationPanel_MouseWheel(object sender, MouseEventArgs e)
        {
            float zoomDelta = e.Delta > 0 ? 0.1f : -0.1f;
            _zoomLevel += zoomDelta;

            // Limit zoom range
            _zoomLevel = Math.Max(Math.Min(_zoomLevel, 5.0f), 0.1f);

            _visualizationPanel.Invalidate();
        }

        private void ExportButton_Click(object sender, EventArgs e)
        {
            try
            {
                using (SaveFileDialog saveDialog = new SaveFileDialog())
                {
                    saveDialog.Filter = "PNG Image|*.png";
                    saveDialog.Title = "Save Density Distribution Image";
                    saveDialog.FileName = $"{_simulationType}DensityDistribution_{DateTime.Now:yyyyMMdd_HHmmss}.png";

                    if (saveDialog.ShowDialog() == DialogResult.OK)
                    {
                        this.Cursor = Cursors.WaitCursor;

                        // Create a high-quality bitmap
                        using (Bitmap bmp = new Bitmap(_visualizationPanel.Width, _visualizationPanel.Height))
                        {
                            // Draw to the bitmap with high quality settings
                            using (Graphics g = Graphics.FromImage(bmp))
                            {
                                g.SmoothingMode = SmoothingMode.AntiAlias;
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.Clear(Color.Black);

                                RenderDensityDistribution(g, bmp.Width, bmp.Height);
                            }

                            // Save the bitmap
                            bmp.Save(saveDialog.FileName, System.Drawing.Imaging.ImageFormat.Png);
                        }

                        this.Cursor = Cursors.Default;
                        MessageBox.Show($"Image saved to {saveDialog.FileName}", "Export Successful",
                                       MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                this.Cursor = Cursors.Default;
                MessageBox.Show($"Error exporting image: {ex.Message}", "Export Error",
                               MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Clean up resources
            if (_rotationTimer != null)
            {
                _rotationTimer.Stop();
                _rotationTimer.Dispose();
                _rotationTimer = null;
            }

            base.OnFormClosing(e);
        }

        private class TriangleRenderInfo
        {
            public Triangle Triangle { get; set; }
            public float Depth { get; set; }
            public Color Color { get; set; }
        }
    }
}