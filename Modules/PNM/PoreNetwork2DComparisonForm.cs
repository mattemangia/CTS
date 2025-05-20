using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;

namespace CTS
{
    /// <summary>
    /// Form for comparing 2D thin section images with 3D pore networks
    /// </summary>
    public class PoreNetwork2DComparisonForm : Form
    {
        // Parent form and data references
        private MainForm mainForm;
        private PoreNetworkModel networkModel;
        private Material selectedMaterial;
        private double pixelSize;

        // UI Components
        private TabControl mainTabControl;
        private TabPage view3DTab;
        private TabPage view2DTab;
        private Label statusLabel;
        private ProgressBar progressBar;

        // 3D view components
        private Panel visualization3DPanel;
        private PictureBox network3DPictureBox;
        private Button resetViewButton;
        private Button screenshotButton;

        // 2D view components
        private Panel thinSectionPanel;
        private PictureBox thinSectionPictureBox;
        private Button loadThinSectionButton;
        private Button selectPoreColorButton;
        private Button setScaleButton;
        private Button extractPorosityButton;
        private Button calculateMatchesButton;
        private Button exportCombinedButton;
        private ComboBox colorToleranceComboBox;

        // Match results components
        private DataGridView matchResultsGridView;
        private NumericUpDown matchCountNumeric;

        // 3D view parameters
        private float rotationX = 30.0f;
        private float rotationY = 30.0f;
        private float rotationZ = 0.0f;
        private float viewScale = 1.0f;
        private Point lastMousePosition;
        private bool isDragging = false;
        private bool isPanning = false;
        private float panOffsetX = 0.0f;
        private float panOffsetY = 0.0f;

        // Thin section properties
        private Bitmap thinSectionImage = null;
        private Bitmap processedThinSectionImage = null;
        private Color selectedPoreColor = Color.Black;
        private int colorTolerance = 30;
        private double scaleFactorMicronsPerPixel = 1.0;
        private PointF scaleStartPoint;
        private PointF scaleEndPoint;
        private bool isSettingScale = false;
        private double thinSectionPhysicalWidth = 0; // in micrometers
        private double thinSectionPhysicalHeight = 0; // in micrometers

        // 2D Pore network properties
        private List<Pore2D> pores2D = new List<Pore2D>();
        private List<Throat2D> throats2D = new List<Throat2D>();
        private double porosity2D = 0;

        // Match results
        private List<OrientationMatch> orientationMatches = new List<OrientationMatch>();

        // GPU Processing
        private Context gpuContext;
        private Accelerator accelerator;
        private bool gpuInitialized = false;
        private CancellationTokenSource cancellationTokenSource;

        /// <summary>
        /// Initializes a new instance of the form for comparing 2D thin sections with 3D networks
        /// </summary>
        /// <param name="mainForm">Reference to the main application form</param>
        /// <param name="networkModel">The 3D pore network model to compare</param>
        /// <param name="selectedMaterial">The material used for the network</param>
        /// <param name="pixelSize">The pixel size in meters</param>
        public PoreNetwork2DComparisonForm(MainForm mainForm, PoreNetworkModel networkModel, Material selectedMaterial, double pixelSize)
        {
            this.mainForm = mainForm;
            this.networkModel = networkModel;
            this.selectedMaterial = selectedMaterial;
            this.pixelSize = pixelSize;

            InitializeComponent();
            InitializeGPU();

            // Update window title with network info
            this.Text = $"2D-3D Pore Network Comparison - {networkModel.Pores.Count} pores, Porosity: {networkModel.Porosity:P2}";

            // Render the initial 3D network
            RenderNetwork3D();
        }

        private void InitializeComponent()
        {
            // Form setup
            this.Size = new Size(1280, 900);
            this.MinimumSize = new Size(1024, 768);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Text = "2D-3D Pore Network Comparison";
            this.BackColor = Color.FromArgb(240, 240, 240);
            this.Font = new Font("Segoe UI", 9F);

            try
            {
                this.Icon = Properties.Resources.favicon;
            }
            catch { /* Ignore if icon not available */ }

            // Main tab control
            mainTabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Padding = new Point(15, 5)
            };

            // Create 3D view tab
            CreateNetwork3DTab();

            // Create 2D view tab
            CreateThinSectionTab();

            // Status bar at the bottom
            Panel statusPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 25,
                BackColor = Color.FromArgb(230, 230, 230)
            };

            progressBar = new ProgressBar
            {
                Location = new Point(10, 3),
                Width = 250,
                Height = 20,
                Style = ProgressBarStyle.Continuous,
                Value = 0
            };
            statusPanel.Controls.Add(progressBar);

            statusLabel = new Label
            {
                Text = "Ready",
                Location = new Point(270, 3),
                Width = 900,
                Height = 20,
                Font = new Font("Segoe UI", 8F),
                ForeColor = Color.DarkBlue
            };
            statusPanel.Controls.Add(statusLabel);

            // Add controls to form
            this.Controls.Add(mainTabControl);
            this.Controls.Add(statusPanel);

