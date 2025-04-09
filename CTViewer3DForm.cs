using System;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using HelixToolkit.Wpf;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.ComponentModel;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Windows.Input;
using Color = System.Windows.Media.Color;
using System.Runtime.InteropServices;

namespace CTSegmenter
{
    public partial class CTViewer3DForm : Form
    {
        private MainForm mainForm;
        private ElementHost host;
        private HelixViewport3D viewport;
        private VolumeRenderer volumeRenderer;
        private bool renderingInProgress = false;

        // UI elements
        private Panel panelViewport;
        private SplitContainer splitContainer;
        private TabControl tabControl;
        private TabPage tabRender;
        private TabPage tabMaterials;
        private TabPage tabOrthoSlices;
        private CheckedListBox lstMaterials;
        private TrackBar trkOpacity;
        private Label lblOpacity;
        private TrackBar trkMinThreshold;
        private TrackBar trkMaxThreshold;
        private Label lblMinThreshold;
        private Label lblMaxThreshold;
        private TrackBar trkXSlice;
        private TrackBar trkYSlice;
        private TrackBar trkZSlice;
        private CheckBox chkShowSlices;
        private CheckBox chkShowOrthoPlanes;
        private Button btnResetView;
        private Button btnExportMesh;
        private ComboBox cmbRenderQuality;
        private Label lblRenderQuality;
        private Button btnApplyThreshold;
        private ProgressBar progressBar;
        private Label lblStatus;
        private CheckBox chkUseLodRendering;
        private Label lblMemoryUsage;
        private System.Windows.Forms.Timer memoryUpdateTimer;
        private Button btnClearCache;
        private CheckBox chkShowBwDataset;

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int awareness);

        public CTViewer3DForm(MainForm main)
        {
            try
            {
                // Set DPI awareness properly
                if (Environment.OSVersion.Version.Major >= 10)
                {
                    // Windows 10 - use SetProcessDpiAwareness with PROCESS_PER_MONITOR_DPI_AWARE (value 2)
                    SetProcessDpiAwareness(2);
                }
                else if (Environment.OSVersion.Version.Major >= 6)
                {
                    // Windows Vista/7/8 - use older SetProcessDPIAware
                    SetProcessDPIAware();
                }

                mainForm = main;

                InitializeComponent();
                InitializeViewport();
                InitializeRenderTab();
                InitializeMaterialsTab();
                InitializeOrthoSlicesTab();
                StyleControls();

                // Create volume renderer
                volumeRenderer = new VolumeRenderer(mainForm);

                // Make sure the dispatcher is set correctly
                if (System.Windows.Application.Current != null)
                {
                    volumeRenderer.SetDispatcher(System.Windows.Application.Current.Dispatcher);
                }
                else
                {
                    // For safety, ensure we have a dispatcher
                    volumeRenderer.SetDispatcher(System.Windows.Threading.Dispatcher.CurrentDispatcher);
                }

                viewport.Children.Add(volumeRenderer.RootModel);

                // Initialize memory usage tracking
                memoryUpdateTimer = new System.Windows.Forms.Timer();
                memoryUpdateTimer.Interval = 2000; // Update every 2 seconds
                memoryUpdateTimer.Tick += (s, e) => UpdateMemoryUsage();
                memoryUpdateTimer.Start();

                // Trigger initial render
                StartRenderingAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error initializing 3D viewer: {ex.Message}\n\n{ex.StackTrace}",
                    "Initialization Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[CTViewer3DForm] Initialization error: {ex}");
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // Force layout recalculation
            this.PerformLayout();

            // Ensure splitContainer has a reasonable size
            splitContainer.SplitterDistance = this.Height * 2 / 3;

            // Ensure viewport is properly sized
            panelViewport.PerformLayout();
            host.PerformLayout();

            // Zoom to fit
            viewport.ZoomExtents();
        }

