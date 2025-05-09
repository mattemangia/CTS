using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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

        // 3d rotation
        private float rotationX = 30.0f;

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
            // TOP RIBBON PANEL - SIMPLIFIED LAYOUT WITH FEWER, WIDER CONTROLS
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

            exportPermeabilityButton = new Button
            {
                Text = "Export Results",
                Location = new Point(15, 55),
                Width = 140,
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
            };
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
                        using (Bitmap screenshot = new Bitmap(networkPictureBox.Image))
                        {
                            // Save the image in the format specified by the file extension
                            string extension = Path.GetExtension(saveDialog.FileName).ToLower();
                            ImageFormat format = ImageFormat.Png; // Default

                            if (extension == ".jpg" || extension == ".jpeg")
                                format = ImageFormat.Jpeg;
                            else if (extension == ".bmp")
                                format = ImageFormat.Bmp;

                            screenshot.Save(saveDialog.FileName, format);

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
                    if (dialog.ShowDialog() != DialogResult.OK)
                        return;

                    // Show progress
                    progressBar.Value = 0;
                    statusLabel.Text = "Simulating permeability...";

                    // Run simulation
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

                    // Update status
                    statusLabel.Text = $"Permeability: {permeabilityResult.PermeabilityDarcy:F3} Darcy ({permeabilityResult.PermeabilityMilliDarcy:F1} mD)";

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

            // Create layout matching the 3D Network View tab
            Panel visualizationPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.Black
            };

            // Control panel at top (similar to 3D Network View)
            Panel controlPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 40,
                BackColor = Color.FromArgb(50, 50, 50)
            };

            // Add rotation label
            Label rotationLabel = new Label
            {
                Text = "Rotation:",
                Location = new Point(10, 12),
                ForeColor = Color.White,
                AutoSize = true
            };
            controlPanel.Controls.Add(rotationLabel);

            // Add reset view button
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
                panOffsetX = 0.0f; // Reset panning offset X
                panOffsetY = 0.0f; // Reset panning offset Y
                permeabilityPictureBox.Image = RenderPressureField();
            };
            controlPanel.Controls.Add(resetViewButton);

            // Add screenshot button in the control panel (not in the top ribbon)
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
            screenshotButton.Click += SavePermeabilityScreenshot;
            controlPanel.Controls.Add(screenshotButton);

            visualizationPanel.Controls.Add(controlPanel);

            // Create the results panel at the top for key permeability info
            Panel resultsPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 90, // Increased height to accommodate tortuosity info
                BackColor = Color.FromArgb(30, 30, 30),
                Padding = new Padding(5)
            };

            // Create a table layout for the results with 3 columns, 3 rows (added a row for tortuosity)
            TableLayoutPanel tableLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 3, // Increased to 3 rows
                BackColor = Color.Transparent
            };

            // Column styles - evenly distribute space
            tableLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            tableLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            tableLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));

            // Row styles
            tableLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
            tableLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
            tableLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F)); // Added third row

            // Add labels with permeability information
            // Row 1
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

            // Row 2
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
                Text = $"Permeability: {permeabilityResult.PermeabilityDarcy:F3} Darcy ({permeabilityResult.PermeabilityMilliDarcy:F1} mD)",
                ForeColor = Color.White,
                AutoSize = true,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Anchor = AnchorStyles.Left
            }, 1, 1);

            tableLayout.Controls.Add(new Label
            {
                Text = $"Sample: L={permeabilityResult.ModelLength * 1000:F2} mm, A={permeabilityResult.ModelArea * 1e6:F2} mm²",
                ForeColor = Color.White,
                AutoSize = true,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Anchor = AnchorStyles.Left
            }, 2, 1);

            // Row 3 - New row for tortuosity information
            tableLayout.Controls.Add(new Label
            {
                Text = $"Tortuosity: {permeabilityResult.Tortuosity:F2}",
                ForeColor = Color.Yellow, // Highlighted in yellow to stand out
                AutoSize = true,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Anchor = AnchorStyles.Left
            }, 0, 2);

            tableLayout.Controls.Add(new Label
            {
                Text = $"Corrected Permeability: {permeabilityResult.CorrectedPermeabilityDarcy:F3} Darcy " +
                       $"({permeabilityResult.CorrectedPermeabilityDarcy * 1000:F1} mD)",
                ForeColor = Color.Yellow, // Highlighted in yellow to stand out
                AutoSize = true,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Anchor = AnchorStyles.Left
            }, 1, 2);

            // Add explanation tooltip for tortuosity correction
            var correctionMethodLabel = new Label
            {
                Text = "Corrected via Kozeny-Carman: k' = k/τ²",
                ForeColor = Color.LightGray,
                AutoSize = true,
                Font = new Font("Segoe UI", 8, FontStyle.Italic),
                Anchor = AnchorStyles.Left
            };
            tableLayout.Controls.Add(correctionMethodLabel, 2, 2);

            resultsPanel.Controls.Add(tableLayout);
            visualizationPanel.Controls.Add(resultsPanel);

            // Create pressure visualization PictureBox
            permeabilityPictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.CenterImage
            };
            visualizationPanel.Controls.Add(permeabilityPictureBox);

            // Render the pressure field
            permeabilityPictureBox.Image = RenderPressureField();

            // Add mouse handling for rotation and zooming (same as the 3D network viewer)
            permeabilityPictureBox.MouseDown += (s, e) =>
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

            permeabilityPictureBox.MouseMove += (s, e) =>
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
                    permeabilityPictureBox.Image = RenderPressureField();

                    lastMousePosition = e.Location;
                }
                else if (isPanning)
                {
                    // Calculate the delta movement for panning
                    float deltaX = (e.X - lastMousePosition.X) * 0.01f;
                    float deltaY = (e.Y - lastMousePosition.Y) * 0.01f;

                    // Update pan offsets
                    panOffsetX += deltaX;
                    panOffsetY += deltaY;

                    // Render with new pan
                    permeabilityPictureBox.Image = RenderPressureField();

                    lastMousePosition = e.Location;
                }
            };

            permeabilityPictureBox.MouseUp += (s, e) =>
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

            permeabilityPictureBox.MouseWheel += (s, e) =>
            {
                // Change zoom level with mouse wheel
                float zoomFactor = 1.0f + (e.Delta > 0 ? 0.1f : -0.1f);
                viewScale *= zoomFactor;

                // Limit minimum and maximum zoom
                viewScale = Math.Max(0.2f, Math.Min(3.0f, viewScale));

                permeabilityPictureBox.Image = RenderPressureField();
            };

            // Add color legend for pressure
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
                Color color = GetPressureColor(1.0 - t);
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

            visualizationPanel.Controls.Add(legendPanel);

            // Add instructions label at the bottom (matches 3D Network View)
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

            // Add the visualization panel to the tab
            permeabilityTab.Controls.Add(visualizationPanel);

            // Enable the export button
            if (exportPermeabilityButton != null) exportPermeabilityButton.Enabled = true;

            // Update status label with tortuosity information
            statusLabel.Text = $"Permeability: {permeabilityResult.PermeabilityDarcy:F3} Darcy " +
                                $"({permeabilityResult.PermeabilityMilliDarcy:F1} mD) | " +
                                $"Tortuosity: {permeabilityResult.Tortuosity:F2} | " +
                                $"Corrected: {permeabilityResult.CorrectedPermeabilityDarcy:F3} Darcy";
        }
        private Bitmap RenderPressureField()
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

                // Find pressure range
                double minPressure = permeabilityResult.PressureField.Values.Min();
                double maxPressure = permeabilityResult.PressureField.Values.Max();
                double pressureRange = maxPressure - minPressure;

                // Project and render throats first (draw from back to front)
                var throatsWithDepth = new List<(double depth, Point p1, Point p2, float thickness, Color color)>();

                foreach (var throat in permeabilityResult.Model.Throats)
                {
                    var pore1 = permeabilityResult.Model.Pores.FirstOrDefault(p => p.Id == throat.PoreId1);
                    var pore2 = permeabilityResult.Model.Pores.FirstOrDefault(p => p.Id == throat.PoreId2);

                    if (pore1 != null && pore2 != null)
                    {
                        // Get pressure for both pores
                        if (!permeabilityResult.PressureField.TryGetValue(pore1.Id, out double pressure1))
                            pressure1 = 0;

                        if (!permeabilityResult.PressureField.TryGetValue(pore2.Id, out double pressure2))
                            pressure2 = 0;

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
                        Color throatColor = GetPressureColor(normalizedPressure);

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
                    if (!permeabilityResult.PressureField.TryGetValue(pore.Id, out double pressure))
                        pressure = 0;

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
                    Color poreColor = GetPressureColor(normalizedPressure);

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

                // Add legend title
                string axisText = permeabilityResult.FlowAxis.ToString();
                g.DrawString($"Flow Direction: {axisText}-Axis",
                    new Font("Arial", 12, FontStyle.Bold), Brushes.White, 20, 20);

                g.DrawString($"Permeability: {permeabilityResult.PermeabilityDarcy:F3} Darcy ({permeabilityResult.PermeabilityMilliDarcy:F1} mD)",
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

        private Color GetPressureColor(double normalizedPressure)
        {
            // Red (high pressure) to Blue (low pressure) gradient
            normalizedPressure = Math.Max(0, Math.Min(1, normalizedPressure));

            if (normalizedPressure < 0.5)
            {
                // Blue to green (0 to 0.5)
                double t = normalizedPressure * 2;
                int r = 0;
                int g = (int)(255 * t);
                int b = (int)(255 * (1 - t));
                return Color.FromArgb(r, g, b);
            }
            else
            {
                // Green to red (0.5 to 1)
                double t = (normalizedPressure - 0.5) * 2;
                int r = (int)(255 * t);
                int g = (int)(255 * (1 - t));
                int b = 0;
                return Color.FromArgb(r, g, b);
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
                            writer.Write(1); // Version number

                            // Write basic simulation parameters
                            writer.Write((int)permeabilityResult.FlowAxis);
                            writer.Write(permeabilityResult.Viscosity);
                            writer.Write(permeabilityResult.InputPressure);
                            writer.Write(permeabilityResult.OutputPressure);
                            writer.Write(permeabilityResult.PermeabilityDarcy);
                            writer.Write(permeabilityResult.PermeabilityMilliDarcy);
                            writer.Write(permeabilityResult.Tortuosity); // Save tortuosity
                            writer.Write(permeabilityResult.CorrectedPermeabilityDarcy); // Save corrected permeability
                            writer.Write(permeabilityResult.TotalFlowRate);
                            writer.Write(permeabilityResult.ModelLength);
                            writer.Write(permeabilityResult.ModelArea);

                            // Write inlet/outlet pores count
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

                            // Write pressure field
                            writer.Write(permeabilityResult.PressureField.Count);
                            foreach (var pair in permeabilityResult.PressureField)
                            {
                                writer.Write(pair.Key);
                                writer.Write(pair.Value);
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
                            if (version != 1)
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
                                OutputPressure = reader.ReadDouble(),
                                PermeabilityDarcy = reader.ReadDouble(),
                                PermeabilityMilliDarcy = reader.ReadDouble()
                            };

                            // Try to read tortuosity and corrected permeability
                            try
                            {
                                if (fs.Position < fs.Length - 16) // Need at least 16 bytes (for 2 doubles)
                                {
                                    permeabilityResult.Tortuosity = reader.ReadDouble();
                                    permeabilityResult.CorrectedPermeabilityDarcy = reader.ReadDouble();
                                }
                                else
                                {
                                    // If not present in file, calculate based on model tortuosity
                                    permeabilityResult.Tortuosity = networkModel.Tortuosity;
                                    permeabilityResult.CorrectedPermeabilityDarcy = permeabilityResult.PermeabilityDarcy /
                                        (permeabilityResult.Tortuosity * permeabilityResult.Tortuosity);
                                }
                            }
                            catch
                            {
                                // If reading fails, use model tortuosity and calculate corrected value
                                permeabilityResult.Tortuosity = networkModel.Tortuosity;
                                permeabilityResult.CorrectedPermeabilityDarcy = permeabilityResult.PermeabilityDarcy /
                                    (permeabilityResult.Tortuosity * permeabilityResult.Tortuosity);
                            }

                            // Continue reading the remaining data
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

                            // Read pressure field
                            int pressureCount = reader.ReadInt32();
                            permeabilityResult.PressureField = new Dictionary<int, double>(pressureCount);
                            for (int i = 0; i < pressureCount; i++)
                            {
                                int key = reader.ReadInt32();
                                double value = reader.ReadDouble();
                                permeabilityResult.PressureField[key] = value;
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

                            Logger.Log($"[PoreNetworkModelingForm] Loaded permeability results from {timestamp}, " +
                                      $"tortuosity: {permeabilityResult.Tortuosity:F2}");
                        }

                        // Update UI with loaded results
                        RenderPermeabilityResults();

                        // Switch to permeability tab
                        mainTabControl.SelectedTab = permeabilityTab;

                        // Update status
                        statusLabel.Text = $"Loaded permeability: {permeabilityResult.PermeabilityDarcy:F3} Darcy | " +
                                          $"Tortuosity: {permeabilityResult.Tortuosity:F2} | " +
                                          $"Corrected: {permeabilityResult.CorrectedPermeabilityDarcy:F3} Darcy";

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

                    // Ensure we have at least 4 worksheets
                    while (workbook.Worksheets.Count < 4)
                    {
                        workbook.Worksheets.Add();
                    }

                    // ==========================================================
                    // Worksheet 1: Summary
                    // ==========================================================
                    progressLabel.Text = "Creating summary sheet...";
                    Application.DoEvents();

                    worksheet = workbook.Worksheets[1];
                    worksheet.Name = "Permeability Summary";

                    // Create a title
                    worksheet.Cells[1, 1] = "Permeability Simulation Results";
                    worksheet.Cells[1, 1].Font.Size = 14;
                    worksheet.Cells[1, 1].Font.Bold = true;
                    worksheet.Range["A1:C1"].Merge();

                    // Add simulation parameters
                    int row = 3;

                    // Format header for simulation parameters
                    worksheet.Cells[row, 1] = "Simulation Parameters";
                    worksheet.Cells[row, 1].Font.Bold = true;
                    worksheet.Range[$"A{row}:C{row}"].Merge();
                    worksheet.Range[$"A{row}:C{row}"].Interior.Color =
                        System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightGray);
                    row++;

                    AddStatistic(worksheet, ref row, "Date", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    AddStatistic(worksheet, ref row, "Flow Axis", permeabilityResult.FlowAxis.ToString());
                    AddStatistic(worksheet, ref row, "Fluid Viscosity (Pa·s)", permeabilityResult.Viscosity);
                    AddStatistic(worksheet, ref row, "Input Pressure (Pa)", permeabilityResult.InputPressure);
                    AddStatistic(worksheet, ref row, "Output Pressure (Pa)", permeabilityResult.OutputPressure);
                    AddStatistic(worksheet, ref row, "Pressure Differential (Pa)",
                        permeabilityResult.InputPressure - permeabilityResult.OutputPressure);

                    // Add empty row for spacing
                    row++;

                    // Format header for results
                    worksheet.Cells[row, 1] = "Results";
                    worksheet.Cells[row, 1].Font.Bold = true;
                    worksheet.Range[$"A{row}:C{row}"].Merge();
                    worksheet.Range[$"A{row}:C{row}"].Interior.Color =
                        System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightGray);
                    row++;

                    // Add permeability results and highlight tortuosity-related rows
                    AddStatistic(worksheet, ref row, "Permeability (Darcy)", permeabilityResult.PermeabilityDarcy);
                    AddStatistic(worksheet, ref row, "Permeability (mD)", permeabilityResult.PermeabilityMilliDarcy);

                    // Add tortuosity with highlighting
                    worksheet.Cells[row, 1] = "Tortuosity";
                    worksheet.Cells[row, 2] = permeabilityResult.Tortuosity;
                    worksheet.Range[$"A{row}:B{row}"].Interior.Color =
                        System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightYellow);
                    worksheet.Range[$"A{row}:B{row}"].Font.Bold = true;
                    row++;

                    // Add corrected permeability with highlighting
                    worksheet.Cells[row, 1] = "Corrected Permeability (Darcy)";
                    worksheet.Cells[row, 2] = permeabilityResult.CorrectedPermeabilityDarcy;
                    worksheet.Range[$"A{row}:B{row}"].Interior.Color =
                        System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightYellow);
                    worksheet.Range[$"A{row}:B{row}"].Font.Bold = true;
                    row++;

                    worksheet.Cells[row, 1] = "Corrected Permeability (mD)";
                    worksheet.Cells[row, 2] = permeabilityResult.CorrectedPermeabilityDarcy * 1000;
                    worksheet.Range[$"A{row}:B{row}"].Interior.Color =
                        System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightYellow);
                    worksheet.Range[$"A{row}:B{row}"].Font.Bold = true;
                    row++;

                    // Add explanation of correction
                    worksheet.Cells[row, 1] = "Correction Method";
                    worksheet.Cells[row, 2] = "Kozeny-Carman: k' = k/τ²";
                    worksheet.Range[$"A{row}:B{row}"].Font.Italic = true;
                    row++;

                    // Continue with other results
                    AddStatistic(worksheet, ref row, "Total Flow Rate (m³/s)", permeabilityResult.TotalFlowRate);
                    AddStatistic(worksheet, ref row, "Model Length (m)", permeabilityResult.ModelLength);
                    AddStatistic(worksheet, ref row, "Model Area (m²)", permeabilityResult.ModelArea);

                    // Add empty row for spacing
                    row++;

                    // Format header for model info
                    worksheet.Cells[row, 1] = "Model Information";
                    worksheet.Cells[row, 1].Font.Bold = true;
                    worksheet.Range[$"A{row}:C{row}"].Merge();
                    worksheet.Range[$"A{row}:C{row}"].Interior.Color =
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
                    // Worksheet 2: Pressure Field
                    // ==========================================================
                    progressLabel.Text = "Exporting pressure field data...";
                    Application.DoEvents();

                    worksheet = workbook.Worksheets[2];
                    worksheet.Name = "Pressure Field";

                    // Add headers
                    worksheet.Cells[1, 1] = "Pore ID";
                    worksheet.Cells[1, 2] = "Pressure (Pa)";
                    worksheet.Cells[1, 3] = "Is Inlet";
                    worksheet.Cells[1, 4] = "Is Outlet";

                    // Format headers
                    dynamic headerRange = worksheet.Range("A1:D1");
                    headerRange.Font.Bold = true;
                    headerRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightGray);

                    // Add pressure field data
                    row = 2;
                    foreach (var pore in permeabilityResult.Model.Pores)
                    {
                        bool isInlet = permeabilityResult.InletPores.Contains(pore.Id);
                        bool isOutlet = permeabilityResult.OutletPores.Contains(pore.Id);
                        double pressure = permeabilityResult.PressureField.TryGetValue(pore.Id, out double p) ? p : 0;

                        worksheet.Cells[row, 1] = pore.Id;
                        worksheet.Cells[row, 2] = pressure;
                        worksheet.Cells[row, 3] = isInlet;
                        worksheet.Cells[row, 4] = isOutlet;

                        // Highlight inlet and outlet pores
                        if (isInlet || isOutlet)
                        {
                            dynamic rowRange = worksheet.Range($"A{row}:D{row}");
                            rowRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(
                                isInlet ? System.Drawing.Color.LightPink : System.Drawing.Color.LightBlue);
                        }

                        row++;
                    }

                    // Auto-fit columns and add filter
                    worksheet.Columns.AutoFit();
                    headerRange.AutoFilter();

                    // ==========================================================
                    // Worksheet 3: Flow Rates
                    // ==========================================================
                    progressLabel.Text = "Exporting flow rate data...";
                    Application.DoEvents();

                    worksheet = workbook.Worksheets[3];
                    worksheet.Name = "Flow Rates";

                    // Add headers
                    worksheet.Cells[1, 1] = "Throat ID";
                    worksheet.Cells[1, 2] = "Pore 1 ID";
                    worksheet.Cells[1, 3] = "Pore 2 ID";
                    worksheet.Cells[1, 4] = "Flow Rate (m³/s)";
                    worksheet.Cells[1, 5] = "Radius (µm)";
                    worksheet.Cells[1, 6] = "Length (µm)";

                    // Format headers
                    headerRange = worksheet.Range("A1:F1");
                    headerRange.Font.Bold = true;
                    headerRange.Interior.Color = System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightGray);

                    // Add flow rate data
                    row = 2;
                    foreach (var throat in permeabilityResult.Model.Throats)
                    {
                        double flowRate = permeabilityResult.ThroatFlowRates.TryGetValue(throat.Id, out double fr) ? fr : 0;

                        worksheet.Cells[row, 1] = throat.Id;
                        worksheet.Cells[row, 2] = throat.PoreId1;
                        worksheet.Cells[row, 3] = throat.PoreId2;
                        worksheet.Cells[row, 4] = flowRate;
                        worksheet.Cells[row, 5] = throat.Radius;
                        worksheet.Cells[row, 6] = throat.Length;

                        row++;
                    }

                    // Auto-fit columns and add filter
                    worksheet.Columns.AutoFit();
                    headerRange.AutoFilter();

                    // ==========================================================
                    // Worksheet 4: Charts
                    // ==========================================================
                    progressLabel.Text = "Creating charts...";
                    Application.DoEvents();

                    try
                    {
                        // Create chart sheet
                        worksheet = workbook.Worksheets[4];
                        worksheet.Name = "Charts";

                        // Add chart title
                        worksheet.Cells[1, 1] = "Permeability Visualization";
                        worksheet.Cells[1, 1].Font.Size = 14;
                        worksheet.Cells[1, 1].Font.Bold = true;
                        worksheet.Range["A1:G1"].Merge();

                        // Add permeability and tortuosity relationship chart
                        row = 3;
                        worksheet.Cells[row, 1] = "Permeability Summary";
                        worksheet.Cells[row, 1].Font.Bold = true;
                        worksheet.Range[$"A{row}:G{row}"].Merge();
                        worksheet.Range[$"A{row}:G{row}"].Interior.Color =
                            System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightGray);
                        row++;

                        // Create data for a simple summary chart
                        // Headers
                        worksheet.Cells[row, 1] = "Measurement";
                        worksheet.Cells[row, 2] = "Value";
                        row++;

                        // Data points
                        worksheet.Cells[row, 1] = "Raw Permeability (mD)";
                        worksheet.Cells[row, 2] = permeabilityResult.PermeabilityMilliDarcy;
                        row++;

                        worksheet.Cells[row, 1] = "Corrected Permeability (mD)";
                        worksheet.Cells[row, 2] = permeabilityResult.CorrectedPermeabilityDarcy * 1000;
                        row++;

                        // Create simple column chart
                        dynamic chartObj = worksheet.ChartObjects.Add(100, 150, 400, 250);
                        dynamic chart = chartObj.Chart;

                        // Set the source data range
                        var dataRange = worksheet.Range[$"A{row - 2}:B{row - 1}"];
                        chart.SetSourceData(dataRange);

                        // Set chart type to column
                        chart.ChartType = 51; // xlColumnClustered

                        // Add title and labels
                        chart.HasTitle = true;
                        chart.ChartTitle.Text = "Permeability Comparison";

                        // Create tortuosity explanation diagram
                        row += 2;
                        worksheet.Cells[row, 1] = "Tortuosity Explanation";
                        worksheet.Cells[row, 1].Font.Bold = true;
                        worksheet.Range[$"A{row}:G{row}"].Merge();
                        worksheet.Range[$"A{row}:G{row}"].Interior.Color =
                            System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.LightGray);
                        row++;

                        // Create simple table explaining tortuosity
                        worksheet.Cells[row, 1] = "Definition:";
                        worksheet.Cells[row, 2] = "Tortuosity (τ) measures how winding or twisted the flow paths are through the porous medium.";
                        worksheet.Range[$"B{row}:G{row}"].Merge();
                        row++;

                        worksheet.Cells[row, 1] = "Mathematical:";
                        worksheet.Cells[row, 2] = "τ = (Le/L)², where Le is actual path length and L is straight-line distance";
                        worksheet.Range[$"B{row}:G{row}"].Merge();
                        row++;

                        worksheet.Cells[row, 1] = "This Model:";
                        worksheet.Cells[row, 2] = $"τ = {permeabilityResult.Tortuosity:F2}";
                        worksheet.Range[$"B{row}:G{row}"].Merge();
                        row++;

                        worksheet.Cells[row, 1] = "Effect:";
                        worksheet.Cells[row, 2] = "Higher tortuosity reduces permeability according to the Kozeny-Carman relationship: k ∝ (ε³/S²)/τ²";
                        worksheet.Range[$"B{row}:G{row}"].Merge();
                        row++;

                        worksheet.Cells[row, 1] = "Correction:";
                        worksheet.Cells[row, 2] = $"Raw k = {permeabilityResult.PermeabilityDarcy:F3} Darcy, Corrected k = {permeabilityResult.CorrectedPermeabilityDarcy:F3} Darcy";
                        worksheet.Range[$"B{row}:G{row}"].Merge();
                        row++;

                        // Format the explanation table
                        worksheet.Range[$"A{row - 5}:A{row - 1}"].Font.Bold = true;

                        // Auto-fit columns
                        worksheet.Columns.AutoFit();
                    }
                    catch (Exception ex)
                    {
                        // If chart creation fails, log error and continue
                        Logger.Log($"[PoreNetworkModelingForm] Error creating permeability charts: {ex.Message}");
                        // We don't want to stop the export if just the chart fails
                    }

                    // Make Summary sheet active
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
                writer.WriteLine($"Date,{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")}");
                writer.WriteLine($"Flow Axis,{permeabilityResult.FlowAxis}");
                writer.WriteLine($"Fluid Viscosity (Pa·s),{permeabilityResult.Viscosity:G8}");
                writer.WriteLine($"Input Pressure (Pa),{permeabilityResult.InputPressure:F2}");
                writer.WriteLine($"Output Pressure (Pa),{permeabilityResult.OutputPressure:F2}");
                writer.WriteLine($"Permeability (Darcy),{permeabilityResult.PermeabilityDarcy:G8}");
                writer.WriteLine($"Permeability (mD),{permeabilityResult.PermeabilityMilliDarcy:G8}");
                writer.WriteLine($"Tortuosity,{permeabilityResult.Tortuosity:G8}");
                writer.WriteLine($"Corrected Permeability (Darcy),{permeabilityResult.CorrectedPermeabilityDarcy:G8}");
                writer.WriteLine($"Corrected Permeability (mD),{permeabilityResult.CorrectedPermeabilityDarcy * 1000:G8}");
                writer.WriteLine($"Total Flow Rate (m³/s),{permeabilityResult.TotalFlowRate:G8}");
                writer.WriteLine($"Model Length (m),{permeabilityResult.ModelLength:G8}");
                writer.WriteLine($"Model Area (m²),{permeabilityResult.ModelArea:G8}");
                writer.WriteLine();

                // Write pressure field
                writer.WriteLine("# Pressure Field");
                writer.WriteLine("Pore ID,Pressure (Pa),Is Inlet,Is Outlet");
                foreach (var pore in permeabilityResult.Model.Pores)
                {
                    bool isInlet = permeabilityResult.InletPores.Contains(pore.Id);
                    bool isOutlet = permeabilityResult.OutletPores.Contains(pore.Id);
                    double pressure = permeabilityResult.PressureField.TryGetValue(pore.Id, out double p) ? p : 0;
                    writer.WriteLine($"{pore.Id},{pressure:F4},{isInlet},{isOutlet}");
                }
                writer.WriteLine();

                // Write throat flow rates
                writer.WriteLine("# Throat Flow Rates");
                writer.WriteLine("Throat ID,Pore 1,Pore 2,Flow Rate (m³/s),Radius (μm),Length (μm)");
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
            }
        }

        private void SavePermeabilityScreenshot(object sender, EventArgs e)
        {
            if (permeabilityPictureBox?.Image == null)
            {
                MessageBox.Show("No permeability visualization to save.",
                    "Screenshot Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (SaveFileDialog saveDialog = new SaveFileDialog())
            {
                saveDialog.Filter = "PNG Image|*.png|JPEG Image|*.jpg|Bitmap Image|*.bmp";
                saveDialog.Title = "Save Permeability Visualization";
                saveDialog.DefaultExt = "png";

                if (saveDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        // Create a copy of the current visualization with added scalebar
                        using (Bitmap originalImage = new Bitmap(permeabilityPictureBox.Image))
                        {
                            // Create a new bitmap with space for the scale bar
                            Bitmap screenshotWithScale = new Bitmap(
                                originalImage.Width,
                                originalImage.Height + 50);

                            using (Graphics g = Graphics.FromImage(screenshotWithScale))
                            {
                                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

                                // Draw the original image
                                g.DrawImage(originalImage, 0, 0, originalImage.Width, originalImage.Height);

                                // Draw a black background for the scale bar area
                                g.FillRectangle(new SolidBrush(Color.Black),
                                    0, originalImage.Height, originalImage.Width, 50);

                                // Draw pressure scale bar
                                DrawPressureScaleBar(g,
                                    new Rectangle(50, originalImage.Height + 5, originalImage.Width - 100, 40),
                                    permeabilityResult.InputPressure,
                                    permeabilityResult.OutputPressure);

                                // Draw timestamp
                                g.DrawString($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                                    new Font("Arial", 8),
                                    Brushes.White,
                                    new Point(originalImage.Width - 200, originalImage.Height + 5));

                                // Draw permeability value
                                g.DrawString($"Permeability: {permeabilityResult.PermeabilityDarcy:F3} Darcy " +
                                            $"({permeabilityResult.PermeabilityMilliDarcy:F1} mD)",
                                    new Font("Arial", 8, FontStyle.Bold),
                                    Brushes.White,
                                    new Point(50, originalImage.Height + 5));
                            }

                            // Save the image with the scale bar
                            string extension = Path.GetExtension(saveDialog.FileName).ToLower();
                            System.Drawing.Imaging.ImageFormat format = System.Drawing.Imaging.ImageFormat.Png; // Default

                            if (extension == ".jpg" || extension == ".jpeg")
                                format = System.Drawing.Imaging.ImageFormat.Jpeg;
                            else if (extension == ".bmp")
                                format = System.Drawing.Imaging.ImageFormat.Bmp;

                            screenshotWithScale.Save(saveDialog.FileName, format);
                        }

                        statusLabel.Text = "Permeability visualization saved successfully.";
                        MessageBox.Show("Visualization screenshot saved successfully.",
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
            int height = 20;

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
                for (int i = 0; i < numTicks; i++)
                {
                    float x = rect.X + (width * i / (numTicks - 1));

                    // Draw tick mark
                    g.DrawLine(Pens.White, x, rect.Y + height, x, rect.Y + height + 5);

                    // Calculate pressure value for this position
                    double pressure = maxPressure - (i * (maxPressure - minPressure) / (numTicks - 1));
                    string label = $"{pressure:F0} Pa";

                    // Measure text and center it under the tick
                    SizeF textSize = g.MeasureString(label, font);
                    g.DrawString(label, font, Brushes.White, x - textSize.Width / 2, rect.Y + height + 6);
                }
            }

            // Draw title
            g.DrawString("Pressure", new Font("Arial", 9, FontStyle.Bold),
                Brushes.White, rect.X + width / 2 - 30, rect.Y - 15);
        }
    }
}