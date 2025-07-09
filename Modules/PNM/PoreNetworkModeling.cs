//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS
{
    public class PoreNetworkModelingForm : Form
    {
        private MainForm mainForm;
        private Material selectedMaterial;
        private PoreNetworkModel networkModel = new PoreNetworkModel();
        private bool viewerMode = false; // New flag to track viewer mode

        // Permeability Simulation
        private PermeabilitySimulationResult permeabilityResult;

        private TabPage permeabilityTab;
        private Button simulateButton;
        private PictureBox permeabilityPictureBox;
        private Button savePermeabilityButton;
        private Button loadPermeabilityButton;
        private Button exportPermeabilityButton;
        private Button screenshotPermeabilityButton;

        // UI Elements
        private SplitContainer mainSplitContainer;

        private Panel previewPanel;
        private PictureBox previewPictureBox;
        private ComboBox materialComboBox;
        private NumericUpDown markerExtentNumeric;
        private CheckBox useGpuCheckBox;
        private TrackBar zoomTrackBar;
        private Button generateButton;
        private Button exportButton;
        private Button saveButton;
        private Button loadButton;
        private DataGridView poreDataGridView;
        private ProgressBar progressBar;
        private Label statusLabel;
        private Panel visualizationPanel;
        private PictureBox networkPictureBox;
        private TabControl mainTabControl;
        private NumericUpDown maxThroatLengthFactorNumeric;
        private NumericUpDown minOverlapFactorNumeric;
        private CheckBox enforceFlowPathCheckBox;
        private double maxThroatLengthFactor = 3.0;
        private double minOverlapFactor = 0.1;
        private bool enforceFlowPath = true;
        private enum VisualizationMethod
        {
            Darcy,
            LatticeBoltzmann,
            NavierStokes,
            Combined
        }
        // 3d rotation
        private float rotationX = 30.0f;
        private Button compareWith2DButton;
        private float rotationY = 30.0f;
        private float rotationZ = 0.0f;
        private float viewScale = 1.0f;
        private Point lastMousePosition;
        private bool isDragging = false;
        private bool isPanning = false;
        private float panOffsetX = 0.0f;
        private float panOffsetY = 0.0f;

        // Processing data
        private CancellationTokenSource cts;

        private ParticleSeparator.SeparationResult separationResult;
        private float previewZoom = 1.0f;
        private int currentSlice = 0;

        /// <summary>
        /// Original constructor for use with an active dataset
        /// </summary>
        public PoreNetworkModelingForm(MainForm mainForm)
        {
            try
            {
                this.Icon = Properties.Resources.favicon;
            }
            catch { }
            this.mainForm = mainForm;
            this.viewerMode = false;
            InitializeComponent();
            PopulateMaterialComboBox();
            EnsureDataGridViewHeadersVisible();
        }

        /// <summary>
        /// New constructor for loading a saved pore network model file (viewer mode)
        /// </summary>
        public PoreNetworkModelingForm(MainForm mainForm, string filePath)
        {
            this.mainForm = mainForm;
            this.viewerMode = true;
            InitializeComponent();
            EnsureDataGridViewHeadersVisible();

            // Load the network model from the file
            try
            {
                LoadNetwork(filePath);

                // Update UI to reflect viewer mode
                UpdateUIForViewerMode();

                // Set the window title to include the filename
                this.Text = $"Pore Network Modeling - {Path.GetFileName(filePath)} [Viewer Mode]";

                // Update status
                statusLabel.Text = $"Loaded network with {networkModel.Pores.Count} pores and {networkModel.Throats.Count} throats";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading pore network model: {ex.Message}",
                    "Loading Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[PoreNetworkModelingForm] Error loading model: {ex.Message}\n{ex.StackTrace}");
            }
        }
        private void InitializeComponent()
        {
            // Form setup with modern style
            this.Text = "Pore Network Modeling";
            this.Size = new Size(1280, 900);
            this.MinimumSize = new Size(1280, 700);  // Consistent minimum width
            this.BackColor = Color.FromArgb(240, 240, 240);
            this.Font = new Font("Segoe UI", 9F);

            // =====================
            // TOP RIBBON PANEL 
            // =====================
            Panel ribbonPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 130,  // Increased height
                BackColor = Color.FromArgb(230, 230, 230),
                Padding = new Padding(5),
                BorderStyle = BorderStyle.FixedSingle
            };
            Size iconSize = new Size(24, 24);
            Color primaryColor = Color.FromArgb(64, 105, 180);
            Bitmap separateIcon = PoreNetworkButtonIcons.CreateParticleSeparationIcon(iconSize, primaryColor);
            Bitmap networkIcon = PoreNetworkButtonIcons.CreateNetworkGenerationIcon(iconSize, primaryColor);
            Bitmap permeabilityIcon = PoreNetworkButtonIcons.CreatePermeabilityIcon(iconSize, primaryColor);
            Bitmap tortuosityIcon = PoreNetworkButtonIcons.CreateTortuosityIcon(iconSize, primaryColor);
            Bitmap comparisonIcon = PoreNetworkButtonIcons.CreateTortuosityIcon(iconSize, primaryColor); // Reuse icon for now

            // FIRST ROW OF CONTROLS - Major functional groups
            // Material Selection Group
            GroupBox materialGroup = new GroupBox
            {
                Text = "Material",
                Location = new Point(10, 10),
                Size = new Size(180, 90),
                BackColor = Color.Transparent
            };

            Label materialLabel = new Label
            {
                Text = "Select Pore Material:",
                Location = new Point(10, 20),
                AutoSize = true
            };
            materialGroup.Controls.Add(materialLabel);

            materialComboBox = new ComboBox
            {
                Location = new Point(10, 45),
                Width = 160,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat
            };
            materialComboBox.DisplayMember = "Name";
            materialComboBox.SelectedIndexChanged += (s, e) =>
            {
                if (materialComboBox.SelectedItem is Material material)
                {
                    selectedMaterial = material;
                    UpdatePreviewImage();
                }
            };
            materialGroup.Controls.Add(materialComboBox);
            ribbonPanel.Controls.Add(materialGroup);

            // Process Group
            GroupBox processGroup = new GroupBox
            {
                Text = "Process",
                Location = new Point(200, 10),
                Size = new Size(310, 90),
                BackColor = Color.Transparent
            };

            Button separateButton = new Button
            {
                Text = "1. Separate Particles",
                Location = new Point(15, 25),
                Width = 135,
                Height = 50,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(225, 225, 225),
                Image = separateIcon,
                ImageAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                Padding = new Padding(5, 0, 5, 0)
            };
            separateButton.Click += async (s, e) => await SeparateParticlesAsync();
            processGroup.Controls.Add(separateButton);

            generateButton = new Button
            {
                Text = "2. Generate Network",
                Location = new Point(160, 25),
                Width = 135,
                Height = 50,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(225, 225, 225),
                Enabled = false,
                Image = networkIcon,
                ImageAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                Padding = new Padding(5, 0, 5, 0)
            };
            generateButton.Click += async (s, e) => await GenerateNetworkAsync();
            processGroup.Controls.Add(generateButton);
            ribbonPanel.Controls.Add(processGroup);

            // Data Group
            GroupBox dataGroup = new GroupBox
            {
                Text = "Data",
                Location = new Point(520, 10),
                Size = new Size(210, 90),
                BackColor = Color.Transparent
            };

            saveButton = new Button
            {
                Text = "Save",
                Location = new Point(15, 25),
                Width = 85,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(225, 225, 225),
                Enabled = false
            };
            saveButton.Click += (s, e) => SaveNetwork();
            dataGroup.Controls.Add(saveButton);

            loadButton = new Button
            {
                Text = "Load",
                Location = new Point(110, 25),
                Width = 85,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(225, 225, 225)
            };
            loadButton.Click += (s, e) => LoadNetwork();
            dataGroup.Controls.Add(loadButton);

            exportButton = new Button
            {
                Text = "Export Data",
                Location = new Point(15, 60),
                Width = 180,
                Height = 25,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(225, 225, 225),
                Enabled = false
            };
            exportButton.Click += (s, e) => ExportData();
            dataGroup.Controls.Add(exportButton);
            ribbonPanel.Controls.Add(dataGroup);

            // Permeability Group - Repositioned and made wider
            GroupBox permeabilityGroup = new GroupBox
            {
                Text = "Permeability",
                Location = new Point(740, 10),
                Size = new Size(250, 90),
                BackColor = Color.Transparent
            };

            simulateButton = new Button
            {
                Text = "Simulate Permeability",
                Location = new Point(15, 25),
                Width = 220,
                Height = 32,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(225, 225, 225),
                Enabled = false,
                Image = permeabilityIcon,
                ImageAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                Padding = new Padding(5, 0, 5, 0)
            };
            simulateButton.Click += SimulatePermeabilityClick;
            permeabilityGroup.Controls.Add(simulateButton);

            savePermeabilityButton = new Button
            {
                Text = "Save Results",
                Location = new Point(15, 60),
                Width = 105,
                Height = 25,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(225, 225, 225),
                Enabled = false
            };
            savePermeabilityButton.Click += SavePermeabilityResults;
            permeabilityGroup.Controls.Add(savePermeabilityButton);

            loadPermeabilityButton = new Button
            {
                Text = "Load Results",
                Location = new Point(130, 60),
                Width = 105,
                Height = 25,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(225, 225, 225)
            };
            loadPermeabilityButton.Click += LoadPermeabilityResults;
            permeabilityGroup.Controls.Add(loadPermeabilityButton);
            ribbonPanel.Controls.Add(permeabilityGroup);

            // Settings Group
            GroupBox settingsGroup = new GroupBox
            {
                Text = "Settings",
                Location = new Point(1000, 10),
                Size = new Size(250, 90),
                BackColor = Color.Transparent
            };

            useGpuCheckBox = new CheckBox
            {
                Text = "Use GPU",
                Location = new Point(15, 25),
                AutoSize = true,
                Checked = true
            };
            settingsGroup.Controls.Add(useGpuCheckBox);

            Button poreConnectivityButton = new Button
            {
                Text = "Pore Connectivity",
                Location = new Point(100, 20),
                Width = 140,
                Height = 32,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(225, 225, 225),
                Image = tortuosityIcon,
                ImageAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                Padding = new Padding(2, 0, 2, 0)
            };
            poreConnectivityButton.Click += OpenPoreConnectivityDialog;
            settingsGroup.Controls.Add(poreConnectivityButton);

            exportPermeabilityButton = new Button
            {
                Text = "Export Data",
                Location = new Point(15, 55),
                Width = 115,
                Height = 32,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(225, 225, 225),
                Enabled = false,
                Image = permeabilityIcon,
                ImageAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                Padding = new Padding(2, 0, 2, 0)
            };
            exportPermeabilityButton.Click += ExportPermeabilityResults;
            settingsGroup.Controls.Add(exportPermeabilityButton);

            compareWith2DButton = new Button
            {
                Text = "2D-3D",
                Location = new Point(135, 55),
                Width = 105,
                Height = 32,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(225, 225, 225),
                Image = comparisonIcon,
                ImageAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                Padding = new Padding(2, 0, 2, 0),
                Enabled = false
            };
            compareWith2DButton.Click += OpenPoreNetwork2DComparison;
            settingsGroup.Controls.Add(compareWith2DButton);

            ribbonPanel.Controls.Add(settingsGroup);

            // Status Bar (placed at the bottom of ribbon panel)
            Panel statusPanel = new Panel
            {
                Location = new Point(10, 105),
                Size = new Size(1240, 20),
                BackColor = Color.Transparent
            };

            progressBar = new ProgressBar
            {
                Location = new Point(0, 0),
                Width = 300,
                Height = 20,
                Style = ProgressBarStyle.Continuous,
                Value = 0
            };
            statusPanel.Controls.Add(progressBar);

            statusLabel = new Label
            {
                Text = "Ready",
                Location = new Point(310, 2),
                Width = 930,
                Height = 20,
                Font = new Font("Segoe UI", 8F),
                ForeColor = Color.DarkBlue
            };
            statusPanel.Controls.Add(statusLabel);
            ribbonPanel.Controls.Add(statusPanel);

            // =====================
            // MAIN CONTENT AREA
            // =====================
            mainTabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Padding = new Point(15, 5)
            };

            // TAB 1: Slice View
            TabPage sliceViewTab = new TabPage("Slice View");
            sliceViewTab.Padding = new Padding(3);

            Panel sliceViewPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                Padding = new Padding(5)
            };

            // Top controls for slice navigation
            Panel sliceControlPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 40
            };

            Label sliceLabel = new Label
            {
                Text = "Slice:",
                Location = new Point(10, 10),
                AutoSize = true
            };
            sliceControlPanel.Controls.Add(sliceLabel);

            TrackBar sliceTrackBar = new TrackBar
            {
                Location = new Point(50, 5),
                Width = 650,
                Minimum = 0,
                Maximum = mainForm.GetDepth() > 0 ? mainForm.GetDepth() - 1 : 0,
                Value = mainForm.CurrentSlice,
                TickFrequency = Math.Max(1, mainForm.GetDepth() / 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            sliceTrackBar.ValueChanged += (s, e) =>
            {
                currentSlice = sliceTrackBar.Value;
                UpdatePreviewImage();
            };
            sliceControlPanel.Controls.Add(sliceTrackBar);

            // Right controls for zoom
            Label zoomLabel = new Label
            {
                Text = "Zoom:",
                Location = new Point(710, 10),
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            sliceControlPanel.Controls.Add(zoomLabel);

            zoomTrackBar = new TrackBar
            {
                Location = new Point(760, 5),
                Width = 150,
                Minimum = 1,
                Maximum = 20,
                Value = 10,
                TickFrequency = 2,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            zoomTrackBar.ValueChanged += (s, e) =>
            {
                previewZoom = zoomTrackBar.Value / 10.0f;
                UpdatePreviewImage();
            };
            sliceControlPanel.Controls.Add(zoomTrackBar);
            sliceViewPanel.Controls.Add(sliceControlPanel);

            // Main slice preview
            previewPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.Black
            };

            previewPictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black
            };
            previewPictureBox.MouseWheel += PreviewPictureBox_MouseWheel;
            previewPanel.Controls.Add(previewPictureBox);
            sliceViewPanel.Controls.Add(previewPanel);

            sliceViewTab.Controls.Add(sliceViewPanel);
            mainTabControl.Controls.Add(sliceViewTab);

            // TAB 2: 3D Network View
            TabPage networkViewTab = new TabPage("3D Network View");
            networkViewTab.Padding = new Padding(3);

            visualizationPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.Black
            };

            // Create mouse dragging instructions
            Label instructionsLabel = new Label
            {
                Text = "Left-click and drag to rotate | Middle-click and drag to pan | Mouse wheel to zoom",
                Dock = DockStyle.Bottom,
                Height = 25,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.LightGray,
                BackColor = Color.FromArgb(20, 20, 20)
            };
            visualizationPanel.Controls.Add(instructionsLabel);

            // Create a toolbar for view controls
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

            Button resetViewButton = new Button
            {
                Text = "Reset View",
                Location = new Point(150, 8),
                Width = 100,
                Height = 25,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(80, 80, 80),
                ForeColor = Color.White
            };
            resetViewButton.Click += (s, e) =>
            {
                rotationX = 30.0f;
                rotationY = 30.0f;
                rotationZ = 0.0f;
                viewScale = 1.0f;
                panOffsetX = 0.0f;
                panOffsetY = 0.0f;
                RenderNetwork3D();
            };
            controlPanel.Controls.Add(resetViewButton);

            Button screenshotButton = new Button
            {
                Text = "Save Screenshot",
                Location = new Point(260, 8),
                Width = 130,
                Height = 25,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(80, 80, 80),
                ForeColor = Color.White
            };
            screenshotButton.Click += (s, e) => SaveNetworkScreenshot();
            controlPanel.Controls.Add(screenshotButton);

            visualizationPanel.Controls.Add(controlPanel);

            // Initial visualization label
            Label visualizationLabel = new Label
            {
                Text = "3D Pore Network Visualization",
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 16),
                ForeColor = Color.White,
                BackColor = Color.Black
            };
            visualizationPanel.Controls.Add(visualizationLabel);

            // Create PictureBox for 3D rendering (will be initialized during rendering)
            networkPictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.CenterImage
            };

            // Add mouse handling for rotation and zooming
            networkPictureBox.MouseDown += (s, e) =>
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
            };

            networkPictureBox.MouseMove += (s, e) =>
            {
                if (isDragging)
                {
                    // Calculate delta movement for rotation
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
                    // Calculate delta movement for panning
                    float deltaX = (e.X - lastMousePosition.X) * 0.01f;
                    float deltaY = (e.Y - lastMousePosition.Y) * 0.01f;

                    // Update pan offsets
                    panOffsetX += deltaX;
                    panOffsetY += deltaY;

                    // Render with new pan
                    RenderNetwork3D();

                    lastMousePosition = e.Location;
                }
            };

            networkPictureBox.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    isDragging = false;
                }
                else if (e.Button == MouseButtons.Middle)
                {
                    isPanning = false;
                }
            };

            networkPictureBox.MouseWheel += (s, e) =>
            {
                // Change zoom level with mouse wheel
                float zoomFactor = 1.0f + (e.Delta > 0 ? 0.1f : -0.1f);
                viewScale *= zoomFactor;

                // Limit minimum and maximum zoom
                viewScale = Math.Max(0.2f, Math.Min(3.0f, viewScale));

                RenderNetwork3D();
            };

            networkViewTab.Controls.Add(visualizationPanel);
            mainTabControl.Controls.Add(networkViewTab);

            // TAB 3: Permeability Results
            permeabilityTab = new TabPage("Permeability Results");
            permeabilityTab.Padding = new Padding(3);

            Panel permeabilityPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.Black
            };

            Label permeabilityLabel = new Label
            {
                Text = "No permeability simulation results yet.\nUse 'Simulate Permeability' to run a simulation.",
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 16),
                ForeColor = Color.White,
                BackColor = Color.Black
            };
            permeabilityPanel.Controls.Add(permeabilityLabel);

            permeabilityTab.Controls.Add(permeabilityPanel);
            mainTabControl.Controls.Add(permeabilityTab);

            // =====================
            // BOTTOM DATA PANEL
            // =====================
            Panel dataOuterPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 250,
                Padding = new Padding(0),
                BorderStyle = BorderStyle.None
            };

            // Header panel - this is a separate panel that sits above the grid
            Panel headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = Color.FromArgb(230, 230, 230),
                BorderStyle = BorderStyle.FixedSingle
            };

            Label dataHeaderLabel = new Label
            {
                Text = "Pore Data",
                Location = new Point(10, 7),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                AutoSize = true
            };
            headerPanel.Controls.Add(dataHeaderLabel);

            // Toggle button to collapse/expand the data grid
            Button toggleDataPanelButton = new Button
            {
                Text = "▲",
                Size = new Size(25, 23),
                Location = new Point(80, 3),
                FlatStyle = FlatStyle.Flat,
                FlatAppearance = { BorderSize = 0 },
                BackColor = Color.Transparent
            };

            // Content panel that holds the DataGridView - separate from the header
            Panel dataContentPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(5)
            };

            // Toggle now only shows/hides the content panel, not the entire outer panel
            toggleDataPanelButton.Click += (s, e) =>
            {
                if (dataContentPanel.Visible)
                {
                    // Collapse - hide content panel but keep header
                    dataContentPanel.Visible = false;
                    toggleDataPanelButton.Text = "▼";
                    dataOuterPanel.Height = headerPanel.Height;
                }
                else
                {
                    // Expand - show content panel
                    dataContentPanel.Visible = true;
                    toggleDataPanelButton.Text = "▲";
                    dataOuterPanel.Height = 250;

                    // Force refresh after expansion
                    this.BeginInvoke(new Action(() =>
                    {
                        if (poreDataGridView != null && !poreDataGridView.IsDisposed)
                        {
                            poreDataGridView.Refresh();
                            poreDataGridView.Update();
                        }
                    }));
                }
            };

            headerPanel.Controls.Add(toggleDataPanelButton);

            // Data grid with fixed and forced headers
            poreDataGridView = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersWidth = 25,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                RowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White },
                AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(240, 240, 240) },
                GridColor = Color.FromArgb(220, 220, 220)
            };

            // Critical header settings
            poreDataGridView.ColumnHeadersVisible = true;
            poreDataGridView.ColumnHeadersHeight = 30;
            poreDataGridView.EnableHeadersVisualStyles = false;
            poreDataGridView.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

            // Set up the columns for the pore data
            poreDataGridView.Columns.Add("Id", "ID");
            poreDataGridView.Columns.Add("Volume", "Volume (µm³)");
            poreDataGridView.Columns.Add("Area", "Surface Area (µm²)");
            poreDataGridView.Columns.Add("Radius", "Equiv. Radius (µm)");
            poreDataGridView.Columns.Add("X", "X (µm)");
            poreDataGridView.Columns.Add("Y", "Y (µm)");
            poreDataGridView.Columns.Add("Z", "Z (µm)");
            poreDataGridView.Columns.Add("Connections", "# Connections");

            // Explicitly set header style with increased padding for better visibility
            DataGridViewCellStyle headerStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(210, 210, 210), // Slightly darker for better contrast
                ForeColor = Color.Black,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                Padding = new Padding(0, 7, 0, 7) // More vertical padding
            };

            // Apply header style to all columns
            foreach (DataGridViewColumn col in poreDataGridView.Columns)
            {
                col.HeaderCell.Style = headerStyle;
            }

            // Set default header style
            poreDataGridView.ColumnHeadersDefaultCellStyle = headerStyle;

            // Setup context menu for DataGridView for exporting
            ContextMenuStrip gridContextMenu = new ContextMenuStrip();
            ToolStripMenuItem exportMenuItem = new ToolStripMenuItem("Export Selected Rows");
            exportMenuItem.Click += (s, e) => ExportSelectedRows();
            gridContextMenu.Items.Add(exportMenuItem);
            poreDataGridView.ContextMenuStrip = gridContextMenu;

            // Force header visibility on load
            poreDataGridView.HandleCreated += (s, e) =>
            {
                poreDataGridView.ColumnHeadersVisible = true;
                poreDataGridView.Refresh();
            };

            // Add grid to the content panel
            dataContentPanel.Controls.Add(poreDataGridView);

            // Add panels to the outer container in correct order
            dataOuterPanel.Controls.Add(dataContentPanel); // Content first (at the bottom)
            dataOuterPanel.Controls.Add(headerPanel);      // Header last (at the top)

            // Add the outer panel to the form
            this.Controls.Add(dataOuterPanel);

            // =====================
            // ASSEMBLE FORM
            // =====================
            this.Controls.Add(mainTabControl);
            this.Controls.Add(dataOuterPanel);
            this.Controls.Add(ribbonPanel);

            // Select the 3D Network View tab if in viewer mode
            if (viewerMode)
            {
                mainTabControl.SelectedIndex = 1; // Switch to 3D Network View tab
            }

            // Force an initial preview update and ensure data grid visibility
            this.Load += (s, e) =>
            {
                UpdatePreviewImage();
                EnsureDataGridViewHeadersVisible();

                // Additional code to ensure the data grid is properly initialized
                if (poreDataGridView != null && !poreDataGridView.IsDisposed)
                {
                    poreDataGridView.ColumnHeadersVisible = true;
                    poreDataGridView.Refresh();
                }
            };

            this.Resize += (s, e) =>
            {
                if (dataContentPanel.Visible && poreDataGridView != null && !poreDataGridView.IsDisposed)
                {
                    poreDataGridView.ColumnHeadersVisible = true;
                    poreDataGridView.Refresh();
                }
            };
            this.Disposed += (s, e) =>
            {
                separateIcon?.Dispose();
                networkIcon?.Dispose();
                permeabilityIcon?.Dispose();
                tortuosityIcon?.Dispose();
                comparisonIcon?.Dispose();
            };
        }
        private void OpenPoreNetwork2DComparison(object sender, EventArgs e)
        {
            if (networkModel == null || networkModel.Pores.Count == 0)
            {
                MessageBox.Show("Please generate a pore network first", "No Network",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                using (var comparisonForm = new PoreNetwork2DComparisonForm(
                    mainForm, networkModel, selectedMaterial, networkModel.PixelSize))
                {
                    comparisonForm.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening 2D comparison: {ex.Message}",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[PoreNetworkModelingForm] Error opening 2D comparison: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Updates the UI to reflect viewer mode (disable processing options)
        /// </summary>
        private void UpdateUIForViewerMode()
        {
            // Disable processing-related controls
            materialComboBox.Enabled = false;
            generateButton.Enabled = false;
            useGpuCheckBox.Enabled = false;
            markerExtentNumeric.Enabled = false;

            // Enable data-related controls
            exportButton.Enabled = true;
            saveButton.Enabled = true;
            compareWith2DButton.Enabled = true;
            // Create a mock "Loaded Model" material for display purposes
            Material viewerMaterial = new Material("Loaded Model", Color.Gray, 0, 255, 1);
            materialComboBox.Items.Clear();
            materialComboBox.Items.Add(viewerMaterial);
            materialComboBox.SelectedIndex = 0;

            // Render the 3D network visualization
            Render3DVisualization();

            // Update pore table
            UpdatePoreTable();
        }

        private void PopulateMaterialComboBox()
        {
            // Skip in viewer mode
            if (viewerMode)
                return;

            materialComboBox.Items.Clear();
            foreach (Material material in mainForm.Materials)
            {
                // Skip the Exterior material
                if (material.Name.ToLower() != "exterior")
                {
                    materialComboBox.Items.Add(material);
                }
            }

            if (materialComboBox.Items.Count > 0)
            {
                materialComboBox.SelectedIndex = 0;
            }
        }

        private async Task SeparateParticlesAsync()
        {
            // Skip in viewer mode
            if (viewerMode)
                return;

            if (selectedMaterial == null)
            {
                MessageBox.Show("Please select a material first", "No Material Selected",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Check if mainForm is initialized
            if (mainForm == null)
            {
                MessageBox.Show("Main form reference is missing", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            progressBar.Value = 0;
            statusLabel.Text = "Separating particles...";

            // Cancel any existing operation
            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                catch { /* Ignore any errors during cancellation */ }
            }
            cts = new CancellationTokenSource();

            try
            {
                // Create progress reporter
                Progress<int> progress = new Progress<int>(percent =>
                {
                    if (!IsDisposed && progressBar != null && !progressBar.IsDisposed)
                        progressBar.Value = percent;
                });

                // Create particle separator - with null checks for UI elements
                bool useGpu = useGpuCheckBox != null && useGpuCheckBox.Checked;
                int markerExtent = markerExtentNumeric != null ? (int)markerExtentNumeric.Value : 3;

                // Make sure we have valid mainForm.volumeLabels
                if (mainForm.volumeLabels == null)
                {
                    MessageBox.Show("No volume data available in the main form", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    statusLabel.Text = "Error: No volume data";
                    return;
                }

                // Safely create and use the separator
                ParticleSeparator separator = null;
                try
                {
                    separator = new ParticleSeparator(mainForm, selectedMaterial, useGpu);

                    // Separate particles (pores)
                    separationResult = await Task.Run(() => separator.SeparateParticles(
                        process3D: true,
                        conservative: true,
                        currentSlice: mainForm.CurrentSlice,
                        progress: progress,
                        cancellationToken: cts.Token
                    ), cts.Token);

                    // Update the preview if we have results
                    if (separationResult != null)
                    {
                        UpdatePreviewImage();

                        // Enable the generate button
                        if (generateButton != null && !generateButton.IsDisposed)
                            generateButton.Enabled = true;

                        if (statusLabel != null && !statusLabel.IsDisposed)
                            statusLabel.Text = $"Identified {separationResult.Particles.Count} potential pores";
                    }
                    else
                    {
                        statusLabel.Text = "Error: No separation results returned";
                    }
                }
                finally
                {
                    // Ensure separator is disposed
                    separator?.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                if (statusLabel != null && !statusLabel.IsDisposed)
                    statusLabel.Text = "Operation cancelled";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error separating particles: {ex.Message}",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

                if (statusLabel != null && !statusLabel.IsDisposed)
                    statusLabel.Text = "Error separating particles";

                Logger.Log($"[PoreNetworkModelingForm] Error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private async Task GenerateNetworkAsync()
        {
            // Skip in viewer mode
            if (viewerMode)
                return;

            if (separationResult == null)
            {
                MessageBox.Show("Please separate particles first", "No Data",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            progressBar.Value = 0;
            statusLabel.Text = "Generating pore network...";

            try
            {
                Progress<int> progress = new Progress<int>(percent =>
                {
                    if (!IsDisposed && progressBar != null && !progressBar.IsDisposed)
                        progressBar.Value = percent;
                });

                // Cancel any existing operation
                if (cts != null)
                {
                    try
                    {
                        cts.Cancel();
                        cts.Dispose();
                    }
                    catch { /* Ignore any errors during cancellation */ }
                }
                cts = new CancellationTokenSource();

                // Use the new generator class
                using (PoreNetworkGenerator generator = new PoreNetworkGenerator())
                {
                    // Generate the network model with petrophysical connectivity controls
                    networkModel = await generator.GenerateNetworkFromSeparationResult(
                        separationResult,
                        mainForm.pixelSize,
                        progress,
                        useGpuCheckBox.Checked,
                        maxThroatLengthFactor,  // Class field
                        minOverlapFactor,       // Class field
                        enforceFlowPath,        // Class field
                        cts.Token);             // Add cancellation token
                }

                // Update UI with results
                UpdatePoreTable();
                Render3DVisualization();

                // Enable export and save buttons
                exportButton.Enabled = true;
                saveButton.Enabled = true;
                simulateButton.Enabled = true;
                compareWith2DButton.Enabled = true;
                statusLabel.Text = $"Generated network with {networkModel.Pores.Count} pores and " +
                                  $"{networkModel.Throats.Count} throats. Porosity: {networkModel.Porosity:P2}";
            }
            catch (OperationCanceledException)
            {
                statusLabel.Text = "Network generation cancelled";
                Logger.Log("[PoreNetworkModelingForm] Network generation was cancelled by user");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error generating network: {ex.Message}",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                statusLabel.Text = "Error generating network";
                Logger.Log($"[PoreNetworkModelingForm] Error: {ex.Message}\n{ex.StackTrace}");
            }
        }
        private void UpdatePreviewImage()
        {
            try
            {
                // Skip preview update if in viewer mode
                if (viewerMode)
                    return;

                // Case 1: No separation result yet, show raw material
                if (separationResult == null || separationResult.LabelVolume == null)
                {
                    // Make sure we have data to display
                    if (mainForm.GetWidth() > 0 && mainForm.GetHeight() > 0 && mainForm.GetDepth() > 0)
                    {
                        // Ensure current slice is valid
                        currentSlice = Math.Min(currentSlice, mainForm.GetDepth() - 1);

                        // Create bitmap for preview
                        Bitmap materialBitmap = new Bitmap(mainForm.GetWidth(), mainForm.GetHeight());
                        using (Graphics g = Graphics.FromImage(materialBitmap))
                        {
                            g.Clear(Color.Black); // Start with black background
                        }

                        // Draw the material
                        for (int y = 0; y < mainForm.GetHeight(); y++)
                        {
                            for (int x = 0; x < mainForm.GetWidth(); x++)
                            {
                                if (selectedMaterial != null &&
                                    x < LabelVolumeHelper.GetWidth(mainForm.volumeLabels) &&
                                    y < LabelVolumeHelper.GetHeight(mainForm.volumeLabels) &&
                                    currentSlice < LabelVolumeHelper.GetDepth(mainForm.volumeLabels) &&
                                    LabelVolumeHelper.GetLabel(mainForm.volumeLabels, x, y, currentSlice) == selectedMaterial.ID)
                                {
                                    materialBitmap.SetPixel(x, y, selectedMaterial.Color);
                                }
                            }
                        }

                        // Set the preview image
                        previewPictureBox.Image = materialBitmap;
                        previewPictureBox.SizeMode = PictureBoxSizeMode.Zoom;

                        // Update status
                        statusLabel.Text = $"Viewing slice {currentSlice + 1} of {mainForm.GetDepth()} - Raw material view";
                    }
                    return;
                }

                // Case 2: We have separation results, show labeled data
                // Use helper methods to get dimensions
                int width = LabelVolumeHelper.GetWidth(separationResult.LabelVolume);
                int height = LabelVolumeHelper.GetHeight(separationResult.LabelVolume);
                int depth = LabelVolumeHelper.GetDepth(separationResult.LabelVolume);

                // Ensure current slice is valid
                int slice = Math.Min(currentSlice, depth - 1);

                // Create a bitmap for the slice
                Bitmap labelBitmap = new Bitmap(width, height);

                // Create a colormap for visualizing the labels
                Dictionary<int, Color> colorMap = new Dictionary<int, Color>();
                Random random = new Random(0); // Fixed seed for consistent colors

                // Add background color
                colorMap[0] = Color.Black;

                // Draw the labeled data
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        // Use helper method to get the label value
                        int label = LabelVolumeHelper.GetLabel(separationResult.LabelVolume, x, y, slice);

                        if (!colorMap.ContainsKey(label))
                        {
                            // Generate a new random color for this label
                            Color randomColor = Color.FromArgb(
                                random.Next(100, 255),
                                random.Next(100, 255),
                                random.Next(100, 255)
                            );
                            colorMap[label] = randomColor;
                        }

                        labelBitmap.SetPixel(x, y, colorMap[label]);
                    }
                }

                // Apply zoom if needed
                if (previewZoom > 1.0f)
                {
                    int newWidth = (int)(width * previewZoom);
                    int newHeight = (int)(height * previewZoom);

                    Bitmap zoomedBitmap = new Bitmap(newWidth, newHeight);
                    using (Graphics g = Graphics.FromImage(zoomedBitmap))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                        g.DrawImage(labelBitmap, 0, 0, newWidth, newHeight);
                    }

                    previewPictureBox.Image = zoomedBitmap;
                    previewPictureBox.SizeMode = PictureBoxSizeMode.CenterImage;
                }
                else
                {
                    previewPictureBox.Image = labelBitmap;
                    previewPictureBox.SizeMode = PictureBoxSizeMode.Zoom;
                }

                // Update status
                statusLabel.Text = $"Viewing slice {slice + 1} of {depth} - {separationResult.Particles.Count} pores identified";
            }
            catch (Exception ex)
            {
                Logger.Log($"Error updating preview: {ex.Message}");
                statusLabel.Text = "Error displaying preview image";

                // Create a simple error message bitmap
                Bitmap errorBitmap = new Bitmap(200, 100);
                using (Graphics g = Graphics.FromImage(errorBitmap))
                {
                    g.Clear(Color.Black);
                    using (Font font = new Font("Arial", 10))
                    {
                        g.DrawString("Preview Error", font, Brushes.Red, 10, 10);
                        g.DrawString(ex.Message, font, Brushes.Red, 10, 30);
                    }
                }
                previewPictureBox.Image = errorBitmap;
            }
        }

        private void PreviewPictureBox_MouseWheel(object sender, MouseEventArgs e)
        {
            // Adjust zoom with mouse wheel
            if (e.Delta > 0 && zoomTrackBar.Value < zoomTrackBar.Maximum)
            {
                zoomTrackBar.Value++;
            }
            else if (e.Delta < 0 && zoomTrackBar.Value > zoomTrackBar.Minimum)
            {
                zoomTrackBar.Value--;
            }
        }

        private void UpdatePoreTable()
        {
            poreDataGridView.Rows.Clear();

            foreach (var pore in networkModel.Pores)
            {
                poreDataGridView.Rows.Add(
                    pore.Id,
                    Math.Round(pore.Volume, 2),
                    Math.Round(pore.Area, 2),
                    Math.Round(pore.Radius, 2),
                    Math.Round(pore.Center.X, 2),
                    Math.Round(pore.Center.Y, 2),
                    Math.Round(pore.Center.Z, 2),
                    pore.ConnectionCount
                );
            }

            // Update any statistics display to include tortuosity
            statusLabel.Text = $"Network: {networkModel.Pores.Count} pores, {networkModel.Throats.Count} throats, " +
                               $"Porosity: {networkModel.Porosity:P2}, Tortuosity: {networkModel.Tortuosity:F2}";

            EnsureDataGridViewHeadersVisible();
        }

        private void Render3DVisualization()
        {
            if (networkModel == null || networkModel.Pores.Count == 0)
            {
                visualizationPanel.Controls.Clear();
                Label label = new Label
                {
                    Text = "No pore network data to visualize.",
                    TextAlign = ContentAlignment.MiddleCenter,
                    Dock = DockStyle.Fill,
                    Font = new Font("Arial", 16),
                    ForeColor = Color.White,
                    BackColor = Color.Black
                };
                visualizationPanel.Controls.Add(label);
                return;
            }

            // Configure visualization panel and create controls
            visualizationPanel.Controls.Clear();

            // Create a toolbar for view controls
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

            Button resetViewButton = new Button
            {
                Text = "Reset View",
                Location = new Point(150, 8),
                Width = 100,
                Height = 25,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(80, 80, 80),
                ForeColor = Color.White
            };
            Button screenshotButton = new Button
            {
                Text = "Save Screenshot",
                Location = new Point(260, 8),
                Width = 130,
                Height = 25,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(80, 80, 80),
                ForeColor = Color.White
            };
            screenshotButton.Click += (s, e) => SaveNetworkScreenshot();
            controlPanel.Controls.Add(screenshotButton);

            // Create PictureBox for 3D rendering (now moved before the event handler)
            networkPictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.CenterImage
            };

            // Now the resetViewButton click event will work correctly
            resetViewButton.Click += (s, e) =>
            {
                rotationX = 30.0f;
                rotationY = 30.0f;
                rotationZ = 0.0f;
                viewScale = 1.0f;
                panOffsetX = 0.0f; // Reset panning
                panOffsetY = 0.0f; // Reset panning
                RenderNetwork3D();
            };
            controlPanel.Controls.Add(resetViewButton);

            // Add mouse handling for rotation and zooming
            networkPictureBox.MouseDown += (s, e) =>
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
            };

            networkPictureBox.MouseMove += (s, e) =>
            {
                if (isDragging)
                {
                    // Calculate the delta movement for rotation
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
                    // Calculate the delta movement for panning
                    float deltaX = (e.X - lastMousePosition.X) * 0.01f;
                    float deltaY = (e.Y - lastMousePosition.Y) * 0.01f;

                    // Update pan offsets (add these as class members)
                    panOffsetX += deltaX;
                    panOffsetY += deltaY;

                    // Render with new pan
                    RenderNetwork3D();

                    lastMousePosition = e.Location;
                }
            };

            networkPictureBox.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    isDragging = false;
                }
                else if (e.Button == MouseButtons.Middle)
                {
                    isPanning = false;
                }
            };

            networkPictureBox.MouseWheel += (s, e) =>
            {
                // Change zoom level with mouse wheel
                float zoomFactor = 1.0f + (e.Delta > 0 ? 0.1f : -0.1f);
                viewScale *= zoomFactor;

                // Limit minimum and maximum zoom
                viewScale = Math.Max(0.2f, Math.Min(3.0f, viewScale));

                RenderNetwork3D();
            };

            // Add controls to panel
            visualizationPanel.Controls.Add(networkPictureBox);
            visualizationPanel.Controls.Add(controlPanel);

            // Create instructions label
            Label instructionsLabel = new Label
            {
                Text = "Left-click and drag to rotate | Mouse wheel to zoom",
                Dock = DockStyle.Bottom,
                Height = 25,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.LightGray,
                BackColor = Color.FromArgb(20, 20, 20)
            };
            visualizationPanel.Controls.Add(instructionsLabel);

            // Initial rendering
            RenderNetwork3D();
        }

        private void SaveNetworkScreenshot()
        {
            if (networkPictureBox?.Image == null)
            {
                MessageBox.Show("No network visualization to save.", "Screenshot Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Create a SaveFileDialog to let the user specify where to save the screenshot
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
                        using (Bitmap originalImage = new Bitmap(networkPictureBox.Image))
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

                                // Add network information
                                int yPos = originalImage.Height + 5;

                                // Line 1: Basic network info
                                g.DrawString($"Pores: {networkModel.Pores.Count} | " +
                                            $"Throats: {networkModel.Throats.Count} | " +
                                            $"Porosity: {networkModel.Porosity:P2}",
                                    new Font("Arial", 9, FontStyle.Bold),
                                    Brushes.White,
                                    new Point(10, yPos));

                                // Line 2: Tortuosity and average stats
                                yPos += 20;

                                double avgRadius = networkModel.Pores.Count > 0 ?
                                    networkModel.Pores.Average(p => p.Radius) : 0;
                                double avgConnections = networkModel.Pores.Count > 0 ?
                                    networkModel.Pores.Average(p => p.ConnectionCount) : 0;

                                g.DrawString($"Tortuosity: {networkModel.Tortuosity:F2}",
                                    new Font("Arial", 9, FontStyle.Bold),
                                    Brushes.Yellow, // Highlight tortuosity in yellow
                                    new Point(10, yPos));

                                g.DrawString($"Avg. Radius: {avgRadius:F2} µm | " +
                                            $"Avg. Connections: {avgConnections:F1}",
                                    new Font("Arial", 9, FontStyle.Bold),
                                    Brushes.White,
                                    new Point(150, yPos));

                                // Add timestamp in the corner
                                g.DrawString($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                                    new Font("Arial", 8),
                                    Brushes.Gray,
                                    new Point(originalImage.Width - 200, yPos));
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

        private void RenderNetwork3D()
        {
            if (networkPictureBox == null) return;

            int width = networkPictureBox.Width;
            int height = networkPictureBox.Height;

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

                // Project and render throats first (draw from back to front)
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

                        // Average Z for depth sorting (to draw back-to-front)
                        double avgZ = (transformedP1.z + transformedP2.z) / 2;

                        // Create gradient color based on depth
                        int intensity = (int)(100 + Math.Min(155, Math.Max(0, 155 * (1 - avgZ / 500))));
                        Color throatColor = Color.FromArgb(intensity, intensity, intensity);

                        throatsWithDepth.Add((avgZ, p1, p2, thickness, throatColor));
                    }
                }

                // Sort throats by depth (Z) to implement basic painter's algorithm (back to front)
                throatsWithDepth = throatsWithDepth.OrderBy(t => t.depth).ToList();

                // Draw sorted throats
                foreach (var (_, p1, p2, thickness, color) in throatsWithDepth)
                {
                    using (Pen pen = new Pen(color, thickness))
                    {
                        g.DrawLine(pen, p1, p2);
                    }
                }

                // Project and render pores
                var poresWithDepth = new List<(double depth, int x, int y, int radius, Color color, Pore pore)>();

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

                    // Assign color based on connection count
                    Color poreColor;
                    int connCount = pore.ConnectionCount;

                    if (connCount <= 1)
                        poreColor = Color.Red;
                    else if (connCount == 2)
                        poreColor = Color.Yellow;
                    else if (connCount <= 4)
                        poreColor = Color.Green;
                    else
                        poreColor = Color.Blue;

                    // Adjust color intensity based on Z position (depth)
                    float intensity = (float)Math.Max(0.5f, Math.Min(1.0f, (transformed.z + 500) / 1000));
                    poreColor = AdjustColorIntensity(poreColor, intensity);

                    poresWithDepth.Add((transformed.z, x, y, radius, poreColor, pore));
                }

                // Sort pores by depth (Z) to implement basic painter's algorithm
                poresWithDepth = poresWithDepth.OrderBy(p => p.depth).ToList();

                // Draw pores from back to front
                foreach (var (_, x, y, radius, color, pore) in poresWithDepth)
                {
                    g.FillEllipse(new SolidBrush(color), x - radius, y - radius, radius * 2, radius * 2);
                    g.DrawEllipse(Pens.White, x - radius, y - radius, radius * 2, radius * 2);

                    // Add ID labels for larger pores (optional)
                    if (radius > 15) // Increase minimum size threshold
                    {
                        using (Font font = new Font("Arial", 8))
                        using (SolidBrush textBrush = new SolidBrush(Color.White))
                        using (StringFormat format = new StringFormat()
                        {
                            Alignment = StringAlignment.Center,
                            LineAlignment = StringAlignment.Center
                        })
                        {
                            // Draw text with a small shadow for better visibility
                            g.DrawString(pore.Id.ToString(), font, Brushes.Black,
                                new RectangleF(x - radius + 1, y - radius + 1, radius * 2, radius * 2), format);
                            g.DrawString(pore.Id.ToString(), font, textBrush,
                                new RectangleF(x - radius, y - radius, radius * 2, radius * 2), format);
                        }
                    }
                }

                // Draw coordinate axes for orientation
                DrawCoordinateAxes(g, width, height, scaleFactor * 0.2, rotationMatrix);

                // Add legend and statistics
                DrawLegendAndStats(g, width, height);
            }

            networkPictureBox.Image = networkImage;
        }

        // Helper method to draw coordinate axes
        private void DrawCoordinateAxes(Graphics g, int width, int height, double scale, double[,] rotationMatrix)
        {
            // Move origin point to bottom right but adjust to prevent overlap with legend
            Point origin = new Point(width - 120, height - 120); // Moved further from edges

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

        // Helper function to draw legend and statistics
        private void DrawLegendAndStats(Graphics g, int width, int height)
        {
            // Add statistics
            int maxConnections = networkModel.Pores.Count > 0 ? networkModel.Pores.Max(p => p.ConnectionCount) : 0;
            float avgConnections = (float)(networkModel.Pores.Count > 0 ? networkModel.Pores.Average(p => p.ConnectionCount) : 0);
            double avgRadius = networkModel.Pores.Count > 0 ? networkModel.Pores.Average(p => p.Radius) : 0;

            string[] stats = {
        $"Pores: {networkModel.Pores.Count}",
        $"Throats: {networkModel.Throats.Count}",
        $"Porosity: {networkModel.Porosity:P2}",
        $"Tortuosity: {networkModel.Tortuosity:F2}",  // Added tortuosity display
        $"Avg. Radius: {avgRadius:F2} µm",
        $"Connectivity: {avgConnections:F1} (max: {maxConnections})"
    };

            Font font = new Font("Arial", 10, FontStyle.Bold);
            int yPos = height - 160; // Adjusted to accommodate the additional line

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
        "Yellow: 2 connections",
        "Green: 3-4 connections",
        "Blue: 5+ connections"
    };

            yPos = 15;
            foreach (string text in legends)
            {
                g.DrawString(text, new Font("Arial", 8), Brushes.White, width - 150, yPos);
                yPos += 15;
            }
        }


        // Helper method to create 3D rotation matrix
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

            // Create rotation matrix (combined X, Y, Z rotations)
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

        // Helper method to transform a 3D point using rotation matrix
        private (double x, double y, double z) Transform3DPoint(double x, double y, double z, double[,] rotationMatrix)
        {
            double newX = x * rotationMatrix[0, 0] + y * rotationMatrix[0, 1] + z * rotationMatrix[0, 2];
            double newY = x * rotationMatrix[1, 0] + y * rotationMatrix[1, 1] + z * rotationMatrix[1, 2];
            double newZ = x * rotationMatrix[2, 0] + y * rotationMatrix[2, 1] + z * rotationMatrix[2, 2];

            return (newX, newY, newZ);
        }

        // Helper method to adjust color intensity based on depth
        private Color AdjustColorIntensity(Color color, float intensity)
        {
            return Color.FromArgb(
                (int)(color.R * intensity),
                (int)(color.G * intensity),
                (int)(color.B * intensity)
            );
        }

        private void ExportData()
        {
            if (networkModel.Pores == null || networkModel.Pores.Count == 0)
            {
                MessageBox.Show("No pore data to export", "No Data",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (SaveFileDialog saveDialog = new SaveFileDialog())
            {
                saveDialog.Filter = "CSV files (*.csv)|*.csv|Excel files (*.xlsx)|*.xlsx|Excel 97-2003 files (*.xls)|*.xls";
                saveDialog.Title = "Export Pore Network Data";
                saveDialog.DefaultExt = "csv";

                if (saveDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        string extension = Path.GetExtension(saveDialog.FileName).ToLower();

                        if (extension == ".csv")
                        {
                            ExportToCsv(saveDialog.FileName);
                        }
                        else if (extension == ".xlsx" || extension == ".xls")
                        {
                            ExportToExcel(saveDialog.FileName);
                        }

                        statusLabel.Text = "Data exported successfully";
                        MessageBox.Show("Data exported successfully", "Export Complete",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error exporting data: {ex.Message}",
                            "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Logger.Log($"[PoreNetworkModelingForm] Export error: {ex.Message}\n{ex.StackTrace}");
                    }
                }
            }
        }
        /// <summary>
        /// Export pore network data to Excel format using COM interop
        /// </summary>
        /// <param name="filename">The filename to export to</param>
        private void ExportToExcel(string filename)
        {
            // Check if we have data to export
            if (networkModel.Pores.Count == 0)
            {
                throw new InvalidOperationException("No pore data to export");
            }

            // Create Excel application instance
            Type excelType = Type.GetTypeFromProgID("Excel.Application");
            if (excelType == null)
            {
                MessageBox.Show("Microsoft Excel is not installed on this system.\nExporting to CSV format instead.",
                    "Excel Not Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                // Fall back to CSV if Excel is not available
                ExportToCsv(Path.ChangeExtension(filename, ".csv"));
                return;
            }

            // Use dynamic to simplify COM interop
            dynamic excel = null;
            dynamic workbook = null;
            dynamic worksheet = null;

            try
            {
                // Start with a progress dialog
                using (var progressDialog = new Form())
                {
                    progressDialog.Text = "Exporting to Excel";
                    progressDialog.Width = 300;
                    progressDialog.Height = 100;
                    progressDialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                    progressDialog.StartPosition = FormStartPosition.CenterParent;
                    progressDialog.ControlBox = false;

                    var progressLabel = new Label
                    {
                        Text = "Creating Excel workbook...",
                        Location = new System.Drawing.Point(10, 15),
                        Width = 280,
                        TextAlign = System.Drawing.ContentAlignment.MiddleCenter
                    };

                    var progressBar = new ProgressBar
                    {
                        Location = new System.Drawing.Point(10, 40),
                        Width = 280,
                        Height = 20,
                        Style = ProgressBarStyle.Marquee
                    };

                    progressDialog.Controls.Add(progressLabel);
                    progressDialog.Controls.Add(progressBar);

                    // Show progress dialog in a non-blocking way
                    progressDialog.Show(this);
                    Application.DoEvents(); // Process UI message loop

                    // Create Excel application
                    excel = Activator.CreateInstance(excelType);
                    excel.Visible = false;
                    excel.DisplayAlerts = false;

                    // Create a new workbook
                    workbook = excel.Workbooks.Add();

                    // Ensure we have at least 3 worksheets
                    while (workbook.Worksheets.Count < 3)
                    {
                        workbook.Worksheets.Add();
                    }

                    // ==========================================================
                    // Worksheet 1: Pores
                    // ==========================================================
                    progressLabel.Text = "Exporting pore data...";
                    Application.DoEvents();

                    worksheet = workbook.Worksheets[1];
                    worksheet.Name = "Pores";

                    // Add headers (bold)
                    worksheet.Cells[1, 1] = "ID";
                    worksheet.Cells[1, 2] = "Volume (µm³)";
                    worksheet.Cells[1, 3] = "Surface Area (µm²)";
                    worksheet.Cells[1, 4] = "Equivalent Radius (µm)";
                    worksheet.Cells[1, 5] = "X (µm)";
                    worksheet.Cells[1, 6] = "Y (µm)";
                    worksheet.Cells[1, 7] = "Z (µm)";
                    worksheet.Cells[1, 8] = "Connections";

                    // Format headers
                    dynamic headerRange = worksheet.Range("A1:H1");
                    headerRange.Font.Bold = true;
                    headerRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightGray);

                    // Add pore data
                    int row = 2;
                    foreach (var pore in networkModel.Pores)
                    {
                        worksheet.Cells[row, 1] = pore.Id;
                        worksheet.Cells[row, 2] = Math.Round(pore.Volume, 2);
                        worksheet.Cells[row, 3] = Math.Round(pore.Area, 2);
                        worksheet.Cells[row, 4] = Math.Round(pore.Radius, 2);
                        worksheet.Cells[row, 5] = Math.Round(pore.Center.X, 2);
                        worksheet.Cells[row, 6] = Math.Round(pore.Center.Y, 2);
                        worksheet.Cells[row, 7] = Math.Round(pore.Center.Z, 2);
                        worksheet.Cells[row, 8] = pore.ConnectionCount;
                        row++;
                    }

                    // Auto-fit columns
                    worksheet.Columns.AutoFit();

                    // Add filter
                    headerRange.AutoFilter();

                    // ==========================================================
                    // Worksheet 2: Throats
                    // ==========================================================
                    progressLabel.Text = "Exporting throat data...";
                    Application.DoEvents();

                    worksheet = workbook.Worksheets[2];
                    worksheet.Name = "Throats";

                    // Add headers
                    worksheet.Cells[1, 1] = "ID";
                    worksheet.Cells[1, 2] = "Pore 1 ID";
                    worksheet.Cells[1, 3] = "Pore 2 ID";
                    worksheet.Cells[1, 4] = "Radius (µm)";
                    worksheet.Cells[1, 5] = "Length (µm)";
                    worksheet.Cells[1, 6] = "Volume (µm³)";

                    // Format headers
                    headerRange = worksheet.Range("A1:F1");
                    headerRange.Font.Bold = true;
                    headerRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightGray);

                    // Add throat data
                    row = 2;
                    foreach (var throat in networkModel.Throats)
                    {
                        worksheet.Cells[row, 1] = throat.Id;
                        worksheet.Cells[row, 2] = throat.PoreId1;
                        worksheet.Cells[row, 3] = throat.PoreId2;
                        worksheet.Cells[row, 4] = Math.Round(throat.Radius, 2);
                        worksheet.Cells[row, 5] = Math.Round(throat.Length, 2);
                        worksheet.Cells[row, 6] = Math.Round(throat.Volume, 2);
                        row++;
                    }

                    // Auto-fit columns
                    worksheet.Columns.AutoFit();

                    // Add filter
                    headerRange.AutoFilter();

                    // ==========================================================
                    // Worksheet 3: Network Statistics
                    // ==========================================================
                    progressLabel.Text = "Creating summary statistics...";
                    Application.DoEvents();

                    worksheet = workbook.Worksheets[3];
                    worksheet.Name = "Network Statistics";

                    // Add headers and data for statistics
                    worksheet.Cells[1, 1] = "Property";
                    worksheet.Cells[1, 2] = "Value";

                    // Format headers
                    headerRange = worksheet.Range("A1:B1");
                    headerRange.Font.Bold = true;
                    headerRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightGray);

                    // Add network statistics
                    row = 2;
                    AddStatistic(worksheet, ref row, "Number of Pores", networkModel.Pores.Count);
                    AddStatistic(worksheet, ref row, "Number of Throats", networkModel.Throats.Count);
                    AddStatistic(worksheet, ref row, "Total Pore Volume (µm³)", Math.Round(networkModel.TotalPoreVolume, 2));
                    AddStatistic(worksheet, ref row, "Total Throat Volume (µm³)", Math.Round(networkModel.TotalThroatVolume, 2));
                    AddStatistic(worksheet, ref row, "Porosity", networkModel.Porosity.ToString("P2"));

                    // Add tortuosity to statistics
                    AddStatistic(worksheet, ref row, "Tortuosity", Math.Round(networkModel.Tortuosity, 4));

                    AddStatistic(worksheet, ref row, "Average Coordination Number",
                        Math.Round(networkModel.Pores.Count > 0 ?
                        networkModel.Pores.Average(p => p.ConnectionCount) : 0, 2));
                    AddStatistic(worksheet, ref row, "Pixel Size (m)", networkModel.PixelSize.ToString("E12"));
                    AddStatistic(worksheet, ref row, "Export Date", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                    // Add a summary chart (Pore size distribution)
                    if (networkModel.Pores.Count > 0)
                    {
                        progressLabel.Text = "Creating charts...";
                        Application.DoEvents();

                        try
                        {
                            // Add pore size distribution data
                            worksheet.Cells[row + 1, 1] = "Pore Size Distribution";
                            worksheet.Cells[row + 1, 1].Font.Bold = true;
                            row += 2;

                            // Create bin headers
                            worksheet.Cells[row, 1] = "Radius Range (µm)";
                            worksheet.Cells[row, 2] = "Count";
                            worksheet.Cells[row, 3] = "Volume Fraction";
                            row++;

                            // Calculate bins
                            double minRadius = networkModel.Pores.Min(p => p.Radius);
                            double maxRadius = networkModel.Pores.Max(p => p.Radius);
                            int numBins = 10;
                            double binWidth = (maxRadius - minRadius) / numBins;

                            // Ensure bin width is not zero
                            if (binWidth < 0.001)
                                binWidth = 0.1;

                            // Create bins
                            for (int i = 0; i < numBins; i++)
                            {
                                double lowerBound = minRadius + i * binWidth;
                                double upperBound = minRadius + (i + 1) * binWidth;

                                string binLabel = $"{lowerBound:F2} - {upperBound:F2}";

                                // Count pores in this bin
                                int count = networkModel.Pores.Count(p =>
                                    p.Radius >= lowerBound && p.Radius < upperBound);

                                // Calculate volume fraction
                                double volumeInBin = networkModel.Pores
                                    .Where(p => p.Radius >= lowerBound && p.Radius < upperBound)
                                    .Sum(p => p.Volume);

                                double volumeFraction = networkModel.TotalPoreVolume > 0 ?
                                    volumeInBin / networkModel.TotalPoreVolume : 0;

                                worksheet.Cells[row, 1] = binLabel;
                                worksheet.Cells[row, 2] = count;
                                worksheet.Cells[row, 3] = volumeFraction;
                                worksheet.Cells[row, 3].NumberFormat = "0.00%";

                                row++;
                            }

                            // Create chart
                            dynamic chartSheet = workbook.Charts.Add();
                            chartSheet.Name = "Pore Size Distribution";

                            // Chart data range
                            dynamic chartRange = worksheet.Range($"A{row - numBins}:C{row - 1}");

                            // Create column chart
                            dynamic chart = chartSheet.ChartObjects.Add(50, 50, 600, 400).Chart;
                            chart.ChartType = 51; // xlColumnClustered
                            chart.SetSourceData(chartRange);
                            chart.HasTitle = true;
                            chart.ChartTitle.Text = "Pore Size Distribution";

                            // Set axis titles
                            chart.Axes(1).HasTitle = true; // x-axis
                            chart.Axes(1).AxisTitle.Text = "Pore Radius Range (µm)";
                            chart.Axes(2).HasTitle = true; // primary y-axis
                            chart.Axes(2).AxisTitle.Text = "Count";

                            chart.SeriesCollection(2).AxisGroup = 2; // xlSecondary (2 = xlSecondary)
                            chart.SeriesCollection(2).ChartType = 65; // xlLineMarkers

                            // Properly set up secondary axis
                            // Excel constants: 1=xlCategory, 2=xlValue, 1=xlPrimary, 2=xlSecondary
                            try
                            {
                                // Add secondary value axis
                                chart.SetElement(142); // 142 = msoElementSecondaryValueAxisShow

                                // Configure the secondary axis
                                dynamic secondaryAxis = chart.Axes(2, 2); // 2=xlValue, 2=xlSecondary
                                secondaryAxis.HasTitle = true;
                                secondaryAxis.AxisTitle.Text = "Volume Fraction";
                                secondaryAxis.Format.Line.ForeColor.RGB = System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.Red);

                                // Format the line series to match secondary axis color
                                chart.SeriesCollection(2).Format.Line.ForeColor.RGB = System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.Red);
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"[PoreNetworkModelingForm] Error setting up secondary axis: {ex.Message}");
                                // Try alternative approach for older Excel versions
                                try
                                {
                                    dynamic axes = chart.Axes;
                                    axes.Add(2, 2); // 2=xlValue, 2=xlSecondary
                                    chart.Axes(2, 2).HasTitle = true;
                                    chart.Axes(2, 2).AxisTitle.Text = "Volume Fraction";
                                }
                                catch
                                {
                                    // If both methods fail, continue without secondary axis
                                    Logger.Log("[PoreNetworkModelingForm] Could not create secondary axis, continuing without it");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // If chart creation fails, log error and continue
                            Logger.Log($"[PoreNetworkModelingForm] Error creating chart: {ex.Message}");
                            // We don't want to stop the export if just the chart fails
                        }
                    }

                    // Auto-fit columns in statistics sheet
                    worksheet.Columns.AutoFit();

                    // Make Pores sheet active
                    workbook.Worksheets[1].Activate();

                    // Save workbook to specified file
                    progressLabel.Text = "Saving Excel file...";
                    Application.DoEvents();

                    // Save based on extension (.xlsx or .xls)
                    if (Path.GetExtension(filename).ToLower() == ".xlsx")
                    {
                        workbook.SaveAs(filename, 51); // xlOpenXMLWorkbook (without macro's in 2007-2016, xlsx)
                    }
                    else
                    {
                        workbook.SaveAs(filename, 56); // xlExcel8 (97-2003 format, xls)
                    }

                    // Close progress dialog
                    progressDialog.Close();
                }

                // Log success
                Logger.Log($"[PoreNetworkModelingForm] Successfully exported to Excel: {filename}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[PoreNetworkModelingForm] Excel export error: {ex.Message}\n{ex.StackTrace}");
                throw new Exception($"Excel export failed: {ex.Message}", ex);
            }
            finally
            {
                // Clean up COM objects to prevent memory leaks
                if (worksheet != null)
                {
                    Marshal.ReleaseComObject(worksheet);
                    worksheet = null;
                }

                if (workbook != null)
                {
                    workbook.Close(false);
                    Marshal.ReleaseComObject(workbook);
                    workbook = null;
                }

                if (excel != null)
                {
                    excel.Quit();
                    Marshal.ReleaseComObject(excel);
                    excel = null;
                }

                // Force garbage collection to release COM objects
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        /// <summary>
        /// Helper method to add a statistic row to the Excel worksheet
        /// </summary>
        private void AddStatistic(dynamic worksheet, ref int row, string property, object value)
        {
            worksheet.Cells[row, 1] = property;
            worksheet.Cells[row, 2] = value;
            row++;
        }
        private void EnsureDataGridViewHeadersVisible()
        {
            // Force the DataGridView to properly render its headers
            if (poreDataGridView != null && !poreDataGridView.IsDisposed)
            {
                poreDataGridView.ColumnHeadersVisible = true;
                poreDataGridView.ColumnHeadersHeight = 30;
                poreDataGridView.EnableHeadersVisualStyles = false;

                // Apply explicit styling to headers
                DataGridViewCellStyle headerStyle = new DataGridViewCellStyle
                {
                    BackColor = Color.FromArgb(220, 220, 220),
                    ForeColor = Color.Black,
                    Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                    Alignment = DataGridViewContentAlignment.MiddleCenter,
                    Padding = new Padding(0, 5, 0, 5)
                };

                poreDataGridView.ColumnHeadersDefaultCellStyle = headerStyle;

                // Ensure each column has its header text set with style
                foreach (DataGridViewColumn col in poreDataGridView.Columns)
                {
                    if (string.IsNullOrEmpty(col.HeaderText))
                    {
                        col.HeaderText = col.Name;
                    }
                    col.HeaderCell.Style = headerStyle;
                }

                // Force a refresh via invalidation
                poreDataGridView.Invalidate(true);

                // Also refresh the grid container if available
                if (poreDataGridView.Parent != null)
                {
                    poreDataGridView.Parent.Invalidate(true);
                }
            }
        }
        private void ExportToCsv(string filename)
        {
            using (StreamWriter writer = new StreamWriter(filename))
            {
                // Write pores header
                writer.WriteLine("# Pores");
                writer.WriteLine("ID,Volume (µm³),Surface Area (µm²),Equivalent Radius (µm),X (µm),Y (µm),Z (µm),Connections");

                // Write pores data
                foreach (var pore in networkModel.Pores)
                {
                    writer.WriteLine($"{pore.Id},{pore.Volume:F2},{pore.Area:F2},{pore.Radius:F2}," +
                                     $"{pore.Center.X:F2},{pore.Center.Y:F2},{pore.Center.Z:F2},{pore.ConnectionCount}");
                }

                // Write throats header
                writer.WriteLine();
                writer.WriteLine("# Throats");
                writer.WriteLine("ID,Pore1 ID,Pore2 ID,Radius (µm),Length (µm),Volume (µm³)");

                // Write throats data
                foreach (var throat in networkModel.Throats)
                {
                    writer.WriteLine($"{throat.Id},{throat.PoreId1},{throat.PoreId2},{throat.Radius:F2}," +
                                     $"{throat.Length:F2},{throat.Volume:F2}");
                }

                // Write network statistics
                writer.WriteLine();
                writer.WriteLine("# Network Statistics");
                writer.WriteLine($"Total Pore Volume (µm³),{networkModel.TotalPoreVolume:F2}");
                writer.WriteLine($"Total Throat Volume (µm³),{networkModel.TotalThroatVolume:F2}");
                writer.WriteLine($"Porosity,{networkModel.Porosity:F4}");
                writer.WriteLine($"Tortuosity,{networkModel.Tortuosity:F4}");  // Added tortuosity to export
                writer.WriteLine($"Pixel Size (m),{networkModel.PixelSize:E12}");
            }
        }
        private void ExportSelectedRows()
        {
            if (poreDataGridView.SelectedRows.Count == 0)
            {
                MessageBox.Show("Please select at least one row to export",
                    "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (SaveFileDialog saveDialog = new SaveFileDialog())
            {
                saveDialog.Filter = "CSV files (*.csv)|*.csv";
                saveDialog.Title = "Export Selected Pores";

                if (saveDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        using (StreamWriter writer = new StreamWriter(saveDialog.FileName))
                        {
                            // Write header
                            writer.WriteLine("ID,Volume (µm³),Surface Area (µm²),Equivalent Radius (µm),X (µm),Y (µm),Z (µm),Connections");

                            // Write selected rows
                            foreach (DataGridViewRow row in poreDataGridView.SelectedRows)
                            {
                                string line = string.Join(",", row.Cells.Cast<DataGridViewCell>()
                                    .Select(cell => cell.Value?.ToString() ?? ""));
                                writer.WriteLine(line);
                            }
                        }

                        MessageBox.Show("Selected rows exported successfully",
                            "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error exporting selection: {ex.Message}",
                            "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }
        private void SaveNetwork(string filename = null)
        {
            if (networkModel.Pores == null || networkModel.Pores.Count == 0)
            {
                MessageBox.Show("No pore network to save", "No Data",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrEmpty(filename))
            {
                using (SaveFileDialog saveDialog = new SaveFileDialog())
                {
                    saveDialog.Filter = "DAT files (*.dat)|*.dat";
                    saveDialog.Title = "Save Pore Network";
                    saveDialog.DefaultExt = "dat";

                    if (saveDialog.ShowDialog() != DialogResult.OK)
                        return;

                    filename = saveDialog.FileName;
                }
            }

            try
            {
                using (FileStream fs = new FileStream(filename, FileMode.Create))
                using (BinaryWriter writer = new BinaryWriter(fs))
                {
                    // Write file header
                    writer.Write("PORENETWORK"); // Magic string
                    writer.Write(1); // Version number

                    // Write metadata
                    writer.Write(networkModel.Pores.Count);
                    writer.Write(networkModel.Throats.Count);
                    writer.Write(networkModel.PixelSize);
                    writer.Write(networkModel.Porosity);
                    writer.Write(networkModel.Tortuosity);  // Added tortuosity to save data

                    // Write pores
                    foreach (var pore in networkModel.Pores)
                    {
                        writer.Write(pore.Id);
                        writer.Write(pore.Volume);
                        writer.Write(pore.Area);
                        writer.Write(pore.Radius);
                        writer.Write(pore.Center.X);
                        writer.Write(pore.Center.Y);
                        writer.Write(pore.Center.Z);
                        writer.Write(pore.ConnectionCount);
                    }

                    // Write throats
                    foreach (var throat in networkModel.Throats)
                    {
                        writer.Write(throat.Id);
                        writer.Write(throat.PoreId1);
                        writer.Write(throat.PoreId2);
                        writer.Write(throat.Radius);
                        writer.Write(throat.Length);
                        writer.Write(throat.Volume);
                    }
                }

                statusLabel.Text = "Network saved successfully";
                MessageBox.Show("Network saved successfully", "Save Complete",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving network: {ex.Message}",
                    "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void LoadNetwork(string filename = null)
        {
            if (string.IsNullOrEmpty(filename))
            {
                using (OpenFileDialog openDialog = new OpenFileDialog())
                {
                    openDialog.Filter = "DAT files (*.dat)|*.dat|All files (*.*)|*.*";
                    openDialog.Title = "Load Pore Network";

                    if (openDialog.ShowDialog() != DialogResult.OK)
                        return;

                    filename = openDialog.FileName;
                }
            }

            try
            {
                // First, analyze the file to determine if we need special handling
                byte[] headerBytes = new byte[20];
                using (FileStream fsRead = new FileStream(filename, FileMode.Open))
                {
                    fsRead.Read(headerBytes, 0, Math.Min(headerBytes.Length, (int)fsRead.Length));
                }

                // Log the hex values to help diagnose byte-level issues
                string hexDump = BitConverter.ToString(headerBytes);
                Logger.Log($"[PoreNetworkModelingForm] File header (hex): {hexDump}");

                // Also log the characters to see what's actually in the file
                string charDump = string.Join("", headerBytes.Select(b => b >= 32 && b < 127 ? (char)b : '.'));
                Logger.Log($"[PoreNetworkModelingForm] File header (chars): {charDump}");

                // Look for known patterns in the hex dump
                bool hasControlCharPrefix = headerBytes.Length > 0 && (headerBytes[0] == 0x0B || headerBytes[0] < 32);
                int headerOffset = hasControlCharPrefix ? 1 : 0;

                if (hasControlCharPrefix)
                {
                    Logger.Log($"[PoreNetworkModelingForm] Detected control character prefix (0x{headerBytes[0]:X2}), using offset {headerOffset}");
                }

                // Now open for actual parsing
                using (FileStream fs = new FileStream(filename, FileMode.Open))
                using (BinaryReader reader = new BinaryReader(fs))
                {
                    // Check if file has enough content
                    if (fs.Length < 15 + headerOffset)
                    {
                        throw new Exception("File is too small to be a valid pore network model");
                    }

                    try
                    {
                        // Skip the control character if needed
                        if (headerOffset > 0)
                        {
                            fs.Position = headerOffset;
                        }

                        // Read the magic string
                        char[] magicChars = reader.ReadChars(11);
                        string magic = new string(magicChars);
                        Logger.Log($"[PoreNetworkModelingForm] File header at offset {headerOffset}: '{magic}'");

                        // Check for expected header
                        if (magic != "PORENETWORK")
                        {
                            // Try loading as raw data
                            DialogResult result = MessageBox.Show(
                                "The file doesn't have the expected format. Would you like to attempt to load it as raw data?",
                                "Format Mismatch",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Warning);

                            if (result == DialogResult.No)
                                throw new Exception($"Invalid file format: '{magic}' (expected 'PORENETWORK')");

                            // Reset position to beginning for raw data loading
                            fs.Position = 0;
                            Logger.Log("[PoreNetworkModelingForm] Attempting to load as raw data");
                            LoadNetworkAsRawData(fs);
                            return;
                        }

                        // Continue with normal parsing
                        int version = reader.ReadInt32();
                        Logger.Log($"[PoreNetworkModelingForm] File version: {version}");

                        if (version != 1)
                            throw new Exception($"Unsupported version: {version}");

                        // Read metadata
                        int poreCount = reader.ReadInt32();
                        int throatCount = reader.ReadInt32();
                        double pixelSize = reader.ReadDouble();
                        double porosity = reader.ReadDouble();

                        // Attempt to read tortuosity if it exists in the file
                        double tortuosity = 1.0; // Default value
                        if (fs.Position < fs.Length - 8) // Check if we have at least 8 more bytes (for a double)
                        {
                            try
                            {
                                tortuosity = reader.ReadDouble();
                            }
                            catch
                            {
                                // If reading fails, use the default tortuosity
                                Logger.Log("[PoreNetworkModelingForm] Could not read tortuosity from file, using default value");
                            }
                        }

                        Logger.Log($"[PoreNetworkModelingForm] Metadata: {poreCount} pores, {throatCount} throats, pixelSize={pixelSize}, porosity={porosity}, tortuosity={tortuosity}");

                        if (poreCount <= 0 || poreCount > 1000000 || throatCount < 0 || throatCount > 10000000)
                        {
                            throw new Exception($"Suspicious data values: poreCount={poreCount}, throatCount={throatCount}");
                        }

                        // Create new network model
                        networkModel = new PoreNetworkModel
                        {
                            PixelSize = pixelSize,
                            Porosity = porosity,
                            Tortuosity = tortuosity,  // Set the tortuosity value
                            Pores = new List<Pore>(poreCount),
                            Throats = new List<Throat>(throatCount)
                        };

                        // Read pores
                        for (int i = 0; i < poreCount; i++)
                        {
                            try
                            {
                                Pore pore = new Pore
                                {
                                    Id = reader.ReadInt32(),
                                    Volume = reader.ReadDouble(),
                                    Area = reader.ReadDouble(),
                                    Radius = reader.ReadDouble(),
                                    Center = new Point3D
                                    {
                                        X = reader.ReadDouble(),
                                        Y = reader.ReadDouble(),
                                        Z = reader.ReadDouble()
                                    },
                                    ConnectionCount = reader.ReadInt32()
                                };
                                networkModel.Pores.Add(pore);
                            }
                            catch (EndOfStreamException)
                            {
                                Logger.Log($"[PoreNetworkModelingForm] Reached end of stream while reading pore {i + 1}/{poreCount}");
                                break;
                            }
                        }

                        // Read throats
                        double totalThroatVolume = 0;
                        for (int i = 0; i < throatCount; i++)
                        {
                            try
                            {
                                Throat throat = new Throat
                                {
                                    Id = reader.ReadInt32(),
                                    PoreId1 = reader.ReadInt32(),
                                    PoreId2 = reader.ReadInt32(),
                                    Radius = reader.ReadDouble(),
                                    Length = reader.ReadDouble(),
                                    Volume = reader.ReadDouble()
                                };
                                networkModel.Throats.Add(throat);
                                totalThroatVolume += throat.Volume;
                            }
                            catch (EndOfStreamException)
                            {
                                Logger.Log($"[PoreNetworkModelingForm] Reached end of stream while reading throat {i + 1}/{throatCount}");
                                break;
                            }
                        }

                        networkModel.TotalPoreVolume = networkModel.Pores.Sum(p => p.Volume);
                        networkModel.TotalThroatVolume = totalThroatVolume;
                    }
                    catch (EndOfStreamException ex)
                    {
                        throw new Exception($"Unexpected end of file while reading. File may be truncated. {ex.Message}");
                    }
                }

                // Update UI
                UpdatePoreTable();
                Render3DVisualization();

                // Enable export and save buttons
                exportButton.Enabled = true;
                saveButton.Enabled = true;

                // Update form title if in viewer mode
                if (viewerMode)
                {
                    this.Text = $"Pore Network Modeling - {Path.GetFileName(filename)} [Viewer Mode]";
                }

                statusLabel.Text = $"Loaded network with {networkModel.Pores.Count} pores and {networkModel.Throats.Count} throats";
                if (!viewerMode)
                {
                    MessageBox.Show("Network loaded successfully", "Load Complete",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                string errorMessage = $"Error loading network: {ex.Message}";
                Logger.Log($"[PoreNetworkModelingForm] {errorMessage}\n{ex.StackTrace}");

                MessageBox.Show(errorMessage, "Load Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

                if (viewerMode)
                {
                    // In viewer mode, this error is critical - create an empty network
                    networkModel = new PoreNetworkModel
                    {
                        Pores = new List<Pore>(),
                        Throats = new List<Throat>()
                    };

                    // Still update UI to show empty state
                    UpdatePoreTable();
                    Render3DVisualization();
                }

                throw; // Re-throw to allow caller to handle the error
            }
        }
        private async void SimulatePermeabilityClick(object sender, EventArgs e)
        {
            if (networkModel?.Pores == null || networkModel.Pores.Count == 0 ||
                networkModel.Throats == null || networkModel.Throats.Count == 0)
            {
                MessageBox.Show("Please generate a pore network first", "No Data",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                // Show dialog to get parameters
                using (var dialog = new PermeabilitySimulationDialog())
                {
                    // Apply the tortuosity value from the network model to the dialog
                    dialog.SetInitialTortuosity(networkModel.Tortuosity);

                    if (dialog.ShowDialog() != DialogResult.OK)
                        return;

                    // Show progress
                    progressBar.Value = 0;
                    statusLabel.Text = "Simulating permeability...";

                    // Run simulation with the new parameters
                    using (var simulator = new PermeabilitySimulator())
                    {
                        Progress<int> progress = new Progress<int>(percent =>
                        {
                            progressBar.Value = percent;
                        });

                        permeabilityResult = await simulator.SimulatePermeabilityAsync(
                            networkModel,
                            dialog.SelectedAxis,
                            dialog.Viscosity,
                            dialog.InputPressure,
                            dialog.OutputPressure,
                            dialog.Tortuosity,
                            dialog.UseDarcyMethod,
                            dialog.UseLatticeBoltzmannMethod,
                            dialog.UseNavierStokesMethod,
                            useGpuCheckBox.Checked,
                            progress);
                    }

                    // Update UI with results
                    RenderPermeabilityResults();

                    // Switch to permeability tab
                    if (mainTabControl != null && permeabilityTab != null)
                    {
                        mainTabControl.SelectedTab = permeabilityTab;
                    }

                    // Update status with comprehensive message
                    StringBuilder statusBuilder = new StringBuilder("Permeability: ");
                    if (permeabilityResult.UsedDarcyMethod)
                    {
                        statusBuilder.Append($"Darcy={permeabilityResult.PermeabilityDarcy:F3}D ");
                    }
                    if (permeabilityResult.UsedLatticeBoltzmannMethod)
                    {
                        statusBuilder.Append($"LBM={permeabilityResult.LatticeBoltzmannPermeabilityDarcy:F3}D ");
                    }
                    if (permeabilityResult.UsedNavierStokesMethod)
                    {
                        statusBuilder.Append($"NS={permeabilityResult.NavierStokesPermeabilityDarcy:F3}D ");
                    }
                    statusBuilder.Append($"| τ={permeabilityResult.Tortuosity:F2}");
                    statusLabel.Text = statusBuilder.ToString();

                    // Enable save and export buttons
                    if (savePermeabilityButton != null) savePermeabilityButton.Enabled = true;
                    if (exportPermeabilityButton != null) exportPermeabilityButton.Enabled = true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error simulating permeability: {ex.Message}",
                    "Simulation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                statusLabel.Text = "Error simulating permeability";
                Logger.Log($"[PoreNetworkModelingForm] Error: {ex.Message}\n{ex.StackTrace}");
            }
        }
        private void RenderPermeabilityResults()
        {
            if (permeabilityResult == null || permeabilityTab == null)
                return;

            // First, clear the tab
            permeabilityTab.Controls.Clear();

            // Create the parent form for multiple visualization methods
            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1
            };

            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 150)); // Results panel
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // Visualization panel

            // Create the results panel at the top for key permeability info
            Panel resultsPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Height = 150,
                BackColor = Color.FromArgb(30, 30, 30),
                Padding = new Padding(5)
            };

            // Create a table layout for the results with all calculation methods
            TableLayoutPanel tableLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 6,
                BackColor = Color.Transparent
            };

            // Column styles - evenly distribute space
            tableLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            tableLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            tableLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));

            // Row styles
            for (int i = 0; i < 6; i++)
            {
                tableLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 16.67F));
            }

            // Row 0: Common properties
            tableLayout.Controls.Add(new Label
            {
                Text = $"Flow Axis: {permeabilityResult.FlowAxis}",
                ForeColor = Color.White,
                AutoSize = true,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Anchor = AnchorStyles.Left
            }, 0, 0);

            tableLayout.Controls.Add(new Label
            {
                Text = $"Pressure Drop: {permeabilityResult.InputPressure - permeabilityResult.OutputPressure:F2} Pa",
                ForeColor = Color.White,
                AutoSize = true,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Anchor = AnchorStyles.Left
            }, 1, 0);

            tableLayout.Controls.Add(new Label
            {
                Text = $"Total Flow Rate: {permeabilityResult.TotalFlowRate:G4} m³/s",
                ForeColor = Color.White,
                AutoSize = true,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Anchor = AnchorStyles.Left
            }, 2, 0);

            // Row 1: More common properties
            tableLayout.Controls.Add(new Label
            {
                Text = $"Fluid Viscosity: {permeabilityResult.Viscosity:G4} Pa·s",
                ForeColor = Color.White,
                AutoSize = true,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Anchor = AnchorStyles.Left
            }, 0, 1);

            tableLayout.Controls.Add(new Label
            {
                Text = $"Sample: L={permeabilityResult.ModelLength * 1000:F2} mm, A={permeabilityResult.ModelArea * 1e6:F2} mm²",
                ForeColor = Color.White,
                AutoSize = true,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Anchor = AnchorStyles.Left
            }, 1, 1);

            tableLayout.Controls.Add(new Label
            {
                Text = $"Tortuosity: {permeabilityResult.Tortuosity:F2}",
                ForeColor = Color.Yellow, // Highlighted in yellow
                AutoSize = true,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Anchor = AnchorStyles.Left
            }, 2, 1);

            // Row 2: Column headers for methods
            tableLayout.Controls.Add(new Label
            {
                Text = "Calculation Method",
                ForeColor = Color.LightGray,
                AutoSize = true,
                Font = new Font("Segoe UI", 9, FontStyle.Underline),
                Anchor = AnchorStyles.Left
            }, 0, 2);

            tableLayout.Controls.Add(new Label
            {
                Text = "Raw Permeability",
                ForeColor = Color.LightGray,
                AutoSize = true,
                Font = new Font("Segoe UI", 9, FontStyle.Underline),
                Anchor = AnchorStyles.Left
            }, 1, 2);

            tableLayout.Controls.Add(new Label
            {
                Text = "Corrected Permeability",
                ForeColor = Color.LightGray,
                AutoSize = true,
                Font = new Font("Segoe UI", 9, FontStyle.Underline),
                Anchor = AnchorStyles.Left
            }, 2, 2);

            // Row 3: Darcy's Law results
            if (permeabilityResult.UsedDarcyMethod)
            {
                tableLayout.Controls.Add(new Label
                {
                    Text = "Darcy's Law",
                    ForeColor = Color.White,
                    AutoSize = true,
                    Font = new Font("Segoe UI", 9, FontStyle.Bold),
                    Anchor = AnchorStyles.Left
                }, 0, 3);

                tableLayout.Controls.Add(new Label
                {
                    Text = $"{permeabilityResult.PermeabilityDarcy:F3} D ({permeabilityResult.PermeabilityMilliDarcy:F1} mD)",
                    ForeColor = Color.White,
                    AutoSize = true,
                    Font = new Font("Segoe UI", 9),
                    Anchor = AnchorStyles.Left
                }, 1, 3);

                tableLayout.Controls.Add(new Label
                {
                    Text = $"{permeabilityResult.CorrectedPermeabilityDarcy:F3} D ({permeabilityResult.CorrectedPermeabilityDarcy * 1000:F1} mD)",
                    ForeColor = Color.LightGreen,
                    AutoSize = true,
                    Font = new Font("Segoe UI", 9, FontStyle.Bold),
                    Anchor = AnchorStyles.Left
                }, 2, 3);
            }

            // Row 4: Lattice Boltzmann results
            if (permeabilityResult.UsedLatticeBoltzmannMethod)
            {
                tableLayout.Controls.Add(new Label
                {
                    Text = "Lattice Boltzmann Method",
                    ForeColor = Color.White,
                    AutoSize = true,
                    Font = new Font("Segoe UI", 9, FontStyle.Bold),
                    Anchor = AnchorStyles.Left
                }, 0, 4);

                tableLayout.Controls.Add(new Label
                {
                    Text = $"{permeabilityResult.LatticeBoltzmannPermeabilityDarcy:F3} D ({permeabilityResult.LatticeBoltzmannPermeabilityMilliDarcy:F1} mD)",
                    ForeColor = Color.White,
                    AutoSize = true,
                    Font = new Font("Segoe UI", 9),
                    Anchor = AnchorStyles.Left
                }, 1, 4);

                tableLayout.Controls.Add(new Label
                {
                    Text = $"{permeabilityResult.CorrectedLatticeBoltzmannPermeabilityDarcy:F3} D ({permeabilityResult.CorrectedLatticeBoltzmannPermeabilityDarcy * 1000:F1} mD)",
                    ForeColor = Color.LightGreen,
                    AutoSize = true,
                    Font = new Font("Segoe UI", 9, FontStyle.Bold),
                    Anchor = AnchorStyles.Left
                }, 2, 4);
            }

            // Row 5: Navier-Stokes results
            if (permeabilityResult.UsedNavierStokesMethod)
            {
                tableLayout.Controls.Add(new Label
                {
                    Text = "Navier-Stokes Method",
                    ForeColor = Color.White,
                    AutoSize = true,
                    Font = new Font("Segoe UI", 9, FontStyle.Bold),
                    Anchor = AnchorStyles.Left
                }, 0, 5);

                tableLayout.Controls.Add(new Label
                {
                    Text = $"{permeabilityResult.NavierStokesPermeabilityDarcy:F3} D ({permeabilityResult.NavierStokesPermeabilityMilliDarcy:F1} mD)",
                    ForeColor = Color.White,
                    AutoSize = true,
                    Font = new Font("Segoe UI", 9),
                    Anchor = AnchorStyles.Left
                }, 1, 5);

                tableLayout.Controls.Add(new Label
                {
                    Text = $"{permeabilityResult.CorrectedNavierStokesPermeabilityDarcy:F3} D ({permeabilityResult.CorrectedNavierStokesPermeabilityDarcy * 1000:F1} mD)",
                    ForeColor = Color.LightGreen,
                    AutoSize = true,
                    Font = new Font("Segoe UI", 9, FontStyle.Bold),
                    Anchor = AnchorStyles.Left
                }, 2, 5);
            }

            resultsPanel.Controls.Add(tableLayout);
            mainLayout.Controls.Add(resultsPanel, 0, 0);

            // Create a TabControl for multiple visualization methods
            TabControl visualizationTabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Appearance = TabAppearance.FlatButtons,
                ItemSize = new Size(0, 30),
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };

            // Add tabs for each method
            int tabIndex = 0;

            // Darcy method tab
            if (permeabilityResult.UsedDarcyMethod)
            {
                TabPage darcyTab = CreateVisualizationTab("Darcy's Law", VisualizationMethod.Darcy);
                visualizationTabs.TabPages.Add(darcyTab);
                tabIndex++;
            }

            // Lattice Boltzmann method tab
            if (permeabilityResult.UsedLatticeBoltzmannMethod)
            {
                TabPage lbmTab = CreateVisualizationTab("Lattice Boltzmann", VisualizationMethod.LatticeBoltzmann);
                visualizationTabs.TabPages.Add(lbmTab);
                tabIndex++;
            }

            // Navier-Stokes method tab
            if (permeabilityResult.UsedNavierStokesMethod)
            {
                TabPage nsTab = CreateVisualizationTab("Navier-Stokes", VisualizationMethod.NavierStokes);
                visualizationTabs.TabPages.Add(nsTab);
            }

            // Add combined view if multiple methods are used
            if ((permeabilityResult.UsedDarcyMethod ? 1 : 0) +
                (permeabilityResult.UsedLatticeBoltzmannMethod ? 1 : 0) +
                (permeabilityResult.UsedNavierStokesMethod ? 1 : 0) > 1)
            {
                TabPage combinedTab = CreateVisualizationTab("Combined View", VisualizationMethod.Combined);
                visualizationTabs.TabPages.Add(combinedTab);
            }

            mainLayout.Controls.Add(visualizationTabs, 0, 1);
            permeabilityTab.Controls.Add(mainLayout);

            // Enable the export button
            if (exportPermeabilityButton != null) exportPermeabilityButton.Enabled = true;

            // Set the first tab as the active tab
            if (visualizationTabs.TabPages.Count > 0)
                visualizationTabs.SelectedIndex = 0;

            // Prepare a comprehensive status message with all calculation methods
            StringBuilder statusBuilder = new StringBuilder("Permeability: ");
            if (permeabilityResult.UsedDarcyMethod)
            {
                statusBuilder.Append($"Darcy={permeabilityResult.PermeabilityDarcy:F3}D ");
            }
            if (permeabilityResult.UsedLatticeBoltzmannMethod)
            {
                statusBuilder.Append($"LBM={permeabilityResult.LatticeBoltzmannPermeabilityDarcy:F3}D ");
            }
            if (permeabilityResult.UsedNavierStokesMethod)
            {
                statusBuilder.Append($"NS={permeabilityResult.NavierStokesPermeabilityDarcy:F3}D ");
            }
            statusBuilder.Append($"| τ={permeabilityResult.Tortuosity:F2}");
            statusLabel.Text = statusBuilder.ToString();
        }
        private Bitmap RenderPressureField(VisualizationMethod method = VisualizationMethod.Darcy)
        {
            if (permeabilityResult == null)
                return null;

            int width = 800;
            int height = 600;

            Bitmap pressureImage = new Bitmap(width, height);

            using (Graphics g = Graphics.FromImage(pressureImage))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Black);

                // Find model bounds and center point
                double minX = permeabilityResult.Model.Pores.Min(p => p.Center.X);
                double maxX = permeabilityResult.Model.Pores.Max(p => p.Center.X);
                double minY = permeabilityResult.Model.Pores.Min(p => p.Center.Y);
                double maxY = permeabilityResult.Model.Pores.Max(p => p.Center.Y);
                double minZ = permeabilityResult.Model.Pores.Min(p => p.Center.Z);
                double maxZ = permeabilityResult.Model.Pores.Max(p => p.Center.Z);

                double centerX = (minX + maxX) / 2;
                double centerY = (minY + maxY) / 2;
                double centerZ = (minZ + maxZ) / 2;

                // Calculate scale factor based on the model size
                double maxRange = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
                double scaleFactor = Math.Min(width, height) * 0.4 / maxRange * viewScale;

                // Create rotation matrices
                var rotationMatrix = Create3DRotationMatrix(rotationX, rotationY, rotationZ);

                // Get the pressure field for the selected method
                Dictionary<int, double> pressureField = GetPressureFieldForMethod(method);

                // Find pressure range - FIXED to handle empty collections
                double minPressure = 0;
                double maxPressure = 0;
                double pressureRange = 0;

                // Check if the pressure field has any values
                if (pressureField != null && pressureField.Count > 0)
                {
                    try
                    {
                        minPressure = pressureField.Values.Min();
                        maxPressure = pressureField.Values.Max();
                        pressureRange = maxPressure - minPressure;
                    }
                    catch (InvalidOperationException)
                    {
                        // Handle the case where Min/Max fails even with count check
                        minPressure = permeabilityResult.OutputPressure;
                        maxPressure = permeabilityResult.InputPressure;
                        pressureRange = maxPressure - minPressure;
                        Logger.Log($"[PoreNetworkModelingForm] Warning: Could not calculate pressure range, using simulation parameters instead");
                    }
                }
                else
                {
                    // Default values if pressure field is empty or null
                    minPressure = permeabilityResult.OutputPressure;
                    maxPressure = permeabilityResult.InputPressure;
                    pressureRange = maxPressure - minPressure;
                    Logger.Log($"[PoreNetworkModelingForm] Warning: Pressure field is empty, using simulation parameters instead");
                }

                // Project and render throats first (draw from back to front)
                var throatsWithDepth = new List<(double depth, Point p1, Point p2, float thickness, Color color)>();

                foreach (var throat in permeabilityResult.Model.Throats)
                {
                    var pore1 = permeabilityResult.Model.Pores.FirstOrDefault(p => p.Id == throat.PoreId1);
                    var pore2 = permeabilityResult.Model.Pores.FirstOrDefault(p => p.Id == throat.PoreId2);

                    if (pore1 != null && pore2 != null)
                    {
                        // Get pressure for both pores
                        double pressure1 = 0;
                        double pressure2 = 0;

                        if (pressureField != null)
                        {
                            pressureField.TryGetValue(pore1.Id, out pressure1);
                            pressureField.TryGetValue(pore2.Id, out pressure2);
                        }

                        // Average pressure for throat color
                        double avgPressure = (pressure1 + pressure2) / 2;
                        double normalizedPressure = pressureRange > 0 ? (avgPressure - minPressure) / pressureRange : 0.5;

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

                        // Average Z for depth sorting (to draw back-to-front)
                        double avgZ = (transformedP1.z + transformedP2.z) / 2;

                        // Create pressure-based color
                        Color throatColor = GetPressureColor(normalizedPressure, method);

                        throatsWithDepth.Add((avgZ, p1, p2, thickness, throatColor));
                    }
                }

                // Sort throats by depth (Z) to implement basic painter's algorithm
                throatsWithDepth = throatsWithDepth.OrderBy(t => t.depth).ToList();

                // Draw sorted throats
                foreach (var (_, p1, p2, thickness, color) in throatsWithDepth)
                {
                    using (Pen pen = new Pen(color, thickness))
                    {
                        g.DrawLine(pen, p1, p2);
                    }
                }

                // Project and render pores
                var poresWithDepth = new List<(double depth, int x, int y, int radius, Color color, Pore pore)>();

                foreach (var pore in permeabilityResult.Model.Pores)
                {
                    // Get pressure for pore
                    double pressure = 0;
                    if (pressureField != null)
                    {
                        pressureField.TryGetValue(pore.Id, out pressure);
                    }

                    double normalizedPressure = pressureRange > 0 ? (pressure - minPressure) / pressureRange : 0.5;

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

                    // Assign color based on pressure
                    Color poreColor = GetPressureColor(normalizedPressure, method);

                    // Highlight inlet and outlet pores
                    bool isInlet = permeabilityResult.InletPores.Contains(pore.Id);
                    bool isOutlet = permeabilityResult.OutletPores.Contains(pore.Id);

                    if (isInlet)
                        poreColor = Color.White;
                    else if (isOutlet)
                        poreColor = Color.DarkGray;

                    // Adjust color intensity based on Z position (depth)
                    float intensity = (float)Math.Max(0.5f, Math.Min(1.0f, (transformed.z + 500) / 1000));
                    poreColor = AdjustColorIntensity(poreColor, intensity);

                    poresWithDepth.Add((transformed.z, x, y, radius, poreColor, pore));
                }

                // Sort pores by depth (Z) to implement basic painter's algorithm
                poresWithDepth = poresWithDepth.OrderBy(p => p.depth).ToList();

                // Draw pores from back to front
                foreach (var (_, x, y, radius, color, pore) in poresWithDepth)
                {
                    g.FillEllipse(new SolidBrush(color), x - radius, y - radius, radius * 2, radius * 2);

                    // Highlight inlet and outlet pores with different border colors
                    Pen borderPen;
                    if (permeabilityResult.InletPores.Contains(pore.Id))
                        borderPen = new Pen(Color.Red, 2);
                    else if (permeabilityResult.OutletPores.Contains(pore.Id))
                        borderPen = new Pen(Color.Blue, 2);
                    else
                        borderPen = Pens.White;

                    g.DrawEllipse(borderPen, x - radius, y - radius, radius * 2, radius * 2);

                    if (borderPen != Pens.White)
                        borderPen.Dispose();
                }

                // Draw coordinate axes for orientation
                DrawCoordinateAxes(g, width, height, scaleFactor * 0.2, rotationMatrix);

                // Add legend title based on method
                string methodName = method.ToString();
                double permeabilityValue = GetPermeabilityValueForMethod(method);

                g.DrawString($"{methodName} Method",
                    new Font("Arial", 12, FontStyle.Bold), Brushes.White, 20, 20);

                g.DrawString($"Permeability: {permeabilityValue:F3} Darcy",
                    new Font("Arial", 12, FontStyle.Bold), Brushes.White, 20, 50);

                // Add inlet/outlet legend
                g.FillEllipse(new SolidBrush(Color.White), width - 150, 20, 12, 12);
                g.DrawEllipse(new Pen(Color.Red, 2), width - 150, 20, 12, 12);
                g.DrawString("Inlet Pores", new Font("Arial", 10), Brushes.White, width - 130, 20);

                g.FillEllipse(new SolidBrush(Color.DarkGray), width - 150, 40, 12, 12);
                g.DrawEllipse(new Pen(Color.Blue, 2), width - 150, 40, 12, 12);
                g.DrawString("Outlet Pores", new Font("Arial", 10), Brushes.White, width - 130, 40);
            }

            return pressureImage;
        }
        private Dictionary<int, double> GetPressureFieldForMethod(VisualizationMethod method)
        {
            switch (method)
            {
                case VisualizationMethod.Darcy:
                    return permeabilityResult.PressureField ?? new Dictionary<int, double>();

                case VisualizationMethod.LatticeBoltzmann:
                    return permeabilityResult.LatticeBoltzmannPressureField ?? new Dictionary<int, double>();

                case VisualizationMethod.NavierStokes:
                    return permeabilityResult.NavierStokesPressureField ?? new Dictionary<int, double>();

                default:
                    return permeabilityResult.PressureField ?? new Dictionary<int, double>();
            }
        }
        private double GetPermeabilityValueForMethod(VisualizationMethod method)
        {
            switch (method)
            {
                case VisualizationMethod.Darcy:
                    return permeabilityResult.CorrectedPermeabilityDarcy;

                case VisualizationMethod.LatticeBoltzmann:
                    return permeabilityResult.CorrectedLatticeBoltzmannPermeabilityDarcy;

                case VisualizationMethod.NavierStokes:
                    return permeabilityResult.CorrectedNavierStokesPermeabilityDarcy;

                default:
                    return permeabilityResult.CorrectedPermeabilityDarcy;
            }
        }
        private Color GetPressureColor(double normalizedPressure, VisualizationMethod method)
        {
            // Use slightly different color schemes for each method to distinguish them visually
            switch (method)
            {
                case VisualizationMethod.Darcy:
                    // Standard red-green-blue gradient
                    return GetStandardPressureGradient(normalizedPressure);

                case VisualizationMethod.LatticeBoltzmann:
                    // Cyan-to-magenta gradient for LBM
                    return GetLatticeBoltzmannGradient(normalizedPressure);

                case VisualizationMethod.NavierStokes:
                    // Yellow-to-purple gradient for NS
                    return GetNavierStokesGradient(normalizedPressure);

                case VisualizationMethod.Combined:
                    // Use standard gradient for combined view
                    return GetStandardPressureGradient(normalizedPressure);

                default:
                    return GetStandardPressureGradient(normalizedPressure);
            }
        }
        private Color GetStandardPressureGradient(double normalizedPressure)
        {
            // Red (high pressure) to Blue (low pressure) gradient
            normalizedPressure = Math.Max(0, Math.Min(1, normalizedPressure));

            if (normalizedPressure < 0.5)
            {
                // Blue to green (0 to 0.5)
                double t = normalizedPressure * 2;
                int r = 0;
                int g = Math.Max(0, Math.Min(255, (int)(255 * t)));
                int b = Math.Max(0, Math.Min(255, (int)(255 * (1 - t))));
                return Color.FromArgb(r, g, b);
            }
            else
            {
                // Green to red (0.5 to 1)
                double t = (normalizedPressure - 0.5) * 2;
                int r = Math.Max(0, Math.Min(255, (int)(255 * t)));
                int g = Math.Max(0, Math.Min(255, (int)(255 * (1 - t))));
                int b = 0;
                return Color.FromArgb(r, g, b);
            }
        }
        private Color GetLatticeBoltzmannGradient(double normalizedPressure)
        {
            // Special enhanced gradient for Lattice Boltzmann
            normalizedPressure = Math.Max(0, Math.Min(1, normalizedPressure));

            // Use a more visible cyan-magenta-yellow gradient
            if (normalizedPressure < 0.33)
            {
                // Cyan to Blue (low pressure)
                double t = normalizedPressure * 3;
                int r = 0;
                int g = Math.Max(0, Math.Min(255, (int)(150 + 105 * (1 - t)))); // Clamp to 0-255
                int b = 255;
                return Color.FromArgb(r, g, b);
            }
            else if (normalizedPressure < 0.67)
            {
                // Blue to Red (medium pressure)
                double t = (normalizedPressure - 0.33) * 3;
                int r = Math.Max(0, Math.Min(255, (int)(255 * t))); // Clamp to 0-255
                int g = 0;
                int b = Math.Max(0, Math.Min(255, (int)(255 * (1 - t)))); // Clamp to 0-255
                return Color.FromArgb(r, g, b);
            }
            else
            {
                // Red to Yellow (high pressure) 
                double t = (normalizedPressure - 0.67) * 3;
                int r = 255;
                int g = Math.Max(0, Math.Min(255, (int)(255 * t))); // Clamp to 0-255
                int b = 0;
                return Color.FromArgb(r, g, b);
            }
        }
        private Color GetNavierStokesGradient(double normalizedPressure)
        {
            // Yellow (high pressure) to Purple (low pressure) gradient
            normalizedPressure = Math.Max(0, Math.Min(1, normalizedPressure));

            if (normalizedPressure < 0.5)
            {
                // Purple to white (0 to 0.5)
                double t = normalizedPressure * 2;
                int r = Math.Max(0, Math.Min(255, (int)(128 + 127 * t)));
                int g = Math.Max(0, Math.Min(255, (int)(0 + 255 * t)));
                int b = Math.Max(0, Math.Min(255, (int)(128 + 127 * t)));
                return Color.FromArgb(r, g, b);
            }
            else
            {
                // White to yellow (0.5 to 1)
                double t = (normalizedPressure - 0.5) * 2;
                int r = 255;
                int g = 255;
                int b = Math.Max(0, Math.Min(255, (int)(255 * (1 - t))));
                return Color.FromArgb(r, g, b);
            }
        }
        private Bitmap RenderCombinedView()
        {
            if (permeabilityResult == null)
                return null;

            int width = 800;
            int height = 600;

            Bitmap combinedImage = new Bitmap(width, height);

            using (Graphics g = Graphics.FromImage(combinedImage))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Black);

                // Draw title
                g.DrawString("Combined View - Multiple Calculation Methods",
                    new Font("Arial", 14, FontStyle.Bold), Brushes.White, 20, 20);

                // Count how many methods are used
                int methodCount = 0;
                if (permeabilityResult.UsedDarcyMethod) methodCount++;
                if (permeabilityResult.UsedLatticeBoltzmannMethod) methodCount++;
                if (permeabilityResult.UsedNavierStokesMethod) methodCount++;

                if (methodCount <= 1)
                {
                    // If only one method, just return that method's visualization
                    VisualizationMethod method = VisualizationMethod.Darcy;
                    if (permeabilityResult.UsedLatticeBoltzmannMethod) method = VisualizationMethod.LatticeBoltzmann;
                    if (permeabilityResult.UsedNavierStokesMethod) method = VisualizationMethod.NavierStokes;

                    return RenderPressureField(method);
                }

                // Collect the methods to display
                List<VisualizationMethod> methods = new List<VisualizationMethod>();
                if (permeabilityResult.UsedDarcyMethod) methods.Add(VisualizationMethod.Darcy);
                if (permeabilityResult.UsedLatticeBoltzmannMethod) methods.Add(VisualizationMethod.LatticeBoltzmann);
                if (permeabilityResult.UsedNavierStokesMethod) methods.Add(VisualizationMethod.NavierStokes);

                // Available height after title
                int availableHeight = height - 50;

                // Calculate thumbnail size based on method count
                int thumbHeight = methodCount <= 2 ? availableHeight : availableHeight / 2;
                int thumbWidth = methodCount <= 2 ? width / methodCount : width / 2;

                // Create consistent-sized thumbnails for each method
                int baseSize = 400; // Base size for rendering

                // Draw thumbnails for each method
                for (int i = 0; i < methodCount; i++)
                {
                    // Render the method at the base resolution for consistent quality
                    Bitmap methodImage = RenderPressureField(methods[i]);

                    // Calculate position for this thumbnail
                    int x, y;
                    if (methodCount <= 2)
                    {
                        // Two methods - place side by side
                        x = i * thumbWidth;
                        y = 40;
                    }
                    else
                    {
                        // Three methods - two on top, one on bottom
                        x = (i % 2) * thumbWidth;
                        y = (i < 2) ? 40 : 40 + thumbHeight;
                    }

                    // Calculate display rectangle - maintain aspect ratio
                    Rectangle destRect = CalculateFitRectangle(
                        methodImage.Width, methodImage.Height,
                        thumbWidth, thumbHeight,
                        x, y);

                    // Draw the image with maintained aspect ratio
                    g.DrawImage(methodImage, destRect);

                    // Draw border around the image
                    g.DrawRectangle(new Pen(Color.FromArgb(80, 80, 80), 2), destRect);

                    // Add method label
                    string methodLabel = GetMethodName(methods[i]);
                    Font labelFont = new Font("Arial", 10, FontStyle.Bold);
                    SizeF textSize = g.MeasureString(methodLabel, labelFont);

                    // Label background
                    g.FillRectangle(new SolidBrush(Color.FromArgb(30, 30, 30)),
                        destRect.X, destRect.Y, textSize.Width + 10, textSize.Height + 6);

                    // Label text
                    g.DrawString(methodLabel, labelFont,
                        new SolidBrush(GetMethodColor(methods[i])),
                        destRect.X + 5, destRect.Y + 3);

                    // Permeability value
                    string valueLabel = $"{GetPermeabilityValueForMethod(methods[i]):F3} Darcy";
                    Font valueFont = new Font("Arial", 9);
                    g.DrawString(valueLabel, valueFont, Brushes.White,
                        destRect.X + 5, destRect.Y + textSize.Height + 20);

                    // Clean up
                    methodImage.Dispose();
                }
            }

            return combinedImage;
        }

        // Helper method to calculate rectangle that maintains aspect ratio
        private Rectangle CalculateFitRectangle(int sourceWidth, int sourceHeight,
                                               int maxWidth, int maxHeight,
                                               int offsetX, int offsetY)
        {
            // Calculate aspect ratios
            double sourceRatio = (double)sourceWidth / sourceHeight;
            double targetRatio = (double)maxWidth / maxHeight;

            int resultWidth, resultHeight;

            if (sourceRatio > targetRatio)
            {
                // Source is wider than target, constrain by width
                resultWidth = maxWidth;
                resultHeight = (int)(resultWidth / sourceRatio);
            }
            else
            {
                // Source is taller than target, constrain by height
                resultHeight = maxHeight;
                resultWidth = (int)(resultHeight * sourceRatio);
            }

            // Center the image in the available space
            int x = offsetX + (maxWidth - resultWidth) / 2;
            int y = offsetY + (maxHeight - resultHeight) / 2;

            return new Rectangle(x, y, resultWidth, resultHeight);
        }
        private string GetMethodName(VisualizationMethod method)
        {
            switch (method)
            {
                case VisualizationMethod.Darcy:
                    return "Darcy's Law";
                case VisualizationMethod.LatticeBoltzmann:
                    return "Lattice Boltzmann";
                case VisualizationMethod.NavierStokes:
                    return "Navier-Stokes";
                default:
                    return "Unknown Method";
            }
        }
        private void SaveMethodScreenshot(VisualizationMethod method)
        {
            Bitmap visualizationImage = RenderMethodVisualization(method);

            if (visualizationImage == null)
            {
                MessageBox.Show("No visualization available to save.",
                    "Screenshot Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (SaveFileDialog saveDialog = new SaveFileDialog())
            {
                saveDialog.Filter = "PNG Image|*.png|JPEG Image|*.jpg|Bitmap Image|*.bmp";
                saveDialog.Title = $"Save {method} Visualization";
                saveDialog.DefaultExt = "png";

                if (saveDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        // Create a copy with added info
                        using (Bitmap originalImage = visualizationImage)
                        {
                            // Create a new bitmap with space for the info panel
                            Bitmap screenshotWithInfo = new Bitmap(
                                originalImage.Width,
                                originalImage.Height + 120); // Space for multiple methods and info

                            using (Graphics g = Graphics.FromImage(screenshotWithInfo))
                            {
                                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

                                // Draw the original image
                                g.DrawImage(originalImage, 0, 0, originalImage.Width, originalImage.Height);

                                // Draw a black background for the info panel
                                g.FillRectangle(new SolidBrush(Color.Black),
                                    0, originalImage.Height, originalImage.Width, 120);

                                // Draw pressure scale bar
                                DrawPressureScaleBar(g,
                                    new Rectangle(50, originalImage.Height + 80, originalImage.Width - 100, 20),
                                    permeabilityResult.InputPressure,
                                    permeabilityResult.OutputPressure);

                                // Draw title at the top of info panel
                                Font titleFont = new Font("Arial", 11, FontStyle.Bold);
                                string titleText = $"Flow Direction: {permeabilityResult.FlowAxis}-Axis | Tortuosity: {permeabilityResult.Tortuosity:F2}";
                                SizeF titleSize = g.MeasureString(titleText, titleFont);
                                g.DrawString(titleText, titleFont, Brushes.White,
                                    (screenshotWithInfo.Width - titleSize.Width) / 2, originalImage.Height + 5);

                                // Draw method result based on which method was used
                                Font methodFont = new Font("Arial", 9, FontStyle.Bold);
                                int yPos = originalImage.Height + 30;

                                // Draw method-specific permeability value
                                string methodText = $"{method} Method: ";
                                string valueText = "";

                                switch (method)
                                {
                                    case VisualizationMethod.Darcy:
                                        methodText += "Darcy's Law";
                                        valueText = $"{permeabilityResult.PermeabilityDarcy:F3} Darcy ({permeabilityResult.PermeabilityMilliDarcy:F1} mD)";
                                        break;

                                    case VisualizationMethod.LatticeBoltzmann:
                                        methodText += "Lattice Boltzmann Method";
                                        valueText = $"{permeabilityResult.LatticeBoltzmannPermeabilityDarcy:F3} Darcy ({permeabilityResult.LatticeBoltzmannPermeabilityMilliDarcy:F1} mD)";
                                        break;

                                    case VisualizationMethod.NavierStokes:
                                        methodText += "Navier-Stokes Method";
                                        valueText = $"{permeabilityResult.NavierStokesPermeabilityDarcy:F3} Darcy ({permeabilityResult.NavierStokesPermeabilityMilliDarcy:F1} mD)";
                                        break;

                                    case VisualizationMethod.Combined:
                                        methodText = "Multiple calculation methods shown";
                                        break;
                                }

                                // Draw method name and value
                                g.DrawString(methodText, methodFont, Brushes.White, 20, yPos);
                                g.DrawString(valueText, methodFont, Brushes.LightGreen, 250, yPos);

                                // Add corrected value with tortuosity
                                if (method != VisualizationMethod.Combined && permeabilityResult.Tortuosity > 1.0)
                                {
                                    yPos += 22;
                                    g.DrawString($"Corrected for Tortuosity (τ = {permeabilityResult.Tortuosity:F2}):",
                                        methodFont, Brushes.White, 20, yPos);

                                    string correctedValue = "";
                                    switch (method)
                                    {
                                        case VisualizationMethod.Darcy:
                                            correctedValue = $"{permeabilityResult.CorrectedPermeabilityDarcy:F3} Darcy ({permeabilityResult.CorrectedPermeabilityDarcy * 1000:F1} mD)";
                                            break;

                                        case VisualizationMethod.LatticeBoltzmann:
                                            correctedValue = $"{permeabilityResult.CorrectedLatticeBoltzmannPermeabilityDarcy:F3} Darcy ({permeabilityResult.CorrectedLatticeBoltzmannPermeabilityDarcy * 1000:F1} mD)";
                                            break;

                                        case VisualizationMethod.NavierStokes:
                                            correctedValue = $"{permeabilityResult.CorrectedNavierStokesPermeabilityDarcy:F3} Darcy ({permeabilityResult.CorrectedNavierStokesPermeabilityDarcy * 1000:F1} mD)";
                                            break;
                                    }

                                    g.DrawString(correctedValue, methodFont, Brushes.LightGreen, 250, yPos);
                                }

                                // Draw timestamp in corner
                                Font timestampFont = new Font("Arial", 8);
                                string timestamp = $"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                                SizeF timestampSize = g.MeasureString(timestamp, timestampFont);
                                g.DrawString(timestamp, timestampFont, Brushes.LightGray,
                                    screenshotWithInfo.Width - timestampSize.Width - 10,
                                    originalImage.Height + 100 - timestampSize.Height);
                            }

                            // Save the image with the info panel
                            string extension = Path.GetExtension(saveDialog.FileName).ToLower();
                            System.Drawing.Imaging.ImageFormat format = System.Drawing.Imaging.ImageFormat.Png; // Default

                            if (extension == ".jpg" || extension == ".jpeg")
                                format = System.Drawing.Imaging.ImageFormat.Jpeg;
                            else if (extension == ".bmp")
                                format = System.Drawing.Imaging.ImageFormat.Bmp;

                            screenshotWithInfo.Save(saveDialog.FileName, format);
                        }

                        statusLabel.Text = $"{method} visualization saved successfully.";
                        MessageBox.Show($"{method} visualization screenshot saved successfully.",
                            "Save Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error saving screenshot: {ex.Message}",
                            "Screenshot Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Logger.Log($"[PoreNetworkModelingForm] Error saving screenshot: {ex.Message}\n{ex.StackTrace}");
                    }
                }
            }
        }
        // New method to handle loading raw data without header validation
        private void LoadNetworkAsRawData(FileStream fs)
        {
            try
            {
                fs.Position = 0;
                using (BinaryReader reader = new BinaryReader(fs))
                {
                    // Create a simple dialog to let user specify the number of pores and throats
                    using (Form inputForm = new Form
                    {
                        Width = 300,
                        Height = 200,
                        FormBorderStyle = FormBorderStyle.FixedDialog,
                        Text = "Raw Data Import",
                        StartPosition = FormStartPosition.CenterParent,
                        MaximizeBox = false,
                        MinimizeBox = false
                    })
                    {
                        Label lblPores = new Label { Left = 20, Top = 20, Text = "Number of Pores:" };
                        NumericUpDown numPores = new NumericUpDown { Left = 150, Top = 18, Width = 100, Minimum = 1, Maximum = 100000, Value = 100 };

                        Label lblThroats = new Label { Left = 20, Top = 50, Text = "Number of Throats:" };
                        NumericUpDown numThroats = new NumericUpDown { Left = 150, Top = 48, Width = 100, Minimum = 0, Maximum = 1000000, Value = 300 };

                        Label lblPixelSize = new Label { Left = 20, Top = 80, Text = "Pixel Size (m):" };
                        TextBox txtPixelSize = new TextBox { Left = 150, Top = 78, Width = 100, Text = "1.0E-6" };

                        Button btnOk = new Button { Text = "OK", Left = 90, Width = 100, Top = 120, DialogResult = DialogResult.OK };

                        inputForm.Controls.Add(lblPores);
                        inputForm.Controls.Add(numPores);
                        inputForm.Controls.Add(lblThroats);
                        inputForm.Controls.Add(numThroats);
                        inputForm.Controls.Add(lblPixelSize);
                        inputForm.Controls.Add(txtPixelSize);
                        inputForm.Controls.Add(btnOk);

                        inputForm.AcceptButton = btnOk;

                        if (inputForm.ShowDialog() != DialogResult.OK)
                            return;

                        int poreCount = (int)numPores.Value;
                        int throatCount = (int)numThroats.Value;
                        double pixelSize = 1.0E-6; // default

                        try
                        {
                            pixelSize = Convert.ToDouble(txtPixelSize.Text);
                        }
                        catch
                        {
                            // Use default if parsing fails
                        }

                        // Create new network model
                        networkModel = new PoreNetworkModel
                        {
                            PixelSize = pixelSize,
                            Porosity = 0.5, // Default value
                            Pores = new List<Pore>(),
                            Throats = new List<Throat>()
                        };

                        // Now determine the correct offset to start reading
                        long estimatedHeaderSize = 0; // Skip header entirely
                        fs.Position = estimatedHeaderSize;

                        // Read pores
                        try
                        {
                            for (int i = 0; i < poreCount; i++)
                            {
                                if (fs.Position >= fs.Length - 32) // Check if we have enough data for at least one pore
                                    break;

                                Pore pore = new Pore
                                {
                                    Id = i + 1, // Generate sequential ID
                                    Volume = reader.ReadDouble(),
                                    Area = reader.ReadDouble(),
                                    Radius = reader.ReadDouble(),
                                    Center = new Point3D
                                    {
                                        X = reader.ReadDouble(),
                                        Y = reader.ReadDouble(),
                                        Z = reader.ReadDouble()
                                    },
                                    ConnectionCount = 0 // Will update later
                                };

                                // Skip one more value that might be there
                                try { reader.ReadInt32(); } catch { }

                                networkModel.Pores.Add(pore);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[PoreNetworkModelingForm] Exception during raw pore reading: {ex.Message}");
                            // Continue with the throats even if pore reading fails
                        }

                        // Read throats if we have any pores
                        if (networkModel.Pores.Count > 0)
                        {
                            double totalThroatVolume = 0;
                            try
                            {
                                for (int i = 0; i < throatCount; i++)
                                {
                                    if (fs.Position >= fs.Length - 24) // Check if we have enough data
                                        break;

                                    Throat throat = new Throat
                                    {
                                        Id = i + 1,
                                        PoreId1 = reader.ReadInt32(),
                                        PoreId2 = reader.ReadInt32(),
                                        Radius = reader.ReadDouble(),
                                        Length = reader.ReadDouble(),
                                        Volume = reader.ReadDouble()
                                    };

                                    // Validate pore references
                                    if (networkModel.Pores.Any(p => p.Id == throat.PoreId1) &&
                                        networkModel.Pores.Any(p => p.Id == throat.PoreId2))
                                    {
                                        networkModel.Throats.Add(throat);
                                        totalThroatVolume += throat.Volume;

                                        // Update connection counts
                                        var pore1 = networkModel.Pores.First(p => p.Id == throat.PoreId1);
                                        var pore2 = networkModel.Pores.First(p => p.Id == throat.PoreId2);
                                        pore1.ConnectionCount++;
                                        pore2.ConnectionCount++;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"[PoreNetworkModelingForm] Exception during raw throat reading: {ex.Message}");
                            }

                            networkModel.TotalPoreVolume = networkModel.Pores.Sum(p => p.Volume);
                            networkModel.TotalThroatVolume = totalThroatVolume;

                            // Calculate porosity (use default if calculation fails)
                            try
                            {
                                double totalVolume = networkModel.Pores.Max(p =>
                                    p.Center.X + p.Radius - networkModel.Pores.Min(p2 => p2.Center.X - p2.Radius)) *
                                    networkModel.Pores.Max(p =>
                                    p.Center.Y + p.Radius - networkModel.Pores.Min(p2 => p2.Center.Y - p2.Radius)) *
                                    networkModel.Pores.Max(p =>
                                    p.Center.Z + p.Radius - networkModel.Pores.Min(p2 => p2.Center.Z - p2.Radius));

                                if (totalVolume > 0)
                                    networkModel.Porosity = Math.Min(1.0, (networkModel.TotalPoreVolume + networkModel.TotalThroatVolume) / totalVolume);
                            }
                            catch
                            {
                                // Use default porosity if calculation fails
                            }
                        }
                    }

                    // Update UI
                    UpdatePoreTable();
                    Render3DVisualization();

                    // Enable export and save buttons
                    exportButton.Enabled = true;
                    saveButton.Enabled = true;

                    // Update form title if in viewer mode
                    if (viewerMode)
                    {
                        this.Text = $"Pore Network Modeling - Raw Import [Viewer Mode]";
                    }

                    statusLabel.Text = $"Loaded raw data: {networkModel.Pores.Count} pores and {networkModel.Throats.Count} throats";
                }
            }
            catch (Exception ex)
            {
                string errorMessage = $"Error loading raw data: {ex.Message}";
                Logger.Log($"[PoreNetworkModelingForm] {errorMessage}\n{ex.StackTrace}");
                MessageBox.Show(errorMessage, "Raw Data Import Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

                if (viewerMode)
                {
                    // In viewer mode, create an empty network
                    networkModel = new PoreNetworkModel
                    {
                        Pores = new List<Pore>(),
                        Throats = new List<Throat>()
                    };
                }
            }
        }
        private void SavePermeabilityResults(object sender, EventArgs e)
        {
            if (permeabilityResult == null)
            {
                MessageBox.Show("No permeability results to save.",
                    "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (SaveFileDialog saveDialog = new SaveFileDialog())
            {
                saveDialog.Filter = "DAT files (*.dat)|*.dat";
                saveDialog.Title = "Save Permeability Results";
                saveDialog.DefaultExt = "dat";

                if (saveDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        using (FileStream fs = new FileStream(saveDialog.FileName, FileMode.Create))
                        using (BinaryWriter writer = new BinaryWriter(fs))
                        {
                            // Write file header
                            writer.Write("PERMEABILITY"); // Magic string
                            writer.Write(3); // Version number (increased for Lattice Boltzmann additions)

                            // Write basic simulation parameters
                            writer.Write((int)permeabilityResult.FlowAxis);
                            writer.Write(permeabilityResult.Viscosity);
                            writer.Write(permeabilityResult.InputPressure);
                            writer.Write(permeabilityResult.OutputPressure);

                            // Write calculation method flags
                            writer.Write(permeabilityResult.UsedDarcyMethod);
                            writer.Write(permeabilityResult.UsedLatticeBoltzmannMethod);
                            writer.Write(permeabilityResult.UsedNavierStokesMethod);

                            // Write Darcy method results
                            writer.Write(permeabilityResult.PermeabilityDarcy);
                            writer.Write(permeabilityResult.PermeabilityMilliDarcy);

                            // Write Lattice Boltzmann results
                            writer.Write(permeabilityResult.LatticeBoltzmannPermeabilityDarcy);
                            writer.Write(permeabilityResult.LatticeBoltzmannPermeabilityMilliDarcy);

                            // Write Navier-Stokes results
                            writer.Write(permeabilityResult.NavierStokesPermeabilityDarcy);
                            writer.Write(permeabilityResult.NavierStokesPermeabilityMilliDarcy);

                            // Write Kozeny-Carman results
                            writer.Write(permeabilityResult.KozenyCarmanPermeabilityDarcy);
                            writer.Write(permeabilityResult.KozenyCarmanPermeabilityMilliDarcy);

                            // Write tortuosity
                            writer.Write(permeabilityResult.Tortuosity);

                            // Write corrected values
                            writer.Write(permeabilityResult.CorrectedPermeabilityDarcy);
                            writer.Write(permeabilityResult.CorrectedLatticeBoltzmannPermeabilityDarcy);
                            writer.Write(permeabilityResult.CorrectedNavierStokesPermeabilityDarcy);
                            writer.Write(permeabilityResult.CorrectedKozenyCarmanPermeabilityDarcy);

                            // Write total flow rate and model dimensions
                            writer.Write(permeabilityResult.TotalFlowRate);
                            writer.Write(permeabilityResult.ModelLength);
                            writer.Write(permeabilityResult.ModelArea);

                            // Write inlet/outlet pores
                            writer.Write(permeabilityResult.InletPores.Count);
                            foreach (var poreId in permeabilityResult.InletPores)
                            {
                                writer.Write(poreId);
                            }

                            writer.Write(permeabilityResult.OutletPores.Count);
                            foreach (var poreId in permeabilityResult.OutletPores)
                            {
                                writer.Write(poreId);
                            }

                            // Write pressure field for Darcy method
                            writer.Write(permeabilityResult.PressureField.Count);
                            foreach (var pair in permeabilityResult.PressureField)
                            {
                                writer.Write(pair.Key);
                                writer.Write(pair.Value);
                            }

                            // Write pressure field for Lattice Boltzmann method
                            int lbmFieldCount = permeabilityResult.LatticeBoltzmannPressureField?.Count ?? 0;
                            writer.Write(lbmFieldCount);
                            if (lbmFieldCount > 0)
                            {
                                foreach (var pair in permeabilityResult.LatticeBoltzmannPressureField)
                                {
                                    writer.Write(pair.Key);
                                    writer.Write(pair.Value);
                                }
                            }

                            // Write pressure field for Navier-Stokes method
                            int nsFieldCount = permeabilityResult.NavierStokesPressureField?.Count ?? 0;
                            writer.Write(nsFieldCount);
                            if (nsFieldCount > 0)
                            {
                                foreach (var pair in permeabilityResult.NavierStokesPressureField)
                                {
                                    writer.Write(pair.Key);
                                    writer.Write(pair.Value);
                                }
                            }

                            // Write throat flow rates
                            writer.Write(permeabilityResult.ThroatFlowRates.Count);
                            foreach (var pair in permeabilityResult.ThroatFlowRates)
                            {
                                writer.Write(pair.Key);
                                writer.Write(pair.Value);
                            }

                            // Write timestamp
                            writer.Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        }

                        statusLabel.Text = "Permeability results saved successfully";
                        MessageBox.Show("Permeability results saved successfully", "Save Complete",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error saving permeability results: {ex.Message}",
                            "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Logger.Log($"[PoreNetworkModelingForm] Error saving permeability: {ex.Message}\n{ex.StackTrace}");
                    }
                }
            }
        }
        private void LoadPermeabilityResults(object sender, EventArgs e)
        {
            using (OpenFileDialog openDialog = new OpenFileDialog())
            {
                openDialog.Filter = "DAT files (*.dat)|*.dat|All files (*.*)|*.*";
                openDialog.Title = "Load Permeability Results";

                if (openDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        using (FileStream fs = new FileStream(openDialog.FileName, FileMode.Open))
                        using (BinaryReader reader = new BinaryReader(fs))
                        {
                            // Read and verify file header
                            string magic = new string(reader.ReadChars(11));
                            if (magic != "PERMEABILITY")
                            {
                                throw new Exception($"Invalid file format: '{magic}' (expected 'PERMEABILITY')");
                            }

                            int version = reader.ReadInt32();
                            if (version != 1 && version != 2 && version != 3)
                            {
                                throw new Exception($"Unsupported version: {version}");
                            }

                            // Create new result object
                            permeabilityResult = new PermeabilitySimulationResult
                            {
                                Model = networkModel,  // Use the currently loaded model
                                FlowAxis = (PermeabilitySimulator.FlowAxis)reader.ReadInt32(),
                                Viscosity = reader.ReadDouble(),
                                InputPressure = reader.ReadDouble(),
                                OutputPressure = reader.ReadDouble()
                            };

                            if (version >= 2)
                            {
                                // Read calculation method flags (version 2+)
                                permeabilityResult.UsedDarcyMethod = reader.ReadBoolean();

                                if (version >= 3)
                                {
                                    // Version 3 uses LatticeBoltzmann
                                    permeabilityResult.UsedLatticeBoltzmannMethod = reader.ReadBoolean();
                                }
                                else
                                {
                                    // Version 2 used StefanBoltzmann - convert to LatticeBoltzmann
                                    permeabilityResult.UsedLatticeBoltzmannMethod = reader.ReadBoolean();
                                }

                                permeabilityResult.UsedNavierStokesMethod = reader.ReadBoolean();

                                // Read all method results
                                permeabilityResult.PermeabilityDarcy = reader.ReadDouble();
                                permeabilityResult.PermeabilityMilliDarcy = reader.ReadDouble();

                                if (version >= 3)
                                {
                                    // Version 3 uses LatticeBoltzmann
                                    permeabilityResult.LatticeBoltzmannPermeabilityDarcy = reader.ReadDouble();
                                    permeabilityResult.LatticeBoltzmannPermeabilityMilliDarcy = reader.ReadDouble();
                                }
                                else
                                {
                                    // Version 2 used StefanBoltzmann - convert to LatticeBoltzmann
                                    permeabilityResult.LatticeBoltzmannPermeabilityDarcy = reader.ReadDouble();
                                    permeabilityResult.LatticeBoltzmannPermeabilityMilliDarcy = reader.ReadDouble();
                                }

                                permeabilityResult.NavierStokesPermeabilityDarcy = reader.ReadDouble();
                                permeabilityResult.NavierStokesPermeabilityMilliDarcy = reader.ReadDouble();

                                // Read Kozeny-Carman results if available (version 3+)
                                if (version >= 3)
                                {
                                    permeabilityResult.KozenyCarmanPermeabilityDarcy = reader.ReadDouble();
                                    permeabilityResult.KozenyCarmanPermeabilityMilliDarcy = reader.ReadDouble();
                                }

                                // Read tortuosity and corrected values
                                permeabilityResult.Tortuosity = reader.ReadDouble();
                                permeabilityResult.CorrectedPermeabilityDarcy = reader.ReadDouble();

                                if (version >= 3)
                                {
                                    permeabilityResult.CorrectedLatticeBoltzmannPermeabilityDarcy = reader.ReadDouble();
                                    permeabilityResult.CorrectedNavierStokesPermeabilityDarcy = reader.ReadDouble();
                                    permeabilityResult.CorrectedKozenyCarmanPermeabilityDarcy = reader.ReadDouble();
                                }
                                else if (version == 2)
                                {
                                    // In version 2, we had Stefan-Boltzmann - convert to LB
                                    permeabilityResult.CorrectedLatticeBoltzmannPermeabilityDarcy = reader.ReadDouble();
                                    permeabilityResult.CorrectedNavierStokesPermeabilityDarcy = reader.ReadDouble();
                                }
                            }
                            else
                            {
                                // Compatibility with version 1 files
                                permeabilityResult.PermeabilityDarcy = reader.ReadDouble();
                                permeabilityResult.PermeabilityMilliDarcy = reader.ReadDouble();

                                // Default method flags
                                permeabilityResult.UsedDarcyMethod = true;
                                permeabilityResult.UsedLatticeBoltzmannMethod = false;
                                permeabilityResult.UsedNavierStokesMethod = false;

                                // Try to read tortuosity if it exists in the file
                                try
                                {
                                    if (fs.Position < fs.Length - 16) // Check for 16 more bytes
                                    {
                                        permeabilityResult.Tortuosity = reader.ReadDouble();
                                        permeabilityResult.CorrectedPermeabilityDarcy = reader.ReadDouble();
                                    }
                                    else
                                    {
                                        permeabilityResult.Tortuosity = networkModel.Tortuosity;
                                        permeabilityResult.CorrectedPermeabilityDarcy =
                                            permeabilityResult.PermeabilityDarcy / (permeabilityResult.Tortuosity * permeabilityResult.Tortuosity);
                                    }
                                }
                                catch
                                {
                                    // Fall back to model tortuosity
                                    permeabilityResult.Tortuosity = networkModel.Tortuosity;
                                    permeabilityResult.CorrectedPermeabilityDarcy =
                                        permeabilityResult.PermeabilityDarcy / (permeabilityResult.Tortuosity * permeabilityResult.Tortuosity);
                                }

                                // Set defaults for other methods
                                permeabilityResult.LatticeBoltzmannPermeabilityDarcy = 0;
                                permeabilityResult.LatticeBoltzmannPermeabilityMilliDarcy = 0;
                                permeabilityResult.NavierStokesPermeabilityDarcy = 0;
                                permeabilityResult.NavierStokesPermeabilityMilliDarcy = 0;
                                permeabilityResult.CorrectedLatticeBoltzmannPermeabilityDarcy = 0;
                                permeabilityResult.CorrectedNavierStokesPermeabilityDarcy = 0;
                                permeabilityResult.KozenyCarmanPermeabilityDarcy = 0;
                                permeabilityResult.KozenyCarmanPermeabilityMilliDarcy = 0;
                                permeabilityResult.CorrectedKozenyCarmanPermeabilityDarcy = 0;
                            }

                            // Read common results
                            permeabilityResult.TotalFlowRate = reader.ReadDouble();
                            permeabilityResult.ModelLength = reader.ReadDouble();
                            permeabilityResult.ModelArea = reader.ReadDouble();

                            // Read inlet/outlet pores
                            int inletCount = reader.ReadInt32();
                            permeabilityResult.InletPores = new List<int>(inletCount);
                            for (int i = 0; i < inletCount; i++)
                            {
                                permeabilityResult.InletPores.Add(reader.ReadInt32());
                            }

                            int outletCount = reader.ReadInt32();
                            permeabilityResult.OutletPores = new List<int>(outletCount);
                            for (int i = 0; i < outletCount; i++)
                            {
                                permeabilityResult.OutletPores.Add(reader.ReadInt32());
                            }

                            // Read Darcy pressure field
                            int pressureCount = reader.ReadInt32();
                            permeabilityResult.PressureField = new Dictionary<int, double>(pressureCount);
                            for (int i = 0; i < pressureCount; i++)
                            {
                                int key = reader.ReadInt32();
                                double value = reader.ReadDouble();
                                permeabilityResult.PressureField[key] = value;
                            }

                            // Read Lattice Boltzmann pressure field if available
                            if (version >= 3)
                            {
                                int lbmPressureCount = reader.ReadInt32();
                                if (lbmPressureCount > 0)
                                {
                                    permeabilityResult.LatticeBoltzmannPressureField = new Dictionary<int, double>(lbmPressureCount);
                                    for (int i = 0; i < lbmPressureCount; i++)
                                    {
                                        int key = reader.ReadInt32();
                                        double value = reader.ReadDouble();
                                        permeabilityResult.LatticeBoltzmannPressureField[key] = value;
                                    }
                                }

                                // Read Navier-Stokes pressure field if available
                                int nsPressureCount = reader.ReadInt32();
                                if (nsPressureCount > 0)
                                {
                                    permeabilityResult.NavierStokesPressureField = new Dictionary<int, double>(nsPressureCount);
                                    for (int i = 0; i < nsPressureCount; i++)
                                    {
                                        int key = reader.ReadInt32();
                                        double value = reader.ReadDouble();
                                        permeabilityResult.NavierStokesPressureField[key] = value;
                                    }
                                }
                            }
                            else if (version == 2)
                            {
                                // In version 2, there's no separate LBM or NS pressure fields
                                // Initialize empty dictionaries
                                permeabilityResult.LatticeBoltzmannPressureField = new Dictionary<int, double>();
                                permeabilityResult.NavierStokesPressureField = new Dictionary<int, double>();

                                // Copy Darcy pressure field to LBM if LBM method was selected
                                if (permeabilityResult.UsedLatticeBoltzmannMethod)
                                {
                                    foreach (var pair in permeabilityResult.PressureField)
                                    {
                                        permeabilityResult.LatticeBoltzmannPressureField[pair.Key] = pair.Value;
                                    }
                                }

                                // Copy Darcy pressure field to NS if NS method was selected
                                if (permeabilityResult.UsedNavierStokesMethod)
                                {
                                    foreach (var pair in permeabilityResult.PressureField)
                                    {
                                        permeabilityResult.NavierStokesPressureField[pair.Key] = pair.Value;
                                    }
                                }
                            }

                            // Read throat flow rates
                            int flowRateCount = reader.ReadInt32();
                            permeabilityResult.ThroatFlowRates = new Dictionary<int, double>(flowRateCount);
                            for (int i = 0; i < flowRateCount; i++)
                            {
                                int key = reader.ReadInt32();
                                double value = reader.ReadDouble();
                                permeabilityResult.ThroatFlowRates[key] = value;
                            }

                            // Read timestamp if available
                            string timestamp = "Unknown";
                            if (fs.Position < fs.Length - 5)
                            {
                                try { timestamp = reader.ReadString(); }
                                catch { /* Ignore if timestamp isn't available */ }
                            }

                            Logger.Log($"[PoreNetworkModelingForm] Loaded permeability results from {timestamp}");

                            // Log information about which methods were loaded
                            string methods = "";
                            if (permeabilityResult.UsedDarcyMethod) methods += "Darcy's Law ";
                            if (permeabilityResult.UsedLatticeBoltzmannMethod) methods += "Lattice Boltzmann ";
                            if (permeabilityResult.UsedNavierStokesMethod) methods += "Navier-Stokes ";
                            Logger.Log($"[PoreNetworkModelingForm] Loaded methods: {methods}, tortuosity: {permeabilityResult.Tortuosity:F2}");

                            // Version conversion warning
                            if (version < 3 && permeabilityResult.UsedLatticeBoltzmannMethod)
                            {
                                Logger.Log("[PoreNetworkModelingForm] Converting old Stefan-Boltzmann data to Lattice Boltzmann");
                                MessageBox.Show("This file was saved with an older version that used a method called 'Stefan-Boltzmann' instead of 'Lattice Boltzmann'. The data has been converted automatically.",
                                    "Version Conversion", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            }
                        }

                        // Update UI with loaded results
                        RenderPermeabilityResults();

                        // Switch to permeability tab
                        mainTabControl.SelectedTab = permeabilityTab;

                        // Update status with a multi-method message
                        StringBuilder statusBuilder = new StringBuilder("Loaded permeability: ");
                        if (permeabilityResult.UsedDarcyMethod)
                        {
                            statusBuilder.Append($"Darcy={permeabilityResult.PermeabilityDarcy:F3}D ");
                        }
                        if (permeabilityResult.UsedLatticeBoltzmannMethod)
                        {
                            statusBuilder.Append($"LBM={permeabilityResult.LatticeBoltzmannPermeabilityDarcy:F3}D ");
                        }
                        if (permeabilityResult.UsedNavierStokesMethod)
                        {
                            statusBuilder.Append($"NS={permeabilityResult.NavierStokesPermeabilityDarcy:F3}D ");
                        }
                        statusBuilder.Append($"| τ={permeabilityResult.Tortuosity:F2}");
                        statusLabel.Text = statusBuilder.ToString();

                        // Enable export buttons
                        if (exportPermeabilityButton != null) exportPermeabilityButton.Enabled = true;
                        if (savePermeabilityButton != null) savePermeabilityButton.Enabled = true;

                        MessageBox.Show("Permeability results loaded successfully", "Load Complete",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error loading permeability results: {ex.Message}",
                            "Load Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Logger.Log($"[PoreNetworkModelingForm] Error loading permeability: {ex.Message}\n{ex.StackTrace}");
                    }
                }
            }
        }
        private TabPage CreateVisualizationTab(string title, VisualizationMethod method)
        {
            TabPage tab = new TabPage(title)
            {
                BackColor = Color.Black,
                Padding = new Padding(0)
            };

            Panel visualizationPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.Black
            };

            // Control panel at top
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

            Button resetViewButton = new Button
            {
                Text = "Reset View",
                Location = new Point(150, 8),
                Width = 100,
                Height = 25,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(80, 80, 80),
                ForeColor = Color.White
            };

            // Store the visualization method with the button
            resetViewButton.Tag = method;
            resetViewButton.Click += (s, e) =>
            {
                rotationX = 30.0f;
                rotationY = 30.0f;
                rotationZ = 0.0f;
                viewScale = 1.0f;
                panOffsetX = 0.0f;
                panOffsetY = 0.0f;
                UpdateVisualization((VisualizationMethod)((Button)s).Tag, ((Button)s).Parent.Parent);
            };
            controlPanel.Controls.Add(resetViewButton);

            Button screenshotButton = new Button
            {
                Text = "Save Screenshot",
                Location = new Point(260, 8),
                Width = 130,
                Height = 25,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(80, 80, 80),
                ForeColor = Color.White,
                Tag = method
            };
            screenshotButton.Click += (s, e) => SaveMethodScreenshot((VisualizationMethod)((Button)s).Tag);
            controlPanel.Controls.Add(screenshotButton);

            visualizationPanel.Controls.Add(controlPanel);

            // Create pressure visualization PictureBox
            PictureBox visualizationPictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.CenterImage,
                Tag = method // Store the visualization method with the PictureBox
            };

            // Render the initial visualization
            visualizationPictureBox.Image = RenderMethodVisualization(method);

            // Add mouse handling for each visualization
            SetupVisualizationMouseHandling(visualizationPictureBox);

            visualizationPanel.Controls.Add(visualizationPictureBox);

            // Add color legend for pressure
            Panel legendPanel = CreatePressureLegendPanel(method);
            visualizationPanel.Controls.Add(legendPanel);

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
            visualizationPanel.Controls.Add(instructionsLabel);

            tab.Controls.Add(visualizationPanel);
            return tab;
        }
        private void SetupVisualizationMouseHandling(PictureBox pictureBox)
        {
            pictureBox.MouseDown += (s, e) =>
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
            };

            pictureBox.MouseMove += (s, e) =>
            {
                if (isDragging)
                {
                    // Calculate delta movement for rotation
                    float deltaX = (e.X - lastMousePosition.X) * 0.5f;
                    float deltaY = (e.Y - lastMousePosition.Y) * 0.5f;

                    // Update rotation angles
                    rotationY += deltaX;
                    rotationX += deltaY;

                    // Update visualization
                    UpdateVisualization((VisualizationMethod)((PictureBox)s).Tag, ((PictureBox)s).Parent);

                    lastMousePosition = e.Location;
                }
                else if (isPanning)
                {
                    // Calculate delta movement for panning
                    float deltaX = (e.X - lastMousePosition.X) * 0.01f;
                    float deltaY = (e.Y - lastMousePosition.Y) * 0.01f;

                    // Update pan offsets
                    panOffsetX += deltaX;
                    panOffsetY += deltaY;

                    // Update visualization
                    UpdateVisualization((VisualizationMethod)((PictureBox)s).Tag, ((PictureBox)s).Parent);

                    lastMousePosition = e.Location;
                }
            };

            pictureBox.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    isDragging = false;
                }
                else if (e.Button == MouseButtons.Middle)
                {
                    isPanning = false;
                }
            };

            pictureBox.MouseWheel += (s, e) =>
            {
                // Change zoom level with mouse wheel
                float zoomFactor = 1.0f + (e.Delta > 0 ? 0.1f : -0.1f);
                viewScale *= zoomFactor;

                // Limit minimum and maximum zoom
                viewScale = Math.Max(0.2f, Math.Min(3.0f, viewScale));

                // Update visualization
                UpdateVisualization((VisualizationMethod)((PictureBox)s).Tag, ((PictureBox)s).Parent);
            };
        }
        private void UpdateVisualization(VisualizationMethod method, Control parent)
        {
            // Find the PictureBox in the parent
            foreach (Control control in parent.Controls)
            {
                if (control is PictureBox pictureBox && pictureBox.Tag is VisualizationMethod)
                {
                    pictureBox.Image = RenderMethodVisualization(method);
                    break;
                }
            }
        }
        private Bitmap RenderMethodVisualization(VisualizationMethod method)
        {
            switch (method)
            {
                case VisualizationMethod.Darcy:
                    return RenderPressureField(method);

                case VisualizationMethod.LatticeBoltzmann:
                    return RenderPressureField(method);

                case VisualizationMethod.NavierStokes:
                    return RenderPressureField(method);

                case VisualizationMethod.Combined:
                    return RenderCombinedView();

                default:
                    return RenderPressureField(method);
            }
        }
        private Panel CreatePressureLegendPanel(VisualizationMethod method)
        {
            Panel legendPanel = new Panel
            {
                Dock = DockStyle.Right,
                Width = 80,
                BackColor = Color.FromArgb(30, 30, 30)
            };

            // Create pressure gradient legend
            PictureBox legendPictureBox = new PictureBox
            {
                Width = 20,
                Height = 200,
                Location = new Point(30, 50),
                BorderStyle = BorderStyle.FixedSingle
            };

            // Generate the gradient image
            Bitmap gradientBitmap = new Bitmap(1, 200);
            for (int y = 0; y < 200; y++)
            {
                // Create gradient from red (high pressure) to blue (low pressure)
                double t = (double)y / 199;
                Color color = GetPressureColor(1.0 - t, method);
                gradientBitmap.SetPixel(0, y, color);
            }

            // Scale to proper width
            Bitmap scaledGradient = new Bitmap(20, 200);
            using (Graphics g = Graphics.FromImage(scaledGradient))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.DrawImage(gradientBitmap, 0, 0, 20, 200);
            }

            legendPictureBox.Image = scaledGradient;
            legendPanel.Controls.Add(legendPictureBox);

            // Add labels for pressure legend
            Label pressureLegendLabel = new Label
            {
                Text = "Pressure",
                ForeColor = Color.White,
                Location = new Point(10, 10),
                Size = new Size(60, 20),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            legendPanel.Controls.Add(pressureLegendLabel);

            // Add labels for high and low pressure
            Label highLabel = new Label
            {
                Text = $"{permeabilityResult.InputPressure:F0} Pa",
                ForeColor = Color.White,
                Location = new Point(10, 30),
                Size = new Size(60, 20),
                TextAlign = ContentAlignment.MiddleCenter
            };
            legendPanel.Controls.Add(highLabel);

            Label lowLabel = new Label
            {
                Text = $"{permeabilityResult.OutputPressure:F0} Pa",
                ForeColor = Color.White,
                Location = new Point(10, 250),
                Size = new Size(60, 20),
                TextAlign = ContentAlignment.MiddleCenter
            };
            legendPanel.Controls.Add(lowLabel);

            // Add method indicator
            Label methodLabel = new Label
            {
                Text = GetMethodShortName(method),
                ForeColor = GetMethodColor(method),
                Location = new Point(10, 280),
                Size = new Size(60, 20),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            legendPanel.Controls.Add(methodLabel);

            return legendPanel;
        }
        private Color GetMethodColor(VisualizationMethod method)
        {
            switch (method)
            {
                case VisualizationMethod.Darcy:
                    return Color.LightGreen;
                case VisualizationMethod.LatticeBoltzmann:
                    return Color.LightBlue;
                case VisualizationMethod.NavierStokes:
                    return Color.LightPink;
                case VisualizationMethod.Combined:
                    return Color.White;
                default:
                    return Color.White;
            }
        }
        private string GetMethodShortName(VisualizationMethod method)
        {
            switch (method)
            {
                case VisualizationMethod.Darcy:
                    return "Darcy";
                case VisualizationMethod.LatticeBoltzmann:
                    return "LBM";
                case VisualizationMethod.NavierStokes:
                    return "NS";
                case VisualizationMethod.Combined:
                    return "All";
                default:
                    return "Unknown";
            }
        }
        private void ExportPermeabilityToExcel(string filename)
        {
            if (permeabilityResult == null)
            {
                throw new InvalidOperationException("No permeability results to export");
            }

            // Create Excel application instance
            Type excelType = Type.GetTypeFromProgID("Excel.Application");
            if (excelType == null)
            {
                MessageBox.Show("Microsoft Excel is not installed on this system.\nExporting to CSV format instead.",
                    "Excel Not Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                // Fall back to CSV if Excel is not available
                ExportPermeabilityCsv(Path.ChangeExtension(filename, ".csv"));
                return;
            }

            // Use dynamic to simplify COM interop
            dynamic excel = null;
            dynamic workbook = null;
            dynamic worksheet = null;

            try
            {
                // Start with a progress dialog
                using (var progressDialog = new Form())
                {
                    progressDialog.Text = "Exporting Permeability to Excel";
                    progressDialog.Width = 350;
                    progressDialog.Height = 100;
                    progressDialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                    progressDialog.StartPosition = FormStartPosition.CenterParent;
                    progressDialog.ControlBox = false;

                    var progressLabel = new Label
                    {
                        Text = "Creating Excel workbook...",
                        Location = new System.Drawing.Point(10, 15),
                        Width = 330,
                        TextAlign = System.Drawing.ContentAlignment.MiddleCenter
                    };

                    var progressBar = new ProgressBar
                    {
                        Location = new System.Drawing.Point(10, 40),
                        Width = 330,
                        Height = 20,
                        Style = ProgressBarStyle.Marquee
                    };

                    progressDialog.Controls.Add(progressLabel);
                    progressDialog.Controls.Add(progressBar);

                    // Show progress dialog in a non-blocking way
                    progressDialog.Show(this);
                    Application.DoEvents(); // Process UI message loop

                    // Create Excel application
                    excel = Activator.CreateInstance(excelType);
                    excel.Visible = false;
                    excel.DisplayAlerts = false;

                    // Create a new workbook
                    workbook = excel.Workbooks.Add();

                    // Ensure we have at least 5 worksheets (Summary, Method Comparison, Pressure Field, Flow Rates, Charts)
                    while (workbook.Worksheets.Count < 5)
                    {
                        workbook.Worksheets.Add();
                    }

                    // ==========================================================
                    // Worksheet 1: Summary
                    // ==========================================================
                    progressLabel.Text = "Creating summary sheet...";
                    Application.DoEvents();

                    worksheet = workbook.Worksheets[1];
                    worksheet.Name = "Summary";

                    // Create a title
                    worksheet.Cells[1, 1] = "Permeability Simulation Results";
                    worksheet.Cells[1, 1].Font.Size = 14;
                    worksheet.Cells[1, 1].Font.Bold = true;
                    worksheet.Range["A1:D1"].Merge();

                    // Add simulation parameters
                    int row = 3;

                    // Format header for simulation parameters
                    worksheet.Cells[row, 1] = "Simulation Parameters";
                    worksheet.Cells[row, 1].Font.Bold = true;
                    worksheet.Range[$"A{row}:D{row}"].Merge();
                    worksheet.Range[$"A{row}:D{row}"].Interior.Color =
                        System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightGray);
                    row++;

                    AddStatistic(worksheet, ref row, "Date", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    AddStatistic(worksheet, ref row, "Flow Axis", permeabilityResult.FlowAxis.ToString());
                    AddStatistic(worksheet, ref row, "Fluid Viscosity (Pa·s)", permeabilityResult.Viscosity);
                    AddStatistic(worksheet, ref row, "Input Pressure (Pa)", permeabilityResult.InputPressure);
                    AddStatistic(worksheet, ref row, "Output Pressure (Pa)", permeabilityResult.OutputPressure);
                    AddStatistic(worksheet, ref row, "Pressure Differential (Pa)",
                        permeabilityResult.InputPressure - permeabilityResult.OutputPressure);
                    AddStatistic(worksheet, ref row, "Total Flow Rate (m³/s)", permeabilityResult.TotalFlowRate);
                    AddStatistic(worksheet, ref row, "Model Length (m)", permeabilityResult.ModelLength);
                    AddStatistic(worksheet, ref row, "Model Area (m²)", permeabilityResult.ModelArea);

                    // Add empty row for spacing
                    row++;

                    // Format header for tortuosity section
                    worksheet.Cells[row, 1] = "Tortuosity Information";
                    worksheet.Cells[row, 1].Font.Bold = true;
                    worksheet.Range[$"A{row}:D{row}"].Merge();
                    worksheet.Range[$"A{row}:D{row}"].Interior.Color =
                        System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightGray);
                    row++;

                    // Add tortuosity with highlighting
                    worksheet.Cells[row, 1] = "Tortuosity Factor (τ)";
                    worksheet.Cells[row, 2] = permeabilityResult.Tortuosity;
                    worksheet.Range[$"A{row}:B{row}"].Interior.Color =
                        System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightYellow);
                    worksheet.Range[$"A{row}:B{row}"].Font.Bold = true;
                    row++;

                    worksheet.Cells[row, 1] = "Correction Method";
                    worksheet.Cells[row, 2] = "Kozeny-Carman: k' = k/τ²";
                    worksheet.Cells[row, 1].Font.Italic = true;
                    worksheet.Cells[row, 2].Font.Italic = true;
                    row++;

                    worksheet.Cells[row, 1] = "Correction Factor (1/τ²)";
                    worksheet.Cells[row, 2] = 1.0 / (permeabilityResult.Tortuosity * permeabilityResult.Tortuosity);
                    worksheet.Cells[row, 2].NumberFormat = "0.0000";
                    row++;

                    // Add empty row for spacing
                    row++;

                    // Format header for model info
                    worksheet.Cells[row, 1] = "Model Information";
                    worksheet.Cells[row, 1].Font.Bold = true;
                    worksheet.Range[$"A{row}:D{row}"].Merge();
                    worksheet.Range[$"A{row}:D{row}"].Interior.Color =
                        System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightGray);
                    row++;

                    // Add model statistics
                    AddStatistic(worksheet, ref row, "Number of Pores", permeabilityResult.Model.Pores.Count);
                    AddStatistic(worksheet, ref row, "Number of Throats", permeabilityResult.Model.Throats.Count);
                    AddStatistic(worksheet, ref row, "Porosity", permeabilityResult.Model.Porosity.ToString("P2"));
                    AddStatistic(worksheet, ref row, "Inlet Pores", permeabilityResult.InletPores.Count);
                    AddStatistic(worksheet, ref row, "Outlet Pores", permeabilityResult.OutletPores.Count);

                    // Auto-fit columns
                    worksheet.Columns.AutoFit();

                    // ==========================================================
                    // Worksheet 2: Method Comparison
                    // ==========================================================
                    progressLabel.Text = "Creating method comparison sheet...";
                    Application.DoEvents();

                    worksheet = workbook.Worksheets[2];
                    worksheet.Name = "Method Comparison";

                    // Create a title
                    worksheet.Cells[1, 1] = "Permeability Calculation Method Comparison";
                    worksheet.Cells[1, 1].Font.Size = 14;
                    worksheet.Cells[1, 1].Font.Bold = true;
                    worksheet.Range["A1:G1"].Merge();

                    // Create headers for the methods table
                    row = 3;
                    worksheet.Cells[row, 1] = "Calculation Method";
                    worksheet.Cells[row, 2] = "Raw Permeability (Darcy)";
                    worksheet.Cells[row, 3] = "Raw Permeability (mD)";
                    worksheet.Cells[row, 4] = "Tortuosity Factor";
                    worksheet.Cells[row, 5] = "Corrected Permeability (Darcy)";
                    worksheet.Cells[row, 6] = "Corrected Permeability (mD)";
                    worksheet.Cells[row, 7] = "Correction (%)";

                    // Format header row
                    var headerRange = worksheet.Range[$"A{row}:G{row}"];
                    headerRange.Font.Bold = true;
                    headerRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightGray);
                    row++;

                    // Add data for each method
                    if (permeabilityResult.UsedDarcyMethod)
                    {
                        worksheet.Cells[row, 1] = "Darcy's Law";
                        worksheet.Cells[row, 2] = permeabilityResult.PermeabilityDarcy;
                        worksheet.Cells[row, 3] = permeabilityResult.PermeabilityMilliDarcy;
                        worksheet.Cells[row, 4] = permeabilityResult.Tortuosity;
                        worksheet.Cells[row, 5] = permeabilityResult.CorrectedPermeabilityDarcy;
                        worksheet.Cells[row, 6] = permeabilityResult.CorrectedPermeabilityDarcy * 1000;

                        // Calculate and format percentage difference
                        double percentDiff = ((permeabilityResult.CorrectedPermeabilityDarcy / permeabilityResult.PermeabilityDarcy) - 1.0) * 100;
                        worksheet.Cells[row, 7] = percentDiff;
                        worksheet.Cells[row, 7].NumberFormat = "+0.00%;-0.00%;0.00%";

                        // Format the row
                        worksheet.Range[$"A{row}:G{row}"].Interior.Color =
                            System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.FromArgb(235, 245, 251));
                        row++;
                    }

                    if (permeabilityResult.UsedLatticeBoltzmannMethod)
                    {
                        worksheet.Cells[row, 1] = "Lattice Boltzmann Method";
                        worksheet.Cells[row, 2] = permeabilityResult.LatticeBoltzmannPermeabilityDarcy;
                        worksheet.Cells[row, 3] = permeabilityResult.LatticeBoltzmannPermeabilityMilliDarcy;
                        worksheet.Cells[row, 4] = permeabilityResult.Tortuosity;
                        worksheet.Cells[row, 5] = permeabilityResult.CorrectedLatticeBoltzmannPermeabilityDarcy;
                        worksheet.Cells[row, 6] = permeabilityResult.CorrectedLatticeBoltzmannPermeabilityDarcy * 1000;

                        // Calculate and format percentage difference
                        double percentDiff = ((permeabilityResult.CorrectedLatticeBoltzmannPermeabilityDarcy /
                            permeabilityResult.LatticeBoltzmannPermeabilityDarcy) - 1.0) * 100;
                        worksheet.Cells[row, 7] = percentDiff;
                        worksheet.Cells[row, 7].NumberFormat = "+0.00%;-0.00%;0.00%";

                        // Format the row
                        worksheet.Range[$"A{row}:G{row}"].Interior.Color =
                            System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.FromArgb(240, 250, 255));
                        row++;
                    }

                    if (permeabilityResult.UsedNavierStokesMethod)
                    {
                        worksheet.Cells[row, 1] = "Navier-Stokes Method";
                        worksheet.Cells[row, 2] = permeabilityResult.NavierStokesPermeabilityDarcy;
                        worksheet.Cells[row, 3] = permeabilityResult.NavierStokesPermeabilityMilliDarcy;
                        worksheet.Cells[row, 4] = permeabilityResult.Tortuosity;
                        worksheet.Cells[row, 5] = permeabilityResult.CorrectedNavierStokesPermeabilityDarcy;
                        worksheet.Cells[row, 6] = permeabilityResult.CorrectedNavierStokesPermeabilityDarcy * 1000;

                        // Calculate and format percentage difference
                        double percentDiff = ((permeabilityResult.CorrectedNavierStokesPermeabilityDarcy /
                            permeabilityResult.NavierStokesPermeabilityDarcy) - 1.0) * 100;
                        worksheet.Cells[row, 7] = percentDiff;
                        worksheet.Cells[row, 7].NumberFormat = "+0.00%;-0.00%;0.00%";

                        // Format the row
                        worksheet.Range[$"A{row}:G{row}"].Interior.Color =
                            System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.FromArgb(240, 255, 240));
                        row++;
                    }

                    // Add Kozeny-Carman results for validation
                    if (permeabilityResult.KozenyCarmanPermeabilityDarcy > 0)
                    {
                        worksheet.Cells[row, 1] = "Kozeny-Carman Method (Reference)";
                        worksheet.Cells[row, 2] = permeabilityResult.KozenyCarmanPermeabilityDarcy;
                        worksheet.Cells[row, 3] = permeabilityResult.KozenyCarmanPermeabilityMilliDarcy;
                        worksheet.Cells[row, 4] = permeabilityResult.Tortuosity;
                        worksheet.Cells[row, 5] = permeabilityResult.CorrectedKozenyCarmanPermeabilityDarcy;
                        worksheet.Cells[row, 6] = permeabilityResult.CorrectedKozenyCarmanPermeabilityDarcy * 1000;

                        // Calculate and format percentage difference
                        double percentDiff = ((permeabilityResult.CorrectedKozenyCarmanPermeabilityDarcy /
                            permeabilityResult.KozenyCarmanPermeabilityDarcy) - 1.0) * 100;
                        worksheet.Cells[row, 7] = percentDiff;
                        worksheet.Cells[row, 7].NumberFormat = "+0.00%;-0.00%;0.00%";

                        // Format the row (subtle gray background)
                        worksheet.Range[$"A{row}:G{row}"].Interior.Color =
                            System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.FromArgb(245, 245, 245));
                        row++;
                    }

                    // Add an explanation of the methods
                    row += 2; // Add some space

                    worksheet.Cells[row, 1] = "Method Descriptions:";
                    worksheet.Cells[row, 1].Font.Bold = true;
                    worksheet.Range[$"A{row}:G{row}"].Merge();
                    row++;

                    worksheet.Cells[row, 1] = "Darcy's Law:";
                    worksheet.Cells[row, 1].Font.Bold = true;
                    worksheet.Cells[row, 2] = "Based on Hagen-Poiseuille flow through pore throats (k = (Q * μ * L) / (A * ΔP))";
                    worksheet.Range[$"B{row}:G{row}"].Merge();
                    row++;

                    worksheet.Cells[row, 1] = "Lattice Boltzmann:";
                    worksheet.Cells[row, 1].Font.Bold = true;
                    worksheet.Cells[row, 2] = "Computational fluid dynamics approach using mesoscopic particle distributions";
                    worksheet.Range[$"B{row}:G{row}"].Merge();
                    row++;

                    worksheet.Cells[row, 1] = "Navier-Stokes:";
                    worksheet.Cells[row, 1].Font.Bold = true;
                    worksheet.Cells[row, 2] = "Includes non-Darcy (inertial) flow effects at higher Reynolds numbers";
                    worksheet.Range[$"B{row}:G{row}"].Merge();
                    row++;

                    worksheet.Cells[row, 1] = "Kozeny-Carman:";
                    worksheet.Cells[row, 1].Font.Bold = true;
                    worksheet.Cells[row, 2] = "Empirical model relating permeability to porosity and specific surface area";
                    worksheet.Range[$"B{row}:G{row}"].Merge();
                    row++;

                    // Auto-fit columns and add filter
                    worksheet.Columns.AutoFit();
                    headerRange.AutoFilter();

                    // ==========================================================
                    // Worksheet 3: Pressure Field
                    // ==========================================================
                    progressLabel.Text = "Exporting pressure field data...";
                    Application.DoEvents();

                    worksheet = workbook.Worksheets[3];
                    worksheet.Name = "Pressure Field";

                    // Create a title
                    worksheet.Cells[1, 1] = "Pressure Field Data by Calculation Method";
                    worksheet.Cells[1, 1].Font.Size = 14;
                    worksheet.Cells[1, 1].Font.Bold = true;
                    worksheet.Range["A1:H1"].Merge();
                    row = 3;

                    // Create a section for each method's pressure field
                    if (permeabilityResult.UsedDarcyMethod && permeabilityResult.PressureField != null)
                    {
                        // Darcy method header
                        worksheet.Cells[row, 1] = "Darcy's Law Pressure Field";
                        worksheet.Cells[row, 1].Font.Bold = true;
                        worksheet.Range[$"A{row}:H{row}"].Merge();
                        worksheet.Range[$"A{row}:H{row}"].Interior.Color =
                            System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightGray);
                        row++;

                        // Add headers
                        worksheet.Cells[row, 1] = "Pore ID";
                        worksheet.Cells[row, 2] = "Pressure (Pa)";
                        worksheet.Cells[row, 3] = "Is Inlet";
                        worksheet.Cells[row, 4] = "Is Outlet";
                        worksheet.Cells[row, 5] = "X (μm)";
                        worksheet.Cells[row, 6] = "Y (μm)";
                        worksheet.Cells[row, 7] = "Z (μm)";
                        worksheet.Cells[row, 8] = "Radius (μm)";

                        // Format headers
                        headerRange = worksheet.Range[$"A{row}:H{row}"];
                        headerRange.Font.Bold = true;
                        headerRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightBlue);
                        row++;

                        // Add data for Darcy pressure field
                        foreach (var pore in permeabilityResult.Model.Pores)
                        {
                            bool isInlet = permeabilityResult.InletPores.Contains(pore.Id);
                            bool isOutlet = permeabilityResult.OutletPores.Contains(pore.Id);

                            if (permeabilityResult.PressureField.TryGetValue(pore.Id, out double pressure))
                            {
                                worksheet.Cells[row, 1] = pore.Id;
                                worksheet.Cells[row, 2] = pressure;
                                worksheet.Cells[row, 3] = isInlet;
                                worksheet.Cells[row, 4] = isOutlet;
                                worksheet.Cells[row, 5] = pore.Center.X;
                                worksheet.Cells[row, 6] = pore.Center.Y;
                                worksheet.Cells[row, 7] = pore.Center.Z;
                                worksheet.Cells[row, 8] = pore.Radius;

                                // Highlight inlet and outlet pores
                                if (isInlet || isOutlet)
                                {
                                    dynamic rowRange = worksheet.Range[$"A{row}:H{row}"];
                                    rowRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(
                                        isInlet ? System.Drawing.Color.LightPink : System.Drawing.Color.LightBlue);
                                }

                                row++;
                            }
                        }

                        // Add a separator row
                        row += 2;
                    }

                    // Add Lattice Boltzmann pressure field if available
                    if (permeabilityResult.UsedLatticeBoltzmannMethod && permeabilityResult.LatticeBoltzmannPressureField != null)
                    {
                        // LBM method header
                        worksheet.Cells[row, 1] = "Lattice Boltzmann Pressure Field";
                        worksheet.Cells[row, 1].Font.Bold = true;
                        worksheet.Range[$"A{row}:H{row}"].Merge();
                        worksheet.Range[$"A{row}:H{row}"].Interior.Color =
                            System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightGray);
                        row++;

                        // Add headers
                        worksheet.Cells[row, 1] = "Pore ID";
                        worksheet.Cells[row, 2] = "Pressure (Pa)";
                        worksheet.Cells[row, 3] = "Is Inlet";
                        worksheet.Cells[row, 4] = "Is Outlet";
                        worksheet.Cells[row, 5] = "X (μm)";
                        worksheet.Cells[row, 6] = "Y (μm)";
                        worksheet.Cells[row, 7] = "Z (μm)";
                        worksheet.Cells[row, 8] = "Radius (μm)";

                        // Format headers
                        headerRange = worksheet.Range[$"A{row}:H{row}"];
                        headerRange.Font.Bold = true;
                        headerRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightCyan);
                        row++;

                        // Add data for LBM pressure field
                        foreach (var pore in permeabilityResult.Model.Pores)
                        {
                            bool isInlet = permeabilityResult.InletPores.Contains(pore.Id);
                            bool isOutlet = permeabilityResult.OutletPores.Contains(pore.Id);

                            if (permeabilityResult.LatticeBoltzmannPressureField.TryGetValue(pore.Id, out double pressure))
                            {
                                worksheet.Cells[row, 1] = pore.Id;
                                worksheet.Cells[row, 2] = pressure;
                                worksheet.Cells[row, 3] = isInlet;
                                worksheet.Cells[row, 4] = isOutlet;
                                worksheet.Cells[row, 5] = pore.Center.X;
                                worksheet.Cells[row, 6] = pore.Center.Y;
                                worksheet.Cells[row, 7] = pore.Center.Z;
                                worksheet.Cells[row, 8] = pore.Radius;

                                // Highlight inlet and outlet pores
                                if (isInlet || isOutlet)
                                {
                                    dynamic rowRange = worksheet.Range[$"A{row}:H{row}"];
                                    rowRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(
                                        isInlet ? System.Drawing.Color.LightPink : System.Drawing.Color.LightBlue);
                                }

                                row++;
                            }
                        }

                        // Add a separator row
                        row += 2;
                    }

                    // Add Navier-Stokes pressure field if available
                    if (permeabilityResult.UsedNavierStokesMethod && permeabilityResult.NavierStokesPressureField != null)
                    {
                        // NS method header
                        worksheet.Cells[row, 1] = "Navier-Stokes Pressure Field";
                        worksheet.Cells[row, 1].Font.Bold = true;
                        worksheet.Range[$"A{row}:H{row}"].Merge();
                        worksheet.Range[$"A{row}:H{row}"].Interior.Color =
                            System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightGray);
                        row++;

                        // Add headers
                        worksheet.Cells[row, 1] = "Pore ID";
                        worksheet.Cells[row, 2] = "Pressure (Pa)";
                        worksheet.Cells[row, 3] = "Is Inlet";
                        worksheet.Cells[row, 4] = "Is Outlet";
                        worksheet.Cells[row, 5] = "X (μm)";
                        worksheet.Cells[row, 6] = "Y (μm)";
                        worksheet.Cells[row, 7] = "Z (μm)";
                        worksheet.Cells[row, 8] = "Radius (μm)";

                        // Format headers
                        headerRange = worksheet.Range[$"A{row}:H{row}"];
                        headerRange.Font.Bold = true;
                        headerRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightPink);
                        row++;

                        // Add data for NS pressure field
                        foreach (var pore in permeabilityResult.Model.Pores)
                        {
                            bool isInlet = permeabilityResult.InletPores.Contains(pore.Id);
                            bool isOutlet = permeabilityResult.OutletPores.Contains(pore.Id);

                            if (permeabilityResult.NavierStokesPressureField.TryGetValue(pore.Id, out double pressure))
                            {
                                worksheet.Cells[row, 1] = pore.Id;
                                worksheet.Cells[row, 2] = pressure;
                                worksheet.Cells[row, 3] = isInlet;
                                worksheet.Cells[row, 4] = isOutlet;
                                worksheet.Cells[row, 5] = pore.Center.X;
                                worksheet.Cells[row, 6] = pore.Center.Y;
                                worksheet.Cells[row, 7] = pore.Center.Z;
                                worksheet.Cells[row, 8] = pore.Radius;

                                // Highlight inlet and outlet pores
                                if (isInlet || isOutlet)
                                {
                                    dynamic rowRange = worksheet.Range[$"A{row}:H{row}"];
                                    rowRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(
                                        isInlet ? System.Drawing.Color.LightPink : System.Drawing.Color.LightBlue);
                                }

                                row++;
                            }
                        }

                        // Add a separator row
                        row += 2;
                    }

                    // Auto-fit columns on the pressure field sheet
                    worksheet.Columns.AutoFit();

                    // ==========================================================
                    // Worksheet 4: Flow Rates
                    // ==========================================================
                    progressLabel.Text = "Exporting flow rate data...";
                    Application.DoEvents();

                    worksheet = workbook.Worksheets[4];
                    worksheet.Name = "Flow Rates";

                    // Add headers
                    worksheet.Cells[1, 1] = "Throat ID";
                    worksheet.Cells[1, 2] = "Pore 1 ID";
                    worksheet.Cells[1, 3] = "Pore 2 ID";
                    worksheet.Cells[1, 4] = "Flow Rate (m³/s)";
                    worksheet.Cells[1, 5] = "Radius (μm)";
                    worksheet.Cells[1, 6] = "Length (μm)";
                    worksheet.Cells[1, 7] = "Connects Inlet/Outlet";

                    // Format headers
                    headerRange = worksheet.Range["A1:G1"];
                    headerRange.Font.Bold = true;
                    headerRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightGray);

                    // Add flow rate data
                    row = 2;
                    foreach (var throat in permeabilityResult.Model.Throats)
                    {
                        double flowRate = permeabilityResult.ThroatFlowRates.TryGetValue(throat.Id, out double fr) ? fr : 0;

                        // Check if this throat connects inlet to outlet (directly or indirectly)
                        bool connectsInletOutlet =
                            (permeabilityResult.InletPores.Contains(throat.PoreId1) && permeabilityResult.OutletPores.Contains(throat.PoreId2)) ||
                            (permeabilityResult.InletPores.Contains(throat.PoreId2) && permeabilityResult.OutletPores.Contains(throat.PoreId1));

                        worksheet.Cells[row, 1] = throat.Id;
                        worksheet.Cells[row, 2] = throat.PoreId1;
                        worksheet.Cells[row, 3] = throat.PoreId2;
                        worksheet.Cells[row, 4] = flowRate;
                        worksheet.Cells[row, 5] = throat.Radius;
                        worksheet.Cells[row, 6] = throat.Length;
                        worksheet.Cells[row, 7] = connectsInletOutlet;

                        // Highlight high flow throats
                        if (Math.Abs(flowRate) > permeabilityResult.TotalFlowRate * 0.01)
                        {
                            dynamic rowRange = worksheet.Range[$"A{row}:G{row}"];
                            rowRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightYellow);
                        }

                        row++;
                    }

                    // Auto-fit columns and add filter
                    worksheet.Columns.AutoFit();
                    headerRange.AutoFilter();

                    // ==========================================================
                    // Worksheet 5: Charts
                    // ==========================================================
                    progressLabel.Text = "Creating charts...";
                    Application.DoEvents();

                    try
                    {
                        // Create chart sheet
                        worksheet = workbook.Worksheets[5];
                        worksheet.Name = "Charts";

                        // Add chart title
                        worksheet.Cells[1, 1] = "Permeability Method Comparison";
                        worksheet.Cells[1, 1].Font.Size = 14;
                        worksheet.Cells[1, 1].Font.Bold = true;
                        worksheet.Range["A1:G1"].Merge();

                        // Create data for permeability comparison chart
                        row = 3;

                        // Headers
                        worksheet.Cells[row, 1] = "Method";
                        worksheet.Cells[row, 2] = "Raw Permeability (mD)";
                        worksheet.Cells[row, 3] = "Corrected Permeability (mD)";
                        row++;

                        // Add method data
                        int methodCount = 0;

                        if (permeabilityResult.UsedDarcyMethod)
                        {
                            worksheet.Cells[row, 1] = "Darcy's Law";
                            worksheet.Cells[row, 2] = permeabilityResult.PermeabilityMilliDarcy;
                            worksheet.Cells[row, 3] = permeabilityResult.CorrectedPermeabilityDarcy * 1000;
                            row++;
                            methodCount++;
                        }

                        if (permeabilityResult.UsedLatticeBoltzmannMethod)
                        {
                            worksheet.Cells[row, 1] = "Lattice Boltzmann";
                            worksheet.Cells[row, 2] = permeabilityResult.LatticeBoltzmannPermeabilityMilliDarcy;
                            worksheet.Cells[row, 3] = permeabilityResult.CorrectedLatticeBoltzmannPermeabilityDarcy * 1000;
                            row++;
                            methodCount++;
                        }

                        if (permeabilityResult.UsedNavierStokesMethod)
                        {
                            worksheet.Cells[row, 1] = "Navier-Stokes";
                            worksheet.Cells[row, 2] = permeabilityResult.NavierStokesPermeabilityMilliDarcy;
                            worksheet.Cells[row, 3] = permeabilityResult.CorrectedNavierStokesPermeabilityDarcy * 1000;
                            row++;
                            methodCount++;
                        }

                        // Add Kozeny-Carman as reference if available
                        if (permeabilityResult.KozenyCarmanPermeabilityDarcy > 0)
                        {
                            worksheet.Cells[row, 1] = "Kozeny-Carman";
                            worksheet.Cells[row, 2] = permeabilityResult.KozenyCarmanPermeabilityMilliDarcy;
                            worksheet.Cells[row, 3] = permeabilityResult.CorrectedKozenyCarmanPermeabilityDarcy * 1000;
                            row++;
                            methodCount++;
                        }

                        // Only create chart if we have data from at least one method
                        if (methodCount > 0)
                        {
                            // Create column chart for method comparison
                            dynamic chartObj = worksheet.ChartObjects.Add(100, 100, 600, 300);
                            dynamic chart = chartObj.Chart;

                            // Set the source data range (exclude headers)
                            var dataRange = worksheet.Range[$"A4:C{3 + methodCount}"];
                            chart.SetSourceData(dataRange);

                            // Set chart type to column
                            chart.ChartType = 51; // xlColumnClustered

                            // Add title and labels
                            chart.HasTitle = true;
                            chart.ChartTitle.Text = "Permeability by Calculation Method";

                            chart.Axes(1).HasTitle = true; // x-axis
                            chart.Axes(1).AxisTitle.Text = "Calculation Method";

                            chart.Axes(2).HasTitle = true; // y-axis
                            chart.Axes(2).AxisTitle.Text = "Permeability (mD)";

                            // Add data labels
                            chart.SeriesCollection(1).HasDataLabels = true;
                            chart.SeriesCollection(2).HasDataLabels = true;
                            chart.SeriesCollection(1).DataLabels.ShowValue = true;
                            chart.SeriesCollection(2).DataLabels.ShowValue = true;
                            chart.SeriesCollection(1).DataLabels.NumberFormat = "0.00";
                            chart.SeriesCollection(2).DataLabels.NumberFormat = "0.00";

                            // Create a legend
                            chart.HasLegend = true;
                            chart.Legend.Position = 2; // xlBottom = 2
                        }

                        // Create tortuosity explanation diagram
                        row += 3; // Add some space

                        worksheet.Cells[row, 1] = "Tortuosity Effect on Permeability";
                        worksheet.Cells[row, 1].Font.Bold = true;
                        worksheet.Range[$"A{row}:G{row}"].Merge();
                        worksheet.Range[$"A{row}:G{row}"].Interior.Color =
                            System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightGray);
                        row++;

                        // Insert diagram explaining tortuosity and permeability
                        row++; // Space for diagram

                        // Add tortuosity explanation text
                        worksheet.Cells[row, 1] = "Tortuosity (τ) Impact:";
                        worksheet.Cells[row, 1].Font.Bold = true;
                        worksheet.Range[$"A{row}:A{row + 5}"].Font.Bold = true;
                        row++;
                        worksheet.Cells[row, 1] = "Definition:";
                        worksheet.Cells[row, 2] = "Tortuosity measures the ratio of actual flow path length to straight-line distance";
                        worksheet.Range[$"B{row}:G{row}"].Merge();
                        row++;
                        worksheet.Cells[row, 1] = "Formula:";
                        worksheet.Cells[row, 2] = "τ = (Le/L)";
                        worksheet.Range[$"B{row}:G{row}"].Merge();
                        row++;
                        worksheet.Cells[row, 1] = "Correction:";
                        worksheet.Cells[row, 2] = "Permeability is reduced by factor of 1/τ² (Kozeny-Carman relation)";
                        worksheet.Range[$"B{row}:G{row}"].Merge();
                        row++;
                        worksheet.Cells[row, 1] = "This sample:";
                        worksheet.Cells[row, 2] = $"τ = {permeabilityResult.Tortuosity:F2}, Correction Factor = {1.0 / (permeabilityResult.Tortuosity * permeabilityResult.Tortuosity):F4}";
                        worksheet.Range[$"B{row}:G{row}"].Merge();
                        worksheet.Range[$"B{row}:G{row}"].Font.Bold = true;
                        worksheet.Range[$"B{row}:G{row}"].Interior.Color =
                            System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightYellow);

                        // Auto-fit columns
                        worksheet.Columns.AutoFit();
                    }
                    catch (Exception ex)
                    {
                        // If chart creation fails, log error and continue
                        Logger.Log($"[PoreNetworkModelingForm] Error creating permeability charts: {ex.Message}");
                        // We don't want to stop the export if just the chart fails
                    }

                    // Make Method Comparison sheet active
                    workbook.Worksheets[2].Activate();

                    // Save workbook to specified file
                    progressLabel.Text = "Saving Excel file...";
                    Application.DoEvents();

                    // Save based on extension (.xlsx or .xls)
                    if (Path.GetExtension(filename).ToLower() == ".xlsx")
                    {
                        workbook.SaveAs(filename, 51); // xlOpenXMLWorkbook (without macro's in 2007-2016, xlsx)
                    }
                    else
                    {
                        workbook.SaveAs(filename, 56); // xlExcel8 (97-2003 format, xls)
                    }

                    // Close progress dialog
                    progressDialog.Close();
                }

                // Log success
                Logger.Log($"[PoreNetworkModelingForm] Successfully exported permeability to Excel: {filename}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[PoreNetworkModelingForm] Excel export error: {ex.Message}\n{ex.StackTrace}");
                throw new Exception($"Excel export failed: {ex.Message}", ex);
            }
            finally
            {
                // Clean up COM objects to prevent memory leaks
                if (worksheet != null)
                {
                    Marshal.ReleaseComObject(worksheet);
                    worksheet = null;
                }

                if (workbook != null)
                {
                    workbook.Close(false);
                    Marshal.ReleaseComObject(workbook);
                    workbook = null;
                }

                if (excel != null)
                {
                    excel.Quit();
                    Marshal.ReleaseComObject(excel);
                    excel = null;
                }

                // Force garbage collection to release COM objects
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        private void ExportPermeabilityResults(object sender, EventArgs e)
        {
            if (permeabilityResult == null)
            {
                MessageBox.Show("No permeability results to export.",
                    "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (SaveFileDialog saveDialog = new SaveFileDialog())
            {
                saveDialog.Filter = "Excel files (*.xlsx)|*.xlsx|Excel 97-2003 files (*.xls)|*.xls|CSV files (*.csv)|*.csv";
                saveDialog.Title = "Export Permeability Results";
                saveDialog.DefaultExt = "xlsx";

                if (saveDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        string extension = Path.GetExtension(saveDialog.FileName).ToLower();

                        if (extension == ".csv")
                        {
                            ExportPermeabilityCsv(saveDialog.FileName);
                        }
                        else if (extension == ".xlsx" || extension == ".xls")
                        {
                            ExportPermeabilityToExcel(saveDialog.FileName);
                        }

                        statusLabel.Text = "Permeability results exported successfully";
                        MessageBox.Show("Permeability results exported successfully", "Export Complete",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error exporting permeability results: {ex.Message}",
                            "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Logger.Log($"[PoreNetworkModelingForm] Error exporting permeability: {ex.Message}\n{ex.StackTrace}");
                    }
                }
            }
        }
        private void ExportPermeabilityCsv(string filename)
        {
            using (StreamWriter writer = new StreamWriter(filename))
            {
                // Write simulation parameters
                writer.WriteLine("# Permeability Simulation Results");
                writer.WriteLine($"Date,{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine($"Flow Axis,{permeabilityResult.FlowAxis}");
                writer.WriteLine($"Fluid Viscosity (Pa·s),{permeabilityResult.Viscosity:G8}");
                writer.WriteLine($"Input Pressure (Pa),{permeabilityResult.InputPressure:F2}");
                writer.WriteLine($"Output Pressure (Pa),{permeabilityResult.OutputPressure:F2}");
                writer.WriteLine($"Tortuosity,{permeabilityResult.Tortuosity:G8}");
                writer.WriteLine($"Total Flow Rate (m³/s),{permeabilityResult.TotalFlowRate:G8}");
                writer.WriteLine($"Model Length (m),{permeabilityResult.ModelLength:G8}");
                writer.WriteLine($"Model Area (m²),{permeabilityResult.ModelArea:G8}");
                writer.WriteLine();

                // Write calculation method results
                writer.WriteLine("# Permeability Results by Method");
                writer.WriteLine("Method,Raw Permeability (Darcy),Raw Permeability (mD),Corrected Permeability (Darcy),Corrected Permeability (mD)");

                if (permeabilityResult.UsedDarcyMethod)
                {
                    writer.WriteLine($"Darcy's Law,{permeabilityResult.PermeabilityDarcy:G8},{permeabilityResult.PermeabilityMilliDarcy:G8}," +
                                   $"{permeabilityResult.CorrectedPermeabilityDarcy:G8},{permeabilityResult.CorrectedPermeabilityDarcy * 1000:G8}");
                }

                if (permeabilityResult.UsedLatticeBoltzmannMethod)
                {
                    writer.WriteLine($"Lattice Boltzmann Method,{permeabilityResult.LatticeBoltzmannPermeabilityDarcy:G8},{permeabilityResult.LatticeBoltzmannPermeabilityMilliDarcy:G8}," +
                                   $"{permeabilityResult.CorrectedLatticeBoltzmannPermeabilityDarcy:G8},{permeabilityResult.CorrectedLatticeBoltzmannPermeabilityDarcy * 1000:G8}");
                }

                if (permeabilityResult.UsedNavierStokesMethod)
                {
                    writer.WriteLine($"Navier-Stokes Method,{permeabilityResult.NavierStokesPermeabilityDarcy:G8},{permeabilityResult.NavierStokesPermeabilityMilliDarcy:G8}," +
                                   $"{permeabilityResult.CorrectedNavierStokesPermeabilityDarcy:G8},{permeabilityResult.CorrectedNavierStokesPermeabilityDarcy * 1000:G8}");
                }

                if (permeabilityResult.KozenyCarmanPermeabilityDarcy > 0)
                {
                    writer.WriteLine($"Kozeny-Carman Method (Reference),{permeabilityResult.KozenyCarmanPermeabilityDarcy:G8},{permeabilityResult.KozenyCarmanPermeabilityMilliDarcy:G8}," +
                                   $"{permeabilityResult.CorrectedKozenyCarmanPermeabilityDarcy:G8},{permeabilityResult.CorrectedKozenyCarmanPermeabilityDarcy * 1000:G8}");
                }

                writer.WriteLine();

                // Write pressure fields for each method
                if (permeabilityResult.UsedDarcyMethod && permeabilityResult.PressureField != null && permeabilityResult.PressureField.Count > 0)
                {
                    writer.WriteLine("# Darcy's Law Pressure Field");
                    writer.WriteLine("Pore ID,Pressure (Pa),Is Inlet,Is Outlet,X (µm),Y (µm),Z (µm),Radius (µm)");

                    foreach (var pore in permeabilityResult.Model.Pores)
                    {
                        bool isInlet = permeabilityResult.InletPores.Contains(pore.Id);
                        bool isOutlet = permeabilityResult.OutletPores.Contains(pore.Id);

                        if (permeabilityResult.PressureField.TryGetValue(pore.Id, out double pressure))
                        {
                            writer.WriteLine($"{pore.Id},{pressure:G8},{isInlet},{isOutlet},{pore.Center.X:F2},{pore.Center.Y:F2},{pore.Center.Z:F2},{pore.Radius:F2}");
                        }
                    }
                    writer.WriteLine();
                }

                // Write Lattice Boltzmann pressure field
                if (permeabilityResult.UsedLatticeBoltzmannMethod && permeabilityResult.LatticeBoltzmannPressureField != null && permeabilityResult.LatticeBoltzmannPressureField.Count > 0)
                {
                    writer.WriteLine("# Lattice Boltzmann Pressure Field");
                    writer.WriteLine("Pore ID,Pressure (Pa),Is Inlet,Is Outlet,X (µm),Y (µm),Z (µm),Radius (µm)");

                    foreach (var pore in permeabilityResult.Model.Pores)
                    {
                        bool isInlet = permeabilityResult.InletPores.Contains(pore.Id);
                        bool isOutlet = permeabilityResult.OutletPores.Contains(pore.Id);

                        if (permeabilityResult.LatticeBoltzmannPressureField.TryGetValue(pore.Id, out double pressure))
                        {
                            writer.WriteLine($"{pore.Id},{pressure:G8},{isInlet},{isOutlet},{pore.Center.X:F2},{pore.Center.Y:F2},{pore.Center.Z:F2},{pore.Radius:F2}");
                        }
                    }
                    writer.WriteLine();
                }

                // Write Navier-Stokes pressure field
                if (permeabilityResult.UsedNavierStokesMethod && permeabilityResult.NavierStokesPressureField != null && permeabilityResult.NavierStokesPressureField.Count > 0)
                {
                    writer.WriteLine("# Navier-Stokes Pressure Field");
                    writer.WriteLine("Pore ID,Pressure (Pa),Is Inlet,Is Outlet,X (µm),Y (µm),Z (µm),Radius (µm)");

                    foreach (var pore in permeabilityResult.Model.Pores)
                    {
                        bool isInlet = permeabilityResult.InletPores.Contains(pore.Id);
                        bool isOutlet = permeabilityResult.OutletPores.Contains(pore.Id);

                        if (permeabilityResult.NavierStokesPressureField.TryGetValue(pore.Id, out double pressure))
                        {
                            writer.WriteLine($"{pore.Id},{pressure:G8},{isInlet},{isOutlet},{pore.Center.X:F2},{pore.Center.Y:F2},{pore.Center.Z:F2},{pore.Radius:F2}");
                        }
                    }
                    writer.WriteLine();
                }

                // Write throat flow rates
                writer.WriteLine("# Throat Flow Rates");
                writer.WriteLine("Throat ID,Pore 1 ID,Pore 2 ID,Flow Rate (m³/s),Radius (µm),Length (µm)");
                foreach (var throat in permeabilityResult.Model.Throats)
                {
                    double flowRate = permeabilityResult.ThroatFlowRates.TryGetValue(throat.Id, out double fr) ? fr : 0;
                    writer.WriteLine($"{throat.Id},{throat.PoreId1},{throat.PoreId2},{flowRate:G8},{throat.Radius:F4},{throat.Length:F4}");
                }
                writer.WriteLine();

                // Write inlet/outlet pores
                writer.WriteLine("# Boundary Pores");
                writer.WriteLine("Type,Pore ID");
                foreach (var poreId in permeabilityResult.InletPores)
                {
                    writer.WriteLine($"Inlet,{poreId}");
                }
                foreach (var poreId in permeabilityResult.OutletPores)
                {
                    writer.WriteLine($"Outlet,{poreId}");
                }

                // Write tortuosity correction information
                writer.WriteLine();
                writer.WriteLine("# Tortuosity Correction Information");
                writer.WriteLine("Description,Value");
                writer.WriteLine($"Tortuosity (τ),{permeabilityResult.Tortuosity:F4}");
                writer.WriteLine("Correction Method,Kozeny-Carman: k' = k/τ²");
                writer.WriteLine($"Correction Factor (1/τ²),{1.0 / (permeabilityResult.Tortuosity * permeabilityResult.Tortuosity):F4}");
            }
        }

        
        private void OpenPoreConnectivityDialog(object sender, EventArgs e)
        {
            using (var dialog = new PoreConnectivityDialog())
            {
                // Set initial values
                dialog.MaxThroatLengthFactor = maxThroatLengthFactor;
                dialog.MinOverlapFactor = minOverlapFactor;
                dialog.EnforceFlowPath = enforceFlowPath;

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    // Save the settings after dialog closes
                    maxThroatLengthFactor = dialog.MaxThroatLengthFactor;
                    minOverlapFactor = dialog.MinOverlapFactor;
                    enforceFlowPath = dialog.EnforceFlowPath;

                    // Update status
                    statusLabel.Text = $"Pore connectivity settings updated. Max Length: {maxThroatLengthFactor:F1}×, Min Overlap: {minOverlapFactor:F2}";
                }
            }
        }

        private void DrawPressureScaleBar(Graphics g, Rectangle rect, double maxPressure, double minPressure)
        {
            // Draw gradient bar
            int width = rect.Width;
            int height = rect.Height;

            // Create gradient brush from red to blue
            using (System.Drawing.Drawing2D.LinearGradientBrush gradientBrush =
                new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Point(rect.X, rect.Y),
                    new Point(rect.X + width, rect.Y),
                    Color.Red,   // High pressure
                    Color.Blue)) // Low pressure
            {
                // Create custom color blend for red-green-blue gradient
                System.Drawing.Drawing2D.ColorBlend colorBlend = new System.Drawing.Drawing2D.ColorBlend(3);
                colorBlend.Colors = new Color[] { Color.Red, Color.Green, Color.Blue };
                colorBlend.Positions = new float[] { 0.0f, 0.5f, 1.0f };
                gradientBrush.InterpolationColors = colorBlend;

                // Draw gradient rectangle
                g.FillRectangle(gradientBrush, rect.X, rect.Y, width, height);
            }

            // Draw border around gradient
            g.DrawRectangle(Pens.White, rect.X, rect.Y, width, height);

            // Draw tick marks and labels
            int numTicks = 5;
            using (Font font = new Font("Arial", 8))
            {
                // Calculate label width to prevent overlap
                string sampleLabel = $"{maxPressure:F0} Pa";
                SizeF labelSize = g.MeasureString(sampleLabel, font);

                // Ensure we never draw labels too close together
                int minLabelSpacing = (int)(labelSize.Width * 1.2);
                int tickSpacing = Math.Max(width / (numTicks - 1), minLabelSpacing);

                // Recalculate numTicks if needed to avoid overlap
                if (tickSpacing > width / (numTicks - 1))
                {
                    numTicks = Math.Max(2, (int)(width / tickSpacing) + 1);
                }

                for (int i = 0; i < numTicks; i++)
                {
                    float x = rect.X + (width * i / (numTicks - 1));

                    // Draw tick mark
                    g.DrawLine(Pens.White, x, rect.Y + height, x, rect.Y + height + 5);

                    // Calculate pressure value for this position
                    double pressure = maxPressure - (i * (maxPressure - minPressure) / (numTicks - 1));
                    string label = $"{pressure:F0} Pa";

                    // Measure this specific label
                    SizeF thisLabelSize = g.MeasureString(label, font);

                    // Center the label under the tick, ensuring it doesn't go off the edges
                    float labelX = Math.Max(rect.X, Math.Min(rect.X + width - thisLabelSize.Width,
                        x - thisLabelSize.Width / 2));

                    // Draw the label
                    g.DrawString(label, font, Brushes.White, labelX, rect.Y + height + 6);
                }
            }

            // Draw title above the scale bar
            Font titleFont = new Font("Arial", 8, FontStyle.Bold);
            string titleText = "Pressure Gradient";
            SizeF titleSize = g.MeasureString(titleText, titleFont);
            g.DrawString(titleText, titleFont, Brushes.White,
                rect.X + (width / 2) - (titleSize.Width / 2), rect.Y - titleSize.Height - 2);
        }
    }
}