        private void InitializeComponent()
        {
            this.panelViewport = new System.Windows.Forms.Panel();
            this.splitContainer = new System.Windows.Forms.SplitContainer();
            this.tabControl = new System.Windows.Forms.TabControl();
            this.tabRender = new System.Windows.Forms.TabPage();
            this.tabMaterials = new System.Windows.Forms.TabPage();
            this.tabOrthoSlices = new System.Windows.Forms.TabPage();
            this.lblStatus = new System.Windows.Forms.Label();
            this.progressBar = new System.Windows.Forms.ProgressBar();
            this.lblMemoryUsage = new System.Windows.Forms.Label();

            // Configure main form
            this.Text = "3D Volume Viewer";
            this.Size = new System.Drawing.Size(1200, 800);
            this.MinimumSize = new System.Drawing.Size(900, 700);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Icon = mainForm.Icon; // Use same icon as main form

            // Configure split container
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer)).BeginInit();
            this.splitContainer.Panel1.Controls.Add(this.panelViewport);
            this.splitContainer.Panel2.Controls.Add(this.tabControl);
            this.splitContainer.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitContainer.Orientation = System.Windows.Forms.Orientation.Horizontal;
            this.splitContainer.SplitterDistance = 600;
            this.splitContainer.FixedPanel = FixedPanel.Panel2;

            // Configure viewport panel
            this.panelViewport.Dock = System.Windows.Forms.DockStyle.Fill;
            this.panelViewport.BackColor = System.Drawing.Color.Black;

            // Configure tab control
            this.tabControl.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tabControl.Controls.Add(this.tabRender);
            this.tabControl.Controls.Add(this.tabMaterials);
            this.tabControl.Controls.Add(this.tabOrthoSlices);

            // Configure tabs
            this.tabRender.Text = "Render Settings";
            this.tabMaterials.Text = "Materials";
            this.tabOrthoSlices.Text = "Orthographic Slices";

            // Status panel
            Panel statusPanel = new Panel();
            statusPanel.Dock = DockStyle.Bottom;
            statusPanel.Height = 30;
            statusPanel.BackColor = System.Drawing.Color.FromArgb(30, 30, 30);
            this.splitContainer.Panel2.Controls.Add(statusPanel);

            // Status label
            this.lblStatus = new System.Windows.Forms.Label();
            this.lblStatus.Text = "Ready";
            this.lblStatus.AutoSize = true;
            this.lblStatus.Location = new System.Drawing.Point(10, 7);
            this.lblStatus.ForeColor = System.Drawing.Color.White;
            statusPanel.Controls.Add(this.lblStatus);

            // Memory usage label
            this.lblMemoryUsage = new System.Windows.Forms.Label();
            this.lblMemoryUsage.Text = "Memory: 0 MB";
            this.lblMemoryUsage.AutoSize = true;
            this.lblMemoryUsage.Location = new System.Drawing.Point(250, 7);
            this.lblMemoryUsage.ForeColor = System.Drawing.Color.White;
            statusPanel.Controls.Add(this.lblMemoryUsage);

            // Progress bar
            this.progressBar = new System.Windows.Forms.ProgressBar();
            this.progressBar.Style = ProgressBarStyle.Marquee;
            this.progressBar.MarqueeAnimationSpeed = 30;
            this.progressBar.Visible = false;
            this.progressBar.Size = new System.Drawing.Size(200, 20);
            this.progressBar.Location = new System.Drawing.Point(80, 5);
            statusPanel.Controls.Add(this.progressBar);

            // Add controls to form
            this.Controls.Add(this.splitContainer);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer)).EndInit();
        }

        private void InitializeViewport()
        {
            host = new ElementHost();
            host.Dock = DockStyle.Fill;

            viewport = new HelixViewport3D();
            viewport.CameraMode = CameraMode.Inspect;
            viewport.ShowFrameRate = true;
            

            // Set mouse gestures correctly
            viewport.RotateGesture = new MouseGesture(MouseAction.LeftClick);
            viewport.PanGesture = new MouseGesture(MouseAction.RightClick);

            viewport.ZoomExtentsWhenLoaded = true;

            // Add camera and lights
            viewport.Camera = new PerspectiveCamera
            {
                Position = new System.Windows.Media.Media3D.Point3D(0, 0, 5),
                LookDirection = new System.Windows.Media.Media3D.Vector3D(0, 0, -1),
                UpDirection = new System.Windows.Media.Media3D.Vector3D(0, 1, 0),
                FieldOfView = 45
            };

            // Add directional light
            var light = new DirectionalLight(Colors.White, new System.Windows.Media.Media3D.Vector3D(-0.5, -0.5, -1));
            viewport.Children.Add(new ModelVisual3D { Content = light });

            // Add ambient light
            var ambientLight = new AmbientLight(Color.FromRgb(120, 120, 120));
            viewport.Children.Add(new ModelVisual3D { Content = ambientLight });

            host.Child = viewport;
            this.panelViewport.Controls.Add(host);

            // Add coordinate system (axis indicator)
            var coordinateSystem = new CoordinateSystemVisual3D();
            coordinateSystem.ArrowLengths = 0.5;
            viewport.Children.Add(coordinateSystem);

            // Add grid
            var gridLinesX = new GridLinesVisual3D();
            gridLinesX.Center = new System.Windows.Media.Media3D.Point3D(0, 0, 0);
            gridLinesX.Length = 10;
            gridLinesX.Width = 10;
            gridLinesX.MinorDistance = 1;
            gridLinesX.MajorDistance = 1;
            gridLinesX.Thickness = 0.01;
            viewport.Children.Add(gridLinesX);

            // Set background
            viewport.Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
        }

        private void InitializeMaterialsTab()
        {
            // Materials list with checkboxes
            this.lstMaterials = new System.Windows.Forms.CheckedListBox();
            this.lstMaterials.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lstMaterials.DrawMode = DrawMode.OwnerDrawVariable;
            this.lstMaterials.DrawItem += LstMaterials_DrawItem;
            this.lstMaterials.ItemCheck += LstMaterials_ItemCheck;
            this.lstMaterials.SelectedIndexChanged += LstMaterials_SelectedIndexChanged;
            this.lstMaterials.IntegralHeight = false;

            // Panel for opacity slider
            Panel panelOpacity = new Panel();
            panelOpacity.Dock = System.Windows.Forms.DockStyle.Bottom;
            panelOpacity.Height = 70;
            panelOpacity.BackColor = System.Drawing.Color.FromArgb(45, 45, 48);

            // Opacity label
            this.lblOpacity = new System.Windows.Forms.Label();
            this.lblOpacity.Text = "Opacity: 100%";
            this.lblOpacity.AutoSize = true;
            this.lblOpacity.Location = new System.Drawing.Point(10, 10);
            this.lblOpacity.ForeColor = System.Drawing.Color.White;
            panelOpacity.Controls.Add(this.lblOpacity);

            // Opacity slider
            this.trkOpacity = new System.Windows.Forms.TrackBar();
            this.trkOpacity.Minimum = 0;
            this.trkOpacity.Maximum = 100;
            this.trkOpacity.Value = 100;
            this.trkOpacity.TickFrequency = 10;
            this.trkOpacity.Location = new System.Drawing.Point(10, 30);
            this.trkOpacity.Width = 300;
            this.trkOpacity.Scroll += TrkOpacity_Scroll;
            panelOpacity.Controls.Add(this.trkOpacity);

            // Add controls to tab
            this.tabMaterials.Controls.Add(this.lstMaterials);
            this.tabMaterials.Controls.Add(panelOpacity);

            // Load materials
            RefreshMaterialsList();
        }

        private void InitializeRenderTab()
        {
            // Top panel for buttons
            Panel buttonsPanel = new Panel();
            buttonsPanel.Dock = System.Windows.Forms.DockStyle.Top;
            buttonsPanel.Height = 45;
            buttonsPanel.Padding = new Padding(5);
            buttonsPanel.BackColor = System.Drawing.Color.FromArgb(30, 30, 30);
            this.tabRender.Controls.Add(buttonsPanel);

            // Reset view button
            this.btnResetView = new System.Windows.Forms.Button();
            this.btnResetView.Text = "Reset View";
            this.btnResetView.Size = new Size(100, 30);
            this.btnResetView.Location = new Point(10, 7);
            this.btnResetView.Click += BtnResetView_Click;
            buttonsPanel.Controls.Add(this.btnResetView);

            // Export mesh button
            this.btnExportMesh = new System.Windows.Forms.Button();
            this.btnExportMesh.Text = "Export 3D Model";
            this.btnExportMesh.Size = new Size(120, 30);
            this.btnExportMesh.Location = new Point(120, 7);
            this.btnExportMesh.Click += BtnExportMesh_Click;
            buttonsPanel.Controls.Add(this.btnExportMesh);

            // Apply threshold button
            this.btnApplyThreshold = new System.Windows.Forms.Button();
            this.btnApplyThreshold.Text = "Apply & Render";
            this.btnApplyThreshold.Size = new Size(120, 30);
            this.btnApplyThreshold.Location = new Point(250, 7);
            this.btnApplyThreshold.Click += BtnApplyThreshold_Click;
            buttonsPanel.Controls.Add(this.btnApplyThreshold);

            // Quick test button
            Button btnQuickRender = new Button();
            btnQuickRender.Text = "Quick Test";
            btnQuickRender.Size = new Size(80, 30);
            btnQuickRender.Location = new Point(380, 7);
            btnQuickRender.Click += (s, e) => {
                Logger.Log("[3D Viewer] Running quick test render");
                volumeRenderer.QuickRenderTest();
            };
            buttonsPanel.Controls.Add(btnQuickRender);

            // Clear Cache button
            this.btnClearCache = new Button();
            this.btnClearCache.Text = "Clear Cache";
            this.btnClearCache.Size = new Size(100, 30);
            this.btnClearCache.Location = new Point(470, 7);
            this.btnClearCache.Click += (s, e) => {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                UpdateMemoryUsage();
                MessageBox.Show("Memory cache cleared", "Cache", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            buttonsPanel.Controls.Add(this.btnClearCache);

            // Add BW dataset visibility checkbox
            chkShowBwDataset = new CheckBox();
            chkShowBwDataset.Text = "Show BW Dataset";
            chkShowBwDataset.Checked = true;
            chkShowBwDataset.AutoSize = true;
            chkShowBwDataset.Location = new Point(580, 12);
            chkShowBwDataset.ForeColor = System.Drawing.Color.White;
            chkShowBwDataset.CheckedChanged += (s, e) => {
                volumeRenderer.ShowBwDataset = chkShowBwDataset.Checked;
            };
            buttonsPanel.Controls.Add(chkShowBwDataset);

            // Render settings panel
            Panel settingsPanel = new Panel();
            settingsPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            settingsPanel.BackColor = System.Drawing.Color.FromArgb(45, 45, 48);
            settingsPanel.Padding = new Padding(10);
            this.tabRender.Controls.Add(settingsPanel);

            // Quality settings section
            GroupBox qualityGroup = new GroupBox();
            qualityGroup.Text = "Render Quality";
            qualityGroup.ForeColor = System.Drawing.Color.White;
            qualityGroup.Location = new Point(10, 10);
            qualityGroup.Size = new Size(350, 120);
            qualityGroup.BackColor = System.Drawing.Color.FromArgb(40, 40, 45);
            settingsPanel.Controls.Add(qualityGroup);

            // Render quality label
            this.lblRenderQuality = new System.Windows.Forms.Label();
            this.lblRenderQuality.Text = "Render Quality:";
            this.lblRenderQuality.AutoSize = true;
            this.lblRenderQuality.Location = new System.Drawing.Point(15, 25);
            this.lblRenderQuality.ForeColor = System.Drawing.Color.White;
            qualityGroup.Controls.Add(this.lblRenderQuality);

            // Render quality combo
            this.cmbRenderQuality = new System.Windows.Forms.ComboBox();
            this.cmbRenderQuality.Items.AddRange(new object[] { "Low", "Medium", "High", "Ultra" });
            this.cmbRenderQuality.SelectedIndex = 1; // Medium by default
            this.cmbRenderQuality.Location = new System.Drawing.Point(120, 22);
            this.cmbRenderQuality.Width = 150;
            this.cmbRenderQuality.BackColor = System.Drawing.Color.FromArgb(50, 50, 55);
            this.cmbRenderQuality.ForeColor = System.Drawing.Color.White;
            this.cmbRenderQuality.SelectedIndexChanged += CmbRenderQuality_SelectedIndexChanged;
            qualityGroup.Controls.Add(this.cmbRenderQuality);

            // LOD Rendering checkbox
            this.chkUseLodRendering = new CheckBox();
            this.chkUseLodRendering.Text = "Use Level of Detail (improves performance)";
            this.chkUseLodRendering.Checked = true;
            this.chkUseLodRendering.AutoSize = true;
            this.chkUseLodRendering.Location = new System.Drawing.Point(15, 55);
            this.chkUseLodRendering.ForeColor = System.Drawing.Color.White;
            this.chkUseLodRendering.CheckedChanged += (s, e) => {
                volumeRenderer.UseLodRendering = chkUseLodRendering.Checked;
            };
            qualityGroup.Controls.Add(this.chkUseLodRendering);

            // Threshold settings section
            GroupBox thresholdGroup = new GroupBox();
            thresholdGroup.Text = "Black & White Thresholds";
            thresholdGroup.ForeColor = System.Drawing.Color.White;
            thresholdGroup.Location = new Point(10, 140);
            thresholdGroup.Size = new Size(350, 130);
            thresholdGroup.BackColor = System.Drawing.Color.FromArgb(40, 40, 45);
            settingsPanel.Controls.Add(thresholdGroup);

            // Min threshold label
            this.lblMinThreshold = new System.Windows.Forms.Label();
            this.lblMinThreshold.Text = "Min: 0";
            this.lblMinThreshold.AutoSize = true;
            this.lblMinThreshold.Location = new System.Drawing.Point(15, 25);
            this.lblMinThreshold.ForeColor = System.Drawing.Color.White;
            thresholdGroup.Controls.Add(this.lblMinThreshold);

            // Min threshold slider
            this.trkMinThreshold = new System.Windows.Forms.TrackBar();
            this.trkMinThreshold.Minimum = 0;
            this.trkMinThreshold.Maximum = 255;
            this.trkMinThreshold.Value = 0;
            this.trkMinThreshold.TickFrequency = 25;
            this.trkMinThreshold.Location = new System.Drawing.Point(15, 45);
            this.trkMinThreshold.Width = 320;
            this.trkMinThreshold.Scroll += TrkMinThreshold_Scroll;
            thresholdGroup.Controls.Add(this.trkMinThreshold);

            // Max threshold label
            this.lblMaxThreshold = new System.Windows.Forms.Label();
            this.lblMaxThreshold.Text = "Max: 255";
            this.lblMaxThreshold.AutoSize = true;
            this.lblMaxThreshold.Location = new System.Drawing.Point(15, 75);
            this.lblMaxThreshold.ForeColor = System.Drawing.Color.White;
            thresholdGroup.Controls.Add(this.lblMaxThreshold);

            // Max threshold slider
            this.trkMaxThreshold = new System.Windows.Forms.TrackBar();
            this.trkMaxThreshold.Minimum = 0;
            this.trkMaxThreshold.Maximum = 255;
            this.trkMaxThreshold.Value = 255;
            this.trkMaxThreshold.TickFrequency = 25;
            this.trkMaxThreshold.Location = new System.Drawing.Point(15, 95);
            this.trkMaxThreshold.Width = 320;
            this.trkMaxThreshold.Scroll += TrkMaxThreshold_Scroll;
            thresholdGroup.Controls.Add(this.trkMaxThreshold);
        }

        private void InitializeOrthoSlicesTab()
        {
            // Panel container for better scrolling
            Panel slicesPanel = new Panel();
            slicesPanel.Dock = DockStyle.Fill;
            slicesPanel.AutoScroll = true;
            slicesPanel.BackColor = System.Drawing.Color.FromArgb(45, 45, 48);
            slicesPanel.Padding = new Padding(10);
            this.tabOrthoSlices.Controls.Add(slicesPanel);

            // Show slices checkbox
            this.chkShowSlices = new System.Windows.Forms.CheckBox();
            this.chkShowSlices.Text = "Enable Slice Controls";
            this.chkShowSlices.Checked = true;
            this.chkShowSlices.AutoSize = true;
            this.chkShowSlices.Location = new System.Drawing.Point(10, 10);
            this.chkShowSlices.ForeColor = System.Drawing.Color.White;
            this.chkShowSlices.CheckedChanged += ChkShowSlices_CheckedChanged;
            slicesPanel.Controls.Add(this.chkShowSlices);

            // Show orthographic planes checkbox
            this.chkShowOrthoPlanes = new System.Windows.Forms.CheckBox();
            this.chkShowOrthoPlanes.Text = "Show Orthographic Planes";
            this.chkShowOrthoPlanes.Checked = true;
            this.chkShowOrthoPlanes.AutoSize = true;
            this.chkShowOrthoPlanes.Location = new System.Drawing.Point(180, 10);
            this.chkShowOrthoPlanes.ForeColor = System.Drawing.Color.White;
            this.chkShowOrthoPlanes.CheckedChanged += ChkShowOrthoPlanes_CheckedChanged;
            slicesPanel.Controls.Add(this.chkShowOrthoPlanes);

            // X slice group
            GroupBox grpXSlice = new GroupBox();
            grpXSlice.Text = "X Slice";
            grpXSlice.Location = new System.Drawing.Point(10, 40);
            grpXSlice.Size = new System.Drawing.Size(320, 80);
            grpXSlice.ForeColor = System.Drawing.Color.White;
            grpXSlice.BackColor = System.Drawing.Color.FromArgb(40, 40, 45);
            slicesPanel.Controls.Add(grpXSlice);

            // X slice slider
            this.trkXSlice = new System.Windows.Forms.TrackBar();
            this.trkXSlice.Minimum = 0;
            this.trkXSlice.Maximum = mainForm.GetWidth() > 0 ? mainForm.GetWidth() - 1 : 100;
            this.trkXSlice.Value = this.trkXSlice.Maximum / 2;
            this.trkXSlice.TickFrequency = this.trkXSlice.Maximum / 10;
            this.trkXSlice.Location = new System.Drawing.Point(10, 25);
            this.trkXSlice.Width = 300;
            this.trkXSlice.Scroll += TrkXSlice_Scroll;
            grpXSlice.Controls.Add(this.trkXSlice);

            // Y slice group
            GroupBox grpYSlice = new GroupBox();
            grpYSlice.Text = "Y Slice";
            grpYSlice.Location = new System.Drawing.Point(10, 130);
            grpYSlice.Size = new System.Drawing.Size(320, 80);
            grpYSlice.ForeColor = System.Drawing.Color.White;
            grpYSlice.BackColor = System.Drawing.Color.FromArgb(40, 40, 45);
            slicesPanel.Controls.Add(grpYSlice);

            // Y slice slider
            this.trkYSlice = new System.Windows.Forms.TrackBar();
            this.trkYSlice.Minimum = 0;
            this.trkYSlice.Maximum = mainForm.GetHeight() > 0 ? mainForm.GetHeight() - 1 : 100;
            this.trkYSlice.Value = this.trkYSlice.Maximum / 2;
            this.trkYSlice.TickFrequency = this.trkYSlice.Maximum / 10;
            this.trkYSlice.Location = new System.Drawing.Point(10, 25);
            this.trkYSlice.Width = 300;
            this.trkYSlice.Scroll += TrkYSlice_Scroll;
            grpYSlice.Controls.Add(this.trkYSlice);

            // Z slice group
            GroupBox grpZSlice = new GroupBox();
            grpZSlice.Text = "Z Slice";
            grpZSlice.Location = new System.Drawing.Point(10, 220);
            grpZSlice.Size = new System.Drawing.Size(320, 80);
            grpZSlice.ForeColor = System.Drawing.Color.White;
            grpZSlice.BackColor = System.Drawing.Color.FromArgb(40, 40, 45);
            slicesPanel.Controls.Add(grpZSlice);

            // Z slice slider
            this.trkZSlice = new System.Windows.Forms.TrackBar();
            this.trkZSlice.Minimum = 0;
            this.trkZSlice.Maximum = mainForm.GetDepth() > 0 ? mainForm.GetDepth() - 1 : 100;
            this.trkZSlice.Value = this.trkZSlice.Maximum / 2;
            this.trkZSlice.TickFrequency = this.trkZSlice.Maximum / 10;
            this.trkZSlice.Location = new System.Drawing.Point(10, 25);
            this.trkZSlice.Width = 300;
            this.trkZSlice.Scroll += TrkZSlice_Scroll;
            grpZSlice.Controls.Add(this.trkZSlice);
        }

        private void StyleControls()
        {
            // Modern dark theme styling
            this.BackColor = System.Drawing.Color.FromArgb(30, 30, 35);
            panelViewport.BackColor = System.Drawing.Color.FromArgb(20, 20, 25);

            this.tabRender.BackColor = System.Drawing.Color.FromArgb(45, 45, 48);
            this.tabMaterials.BackColor = System.Drawing.Color.FromArgb(45, 45, 48);
            this.tabOrthoSlices.BackColor = System.Drawing.Color.FromArgb(45, 45, 48);

            // Custom tab drawing
            tabControl.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabControl.DrawItem += TabControl_DrawItem;

            // Style sliders with custom appearance
            StyleTrackBar(trkMinThreshold);
            StyleTrackBar(trkMaxThreshold);
            StyleTrackBar(trkOpacity);
            StyleTrackBar(trkXSlice);
            StyleTrackBar(trkYSlice);
            StyleTrackBar(trkZSlice);

            // Style buttons with modern look
            StyleButton(btnResetView);
            StyleButton(btnExportMesh);
            StyleButton(btnApplyThreshold);
            StyleButton(btnClearCache);

            // Style labels
            StyleLabel(lblMinThreshold);
            StyleLabel(lblMaxThreshold);
            StyleLabel(lblOpacity);
            StyleLabel(lblRenderQuality);
            StyleLabel(lblStatus);
            StyleLabel(lblMemoryUsage);

            // Style progress bar
            progressBar.ForeColor = System.Drawing.Color.DodgerBlue;
            progressBar.BackColor = System.Drawing.Color.FromArgb(62, 62, 64);

            // Style combo box
            cmbRenderQuality.BackColor = System.Drawing.Color.FromArgb(62, 62, 64);
            cmbRenderQuality.ForeColor = System.Drawing.Color.White;
            cmbRenderQuality.FlatStyle = FlatStyle.Flat;

            // Style checked list box
            lstMaterials.BackColor = System.Drawing.Color.FromArgb(50, 50, 55);
            lstMaterials.ForeColor = System.Drawing.Color.White;
            lstMaterials.BorderStyle = BorderStyle.FixedSingle;

            // Style checkboxes
            chkShowSlices.ForeColor = System.Drawing.Color.White;
            chkShowOrthoPlanes.ForeColor = System.Drawing.Color.White;
            chkUseLodRendering.ForeColor = System.Drawing.Color.White;
            chkShowBwDataset.ForeColor = System.Drawing.Color.White;
        }

        private void TabControl_DrawItem(object sender, DrawItemEventArgs e)
        {
            var g = e.Graphics;
            var tabRect = tabControl.GetTabRect(e.Index);

            // Background
            using (var brush = new SolidBrush(System.Drawing.Color.FromArgb(40, 40, 45)))
            {
                g.FillRectangle(brush, tabRect);
            }

            // Text
            string tabText = tabControl.TabPages[e.Index].Text;
            using (var brush = new SolidBrush(System.Drawing.Color.White))
            {
                var format = new StringFormat
                {
                    LineAlignment = StringAlignment.Center,
                    Alignment = StringAlignment.Center
                };
                g.DrawString(tabText, tabControl.Font, brush, tabRect, format);
            }

            // Selected tab indicator
            if (e.Index == tabControl.SelectedIndex)
            {
                using (var pen = new System.Drawing.Pen(System.Drawing.Color.DodgerBlue, 3))
                {
                    g.DrawLine(pen, tabRect.Left, tabRect.Bottom, tabRect.Right, tabRect.Bottom);
                }
            }
        }

        private void LstMaterials_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || mainForm.Materials.Count <= e.Index)
                return;

            e.DrawBackground();

            // Only show non-exterior materials
            var materials = mainForm.Materials.Where(m => !m.IsExterior).ToList();
            if (e.Index >= materials.Count)
                return;

            var material = materials[e.Index];

            // Draw checkbox
            var checkboxRect = new Rectangle(e.Bounds.X + 5, e.Bounds.Y + 2, 16, 16);
            ControlPaint.DrawCheckBox(e.Graphics, checkboxRect,
                lstMaterials.GetItemChecked(e.Index) ? ButtonState.Checked : ButtonState.Normal);

            // Draw colored rectangle for material
            var colorRect = new Rectangle(e.Bounds.X + 30, e.Bounds.Y + 2, 20, e.Bounds.Height - 4);
            using (var brush = new SolidBrush(material.Color))
            {
                e.Graphics.FillRectangle(brush, colorRect);
            }
            e.Graphics.DrawRectangle(Pens.Gray, colorRect);

            // Draw material name
            var textRect = new Rectangle(e.Bounds.X + 60, e.Bounds.Y + 2, e.Bounds.Width - 60, e.Bounds.Height);
            using (var brush = new SolidBrush(System.Drawing.Color.White))
            {
                e.Graphics.DrawString(material.Name, e.Font, brush, textRect);
            }

            e.DrawFocusRectangle();
        }

        private void StyleTrackBar(TrackBar trackBar)
        {
            trackBar.BackColor = System.Drawing.Color.FromArgb(45, 45, 48);
            trackBar.ForeColor = System.Drawing.Color.White;
        }

        private void StyleButton(Button button)
        {
            button.BackColor = System.Drawing.Color.FromArgb(0, 122, 204);
            button.ForeColor = System.Drawing.Color.White;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.Font = new System.Drawing.Font(button.Font.FontFamily, button.Font.Size, System.Drawing.FontStyle.Bold);

            // Add hover effects
            button.MouseEnter += (s, e) =>
            {
                button.BackColor = System.Drawing.Color.FromArgb(28, 151, 234);
            };

            button.MouseLeave += (s, e) =>
            {
                button.BackColor = System.Drawing.Color.FromArgb(0, 122, 204);
            };
        }

        private void StyleLabel(Label label)
        {
            label.ForeColor = System.Drawing.Color.White;
            label.Font = new System.Drawing.Font(label.Font.FontFamily, label.Font.Size, System.Drawing.FontStyle.Regular);
        }

        private void UpdateMemoryUsage()
        {
            // Get current memory usage
            long memoryUsageMB = System.GC.GetTotalMemory(false) / (1024 * 1024);
            lblMemoryUsage.Text = $"Memory: {memoryUsageMB} MB";

            // Change color based on memory usage
            if (memoryUsageMB > 2000)
                lblMemoryUsage.ForeColor = System.Drawing.Color.Red;
            else if (memoryUsageMB > 1000)
                lblMemoryUsage.ForeColor = System.Drawing.Color.Yellow;
            else
                lblMemoryUsage.ForeColor = System.Drawing.Color.LightGreen;
        }

        private async void StartRenderingAsync()
        {
            Logger.Log("[3D Viewer] Starting volume rendering process");
            Logger.Log($"[3D Viewer] Dataset dimensions: {mainForm.GetWidth()}x{mainForm.GetHeight()}x{mainForm.GetDepth()}");
            Logger.Log($"[3D Viewer] Threshold range: {trkMinThreshold.Value}-{trkMaxThreshold.Value}");

            if (renderingInProgress) return;
            renderingInProgress = true;

            try
            {
                // Update renderer settings
                volumeRenderer.MinThreshold = trkMinThreshold.Value;
                volumeRenderer.MaxThreshold = trkMaxThreshold.Value;
                volumeRenderer.UseLodRendering = chkUseLodRendering.Checked;
                volumeRenderer.ShowBwDataset = chkShowBwDataset.Checked;

                // Set voxel stride based on render quality
                switch (cmbRenderQuality.SelectedItem.ToString())
                {
                    case "Low":
                        volumeRenderer.VoxelStride = 8;
                        break;
                    case "Medium":
                        volumeRenderer.VoxelStride = 4;
                        break;
                    case "High":
                        volumeRenderer.VoxelStride = 2;
                        break;
                    case "Ultra":
                        volumeRenderer.VoxelStride = 1;
                        break;
                }

                // Show progress during rendering
                lblStatus.Text = "Rendering...";
                progressBar.Visible = true;

                // Force UI update
                Application.DoEvents();

                // Update the volume rendering
                await volumeRenderer.UpdateAsync();

                // Set slice planes positions
                if (chkShowOrthoPlanes.Checked)
                {
                    volumeRenderer.UpdateSlicePlanes(
                        trkXSlice.Value,
                        trkYSlice.Value,
                        trkZSlice.Value,
                        mainForm.GetWidth(),
                        mainForm.GetHeight(),
                        mainForm.GetDepth(),
                        mainForm.GetPixelSize());
                }

                // Update viewport
                viewport.ZoomExtents();

                // Update memory usage display
                UpdateMemoryUsage();

                // Trigger test render if nothing appears
                //volumeRenderer.QuickRenderTest();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error rendering volume: {ex.Message}", "Render Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[3D Viewer] Render error: {ex}");
            }
            finally
            {
                // Hide progress bar
                lblStatus.Text = "Ready";
                progressBar.Visible = false;
                renderingInProgress = false;
            }
        }

        private void RefreshMaterialsList()
        {
            // Temporarily remove the event handler
            lstMaterials.ItemCheck -= LstMaterials_ItemCheck;

            lstMaterials.Items.Clear();

            foreach (var material in mainForm.Materials)
            {
                if (!material.IsExterior)
                {
                    lstMaterials.Items.Add(material.Name, true);
                }
            }

            // Reattach the event handler after items are added
            lstMaterials.ItemCheck += LstMaterials_ItemCheck;
        }

        #region Event Handlers

        private void LstMaterials_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            // Check if we're still initializing
            if (volumeRenderer == null)
                return;

            try
            {
                bool isVisible = e.NewValue == CheckState.Checked;
                var materials = mainForm.Materials.Where(m => !m.IsExterior).ToList();

                if (e.Index >= 0 && e.Index < materials.Count)
                {
                    var materialId = materials[e.Index].ID;
                    volumeRenderer.SetMaterialVisibility(materialId, isVisible);
                }
            }
            catch (Exception ex)
            {
                // Safely log the error but continue execution
                Logger.Log($"Error in material check: {ex.Message}");
            }
        }

        private void LstMaterials_SelectedIndexChanged(object sender, EventArgs e)
        {
            int selectedIndex = lstMaterials.SelectedIndex;
            var materials = mainForm.Materials.Where(m => !m.IsExterior).ToList();

            if (selectedIndex >= 0 && selectedIndex < materials.Count)
            {
                var materialId = materials[selectedIndex].ID;
                double opacity = volumeRenderer.GetMaterialOpacity(materialId);
                trkOpacity.Value = (int)(opacity * 100);
                lblOpacity.Text = $"Opacity: {trkOpacity.Value}%";
            }
        }

        private void TrkOpacity_Scroll(object sender, EventArgs e)
        {
            int selectedIndex = lstMaterials.SelectedIndex;
            var materials = mainForm.Materials.Where(m => !m.IsExterior).ToList();

            if (selectedIndex >= 0 && selectedIndex < materials.Count)
            {
                double opacity = trkOpacity.Value / 100.0;
                lblOpacity.Text = $"Opacity: {trkOpacity.Value}%";

                var materialId = materials[selectedIndex].ID;
                volumeRenderer.SetMaterialOpacity(materialId, opacity);
            }
        }

        private void TrkMinThreshold_Scroll(object sender, EventArgs e)
        {
            // Ensure min threshold doesn't exceed max threshold
            if (trkMinThreshold.Value > trkMaxThreshold.Value)
            {
                trkMinThreshold.Value = trkMaxThreshold.Value;
            }

            lblMinThreshold.Text = $"Min: {trkMinThreshold.Value}";

            // Real-time preview if enabled
            if (volumeRenderer.RealTimeUpdate)
            {
                volumeRenderer.MinThreshold = trkMinThreshold.Value;
                volumeRenderer.UpdateThreshold();
            }
        }

        private void TrkMaxThreshold_Scroll(object sender, EventArgs e)
        {
            // Ensure max threshold doesn't go below min threshold
            if (trkMaxThreshold.Value < trkMinThreshold.Value)
            {
                trkMaxThreshold.Value = trkMinThreshold.Value;
            }

            lblMaxThreshold.Text = $"Max: {trkMaxThreshold.Value}";

            // Real-time preview if enabled
            if (volumeRenderer.RealTimeUpdate)
            {
                volumeRenderer.MaxThreshold = trkMaxThreshold.Value;
                volumeRenderer.UpdateThreshold();
            }
        }

        private void CmbRenderQuality_SelectedIndexChanged(object sender, EventArgs e)
        {
            // Quality changes will be applied when the user clicks Apply & Render
        }

        private void BtnResetView_Click(object sender, EventArgs e)
        {
            viewport.ResetCamera();
            viewport.ZoomExtents();
        }

        private void BtnExportMesh_Click(object sender, EventArgs e)
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Filter = "OBJ Files (*.obj)|*.obj|STL Files (*.stl)|*.stl|All Files (*.*)|*.*";
                dialog.Title = "Export 3D Model";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        // Show progress during export
                        lblStatus.Text = "Exporting model...";
                        progressBar.Visible = true;

                        // Export the model
                        volumeRenderer.ExportModel(dialog.FileName);

                        MessageBox.Show("Export completed successfully.", "Export",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error exporting model: {ex.Message}", "Export Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    finally
                    {
                        lblStatus.Text = "Ready";
                        progressBar.Visible = false;
                    }
                }
            }
        }

        private void ChkShowSlices_CheckedChanged(object sender, EventArgs e)
        {
            bool showSlices = chkShowSlices.Checked;

            trkXSlice.Enabled = showSlices;
            trkYSlice.Enabled = showSlices;
            trkZSlice.Enabled = showSlices;

            if (showSlices)
            {
                volumeRenderer.ShowSlicePlanes(true);

                // Update slice positions
                if (chkShowOrthoPlanes.Checked)
                {
                    volumeRenderer.UpdateSlicePlanes(
                        trkXSlice.Value,
                        trkYSlice.Value,
                        trkZSlice.Value,
                        mainForm.GetWidth(),
                        mainForm.GetHeight(),
                        mainForm.GetDepth(),
                        mainForm.GetPixelSize());
                }
            }
            else
            {
                volumeRenderer.ShowSlicePlanes(false);
            }
        }

        private void ChkShowOrthoPlanes_CheckedChanged(object sender, EventArgs e)
        {
            if (chkShowOrthoPlanes.Checked)
            {
                if (chkShowSlices.Checked)
                {
                    volumeRenderer.UpdateSlicePlanes(
                        trkXSlice.Value,
                        trkYSlice.Value,
                        trkZSlice.Value,
                        mainForm.GetWidth(),
                        mainForm.GetHeight(),
                        mainForm.GetDepth(),
                        mainForm.GetPixelSize());
                }
            }
            else
            {
                volumeRenderer.ShowSlicePlanes(false);
            }
        }

        private void TrkXSlice_Scroll(object sender, EventArgs e)
        {
            if (chkShowSlices.Checked && chkShowOrthoPlanes.Checked)
            {
                volumeRenderer.UpdateXSlice(
                    trkXSlice.Value,
                    mainForm.GetWidth(),
                    mainForm.GetPixelSize());
            }
        }

        private void TrkYSlice_Scroll(object sender, EventArgs e)
        {
            if (chkShowSlices.Checked && chkShowOrthoPlanes.Checked)
            {
                volumeRenderer.UpdateYSlice(
                    trkYSlice.Value,
                    mainForm.GetHeight(),
                    mainForm.GetPixelSize());
            }
        }

        private void TrkZSlice_Scroll(object sender, EventArgs e)
        {
            if (chkShowSlices.Checked && chkShowOrthoPlanes.Checked)
            {
                volumeRenderer.UpdateZSlice(
                    trkZSlice.Value,
                    mainForm.GetDepth(),
                    mainForm.GetPixelSize());
            }
        }

        private void BtnApplyThreshold_Click(object sender, EventArgs e)
        {
            StartRenderingAsync();
        }

        #endregion

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            // Clean up resources
            memoryUpdateTimer?.Stop();
            memoryUpdateTimer?.Dispose();

            // Force garbage collection to clean up any memory
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }
}