            // Event handlers
            this.FormClosing += PoreNetwork2DComparisonForm_FormClosing;
        }
        private void CreateNetwork3DTab()
        {
            // Create 3D network view tab
            view3DTab = new TabPage("3D Network View");
            view3DTab.Padding = new Padding(3);

            // Main container panel for the entire tab
            Panel mainPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(0)
            };
            view3DTab.Controls.Add(mainPanel);

            // Create right-side match results panel (fixed width)
            Panel matchResultsPanel = new Panel
            {
                Width = 250,
                Dock = DockStyle.Right,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(30, 30, 30)
            };

            // Add label at top of match results panel
            Label matchResultsLabel = new Label
            {
                Text = "Orientation Match Results",
                Dock = DockStyle.Top,
                Height = 25,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(40, 40, 40),
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            matchResultsPanel.Controls.Add(matchResultsLabel);

            // Control panel for match options
            Panel matchControlPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 35,
                BackColor = Color.FromArgb(40, 40, 40),
                Padding = new Padding(5)
            };

            Label matchCountLabel = new Label
            {
                Text = "Match Count:",
                Location = new Point(10, 8),
                AutoSize = true,
                ForeColor = Color.White
            };
            matchControlPanel.Controls.Add(matchCountLabel);

            matchCountNumeric = new NumericUpDown
            {
                Location = new Point(100, 6),
                Width = 60,
                Minimum = 1,
                Maximum = 50,
                Value = 10,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            matchCountNumeric.ValueChanged += (s, e) =>
            {
                if (orientationMatches.Count > 0)
                    CalculateMatches();
            };
            matchControlPanel.Controls.Add(matchCountNumeric);

            Button recalculateButton = new Button
            {
                Text = "Recalc.",
                Location = new Point(170, 5),
                Width = 70,
                Height = 25,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(80, 80, 80),
                ForeColor = Color.White
            };
            recalculateButton.Click += (s, e) => CalculateMatches();
            matchControlPanel.Controls.Add(recalculateButton);

            matchResultsPanel.Controls.Add(matchControlPanel);
            matchControlPanel.BringToFront();

            // Create results grid
            matchResultsGridView = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                BackgroundColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.White,
                GridColor = Color.Gray,
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false,
                EnableHeadersVisualStyles = false
            };

            // Set column styles
            matchResultsGridView.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };

            // Define columns
            matchResultsGridView.Columns.Add("Rank", "Rank");
            matchResultsGridView.Columns.Add("MatchPercent", "Match %");
            matchResultsGridView.Columns.Add("Orientation", "Orientation");
            matchResultsGridView.Columns.Add("Position", "Position");
            matchResultsGridView.Columns.Add("Porosity", "Porosity Ratio");

            // Set column properties
            matchResultsGridView.Columns["Rank"].Width = 50;
            matchResultsGridView.Columns["MatchPercent"].Width = 70;
            matchResultsGridView.Columns["Orientation"].Width = 90;
            matchResultsGridView.Columns["Position"].Width = 90;
            matchResultsGridView.Columns["Porosity"].Width = 80;

            // Selection changed event
            matchResultsGridView.SelectionChanged += (s, e) =>
            {
                if (matchResultsGridView.SelectedRows.Count > 0)
                {
                    int rowIndex = matchResultsGridView.SelectedRows[0].Index;
                    if (rowIndex >= 0 && rowIndex < orientationMatches.Count)
                    {
                        // Get selected orientation and update 3D view
                        RenderNetwork3D(rowIndex);
                    }
                }
            };

            matchResultsPanel.Controls.Add(matchResultsGridView);

            // Create 3D view panel (fill remaining space)
            visualization3DPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.Black
            };

            // Control panel at top of 3D view
            Panel controlPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 40,
                BackColor = Color.FromArgb(50, 50, 50)
            };

            Label rotationLabel = new Label
            {
                Text = "Rotation:",
                Location = new Point(10, 12),
                ForeColor = Color.White,
                AutoSize = true
            };
            controlPanel.Controls.Add(rotationLabel);

            resetViewButton = new Button
            {
                Text = "Reset View",
                Location = new Point(150, 8),
                Width = 100,
                Height = 25,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(80, 80, 80),
                ForeColor = Color.White
            };
            resetViewButton.Click += ResetViewButton_Click;
            controlPanel.Controls.Add(resetViewButton);

            screenshotButton = new Button
            {
                Text = "Save Screenshot",
                Location = new Point(260, 8),
                Width = 130,
                Height = 25,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(80, 80, 80),
                ForeColor = Color.White
            };
            screenshotButton.Click += ScreenshotButton_Click;
            controlPanel.Controls.Add(screenshotButton);

            // Match rendering options
            CheckBox showMatchesCheckbox = new CheckBox
            {
                Text = "Show Match Orientations",
                Location = new Point(410, 10),
                Width = 180,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Checked = true
            };
            showMatchesCheckbox.CheckedChanged += (s, e) => RenderNetwork3D();
            controlPanel.Controls.Add(showMatchesCheckbox);

            // Add a marker for the currently selected match
            CheckBox highlightMatchCheckbox = new CheckBox
            {
                Text = "Highlight Selected Match",
                Location = new Point(600, 10),
                Width = 180,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Checked = true
            };
            highlightMatchCheckbox.CheckedChanged += (s, e) => RenderNetwork3D();
            controlPanel.Controls.Add(highlightMatchCheckbox);

            visualization3DPanel.Controls.Add(controlPanel);

            // Create the PictureBox for 3D rendering
            network3DPictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.CenterImage
            };

            // Add mouse handling
            network3DPictureBox.MouseDown += Network3DPictureBox_MouseDown;
            network3DPictureBox.MouseMove += Network3DPictureBox_MouseMove;
            network3DPictureBox.MouseUp += Network3DPictureBox_MouseUp;
            network3DPictureBox.MouseWheel += Network3DPictureBox_MouseWheel;

            // Add instructions label
            Label instructionsLabel = new Label
            {
                Text = "Left-click and drag to rotate | Middle-click and drag to pan | Mouse wheel to zoom",
                Dock = DockStyle.Bottom,
                Height = 25,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.LightGray,
                BackColor = Color.FromArgb(20, 20, 20)
            };

            visualization3DPanel.Controls.Add(network3DPictureBox);
            visualization3DPanel.Controls.Add(instructionsLabel);

            // Add panels to the main panel
            mainPanel.Controls.Add(visualization3DPanel);
            mainPanel.Controls.Add(matchResultsPanel);

            // Add the tab to the tab control
            mainTabControl.Controls.Add(view3DTab);
        }
        private void CreateThinSectionTab()
        {
            // Create 2D view tab
            view2DTab = new TabPage("2D Thin Section");
            view2DTab.Padding = new Padding(3);

            // Main panel for the tab
            Panel mainPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(0)
            };
            view2DTab.Controls.Add(mainPanel);

            // Top panel for tools - fixed height
            Panel toolsPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 100,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(230, 230, 230),
                Padding = new Padding(5)
            };

            // Create tool buttons
            loadThinSectionButton = new Button
            {
                Text = "Load Thin Section",
                Location = new Point(10, 10),
                Size = new Size(140, 35),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(100, 140, 200),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            loadThinSectionButton.Click += LoadThinSectionButton_Click;
            toolsPanel.Controls.Add(loadThinSectionButton);

            selectPoreColorButton = new Button
            {
                Text = "Select Pore Color",
                Location = new Point(160, 10),
                Size = new Size(140, 35),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(220, 220, 220),
                Enabled = false
            };
            selectPoreColorButton.Click += SelectPoreColorButton_Click;
            toolsPanel.Controls.Add(selectPoreColorButton);

            // Add color tolerance slider
            Label toleranceLabel = new Label
            {
                Text = "Color Tolerance:",
                Location = new Point(310, 20),
                AutoSize = true
            };
            toolsPanel.Controls.Add(toleranceLabel);

            colorToleranceComboBox = new ComboBox
            {
                Location = new Point(410, 17),
                Width = 70,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            // Add tolerance values
            colorToleranceComboBox.Items.AddRange(new object[] { "5", "10", "15", "20", "30", "40", "50" });
            colorToleranceComboBox.SelectedIndex = 3; // Default to 20
            colorToleranceComboBox.SelectedIndexChanged += (s, e) =>
            {
                colorTolerance = int.Parse(colorToleranceComboBox.SelectedItem.ToString());
                ProcessThinSectionImage();
            };
            colorToleranceComboBox.Enabled = false;
            toolsPanel.Controls.Add(colorToleranceComboBox);

            setScaleButton = new Button
            {
                Text = "Set Scale",
                Location = new Point(490, 10),
                Size = new Size(120, 35),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(220, 220, 220),
                Enabled = false
            };
            setScaleButton.Click += SetScaleButton_Click;
            toolsPanel.Controls.Add(setScaleButton);

            extractPorosityButton = new Button
            {
                Text = "Extract Porosity",
                Location = new Point(620, 10),
                Size = new Size(120, 35),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(220, 220, 220),
                Enabled = false
            };
            extractPorosityButton.Click += ExtractPorosityButton_Click;
            toolsPanel.Controls.Add(extractPorosityButton);

            calculateMatchesButton = new Button
            {
                Text = "Calculate Matches",
                Location = new Point(750, 10),
                Size = new Size(140, 35),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(220, 220, 220),
                Enabled = false
            };
            calculateMatchesButton.Click += CalculateMatchesButton_Click;
            toolsPanel.Controls.Add(calculateMatchesButton);

            exportCombinedButton = new Button
            {
                Text = "Export Combined View",
                Location = new Point(900, 10),
                Size = new Size(150, 35),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(220, 220, 220),
                Enabled = false
            };
            exportCombinedButton.Click += ExportCombinedButton_Click;
            toolsPanel.Controls.Add(exportCombinedButton);

            // Add processing info row
            Label processingInfoLabel = new Label
            {
                Text = "No thin section loaded",
                Location = new Point(10, 60),
                Size = new Size(1000, 20),
                ForeColor = Color.DarkBlue
            };
            toolsPanel.Controls.Add(processingInfoLabel);

            // Panel for thin section view (fills remaining space)
            thinSectionPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.Black
            };

            thinSectionPictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black
            };

            // Add mouse events for scale setting
            thinSectionPictureBox.MouseDown += ThinSectionPictureBox_MouseDown;
            thinSectionPictureBox.MouseMove += ThinSectionPictureBox_MouseMove;
            thinSectionPictureBox.MouseUp += ThinSectionPictureBox_MouseUp;

            thinSectionPanel.Controls.Add(thinSectionPictureBox);

            // Add panels to main panel
            mainPanel.Controls.Add(thinSectionPanel);
            mainPanel.Controls.Add(toolsPanel);

            // Add the tab to the tab control
            mainTabControl.Controls.Add(view2DTab);
        }
        private void InitializeGPU()
        {
            Task.Run(() =>
            {
                try
                {
                    gpuContext = Context.Create(builder => builder.Default().EnableAlgorithms());

                    // Try to use GPU first
                    foreach (var device in gpuContext.Devices)
                    {
                        try
                        {
                            if (device.AcceleratorType != AcceleratorType.CPU)
                            {
                                accelerator = device.CreateAccelerator(gpuContext);
                                Logger.Log($"[PoreNetwork2DComparisonForm] Using GPU accelerator: {device.Name}");
                                gpuInitialized = true;
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[PoreNetwork2DComparisonForm] Failed to initialize GPU device: {ex.Message}");
                        }
                    }

                    // Fall back to CPU if no GPU available
                    var cpuDevice = gpuContext.GetCPUDevice(0);
                    accelerator = cpuDevice.CreateAccelerator(gpuContext);
                    Logger.Log("[PoreNetwork2DComparisonForm] Using CPU accelerator");
                    gpuInitialized = true;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[PoreNetwork2DComparisonForm] Failed to initialize GPU: {ex.Message}");
                    gpuInitialized = false;
                }
            });
        }

        #region 3D Network Visualization Methods

        private void RenderNetwork3D(int selectedMatchIndex = -1)
        {
            if (networkModel == null || network3DPictureBox == null) return;

            int width = network3DPictureBox.Width;
            int height = network3DPictureBox.Height;

            if (width <= 0 || height <= 0) return;

            Bitmap networkImage = new Bitmap(Math.Max(1, width), Math.Max(1, height));

            using (Graphics g = Graphics.FromImage(networkImage))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Black);

                // Find model bounds and center point
                double minX = networkModel.Pores.Min(p => p.Center.X);
                double maxX = networkModel.Pores.Max(p => p.Center.X);
                double minY = networkModel.Pores.Min(p => p.Center.Y);
                double maxY = networkModel.Pores.Max(p => p.Center.Y);
                double minZ = networkModel.Pores.Min(p => p.Center.Z);
                double maxZ = networkModel.Pores.Max(p => p.Center.Z);

                double centerX = (minX + maxX) / 2;
                double centerY = (minY + maxY) / 2;
                double centerZ = (minZ + maxZ) / 2;

                // Calculate scale factor based on the model size
                double maxRange = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
                double scaleFactor = Math.Min(width, height) * 0.4 / maxRange * viewScale;

                // Create rotation matrices
                var rotationMatrix = Create3DRotationMatrix(rotationX, rotationY, rotationZ);

                // Get a reference to checkboxes to determine visualization options
                bool showMatches = true;
                bool highlightSelected = true;

                try
                {
                    foreach (Control control in visualization3DPanel.Controls)
                    {
                        if (control is Panel panel)
                        {
                            foreach (Control panelControl in panel.Controls)
                            {
                                if (panelControl is CheckBox checkbox)
                                {
                                    if (checkbox.Text.Contains("Show Match"))
                                        showMatches = checkbox.Checked;
                                    else if (checkbox.Text.Contains("Highlight"))
                                        highlightSelected = checkbox.Checked;
                                }
                            }
                        }
                    }
                }
                catch { /* Ignore any errors getting checkbox states */ }

                // Draw the match plane if we have orientation matches and show matches is enabled
                if (orientationMatches.Count > 0 && showMatches)
                {
                    // Get the current match to visualize
                    int matchIndex = selectedMatchIndex >= 0 && selectedMatchIndex < orientationMatches.Count
                                   ? selectedMatchIndex
                                   : 0;

                    var currentMatch = orientationMatches[matchIndex];

                    // Calculate the corners of the plane in the 3D space
                    List<Point3D> planeCorners = CalculatePlaneCorners(
                        currentMatch.Position,
                        currentMatch.Normal,
                        currentMatch.Orientation,
                        thinSectionPhysicalWidth,
                        thinSectionPhysicalHeight);

                    // Project the corners to 2D
                    List<Point> projectedCorners = new List<Point>();
                    foreach (var corner in planeCorners)
                    {
                        var transformed = Transform3DPoint(
                            corner.X - centerX,
                            corner.Y - centerY,
                            corner.Z - centerZ,
                            rotationMatrix);

                        projectedCorners.Add(new Point(
                            (int)(width / 2 + transformed.x * scaleFactor + panOffsetX * width),
                            (int)(height / 2 - transformed.y * scaleFactor + panOffsetY * height)));
                    }

                    // Draw the match plane with transparency
                    if (projectedCorners.Count >= 4)
                    {
                        Point[] polygonPoints = projectedCorners.ToArray();

                        // Fill with semi-transparent color
                        using (SolidBrush fillBrush = new SolidBrush(Color.FromArgb(100, 255, 255, 100)))
                        {
                            g.FillPolygon(fillBrush, polygonPoints);
                        }

                        // Draw border
                        using (Pen borderPen = new Pen(Color.Yellow, 2))
                        {
                            g.DrawPolygon(borderPen, polygonPoints);
                        }

                        // Draw normal vector from center of plane
                        Point3D center = new Point3D
                        {
                            X = currentMatch.Position.X,
                            Y = currentMatch.Position.Y,
                            Z = currentMatch.Position.Z
                        };

                        Point3D normalEnd = new Point3D
                        {
                            X = center.X + currentMatch.Normal.X * maxRange * 0.2,
                            Y = center.Y + currentMatch.Normal.Y * maxRange * 0.2,
                            Z = center.Z + currentMatch.Normal.Z * maxRange * 0.2
                        };

                        var transformedCenter = Transform3DPoint(
                            center.X - centerX, center.Y - centerY, center.Z - centerZ, rotationMatrix);

                        var transformedEnd = Transform3DPoint(
                            normalEnd.X - centerX, normalEnd.Y - centerY, normalEnd.Z - centerZ, rotationMatrix);

                        Point centerPoint = new Point(
                            (int)(width / 2 + transformedCenter.x * scaleFactor + panOffsetX * width),
                            (int)(height / 2 - transformedCenter.y * scaleFactor + panOffsetY * height));

                        Point endPoint = new Point(
                            (int)(width / 2 + transformedEnd.x * scaleFactor + panOffsetX * width),
                            (int)(height / 2 - transformedEnd.y * scaleFactor + panOffsetY * height));

                        using (Pen normalPen = new Pen(Color.Red, 2))
                        {
                            g.DrawLine(normalPen, centerPoint, endPoint);

                            // Draw arrowhead
                            const float arrowSize = 8F;
                            PointF[] arrowHead = GetArrowHead(centerPoint, endPoint, arrowSize);
                            g.FillPolygon(Brushes.Red, arrowHead);
                        }

                        // Add match info text
                        string matchInfo = $"Match #{matchIndex + 1}: {currentMatch.MatchPercentage:F1}% match - {GetOrientationName(currentMatch.Orientation)}";
                        using (Font font = new Font("Arial", 10, FontStyle.Bold))
                        {
                            g.DrawString(matchInfo, font, Brushes.Yellow, 20, 50);
                        }
                    }
                }

                // Project and render throats (connections between pores)
                var throatsWithDepth = new List<(double depth, Point p1, Point p2, float thickness, Color color)>();

                foreach (var throat in networkModel.Throats)
                {
                    var pore1 = networkModel.Pores.FirstOrDefault(p => p.Id == throat.PoreId1);
                    var pore2 = networkModel.Pores.FirstOrDefault(p => p.Id == throat.PoreId2);

                    if (pore1 != null && pore2 != null)
                    {
                        // Transform 3D coordinates to project to 2D
                        var transformedP1 = Transform3DPoint(
                            pore1.Center.X - centerX,
                            pore1.Center.Y - centerY,
                            pore1.Center.Z - centerZ,
                            rotationMatrix);

                        var transformedP2 = Transform3DPoint(
                            pore2.Center.X - centerX,
                            pore2.Center.Y - centerY,
                            pore2.Center.Z - centerZ,
                            rotationMatrix);

                        // Calculate projected points
                        Point p1 = new Point(
                            (int)(width / 2 + transformedP1.x * scaleFactor + panOffsetX * width),
                            (int)(height / 2 - transformedP1.y * scaleFactor + panOffsetY * height));

                        Point p2 = new Point(
                            (int)(width / 2 + transformedP2.x * scaleFactor + panOffsetX * width),
                            (int)(height / 2 - transformedP2.y * scaleFactor + panOffsetY * height));

                        // Calculate throat thickness
                        float thickness = (float)(throat.Radius * scaleFactor * 0.25);
                        thickness = Math.Max(1, thickness);

                        // Average Z for depth sorting
                        double avgZ = (transformedP1.z + transformedP2.z) / 2;

                        // Create gradient color based on depth
                        int intensity = (int)(100 + Math.Min(155, Math.Max(0, 155 * (1 - avgZ / 500))));
                        Color throatColor = Color.FromArgb(intensity, intensity, intensity);

                        throatsWithDepth.Add((avgZ, p1, p2, thickness, throatColor));
                    }
                }

                // Sort throats by depth and draw
                foreach (var (_, p1, p2, thickness, color) in throatsWithDepth.OrderBy(t => t.depth))
                {
                    using (Pen pen = new Pen(color, thickness))
                    {
                        g.DrawLine(pen, p1, p2);
                    }
                }

                // Project and render pores
                var poresWithDepth = new List<(double depth, int x, int y, int radius, Color color, Pore pore)>();

                // Check if we need to highlight pores near the match plane
                HashSet<int> highlightedPores = new HashSet<int>();

                if (selectedMatchIndex >= 0 && selectedMatchIndex < orientationMatches.Count && highlightSelected)
                {
                    var match = orientationMatches[selectedMatchIndex];
                    highlightedPores = new HashSet<int>(match.MatchingPoreIds);
                }

                foreach (var pore in networkModel.Pores)
                {
                    // Transform 3D coordinates to project to 2D
                    var transformed = Transform3DPoint(
                        pore.Center.X - centerX,
                        pore.Center.Y - centerY,
                        pore.Center.Z - centerZ,
                        rotationMatrix);

                    // Calculate projected point
                    int x = (int)(width / 2 + transformed.x * scaleFactor + panOffsetX * width);
                    int y = (int)(height / 2 - transformed.y * scaleFactor + panOffsetY * height);

                    // Calculate pore radius in screen space
                    int radius = Math.Max(3, (int)(pore.Radius * scaleFactor * 0.5));

                    // Determine color based on pore properties
                    Color poreColor;

                    if (highlightedPores.Contains(pore.Id))
                    {
                        // Highlight pores that are part of the match
                        poreColor = Color.Yellow;
                    }
                    else
                    {
                        // Normal coloring based on connections
                        int connCount = pore.ConnectionCount;
                        if (connCount <= 1)
                            poreColor = Color.Red;
                        else if (connCount == 2)
                            poreColor = Color.Orange;
                        else if (connCount <= 4)
                            poreColor = Color.Green;
                        else
                            poreColor = Color.Blue;
                    }

                    // Adjust color intensity based on Z depth
                    float intensity = (float)Math.Max(0.5f, Math.Min(1.0f, (transformed.z + 500) / 1000));
                    poreColor = AdjustColorIntensity(poreColor, intensity);

                    poresWithDepth.Add((transformed.z, x, y, radius, poreColor, pore));
                }

                // Sort and draw pores from back to front
                foreach (var (_, x, y, radius, color, pore) in poresWithDepth.OrderBy(p => p.depth))
                {
                    g.FillEllipse(new SolidBrush(color), x - radius, y - radius, radius * 2, radius * 2);
                    g.DrawEllipse(Pens.White, x - radius, y - radius, radius * 2, radius * 2);
                }

                // Draw coordinate axes for orientation
                DrawCoordinateAxes(g, width, height, scaleFactor * 0.2, rotationMatrix);

                // Add legend
                DrawLegendAndStats(g, width, height);
            }

            network3DPictureBox.Image = networkImage;
        }

        private PointF[] GetArrowHead(Point start, Point end, float size)
        {
            // Calculate direction vector
            float dx = end.X - start.X;
            float dy = end.Y - start.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);

            // Normalize
            if (length > 0)
            {
                dx /= length;
                dy /= length;
            }

            // Calculate perpendicular vector
            float perpX = -dy;
            float perpY = dx;

            // Calculate arrow points
            PointF[] arrowHead = new PointF[3];
            arrowHead[0] = end;
            arrowHead[1] = new PointF(end.X - size * dx + size * 0.5f * perpX,
                                      end.Y - size * dy + size * 0.5f * perpY);
            arrowHead[2] = new PointF(end.X - size * dx - size * 0.5f * perpX,
                                      end.Y - size * dy - size * 0.5f * perpY);

            return arrowHead;
        }

        private string GetOrientationName(string orientation)
        {
            switch (orientation)
            {
                case "XY": return "XY Plane (Horizontal)";
                case "XZ": return "XZ Plane (Front-Back)";
                case "YZ": return "YZ Plane (Side)";
                default: return orientation;
            }
        }

        private List<Point3D> CalculatePlaneCorners(Point3D position, Point3D normal, string orientation,
                                                  double width, double height)
        {
            // Define basis vectors according to orientation
            Point3D uVector, vVector;

            switch (orientation)
            {
                case "XY":
                    uVector = new Point3D { X = 1, Y = 0, Z = 0 };
                    vVector = new Point3D { X = 0, Y = 1, Z = 0 };
                    break;
                case "XZ":
                    uVector = new Point3D { X = 1, Y = 0, Z = 0 };
                    vVector = new Point3D { X = 0, Y = 0, Z = 1 };
                    break;
                case "YZ":
                    uVector = new Point3D { X = 0, Y = 1, Z = 0 };
                    vVector = new Point3D { X = 0, Y = 0, Z = 1 };
                    break;
                default:
                    // For arbitrary planes, compute perpendicular vectors
                    uVector = ComputePerpendicularVector(normal);
                    vVector = CrossProduct(normal, uVector);
                    NormalizeVector(ref uVector);
                    NormalizeVector(ref vVector);
                    break;
            }

            // Scale vectors by width and height
            ScaleVector(ref uVector, width / 2);
            ScaleVector(ref vVector, height / 2);

            // Calculate corners
            List<Point3D> corners = new List<Point3D>();
            corners.Add(new Point3D
            {
                X = position.X - uVector.X - vVector.X,
                Y = position.Y - uVector.Y - vVector.Y,
                Z = position.Z - uVector.Z - vVector.Z
            });
            corners.Add(new Point3D
            {
                X = position.X + uVector.X - vVector.X,
                Y = position.Y + uVector.Y - vVector.Y,
                Z = position.Z + uVector.Z - vVector.Z
            });
            corners.Add(new Point3D
            {
                X = position.X + uVector.X + vVector.X,
                Y = position.Y + uVector.Y + vVector.Y,
                Z = position.Z + uVector.Z + vVector.Z
            });
            corners.Add(new Point3D
            {
                X = position.X - uVector.X + vVector.X,
                Y = position.Y - uVector.Y + vVector.Y,
                Z = position.Z - uVector.Z + vVector.Z
            });

            return corners;
        }

        private Point3D ComputePerpendicularVector(Point3D v)
        {
            Point3D result;

            // Find the non-zero component with smallest absolute value
            double absX = Math.Abs(v.X);
            double absY = Math.Abs(v.Y);
            double absZ = Math.Abs(v.Z);

            if (absX <= absY && absX <= absZ)
            {
                // X is smallest, use Y-Z plane
                result = new Point3D { X = 0, Y = -v.Z, Z = v.Y };
            }
            else if (absY <= absX && absY <= absZ)
            {
                // Y is smallest, use X-Z plane
                result = new Point3D { X = -v.Z, Y = 0, Z = v.X };
            }
            else
            {
                // Z is smallest, use X-Y plane
                result = new Point3D { X = -v.Y, Y = v.X, Z = 0 };
            }

            return result;
        }

        private Point3D CrossProduct(Point3D a, Point3D b)
        {
            return new Point3D
            {
                X = a.Y * b.Z - a.Z * b.Y,
                Y = a.Z * b.X - a.X * b.Z,
                Z = a.X * b.Y - a.Y * b.X
            };
        }

        private void NormalizeVector(ref Point3D v)
        {
            double length = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            if (length > 0)
            {
                v.X /= length;
                v.Y /= length;
                v.Z /= length;
            }
        }

        private void ScaleVector(ref Point3D v, double scale)
        {
            v.X *= scale;
            v.Y *= scale;
            v.Z *= scale;
        }

        private void DrawCoordinateAxes(Graphics g, int width, int height, double scale, double[,] rotationMatrix)
        {
            // Origin point in bottom right corner with some margin
            Point origin = new Point(width - 120, height - 120);

            // X-axis (red)
            var transformedX = Transform3DPoint(scale, 0, 0, rotationMatrix);
            Point xPoint = new Point(
                (int)(origin.X + transformedX.x * 50),
                (int)(origin.Y - transformedX.y * 50));
            g.DrawLine(new Pen(Color.Red, 2), origin, xPoint);
            g.DrawString("X", new Font("Arial", 8, FontStyle.Bold), Brushes.Red, xPoint);

            // Y-axis (green)
            var transformedY = Transform3DPoint(0, scale, 0, rotationMatrix);
            Point yPoint = new Point(
                (int)(origin.X + transformedY.x * 50),
                (int)(origin.Y - transformedY.y * 50));
            g.DrawLine(new Pen(Color.Green, 2), origin, yPoint);
            g.DrawString("Y", new Font("Arial", 8, FontStyle.Bold), Brushes.Green, yPoint);

            // Z-axis (blue)
            var transformedZ = Transform3DPoint(0, 0, scale, rotationMatrix);
            Point zPoint = new Point(
                (int)(origin.X + transformedZ.x * 50),
                (int)(origin.Y - transformedZ.y * 50));
            g.DrawLine(new Pen(Color.Blue, 2), origin, zPoint);
            g.DrawString("Z", new Font("Arial", 8, FontStyle.Bold), Brushes.Blue, zPoint);
        }

        private void DrawLegendAndStats(Graphics g, int width, int height)
        {
            // Add statistics
            string[] stats = {
                $"Pores: {networkModel.Pores.Count}",
                $"Throats: {networkModel.Throats.Count}",
                $"Porosity: {networkModel.Porosity:P2}",
                $"Tortuosity: {networkModel.Tortuosity:F2}"
            };

            Font font = new Font("Arial", 10, FontStyle.Bold);
            int yPos = height - 120;

            foreach (string stat in stats)
            {
                g.FillRectangle(new SolidBrush(Color.FromArgb(100, 0, 0, 0)),
                    10, yPos, g.MeasureString(stat, font).Width + 10, 20);
                g.DrawString(stat, font, Brushes.White, 15, yPos);
                yPos += 20;
            }

            // Add color legend
            string[] legends = {
                "Red: 0-1 connections",
                "Orange: 2 connections",
                "Green: 3-4 connections",
                "Blue: 5+ connections",
                "Yellow: Matching pores"
            };

            yPos = 15;
            foreach (string text in legends)
            {
                g.DrawString(text, new Font("Arial", 8), Brushes.White, width - 150, yPos);
                yPos += 15;
            }
        }

        private double[,] Create3DRotationMatrix(float angleX, float angleY, float angleZ)
        {
            // Convert angles to radians
            double radX = angleX * Math.PI / 180.0;
            double radY = angleY * Math.PI / 180.0;
            double radZ = angleZ * Math.PI / 180.0;

            // Precompute sine and cosine values
            double sinX = Math.Sin(radX);
            double cosX = Math.Cos(radX);
            double sinY = Math.Sin(radY);
            double cosY = Math.Cos(radY);
            double sinZ = Math.Sin(radZ);
            double cosZ = Math.Cos(radZ);

            // Create combined rotation matrix
            double[,] matrix = new double[3, 3];

            matrix[0, 0] = cosY * cosZ;
            matrix[0, 1] = cosY * sinZ;
            matrix[0, 2] = -sinY;

            matrix[1, 0] = sinX * sinY * cosZ - cosX * sinZ;
            matrix[1, 1] = sinX * sinY * sinZ + cosX * cosZ;
            matrix[1, 2] = sinX * cosY;

            matrix[2, 0] = cosX * sinY * cosZ + sinX * sinZ;
            matrix[2, 1] = cosX * sinY * sinZ - sinX * cosZ;
            matrix[2, 2] = cosX * cosY;

            return matrix;
        }

        private (double x, double y, double z) Transform3DPoint(double x, double y, double z, double[,] rotationMatrix)
        {
            double newX = x * rotationMatrix[0, 0] + y * rotationMatrix[0, 1] + z * rotationMatrix[0, 2];
            double newY = x * rotationMatrix[1, 0] + y * rotationMatrix[1, 1] + z * rotationMatrix[1, 2];
            double newZ = x * rotationMatrix[2, 0] + y * rotationMatrix[2, 1] + z * rotationMatrix[2, 2];

            return (newX, newY, newZ);
        }

        private Color AdjustColorIntensity(Color color, float intensity)
        {
            return Color.FromArgb(
                (int)(color.R * intensity),
                (int)(color.G * intensity),
                (int)(color.B * intensity)
            );
        }

        #endregion

        #region 2D Thin Section Processing Methods

        private void LoadThinSectionButton_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog openDialog = new OpenFileDialog())
            {
                openDialog.Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff";
                openDialog.Title = "Load Thin Section Image";

                if (openDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        // Load the image
                        thinSectionImage = new Bitmap(openDialog.FileName);

                        // Display the image
                        thinSectionPictureBox.Image = thinSectionImage;

                        // Enable other buttons
                        selectPoreColorButton.Enabled = true;
                        colorToleranceComboBox.Enabled = true;
                        setScaleButton.Enabled = true;

                        // Update status
                        statusLabel.Text = $"Loaded thin section image: {Path.GetFileName(openDialog.FileName)} - {thinSectionImage.Width}x{thinSectionImage.Height} pixels";

                        // Reset any existing processing
                        processedThinSectionImage = null;
                        pores2D.Clear();
                        throats2D.Clear();
                        porosity2D = 0;

                        // Reset scale values
                        scaleFactorMicronsPerPixel = 1.0;
                        thinSectionPhysicalWidth = thinSectionImage.Width;
                        thinSectionPhysicalHeight = thinSectionImage.Height;

                        // Disable buttons that require processing
                        extractPorosityButton.Enabled = false;
                        calculateMatchesButton.Enabled = false;
                        exportCombinedButton.Enabled = false;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error loading image: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Logger.Log($"[PoreNetwork2DComparisonForm] Error loading thin section: {ex.Message}");
                    }
                }
            }
        }

        private void SelectPoreColorButton_Click(object sender, EventArgs e)
        {
            if (thinSectionImage == null)
            {
                MessageBox.Show("Please load a thin section image first.", "No Image", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Use a ColorDialog to select the pore color
            using (ColorDialog colorDialog = new ColorDialog())
            {
                colorDialog.Color = selectedPoreColor;
                colorDialog.FullOpen = true;

                if (colorDialog.ShowDialog() == DialogResult.OK)
                {
                    selectedPoreColor = colorDialog.Color;
                    selectPoreColorButton.BackColor = selectedPoreColor;

                    // Process the image with the selected color
                    ProcessThinSectionImage();

                    // Enable the extract porosity button
                    extractPorosityButton.Enabled = true;
                }
            }
        }

        private void ProcessThinSectionImage()
        {
            if (thinSectionImage == null) return;

            try
            {
                // Create a new processed image with same dimensions
                processedThinSectionImage = new Bitmap(thinSectionImage.Width, thinSectionImage.Height);

                // Create black/white mask based on color similarity
                int width = thinSectionImage.Width;
                int height = thinSectionImage.Height;

                int totalPixels = width * height;
                int porePixels = 0;

                // Lock bits for faster processing
                BitmapData originalData = thinSectionImage.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format24bppRgb);

                BitmapData processedData = processedThinSectionImage.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format24bppRgb);

                try
                {
                    unsafe
                    {
                        byte* originalPtr = (byte*)originalData.Scan0;
                        byte* processedPtr = (byte*)processedData.Scan0;

                        int originalStride = originalData.Stride;
                        int processedStride = processedData.Stride;

                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                int originalPos = y * originalStride + x * 3;
                                int processedPos = y * processedStride + x * 3;

                                // Get pixel color (BGR order)
                                byte b = originalPtr[originalPos];
                                byte g = originalPtr[originalPos + 1];
                                byte r = originalPtr[originalPos + 2];

                                // Calculate color distance
                                double colorDistance = Math.Sqrt(
                                    Math.Pow(r - selectedPoreColor.R, 2) +
                                    Math.Pow(g - selectedPoreColor.G, 2) +
                                    Math.Pow(b - selectedPoreColor.B, 2));

                                // Check if within tolerance
                                if (colorDistance <= colorTolerance)
                                {
                                    // Mark as pore (white)
                                    processedPtr[processedPos] = 255;     // B
                                    processedPtr[processedPos + 1] = 255; // G
                                    processedPtr[processedPos + 2] = 255; // R
                                    porePixels++;
                                }
                                else
                                {
                                    // Mark as non-pore (black)
                                    processedPtr[processedPos] = 0;     // B
                                    processedPtr[processedPos + 1] = 0; // G
                                    processedPtr[processedPos + 2] = 0; // R
                                }
                            }
                        }
                    }
                }
                finally
                {
                    // Unlock bits
                    thinSectionImage.UnlockBits(originalData);
                    processedThinSectionImage.UnlockBits(processedData);
                }

                // Calculate porosity
                porosity2D = (double)porePixels / totalPixels;

                // Display the processed image
                thinSectionPictureBox.Image = processedThinSectionImage;

                // Update status
                statusLabel.Text = $"Processed image - Porosity: {porosity2D:P2}, Pore Pixels: {porePixels:N0} of {totalPixels:N0}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error processing image: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[PoreNetwork2DComparisonForm] Error processing thin section: {ex.Message}");
            }
        }

        private void SetScaleButton_Click(object sender, EventArgs e)
        {
            if (thinSectionImage == null)
            {
                MessageBox.Show("Please load a thin section image first.", "No Image", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Toggle scale setting mode
            isSettingScale = !isSettingScale;

            if (isSettingScale)
            {
                setScaleButton.Text = "Setting Scale...";
                statusLabel.Text = "Click and drag to define a scale line, then enter the physical length";
                setScaleButton.BackColor = Color.LightGreen;

                // Reset points
                scaleStartPoint = Point.Empty;
                scaleEndPoint = Point.Empty;
            }
            else
            {
                setScaleButton.Text = "Set Scale";
                statusLabel.Text = "Scale setting canceled";
                setScaleButton.BackColor = Color.FromArgb(220, 220, 220);
            }
        }

        private void ThinSectionPictureBox_MouseDown(object sender, MouseEventArgs e)
        {
            if (!isSettingScale) return;

            // Convert screen coordinates to image coordinates
            scaleStartPoint = ConvertCoordinates(e.Location);
            scaleEndPoint = scaleStartPoint;

            // Redraw with scale line
            DrawScaleLine();
        }

        private void ThinSectionPictureBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isSettingScale || scaleStartPoint.IsEmpty) return;

            // Update end point
            scaleEndPoint = ConvertCoordinates(e.Location);

            // Redraw with scale line
            DrawScaleLine();
        }

        private void ThinSectionPictureBox_MouseUp(object sender, MouseEventArgs e)
        {
            if (!isSettingScale || scaleStartPoint.IsEmpty) return;

            // Finalize end point
            scaleEndPoint = ConvertCoordinates(e.Location);

            // Calculate pixel length
            double pixelLength = Math.Sqrt(
                Math.Pow(scaleEndPoint.X - scaleStartPoint.X, 2) +
                Math.Pow(scaleEndPoint.Y - scaleStartPoint.Y, 2));

            // Ask user for physical length
            using (Form inputForm = new Form())
            {
                inputForm.Width = 300;
                inputForm.Height = 150;
                inputForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                inputForm.StartPosition = FormStartPosition.CenterParent;
                inputForm.Text = "Enter Scale";
                inputForm.MaximizeBox = false;
                inputForm.MinimizeBox = false;

                Label lengthLabel = new Label
                {
                    Text = "Physical length (μm):",
                    Location = new Point(20, 20),
                    AutoSize = true
                };

                NumericUpDown lengthInput = new NumericUpDown
                {
                    Location = new Point(150, 18),
                    Width = 100,
                    Minimum = 1,
                    Maximum = 100000,
                    Value = 100,
                    DecimalPlaces = 1
                };

                Button okButton = new Button
                {
                    Text = "OK",
                    Location = new Point(110, 60),
                    Width = 80,
                    DialogResult = DialogResult.OK
                };

                inputForm.Controls.Add(lengthLabel);
                inputForm.Controls.Add(lengthInput);
                inputForm.Controls.Add(okButton);
                inputForm.AcceptButton = okButton;

                if (inputForm.ShowDialog() == DialogResult.OK)
                {
                    double physicalLength = (double)lengthInput.Value;

                    // Calculate scale factor
                    scaleFactorMicronsPerPixel = physicalLength / pixelLength;

                    // Calculate physical dimensions
                    thinSectionPhysicalWidth = thinSectionImage.Width * scaleFactorMicronsPerPixel;
                    thinSectionPhysicalHeight = thinSectionImage.Height * scaleFactorMicronsPerPixel;

                    // Exit scale setting mode
                    isSettingScale = false;
                    setScaleButton.Text = "Set Scale";
                    setScaleButton.BackColor = Color.FromArgb(220, 220, 220);

                    // Update status
                    statusLabel.Text = $"Scale set: {scaleFactorMicronsPerPixel:F2} μm/pixel, " +
                                      $"Physical size: {thinSectionPhysicalWidth:F1} × {thinSectionPhysicalHeight:F1} μm";

                    // Redraw without scale line
                    thinSectionPictureBox.Image = processedThinSectionImage ?? thinSectionImage;
                }
                else
                {
                    // Cancel scale setting
                    isSettingScale = false;
                    setScaleButton.Text = "Set Scale";
                    setScaleButton.BackColor = Color.FromArgb(220, 220, 220);

                    // Redraw without scale line
                    thinSectionPictureBox.Image = processedThinSectionImage ?? thinSectionImage;
                }
            }
        }

        private PointF ConvertCoordinates(Point screenPoint)
        {
            // Convert screen coordinates to image coordinates
            if (thinSectionPictureBox.Image == null) return Point.Empty;

            // Get the size of the displayed image
            int displayWidth = thinSectionPictureBox.ClientSize.Width;
            int displayHeight = thinSectionPictureBox.ClientSize.Height;

            // Get the size of the actual image
            int imageWidth = thinSectionPictureBox.Image.Width;
            int imageHeight = thinSectionPictureBox.Image.Height;

            // Calculate scaling factors
            float scaleX = (float)imageWidth / displayWidth;
            float scaleY = (float)imageHeight / displayHeight;

            // Adjust for aspect ratio and zoom mode
            float scale = Math.Max(scaleX, scaleY);

            if (thinSectionPictureBox.SizeMode == PictureBoxSizeMode.Zoom)
            {
                scale = Math.Min(scaleX, scaleY);

                // Calculate the centered position
                int scaledWidth = (int)(imageWidth / scale);
                int scaledHeight = (int)(imageHeight / scale);

                int offsetX = (displayWidth - scaledWidth) / 2;
                int offsetY = (displayHeight - scaledHeight) / 2;

                // Convert screen position to image position
                float imageX = (screenPoint.X - offsetX) * scale;
                float imageY = (screenPoint.Y - offsetY) * scale;

                return new PointF(imageX, imageY);
            }
            else
            {
                // Direct conversion for other size modes
                return new PointF(screenPoint.X * scaleX, screenPoint.Y * scaleY);
            }
        }

        private void DrawScaleLine()
        {
            if (thinSectionImage == null || scaleStartPoint.IsEmpty) return;

            // Create a copy of the image to draw on
            Bitmap drawImage = processedThinSectionImage != null
                ? new Bitmap(processedThinSectionImage)
                : new Bitmap(thinSectionImage);

            using (Graphics g = Graphics.FromImage(drawImage))
            {
                // Draw the scale line
                g.DrawLine(new Pen(Color.Red, 2), scaleStartPoint, scaleEndPoint);

                // Draw start and end points
                g.FillEllipse(Brushes.Red, scaleStartPoint.X - 3, scaleStartPoint.Y - 3, 6, 6);
                g.FillEllipse(Brushes.Red, scaleEndPoint.X - 3, scaleEndPoint.Y - 3, 6, 6);

                // Calculate length
                double pixelLength = Math.Sqrt(
                    Math.Pow(scaleEndPoint.X - scaleStartPoint.X, 2) +
                    Math.Pow(scaleEndPoint.Y - scaleStartPoint.Y, 2));

                // Draw length label
                string lengthText = $"{pixelLength:F1} px";
                g.DrawString(lengthText, new Font("Arial", 10, FontStyle.Bold),
                    Brushes.Red, (scaleStartPoint.X + scaleEndPoint.X) / 2,
                    (scaleStartPoint.Y + scaleEndPoint.Y) / 2);
            }

            // Display the image with scale line
            thinSectionPictureBox.Image = drawImage;
        }

        private async void ExtractPorosityButton_Click(object sender, EventArgs e)
        {
            if (processedThinSectionImage == null)
            {
                MessageBox.Show("Please process the thin section image first.", "No Processed Image",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                progressBar.Value = 0;
                statusLabel.Text = "Extracting 2D pore network...";

                // Cancel any existing operation
                cancellationTokenSource?.Cancel();
                cancellationTokenSource = new CancellationTokenSource();

                // Create progress reporter
                IProgress<int> progress = new Progress<int>(value => progressBar.Value = value);

                // Extract 2D pore network in background thread
                await Task.Run(() => Extract2DPoreNetwork(progress, cancellationTokenSource.Token),
                    cancellationTokenSource.Token);

                // Display extracted pores on the processed image
                DisplayPoreNetwork2D();

                // Enable match calculation
                calculateMatchesButton.Enabled = true;

                // Update status
                statusLabel.Text = $"Extracted 2D pore network: {pores2D.Count} pores, " +
                                  $"{throats2D.Count} throats, Porosity: {porosity2D:P2}";
            }
            catch (OperationCanceledException)
            {
                statusLabel.Text = "Extraction cancelled";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error extracting pore network: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                statusLabel.Text = "Error extracting pore network";
                Logger.Log($"[PoreNetwork2DComparisonForm] Error extracting 2D network: {ex.Message}");
            }
        }

        private void Extract2DPoreNetwork(IProgress<int> progress, CancellationToken token)
        {
            if (processedThinSectionImage == null) return;

            // Clear existing data
            pores2D.Clear();
            throats2D.Clear();

            // Get image dimensions
            int width = processedThinSectionImage.Width;
            int height = processedThinSectionImage.Height;

            progress?.Report(10);
            token.ThrowIfCancellationRequested();

            // Use watershed or connected component analysis to identify pores
            int[,] labeledImage = new int[width, height];

            // Start with connected component labeling to identify pore regions
            int nextLabel = 1;
            Dictionary<int, int> equivalenceTable = new Dictionary<int, int>();

            // First pass - assign labels
            BitmapData bmpData = processedThinSectionImage.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly,
                processedThinSectionImage.PixelFormat);

            try
            {
                unsafe
                {
                    byte* ptr = (byte*)bmpData.Scan0;
                    int stride = bmpData.Stride;

                    for (int y = 0; y < height; y++)
                    {
                        token.ThrowIfCancellationRequested();

                        for (int x = 0; x < width; x++)
                        {
                            // Get pixel value (in binary/threshold image, we only need one channel)
                            int pos = y * stride + x * 3;
                            byte pixelValue = ptr[pos]; // Using B channel for threshold image

                            // Skip non-pore pixels
                            if (pixelValue == 0)
                            {
                                labeledImage[x, y] = 0;
                                continue;
                            }

                            // Check neighbors
                            int westLabel = (x > 0) ? labeledImage[x - 1, y] : 0;
                            int northLabel = (y > 0) ? labeledImage[x, y - 1] : 0;

                            if (westLabel == 0 && northLabel == 0)
                            {
                                // New label
                                labeledImage[x, y] = nextLabel++;
                            }
                            else if (westLabel != 0 && northLabel == 0)
                            {
                                // Use west label
                                labeledImage[x, y] = westLabel;
                            }
                            else if (westLabel == 0 && northLabel != 0)
                            {
                                // Use north label
                                labeledImage[x, y] = northLabel;
                            }
                            else
                            {
                                // Both neighbors have labels, use min and record equivalence
                                int minLabel = Math.Min(westLabel, northLabel);
                                labeledImage[x, y] = minLabel;

                                if (westLabel != northLabel)
                                {
                                    int maxLabel = Math.Max(westLabel, northLabel);

                                    // Update equivalence table to point to the smallest equivalent label
                                    if (!equivalenceTable.ContainsKey(maxLabel) ||
                                        equivalenceTable[maxLabel] > minLabel)
                                    {
                                        equivalenceTable[maxLabel] = minLabel;
                                    }
                                }
                            }
                        }

                        // Report progress periodically
                        if (y % 20 == 0)
                        {
                            progress?.Report(10 + (int)(20 * y / (float)height));
                        }
                    }
                }
            }
            finally
            {
                processedThinSectionImage.UnlockBits(bmpData);
            }

            progress?.Report(35);
            token.ThrowIfCancellationRequested();

            // Resolve equivalence chains for each label to find the root
            Dictionary<int, int> finalLabels = new Dictionary<int, int>();

            for (int i = 1; i < nextLabel; i++)
            {
                int label = i;
                while (equivalenceTable.ContainsKey(label))
                {
                    label = equivalenceTable[label];
                }
                finalLabels[i] = label;
            }

            // Second pass - relabel with the resolved equivalences
            for (int y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();

                for (int x = 0; x < width; x++)
                {
                    int originalLabel = labeledImage[x, y];
                    if (originalLabel > 0)
                    {
                        labeledImage[x, y] = finalLabels.ContainsKey(originalLabel) ?
                            finalLabels[originalLabel] : originalLabel;
                    }
                }

                // Report progress periodically
                if (y % 20 == 0)
                {
                    progress?.Report(35 + (int)(15 * y / (float)height));
                }
            }

            progress?.Report(50);
            token.ThrowIfCancellationRequested();

            // Count pixels per region and calculate centroids
            Dictionary<int, List<(int x, int y)>> regionPixels = new Dictionary<int, List<(int x, int y)>>();

            for (int y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();

                for (int x = 0; x < width; x++)
                {
                    int label = labeledImage[x, y];
                    if (label > 0)
                    {
                        if (!regionPixels.ContainsKey(label))
                        {
                            regionPixels[label] = new List<(int x, int y)>();
                        }
                        regionPixels[label].Add((x, y));
                    }
                }
            }

            progress?.Report(60);
            token.ThrowIfCancellationRequested();

            // Create pores for each significant region (filter out small noise)
            const int minimumPoreSize = 5; // Minimum pixel count to be considered a pore

            for (int i = 1; i < nextLabel; i++)
            {
                token.ThrowIfCancellationRequested();

                // Skip if this label doesn't exist in final labeling
                if (!regionPixels.ContainsKey(i)) continue;

                List<(int x, int y)> pixels = regionPixels[i];

                // Skip tiny regions (noise)
                if (pixels.Count < minimumPoreSize) continue;

                // Calculate centroid
                double sumX = 0, sumY = 0;
                foreach (var (x, y) in pixels)
                {
                    sumX += x;
                    sumY += y;
                }
                double centerX = sumX / pixels.Count;
                double centerY = sumY / pixels.Count;

                // Calculate equivalent radius
                double area = pixels.Count;
                double radius = Math.Sqrt(area / Math.PI);

                // Convert to physical dimensions
                double physicalX = centerX * scaleFactorMicronsPerPixel;
                double physicalY = centerY * scaleFactorMicronsPerPixel;
                double physicalRadius = radius * scaleFactorMicronsPerPixel;

                // Create pore
                Pore2D pore = new Pore2D
                {
                    Id = i,
                    CenterX = physicalX,
                    CenterY = physicalY,
                    Radius = physicalRadius,
                    Area = area * scaleFactorMicronsPerPixel * scaleFactorMicronsPerPixel
                };

                pores2D.Add(pore);
            }

            progress?.Report(75);
            token.ThrowIfCancellationRequested();

            // Create throats between neighboring pores
            // First, identify potential neighbors based on distance
            const double maxThroatLengthFactor = 3.0; // Maximum throat length as factor of pore radii

            for (int i = 0; i < pores2D.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                Pore2D pore1 = pores2D[i];
                double maxDistance = pore1.Radius * maxThroatLengthFactor;

                for (int j = i + 1; j < pores2D.Count; j++)
                {
                    Pore2D pore2 = pores2D[j];

                    // Calculate Euclidean distance
                    double distance = Math.Sqrt(
                        Math.Pow(pore2.CenterX - pore1.CenterX, 2) +
                        Math.Pow(pore2.CenterY - pore1.CenterY, 2));

                    // Check if pores are close enough to connect
                    double minDistanceToConnect = (pore1.Radius + pore2.Radius) * 1.5;
                    if (distance < Math.Min(maxDistance, minDistanceToConnect))
                    {
                        // Create a throat
                        Throat2D throat = new Throat2D
                        {
                            Id = throats2D.Count + 1,
                            PoreId1 = pore1.Id,
                            PoreId2 = pore2.Id,
                            Length = Math.Max(0.1, distance - pore1.Radius - pore2.Radius),
                            Radius = Math.Min(pore1.Radius, pore2.Radius) * 0.4 // Using same logic as 3D
                        };

                        // Calculate throat volume/area
                        throat.Area = throat.Length * throat.Radius * 2;

                        throats2D.Add(throat);

                        // Count connections
                        pore1.ConnectionCount++;
                        pore2.ConnectionCount++;
                    }
                }

                // Report progress periodically
                if (i % 10 == 0)
                {
                    progress?.Report(75 + (int)(20 * i / (float)pores2D.Count));
                }
            }

            progress?.Report(95);
            token.ThrowIfCancellationRequested();

            // Calculate final 2D porosity
            double totalPoreArea = pores2D.Sum(p => p.Area);
            double totalThroatArea = throats2D.Sum(t => t.Area);
            double imageArea = width * height * scaleFactorMicronsPerPixel * scaleFactorMicronsPerPixel;

            porosity2D = (totalPoreArea + totalThroatArea) / imageArea;

            progress?.Report(100);
        }

        private void DisplayPoreNetwork2D()
        {
            if (processedThinSectionImage == null || pores2D.Count == 0) return;

            try
            {
                // Create a copy of the processed image to draw on
                Bitmap networkImage = new Bitmap(processedThinSectionImage);

                using (Graphics g = Graphics.FromImage(networkImage))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                    // Calculate conversion from physical to pixel coordinates
                    float invScale = (float)(1.0 / scaleFactorMicronsPerPixel);

                    // Draw throats first (connections)
                    foreach (var throat in throats2D)
                    {
                        // Get the connected pores
                        Pore2D pore1 = pores2D.FirstOrDefault(p => p.Id == throat.PoreId1);
                        Pore2D pore2 = pores2D.FirstOrDefault(p => p.Id == throat.PoreId2);

                        if (pore1 != null && pore2 != null)
                        {
                            // Convert to pixel coordinates
                            float x1 = (float)(pore1.CenterX * invScale);
                            float y1 = (float)(pore1.CenterY * invScale);
                            float x2 = (float)(pore2.CenterX * invScale);
                            float y2 = (float)(pore2.CenterY * invScale);

                            // Draw the throat
                            float thickness = (float)(throat.Radius * invScale * 2);
                            thickness = Math.Max(1, thickness);

                            g.DrawLine(new Pen(Color.Blue, thickness), x1, y1, x2, y2);
                        }
                    }

                    // Draw pores
                    foreach (var pore in pores2D)
                    {
                        // Convert to pixel coordinates
                        float x = (float)(pore.CenterX * invScale);
                        float y = (float)(pore.CenterY * invScale);
                        float radius = (float)(pore.Radius * invScale);

                        // Choose color based on connection count
                        Color color;
                        if (pore.ConnectionCount <= 1) color = Color.Red;
                        else if (pore.ConnectionCount == 2) color = Color.Orange;
                        else if (pore.ConnectionCount <= 4) color = Color.Green;
                        else color = Color.Blue;

                        // Draw the pore
                        g.FillEllipse(new SolidBrush(color), x - radius, y - radius, radius * 2, radius * 2);
                        g.DrawEllipse(Pens.White, x - radius, y - radius, radius * 2, radius * 2);

                        // Optionally add ID for larger pores
                        if (radius > 10)
                        {
                            g.DrawString(pore.Id.ToString(), new Font("Arial", 8), Brushes.White, x - 4, y - 4);
                        }
                    }
                }

                // Display the network visualization
                thinSectionPictureBox.Image = networkImage;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error displaying 2D network: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[PoreNetwork2DComparisonForm] Error displaying 2D network: {ex.Message}");
            }
        }

        #endregion

        #region Match Calculation Methods

        private async void CalculateMatchesButton_Click(object sender, EventArgs e)
        {
            try
            {
                await CalculateMatches();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error calculating matches: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[PoreNetwork2DComparisonForm] Error calculating matches: {ex.Message}");
            }
        }

        private async Task CalculateMatches()
        {
            if (pores2D.Count == 0 || networkModel.Pores.Count == 0)
            {
                MessageBox.Show("Please extract the 2D pore network first.", "No Network",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                progressBar.Value = 0;
                statusLabel.Text = "Calculating potential orientation matches...";

                // Cancel any existing operation
                cancellationTokenSource?.Cancel();
                cancellationTokenSource = new CancellationTokenSource();

                // Create progress reporter
                IProgress<int> progress = new Progress<int>(value => progressBar.Value = value);

                // Get count of matches to find
                int matchCount = (int)matchCountNumeric.Value;

                // Calculate matches in background thread
                orientationMatches = await Task.Run(() =>
                    FindPossibleOrientations(matchCount, progress, cancellationTokenSource.Token),
                    cancellationTokenSource.Token);

                // Update match results grid
                UpdateMatchResultsGrid();

                // Update 3D visualization
                RenderNetwork3D(0); // Show the best match
                mainTabControl.SelectedIndex = 0; // Switch to 3D view

                // Enable export
                exportCombinedButton.Enabled = true;

                // Update status
                statusLabel.Text = $"Found {orientationMatches.Count} potential orientation matches";
            }
            catch (OperationCanceledException)
            {
                statusLabel.Text = "Match calculation cancelled";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error calculating matches: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                statusLabel.Text = "Error calculating matches";
                Logger.Log($"[PoreNetwork2DComparisonForm] Error calculating matches: {ex.Message}");
            }
        }

        private List<OrientationMatch> FindPossibleOrientations(int matchCount, IProgress<int> progress, CancellationToken token)
        {
            List<OrientationMatch> results = new List<OrientationMatch>();

            progress?.Report(5);
            token.ThrowIfCancellationRequested();

            // Scale 2D porosity and network to match 3D model
            double porosityRatio = networkModel.Porosity / porosity2D;

            // Define planes to test (standard orientations)
            List<(string name, Point3D normal)> orientations = new List<(string, Point3D)>
            {
                ("XY", new Point3D { X = 0, Y = 0, Z = 1 }),    // XY plane (horizontal)
                ("XZ", new Point3D { X = 0, Y = 1, Z = 0 }),    // XZ plane (front-back)
                ("YZ", new Point3D { X = 1, Y = 0, Z = 0 })     // YZ plane (side)
            };

            // Add some additional orientations at angles
            orientations.Add(("XY+30", NormalizeVector(new Point3D { X = 0, Y = 0.5, Z = 0.866 })));
            orientations.Add(("XZ+30", NormalizeVector(new Point3D { X = 0, Y = 0.866, Z = 0.5 })));
            orientations.Add(("YZ+30", NormalizeVector(new Point3D { X = 0.866, Y = 0, Z = 0.5 })));
            orientations.Add(("XY-30", NormalizeVector(new Point3D { X = 0, Y = -0.5, Z = 0.866 })));
            orientations.Add(("XZ-30", NormalizeVector(new Point3D { X = 0, Y = 0.866, Z = -0.5 })));
            orientations.Add(("YZ-30", NormalizeVector(new Point3D { X = 0.866, Y = 0, Z = -0.5 })));

            // Find min and max coordinates of 3D model
            double minX = networkModel.Pores.Min(p => p.Center.X - p.Radius);
            double maxX = networkModel.Pores.Max(p => p.Center.X + p.Radius);
            double minY = networkModel.Pores.Min(p => p.Center.Y - p.Radius);
            double maxY = networkModel.Pores.Max(p => p.Center.Y + p.Radius);
            double minZ = networkModel.Pores.Min(p => p.Center.Z - p.Radius);
            double maxZ = networkModel.Pores.Max(p => p.Center.Z + p.Radius);

            // Calculate physical size of thin section
            double tsWidth = thinSectionPhysicalWidth;
            double tsHeight = thinSectionPhysicalHeight;

            // Calculate model dimensions
            double modelWidth = maxX - minX;
            double modelHeight = maxY - minY;
            double modelDepth = maxZ - minZ;

            // Check if thin section can fit in the model
            if (tsWidth > modelWidth || tsHeight > Math.Max(modelHeight, modelDepth))
            {
                results.Add(new OrientationMatch
                {
                    Orientation = "Error",
                    Normal = new Point3D { X = 0, Y = 0, Z = 1 },
                    Position = new Point3D { X = (minX + maxX) / 2, Y = (minY + maxY) / 2, Z = (minZ + maxZ) / 2 },
                    MatchPercentage = 0,
                    PorosityRatio = porosityRatio,
                    MatchingPoreIds = new List<int>()
                });

                progress?.Report(100);
                return results;
            }

            // All potential match candidates
            List<OrientationMatch> candidates = new List<OrientationMatch>();

            // Process each orientation
            for (int orientIndex = 0; orientIndex < orientations.Count; orientIndex++)
            {
                token.ThrowIfCancellationRequested();

                var (orientName, normal) = orientations[orientIndex];

                // Calculate the scan direction based on orientation
                // For a given plane with normal N, find the span vectors in the plane
                Point3D uVector, vVector;

                if (orientName.StartsWith("XY"))
                {
                    uVector = new Point3D { X = 1, Y = 0, Z = 0 };
                    vVector = new Point3D { X = 0, Y = 1, Z = 0 };
                }
                else if (orientName.StartsWith("XZ"))
                {
                    uVector = new Point3D { X = 1, Y = 0, Z = 0 };
                    vVector = new Point3D { X = 0, Y = 0, Z = 1 };
                }
                else // YZ
                {
                    uVector = new Point3D { X = 0, Y = 1, Z = 0 };
                    vVector = new Point3D { X = 0, Y = 0, Z = 1 };
                }

                // For angled planes, apply rotation
                if (orientName.EndsWith("+30") || orientName.EndsWith("-30"))
                {
                    // For simplicity, just use original vectors but ensure they're perpendicular to normal
                    uVector = CrossProductWithNormal(normal, vVector);
                    vVector = CrossProductWithNormal(normal, uVector);

                    // Normalize vectors
                    uVector = NormalizeVector(uVector);
                    vVector = NormalizeVector(vVector);
                }

                // Generate sample plane positions to test
                int samplesPerDimension = 5; // Number of positions to try along each axis

                for (int i = 0; i < samplesPerDimension; i++)
                {
                    token.ThrowIfCancellationRequested();

                    for (int j = 0; j < samplesPerDimension; j++)
                    {
                        // Calculate position along each axis
                        double posX = minX + (maxX - minX) * (i + 0.5) / samplesPerDimension;
                        double posY = minY + (maxY - minY) * (j + 0.5) / samplesPerDimension;
                        double posZ = (minZ + maxZ) / 2; // Start in middle for Z

                        // Create position point
                        Point3D position = new Point3D { X = posX, Y = posY, Z = posZ };

                        // Try different Z positions for each X,Y
                        List<OrientationMatch> positionMatches = new List<OrientationMatch>();

                        for (int k = 0; k < samplesPerDimension; k++)
                        {
                            posZ = minZ + (maxZ - minZ) * (k + 0.5) / samplesPerDimension;
                            position.Z = posZ;

                            // Calculate match quality for this position
                            OrientationMatch match = CalculateMatchAtPosition(
                                position, normal, uVector, vVector,
                                tsWidth, tsHeight, orientName, porosityRatio);

                            if (match != null)
                            {
                                positionMatches.Add(match);
                            }
                        }

                        // Add the best match at this X,Y position
                        if (positionMatches.Count > 0)
                        {
                            // Find the best match
                            OrientationMatch bestPositionMatch = positionMatches
                                .OrderByDescending(m => m.MatchPercentage)
                                .First();

                            candidates.Add(bestPositionMatch);
                        }
                    }

                    // Report progress periodically
                    progress?.Report(10 + (int)(80 * orientIndex / orientations.Count));
                }
            }

            progress?.Report(90);
            token.ThrowIfCancellationRequested();

            // Sort candidates by match percentage and take the top N
            var topMatches = candidates
                .OrderByDescending(m => m.MatchPercentage)
                .Take(matchCount)
                .ToList();

            progress?.Report(100);
            return topMatches;
        }

        private OrientationMatch CalculateMatchAtPosition(
            Point3D position, Point3D normal, Point3D uVector, Point3D vVector,
            double width, double height, string orientation, double porosityRatio)
        {
            try
            {
                // Scale vectors by half width and height to get the extent in each direction
                Point3D uScaled = new Point3D
                {
                    X = uVector.X * width / 2,
                    Y = uVector.Y * width / 2,
                    Z = uVector.Z * width / 2
                };

                Point3D vScaled = new Point3D
                {
                    X = vVector.X * height / 2,
                    Y = vVector.Y * height / 2,
                    Z = vVector.Z * height / 2
                };

                // Calculate corners of the section
                List<Point3D> corners = new List<Point3D>();
                corners.Add(new Point3D
                {
                    X = position.X - uScaled.X - vScaled.X,
                    Y = position.Y - uScaled.Y - vScaled.Y,
                    Z = position.Z - uScaled.Z - vScaled.Z
                });
                corners.Add(new Point3D
                {
                    X = position.X + uScaled.X - vScaled.X,
                    Y = position.Y + uScaled.Y - vScaled.Y,
                    Z = position.Z + uScaled.Z - vScaled.Z
                });
                corners.Add(new Point3D
                {
                    X = position.X + uScaled.X + vScaled.X,
                    Y = position.Y + uScaled.Y + vScaled.Y,
                    Z = position.Z + uScaled.Z + vScaled.Z
                });
                corners.Add(new Point3D
                {
                    X = position.X - uScaled.X + vScaled.X,
                    Y = position.Y - uScaled.Y + vScaled.Y,
                    Z = position.Z - uScaled.Z + vScaled.Z
                });

                // Find pores that intersect with this plane section
                List<int> intersectingPoreIds = new List<int>();
                List<(Pore, double)> intersectingPores = new List<(Pore, double)>();

                foreach (var pore in networkModel.Pores)
                {
                    // Calculate distance from pore center to plane
                    double distance = Math.Abs(
                        normal.X * (pore.Center.X - position.X) +
                        normal.Y * (pore.Center.Y - position.Y) +
                        normal.Z * (pore.Center.Z - position.Z));

                    // If the distance is less than the radius, the pore intersects the plane
                    if (distance <= pore.Radius)
                    {
                        // Further check if the pore is within the bounds of the thin section
                        // Project the pore center onto the plane
                        Point3D projectedCenter = new Point3D
                        {
                            X = pore.Center.X - normal.X * distance,
                            Y = pore.Center.Y - normal.Y * distance,
                            Z = pore.Center.Z - normal.Z * distance
                        };

                        // Calculate coordinates in the u,v space
                        double uCoord =
                            (projectedCenter.X - position.X) * uVector.X +
                            (projectedCenter.Y - position.Y) * uVector.Y +
                            (projectedCenter.Z - position.Z) * uVector.Z;

                        double vCoord =
                            (projectedCenter.X - position.X) * vVector.X +
                            (projectedCenter.Y - position.Y) * vVector.Y +
                            (projectedCenter.Z - position.Z) * vVector.Z;

                        // Scale to ensure we're using the correct units (uv space)
                        uCoord /= Math.Sqrt(uVector.X * uVector.X + uVector.Y * uVector.Y + uVector.Z * uVector.Z);
                        vCoord /= Math.Sqrt(vVector.X * vVector.X + vVector.Y * vVector.Y + vVector.Z * vVector.Z);

                        // Check if within bounds of thin section
                        if (Math.Abs(uCoord) <= width / 2 && Math.Abs(vCoord) <= height / 2)
                        {
                            intersectingPoreIds.Add(pore.Id);
                            intersectingPores.Add((pore, distance));
                        }
                    }
                }

                // If no intersecting pores, return null (no match)
                if (intersectingPores.Count == 0)
                {
                    return null;
                }

                // Calculate match percentage based on similarity to 2D network
                double matchPercentage = CalculateMatchPercentage(intersectingPores, position, normal);

                // Create and return the match object
                return new OrientationMatch
                {
                    Orientation = orientation,
                    Normal = normal,
                    Position = position,
                    MatchPercentage = matchPercentage,
                    PorosityRatio = porosityRatio,
                    MatchingPoreIds = intersectingPoreIds
                };
            }
            catch
            {
                // In case of any errors, return null
                return null;
            }
        }

        private double CalculateMatchPercentage(
            List<(Pore pore, double distance)> intersectingPores,
            Point3D position,
            Point3D normal)
        {
            try
            {
                // This is a simple match calculation that compares the distribution of pore sizes,
                // connection counts, and spatial distributions between the 2D and 3D networks

                // If no pores, it's a 0% match
                if (intersectingPores.Count == 0 || pores2D.Count == 0)
                    return 0;

                // First, compare number of pores (as a ratio)
                double poreCountRatio = Math.Min(1.0,
                    (double)intersectingPores.Count / pores2D.Count);

                // Compare size distributions using histograms
                // For simplicity, we'll use 5 bins for radius distribution
                int binCount = 5;

                // Get min/max radius for 2D pores
                double min2DRadius = pores2D.Min(p => p.Radius);
                double max2DRadius = pores2D.Max(p => p.Radius);
                double range2D = max2DRadius - min2DRadius;

                // Get min/max radius for intersecting 3D pores
                double min3DRadius = intersectingPores.Min(p => p.pore.Radius);
                double max3DRadius = intersectingPores.Max(p => p.pore.Radius);
                double range3D = max3DRadius - min3DRadius;

                // Create histograms (distributions) for both networks
                int[] dist2D = new int[binCount];
                int[] dist3D = new int[binCount];

                // Fill 2D histogram
                foreach (var pore in pores2D)
                {
                    // Normalize radius to [0,1]
                    double normalized = range2D > 0 ?
                        (pore.Radius - min2DRadius) / range2D : 0.5;

                    // Determine bin (clamp to valid range)
                    int bin = (int)(normalized * binCount);
                    bin = Math.Max(0, Math.Min(binCount - 1, bin));

                    // Increment bin count
                    dist2D[bin]++;
                }

                // Fill 3D histogram
                foreach (var (pore, _) in intersectingPores)
                {
                    // Normalize radius to [0,1]
                    double normalized = range3D > 0 ?
                        (pore.Radius - min3DRadius) / range3D : 0.5;

                    // Determine bin
                    int bin = (int)(normalized * binCount);
                    bin = Math.Max(0, Math.Min(binCount - 1, bin));

                    // Increment bin count
                    dist3D[bin]++;
                }

                // Normalize histograms to percentages
                double[] normDist2D = new double[binCount];
                double[] normDist3D = new double[binCount];

                for (int i = 0; i < binCount; i++)
                {
                    normDist2D[i] = (double)dist2D[i] / pores2D.Count;
                    normDist3D[i] = (double)dist3D[i] / intersectingPores.Count;
                }

                // Calculate distribution similarity (1 - average difference)
                double distributionDiff = 0;
                for (int i = 0; i < binCount; i++)
                {
                    distributionDiff += Math.Abs(normDist2D[i] - normDist3D[i]);
                }

                double distributionSimilarity = 1.0 - (distributionDiff / binCount);

                // Compare connectivity (average connection count)
                double avg2DConnections = pores2D.Average(p => p.ConnectionCount);
                double avg3DConnections = networkModel.Pores
                    .Where(p => intersectingPores.Any(ip => ip.pore.Id == p.Id))
                    .Average(p => p.ConnectionCount);

                // Normalize connectivity similarity (1.0 means identical connection counts)
                double maxAvgConnections = Math.Max(avg2DConnections, avg3DConnections);
                double connectionSimilarity = maxAvgConnections > 0 ?
                    1.0 - Math.Abs(avg2DConnections - avg3DConnections) / maxAvgConnections : 0.5;

                // Compare spatial distribution using a simplified measure
                // For each pore, find the nearest neighbor distance and compare distributions

                // Calculate weights for each component
                double poreCountWeight = 0.2;
                double distributionWeight = 0.4;
                double connectivityWeight = 0.4;

                // Calculate final match percentage
                double matchPercentage = 100 * (
                    poreCountWeight * poreCountRatio +
                    distributionWeight * distributionSimilarity +
                    connectivityWeight * connectionSimilarity);

                // Clamp result to valid percentage range
                return Math.Max(0, Math.Min(100, matchPercentage));
            }
            catch
            {
                // In case of any errors, return a minimum match
                return 1.0; // 1% match, not 0% to distinguish from null returns
            }
        }

        private void UpdateMatchResultsGrid()
        {
            if (matchResultsGridView == null) return;

            try
            {
                matchResultsGridView.Rows.Clear();

                // Add each match to the grid
                for (int i = 0; i < orientationMatches.Count; i++)
                {
                    var match = orientationMatches[i];

                    // Format the position string
                    string positionStr = $"({match.Position.X:F1}, {match.Position.Y:F1}, {match.Position.Z:F1})";

                    // Add the row
                    matchResultsGridView.Rows.Add(
                        i + 1, // Rank
                        $"{match.MatchPercentage:F1}%",
                        GetOrientationName(match.Orientation),
                        positionStr,
                        $"{match.PorosityRatio:F2}x"
                    );
                }

                // Select the top match
                if (matchResultsGridView.Rows.Count > 0)
                {
                    matchResultsGridView.Rows[0].Selected = true;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[PoreNetwork2DComparisonForm] Error updating match results: {ex.Message}");
            }
        }

        private Point3D NormalizeVector(Point3D v)
        {
            double length = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

            if (length > 0)
            {
                return new Point3D
                {
                    X = v.X / length,
                    Y = v.Y / length,
                    Z = v.Z / length
                };
            }

            return v; // Return original if length is zero
        }

        private Point3D CrossProductWithNormal(Point3D normal, Point3D v)
        {
            return new Point3D
            {
                X = normal.Y * v.Z - normal.Z * v.Y,
                Y = normal.Z * v.X - normal.X * v.Z,
                Z = normal.X * v.Y - normal.Y * v.X
            };
        }

        private void ExportCombinedButton_Click(object sender, EventArgs e)
        {
            if (orientationMatches.Count == 0)
            {
                MessageBox.Show("Please calculate matches first.", "No Matches",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Get the selected match
            int selectedIndex = 0;
            if (matchResultsGridView.SelectedRows.Count > 0)
            {
                selectedIndex = matchResultsGridView.SelectedRows[0].Index;
            }

            if (selectedIndex < 0 || selectedIndex >= orientationMatches.Count)
            {
                MessageBox.Show("Please select a valid match.", "Invalid Selection",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (SaveFileDialog saveDialog = new SaveFileDialog())
            {
                saveDialog.Filter = "PNG Image|*.png|JPEG Image|*.jpg|Bitmap Image|*.bmp";
                saveDialog.Title = "Save Combined View";
                saveDialog.DefaultExt = "png";

                if (saveDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        // Create a combined visualization showing the thin section 
                        // in its position within the 3D volume
                        Bitmap combinedImage = CreateCombinedVisualization(selectedIndex);

                        // Save the image
                        string extension = Path.GetExtension(saveDialog.FileName).ToLower();

                        ImageFormat format = ImageFormat.Png;
                        if (extension == ".jpg" || extension == ".jpeg")
                            format = ImageFormat.Jpeg;
                        else if (extension == ".bmp")
                            format = ImageFormat.Bmp;

                        combinedImage.Save(saveDialog.FileName, format);

                        // Show success message
                        statusLabel.Text = "Combined view saved successfully";
                        MessageBox.Show("Combined view saved successfully.", "Save Complete",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error saving combined view: {ex.Message}", "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Logger.Log($"[PoreNetwork2DComparisonForm] Error saving combined view: {ex.Message}");
                    }
                }
            }
        }

        private Bitmap CreateCombinedVisualization(int matchIndex)
        {
            int width = 1500;
            int height = 900;

            Bitmap combinedImage = new Bitmap(width, height);

            using (Graphics g = Graphics.FromImage(combinedImage))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.Clear(Color.Black);

                // Draw title
                string title = "Thin Section & 3D Pore Network Comparison";
                using (Font titleFont = new Font("Arial", 16, FontStyle.Bold))
                {
                    SizeF titleSize = g.MeasureString(title, titleFont);
                    g.DrawString(title, titleFont, Brushes.White, (width - titleSize.Width) / 2, 20);
                }

                // 1. Draw the 3D network with plane on the left side
                var match = orientationMatches[matchIndex];
                Bitmap network3D = Render3DNetworkForCombinedView(match, 700, 700);
                g.DrawImage(network3D, 50, 80, 700, 700);

                // 2. Draw the 2D thin section on the right side
                if (processedThinSectionImage != null)
                {
                    Bitmap thinSection = new Bitmap(processedThinSectionImage);

                    // Overlay the pore network on the thin section image
                    using (Graphics imgG = Graphics.FromImage(thinSection))
                    {
                        imgG.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                        // Convert from physical to pixel coordinates
                        float invScale = (float)(1.0 / scaleFactorMicronsPerPixel);

                        // Draw throats first with translucent blue
                        using (Pen throatPen = new Pen(Color.FromArgb(150, 0, 0, 255), 1.5f))
                        {
                            foreach (var throat in throats2D)
                            {
                                // Get the connected pores
                                Pore2D pore1 = pores2D.FirstOrDefault(p => p.Id == throat.PoreId1);
                                Pore2D pore2 = pores2D.FirstOrDefault(p => p.Id == throat.PoreId2);

                                if (pore1 != null && pore2 != null)
                                {
                                    // Convert to pixel coordinates
                                    float x1 = (float)(pore1.CenterX * invScale);
                                    float y1 = (float)(pore1.CenterY * invScale);
                                    float x2 = (float)(pore2.CenterX * invScale);
                                    float y2 = (float)(pore2.CenterY * invScale);

                                    // Draw the throat
                                    imgG.DrawLine(throatPen, x1, y1, x2, y2);
                                }
                            }
                        }

                        // Draw pores with translucent colors
                        foreach (var pore in pores2D)
                        {
                            // Convert to pixel coordinates
                            float x = (float)(pore.CenterX * invScale);
                            float y = (float)(pore.CenterY * invScale);
                            float radius = (float)(pore.Radius * invScale);

                            // Choose color based on connection count
                            Color color;
                            if (pore.ConnectionCount <= 1) color = Color.FromArgb(150, 255, 0, 0);
                            else if (pore.ConnectionCount == 2) color = Color.FromArgb(150, 255, 165, 0);
                            else if (pore.ConnectionCount <= 4) color = Color.FromArgb(150, 0, 255, 0);
                            else color = Color.FromArgb(150, 0, 0, 255);

                            // Draw the pore
                            imgG.FillEllipse(new SolidBrush(color), x - radius, y - radius, radius * 2, radius * 2);
                            imgG.DrawEllipse(new Pen(Color.FromArgb(200, 255, 255, 255), 1),
                                x - radius, y - radius, radius * 2, radius * 2);
                        }
                    }

                    // Draw the thin section with overlay to fit right side area
                    float aspectRatio = (float)thinSection.Width / thinSection.Height;
                    int maxWidth = 650;
                    int maxHeight = 650;

                    int drawWidth, drawHeight;
                    if (aspectRatio > 1)
                    {
                        drawWidth = maxWidth;
                        drawHeight = (int)(maxWidth / aspectRatio);
                    }
                    else
                    {
                        drawHeight = maxHeight;
                        drawWidth = (int)(maxHeight * aspectRatio);
                    }

                    g.DrawImage(thinSection, 800, 80, drawWidth, drawHeight);

                    // Draw border around thin section
                    g.DrawRectangle(new Pen(Color.White, 2), 800, 80, drawWidth, drawHeight);
                }

                // 3. Draw match information between the views
                DrawMatchInformation(g, match, width, height);
            }

            return combinedImage;
        }

        private Bitmap Render3DNetworkForCombinedView(OrientationMatch match, int width, int height)
        {
            Bitmap networkImage = new Bitmap(width, height);

            using (Graphics g = Graphics.FromImage(networkImage))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Black);

                // Find model bounds and center point
                double minX = networkModel.Pores.Min(p => p.Center.X);
                double maxX = networkModel.Pores.Max(p => p.Center.X);
                double minY = networkModel.Pores.Min(p => p.Center.Y);
                double maxY = networkModel.Pores.Max(p => p.Center.Y);
                double minZ = networkModel.Pores.Min(p => p.Center.Z);
                double maxZ = networkModel.Pores.Max(p => p.Center.Z);

                double centerX = (minX + maxX) / 2;
                double centerY = (minY + maxY) / 2;
                double centerZ = (minZ + maxZ) / 2;

                // Calculate scale factor
                double maxRange = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
                double scaleFactor = Math.Min(width, height) * 0.4 / maxRange;

                // Set optimal viewing angle to show the match plane clearly
                double[,] rotationMatrix = Create3DRotationMatrix(30, 30, 0);

                // Draw the match plane
                if (match != null)
                {
                    // Calculate the corners of the plane
                    List<Point3D> planeCorners = CalculatePlaneCorners(
                        match.Position,
                        match.Normal,
                        match.Orientation,
                        thinSectionPhysicalWidth,
                        thinSectionPhysicalHeight);

                    // Project and draw the plane
                    List<Point> projectedCorners = new List<Point>();
                    foreach (var corner in planeCorners)
                    {
                        var transformed = Transform3DPoint(
                            corner.X - centerX,
                            corner.Y - centerY,
                            corner.Z - centerZ,
                            rotationMatrix);

                        projectedCorners.Add(new Point(
                            (int)(width / 2 + transformed.x * scaleFactor),
                            (int)(height / 2 - transformed.y * scaleFactor)));
                    }

                    // Draw the match plane with transparency
                    if (projectedCorners.Count >= 4)
                    {
                        Point[] polygonPoints = projectedCorners.ToArray();

                        // Fill with semi-transparent color
                        using (SolidBrush fillBrush = new SolidBrush(Color.FromArgb(120, 255, 255, 100)))
                        {
                            g.FillPolygon(fillBrush, polygonPoints);
                        }

                        // Draw border
                        using (Pen borderPen = new Pen(Color.Yellow, 2))
                        {
                            g.DrawPolygon(borderPen, polygonPoints);
                        }

                        // Draw normal vector
                        Point3D center = match.Position;
                        Point3D normalEnd = new Point3D
                        {
                            X = center.X + match.Normal.X * maxRange * 0.2,
                            Y = center.Y + match.Normal.Y * maxRange * 0.2,
                            Z = center.Z + match.Normal.Z * maxRange * 0.2
                        };

                        var transformedCenter = Transform3DPoint(
                            center.X - centerX, center.Y - centerY, center.Z - centerZ, rotationMatrix);

                        var transformedEnd = Transform3DPoint(
                            normalEnd.X - centerX, normalEnd.Y - centerY, normalEnd.Z - centerZ, rotationMatrix);

                        Point centerPoint = new Point(
                            (int)(width / 2 + transformedCenter.x * scaleFactor),
                            (int)(height / 2 - transformedCenter.y * scaleFactor));

                        Point endPoint = new Point(
                            (int)(width / 2 + transformedEnd.x * scaleFactor),
                            (int)(height / 2 - transformedEnd.y * scaleFactor));

                        using (Pen normalPen = new Pen(Color.Red, 2))
                        {
                            g.DrawLine(normalPen, centerPoint, endPoint);

                            // Draw arrowhead
                            PointF[] arrowHead = GetArrowHead(centerPoint, endPoint, 8);
                            g.FillPolygon(Brushes.Red, arrowHead);
                        }
                    }
                }

                // Draw throats
                foreach (var throat in networkModel.Throats)
                {
                    var pore1 = networkModel.Pores.FirstOrDefault(p => p.Id == throat.PoreId1);
                    var pore2 = networkModel.Pores.FirstOrDefault(p => p.Id == throat.PoreId2);

                    if (pore1 != null && pore2 != null)
                    {
                        // Transform coordinates
                        var transformedP1 = Transform3DPoint(
                            pore1.Center.X - centerX,
                            pore1.Center.Y - centerY,
                            pore1.Center.Z - centerZ,
                            rotationMatrix);

                        var transformedP2 = Transform3DPoint(
                            pore2.Center.X - centerX,
                            pore2.Center.Y - centerY,
                            pore2.Center.Z - centerZ,
                            rotationMatrix);

                        // Calculate projected points
                        Point p1 = new Point(
                            (int)(width / 2 + transformedP1.x * scaleFactor),
                            (int)(height / 2 - transformedP1.y * scaleFactor));

                        Point p2 = new Point(
                            (int)(width / 2 + transformedP2.x * scaleFactor),
                            (int)(height / 2 - transformedP2.y * scaleFactor));

                        // Calculate throat thickness
                        float thickness = (float)(throat.Radius * scaleFactor * 0.25);
                        thickness = Math.Max(1, thickness);

                        // Create gradient color based on depth
                        double avgZ = (transformedP1.z + transformedP2.z) / 2;
                        int intensity = (int)(100 + Math.Min(155, Math.Max(0, 155 * (1 - avgZ / 500))));
                        Color throatColor = Color.FromArgb(intensity, intensity, intensity);

                        using (Pen pen = new Pen(throatColor, thickness))
                        {
                            g.DrawLine(pen, p1, p2);
                        }
                    }
                }

                // Draw pores
                HashSet<int> highlightedPores = new HashSet<int>(match.MatchingPoreIds);

                foreach (var pore in networkModel.Pores)
                {
                    // Transform coordinates
                    var transformed = Transform3DPoint(
                        pore.Center.X - centerX,
                        pore.Center.Y - centerY,
                        pore.Center.Z - centerZ,
                        rotationMatrix);

                    // Calculate projected point
                    int x = (int)(width / 2 + transformed.x * scaleFactor);
                    int y = (int)(height / 2 - transformed.y * scaleFactor);

                    // Calculate pore radius in screen space
                    int radius = Math.Max(3, (int)(pore.Radius * scaleFactor * 0.5));

                    // Determine color
                    Color poreColor;

                    if (highlightedPores.Contains(pore.Id))
                    {
                        // Highlight pores that are part of the match
                        poreColor = Color.Yellow;
                    }
                    else
                    {
                        // Normal coloring based on connections
                        int connCount = pore.ConnectionCount;
                        if (connCount <= 1)
                            poreColor = Color.Red;
                        else if (connCount == 2)
                            poreColor = Color.Orange;
                        else if (connCount <= 4)
                            poreColor = Color.Green;
                        else
                            poreColor = Color.Blue;
                    }

                    // Adjust color intensity based on Z depth
                    float intensity = (float)Math.Max(0.5f, Math.Min(1.0f, (transformed.z + 500) / 1000));
                    poreColor = AdjustColorIntensity(poreColor, intensity);

                    // Draw the pore
                    g.FillEllipse(new SolidBrush(poreColor), x - radius, y - radius, radius * 2, radius * 2);
                    g.DrawEllipse(Pens.White, x - radius, y - radius, radius * 2, radius * 2);
                }

                // Draw coordinate axes
                DrawCoordinateAxes(g, width, height, scaleFactor * 0.2, rotationMatrix);
            }

            return networkImage;
        }

        private void DrawMatchInformation(Graphics g, OrientationMatch match, int width, int height)
        {
            // Drawing area for match information
            Rectangle infoRect = new Rectangle(800, 740, 650, 120);

            // Draw background
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(40, 40, 40)))
            {
                g.FillRectangle(brush, infoRect);
            }

            g.DrawRectangle(Pens.Gray, infoRect);

            // Draw title
            string title = "Match Information";
            using (Font titleFont = new Font("Arial", 12, FontStyle.Bold))
            {
                g.DrawString(title, titleFont, Brushes.White, infoRect.X + 10, infoRect.Y + 5);
            }

            // Draw match details
            using (Font detailFont = new Font("Arial", 10))
            {
                int y = infoRect.Y + 30;

                g.DrawString($"Match Percentage: {match.MatchPercentage:F1}%",
                    detailFont, Brushes.Yellow, infoRect.X + 10, y);
                y += 20;

                g.DrawString($"Orientation: {GetOrientationName(match.Orientation)}",
                    detailFont, Brushes.White, infoRect.X + 10, y);
                y += 20;

                g.DrawString($"Position: ({match.Position.X:F1}, {match.Position.Y:F1}, {match.Position.Z:F1})",
                    detailFont, Brushes.White, infoRect.X + 10, y);
                y += 20;

                g.DrawString($"Porosity Ratio (3D/2D): {match.PorosityRatio:F2}x",
                    detailFont, Brushes.White, infoRect.X + 10, y);

                // Draw additional statistics in the right column
                int x2 = infoRect.X + 350;
                y = infoRect.Y + 30;

                g.DrawString($"2D Pores: {pores2D.Count}", detailFont, Brushes.White, x2, y);
                y += 20;

                g.DrawString($"2D Porosity: {porosity2D:P2}", detailFont, Brushes.White, x2, y);
                y += 20;

                g.DrawString($"3D Pores: {networkModel.Pores.Count}", detailFont, Brushes.White, x2, y);
                y += 20;

                g.DrawString($"3D Porosity: {networkModel.Porosity:P2}", detailFont, Brushes.White, x2, y);
            }
        }

        #endregion

        #region Support Classes 

        /// <summary>
        /// Represents a pore in a 2D pore network
        /// </summary>
        private class Pore2D
        {
            public int Id { get; set; }
            public double CenterX { get; set; }
            public double CenterY { get; set; }
            public double Radius { get; set; }
            public double Area { get; set; }
            public int ConnectionCount { get; set; }
        }

        /// <summary>
        /// Represents a throat connection in a 2D pore network
        /// </summary>
        private class Throat2D
        {
            public int Id { get; set; }
            public int PoreId1 { get; set; }
            public int PoreId2 { get; set; }
            public double Length { get; set; }
            public double Radius { get; set; }
            public double Area { get; set; }
        }

        /// <summary>
        /// Represents a potential match between a 2D thin section and the 3D pore network
        /// </summary>
        private class OrientationMatch
        {
            public string Orientation { get; set; }
            public Point3D Normal { get; set; }
            public Point3D Position { get; set; }
            public double MatchPercentage { get; set; }
            public double PorosityRatio { get; set; }
            public List<int> MatchingPoreIds { get; set; }
        }

        #endregion

        #region Event Handlers

        private void ResetViewButton_Click(object sender, EventArgs e)
        {
            rotationX = 30.0f;
            rotationY = 30.0f;
            rotationZ = 0.0f;
            viewScale = 1.0f;
            panOffsetX = 0.0f;
            panOffsetY = 0.0f;
            RenderNetwork3D();
        }

        private void ScreenshotButton_Click(object sender, EventArgs e)
        {
            if (network3DPictureBox.Image == null)
            {
                MessageBox.Show("No network visualization to save.", "Screenshot Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (SaveFileDialog saveDialog = new SaveFileDialog())
            {
                saveDialog.Filter = "PNG Image|*.png|JPEG Image|*.jpg|Bitmap Image|*.bmp";
                saveDialog.Title = "Save Network Screenshot";
                saveDialog.DefaultExt = "png";

                if (saveDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        // Create a copy of the current network image
                        using (Bitmap originalImage = new Bitmap(network3DPictureBox.Image))
                        {
                            // Create a new bitmap with additional space for information at the bottom
                            Bitmap screenshotWithInfo = new Bitmap(
                                originalImage.Width,
                                originalImage.Height + 50); // Add 50 pixels for info bar

                            using (Graphics g = Graphics.FromImage(screenshotWithInfo))
                            {
                                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

                                // Draw the original network image
                                g.DrawImage(originalImage, 0, 0, originalImage.Width, originalImage.Height);

                                // Draw a black background for the info area
                                g.FillRectangle(new SolidBrush(Color.Black),
                                    0, originalImage.Height, originalImage.Width, 50);

                                // Get selected match if any
                                OrientationMatch match = null;
                                if (matchResultsGridView.SelectedRows.Count > 0)
                                {
                                    int selectedIndex = matchResultsGridView.SelectedRows[0].Index;
                                    if (selectedIndex >= 0 && selectedIndex < orientationMatches.Count)
                                    {
                                        match = orientationMatches[selectedIndex];
                                    }
                                }

                                // Add network information
                                int yPos = originalImage.Height + 5;

                                // Draw network info
                                string networkInfo = $"Pores: {networkModel.Pores.Count} | " +
                                    $"Throats: {networkModel.Throats.Count} | " +
                                    $"Porosity: {networkModel.Porosity:P2}";
                                g.DrawString(networkInfo, new Font("Arial", 9, FontStyle.Bold),
                                    Brushes.White, new Point(10, yPos));

                                // Draw match info if available
                                yPos += 20;
                                if (match != null)
                                {
                                    string matchInfo = $"Match: {match.MatchPercentage:F1}% | " +
                                        $"Orientation: {GetOrientationName(match.Orientation)} | " +
                                        $"Matching Pores: {match.MatchingPoreIds.Count}";
                                    g.DrawString(matchInfo, new Font("Arial", 9, FontStyle.Bold),
                                        Brushes.Yellow, new Point(10, yPos));
                                }
                                else
                                {
                                    g.DrawString("No match selected", new Font("Arial", 9, FontStyle.Bold),
                                        Brushes.White, new Point(10, yPos));
                                }

                                // Add timestamp in the corner
                                g.DrawString($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                                    new Font("Arial", 8),
                                    Brushes.Gray,
                                    new Point(originalImage.Width - 200, originalImage.Height + 30));
                            }

                            // Save the image in the format specified by the file extension
                            string extension = Path.GetExtension(saveDialog.FileName).ToLower();
                            ImageFormat format = ImageFormat.Png; // Default

                            if (extension == ".jpg" || extension == ".jpeg")
                                format = ImageFormat.Jpeg;
                            else if (extension == ".bmp")
                                format = ImageFormat.Bmp;

                            screenshotWithInfo.Save(saveDialog.FileName, format);

                            // Notify the user of success
                            statusLabel.Text = "Screenshot saved successfully.";
                            MessageBox.Show("Screenshot saved successfully.", "Save Complete",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error saving screenshot: {ex.Message}",
                            "Screenshot Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void Network3DPictureBox_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                isDragging = true;
                lastMousePosition = e.Location;
            }
            else if (e.Button == MouseButtons.Middle)
            {
                isPanning = true;
                lastMousePosition = e.Location;
            }
        }

        private void Network3DPictureBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (isDragging)
            {
                // Calculate rotation deltas
                float deltaX = (e.X - lastMousePosition.X) * 0.5f;
                float deltaY = (e.Y - lastMousePosition.Y) * 0.5f;

                // Update rotation angles
                rotationY += deltaX;
                rotationX += deltaY;

                // Render with new rotation
                RenderNetwork3D();

                lastMousePosition = e.Location;
            }
            else if (isPanning)
            {
                // Calculate panning deltas
                float deltaX = (e.X - lastMousePosition.X) * 0.01f;
                float deltaY = (e.Y - lastMousePosition.Y) * 0.01f;

                // Update pan offsets
                panOffsetX += deltaX;
                panOffsetY += deltaY;

                // Render with new pan
                RenderNetwork3D();

                lastMousePosition = e.Location;
            }
        }

        private void Network3DPictureBox_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                isDragging = false;
            }
            else if (e.Button == MouseButtons.Middle)
            {
                isPanning = false;
            }
        }

        private void Network3DPictureBox_MouseWheel(object sender, MouseEventArgs e)
        {
            // Handle mouse wheel zooming
            float zoomFactor = 1.0f + (e.Delta > 0 ? 0.1f : -0.1f);
            viewScale *= zoomFactor;

            // Limit minimum and maximum zoom
            viewScale = Math.Max(0.2f, Math.Min(3.0f, viewScale));

            RenderNetwork3D();
        }

        private void PoreNetwork2DComparisonForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            // Cancel any ongoing operations
            cancellationTokenSource?.Cancel();

            // Clean up resources
            if (accelerator != null)
            {
                accelerator.Dispose();
                accelerator = null;
            }

            if (gpuContext != null)
            {
                gpuContext.Dispose();
                gpuContext = null;
            }

            // Dispose images
            if (thinSectionImage != null)
            {
                thinSectionImage.Dispose();
                thinSectionImage = null;
            }

            if (processedThinSectionImage != null)
            {
                processedThinSectionImage.Dispose();
                processedThinSectionImage = null;
            }
        }

        #endregion
    }
}