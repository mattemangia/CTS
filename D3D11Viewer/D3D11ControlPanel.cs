// Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace CTS.D3D11
{
    public struct ClippingPlane
    {
        public Vector3 Normal;
        public float Distance;
        public bool Enabled;
        public bool Mirrored;
        public string Name;
    }

    public partial class D3D11ControlPanel : Form
    {
        private D3D11ViewerForm viewerForm;
        private MainForm mainForm;
        private D3D11VolumeRenderer volumeRenderer;

        // UI elements
        private TabControl mainTabControl;

        // Rendering tab
        private TrackBar trkMinThreshold, trkMaxThreshold;
        private Label lblMinThreshold, lblMaxThreshold;
        private CheckBox chkShowGrayscale;
        private ComboBox cmbQuality;

        // Materials tab
        private CheckedListBox lstMaterials;
        private TrackBar trkOpacity;
        private Label lblOpacity;
        private Label lblMaterialName;
        private Panel materialColorPanel;

        // Clipping tab
        private ListView lstClippingPlanes;
        private Button btnAddPlane, btnRemovePlane;
        private CheckBox chkEnablePlane, chkMirrorPlane;
        private TrackBar trkPlaneNormalX, trkPlaneNormalY, trkPlaneNormalZ;
        private TrackBar trkPlaneRotation, trkPlaneDistance;
        private Label lblNormalX, lblNormalY, lblNormalZ;
        private Label lblRotation, lblDistance;
        private ComboBox cmbPlanePresets;
        private Panel planePreviewPanel;

        // Visualization tab
        private CheckBox chkShowScaleBar;
        private ComboBox cmbScaleBarPosition;
        private CheckBox chkShowScaleText;
        private NumericUpDown numScaleBarLength;
        private Label lblScaleBarUnits;
        private Button btnBackgroundColor;
        private ColorDialog colorDialog;

        private List<ClippingPlane> clippingPlanes = new List<ClippingPlane>();
        private int selectedPlaneIndex = -1;
        private bool updatingUI = false;

        public D3D11ControlPanel(D3D11ViewerForm viewer, MainForm main, D3D11VolumeRenderer renderer)
        {
            viewerForm = viewer;
            mainForm = main;
            volumeRenderer = renderer;

            InitializeComponent();
            PopulateMaterials();
            InitializeClippingPlanes();
            ApplyDarkTheme();
        }

        private void InitializeComponent()
        {
            this.Text = "3D Rendering Control Panel";
            this.Size = new Size(480, 820);
            this.MinimumSize = new Size(400, 600);
            this.FormBorderStyle = FormBorderStyle.SizableToolWindow;
            this.StartPosition = FormStartPosition.Manual;
            this.ShowInTaskbar = false;

            try
            {
                this.Icon = Properties.Resources.favicon;
            }
            catch { }

            // Main container with padding
            var mainPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8)
            };

            // Create tab control
            mainTabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F),
                ItemSize = new Size(110, 32)
            };

            // Create tabs
            var tabRendering = new TabPage("Rendering");
            var tabMaterials = new TabPage("Materials");
            var tabClipping = new TabPage("Clipping");
            var tabVisualization = new TabPage("Display");

            SetupRenderingTab(tabRendering);
            SetupMaterialsTab(tabMaterials);
            SetupClippingTab(tabClipping);
            SetupVisualizationTab(tabVisualization);

            mainTabControl.TabPages.AddRange(new[] { tabRendering, tabMaterials, tabClipping, tabVisualization });
            mainPanel.Controls.Add(mainTabControl);
            this.Controls.Add(mainPanel);

            // Set initial form position to right of screen
            var screen = Screen.PrimaryScreen.WorkingArea;
            this.Location = new Point(screen.Right - this.Width - 20, 100);
        }

        private void SetupRenderingTab(TabPage page)
        {
            var container = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(10),
                AutoScroll = true
            };
            container.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            container.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            container.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            container.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            // Quality Section
            var qualityGroup = CreateStyledGroupBox("Rendering Quality");
            var qualityPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Height = 40
            };
            qualityPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100F));
            qualityPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            qualityPanel.Controls.Add(new Label { Text = "Quality:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            cmbQuality = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9F)
            };
            cmbQuality.Items.AddRange(new[] { "Fast (Draft)", "Balanced", "High Quality" });
            cmbQuality.SelectedIndex = 1;
            cmbQuality.SelectedIndexChanged += (s, e) => UpdateRenderParams();
            qualityPanel.Controls.Add(cmbQuality, 1, 0);

            qualityGroup.Controls.Add(qualityPanel);
            container.Controls.Add(qualityGroup, 0, 0);

            // Grayscale Section
            var grayscaleGroup = CreateStyledGroupBox("Grayscale Volume");
            var grayscalePanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                AutoSize = true
            };

            chkShowGrayscale = new CheckBox
            {
                Text = "Show Grayscale Data",
                AutoSize = true,
                Checked = true,
                Font = new Font("Segoe UI", 9F),
                Margin = new Padding(0, 0, 0, 10)
            };
            chkShowGrayscale.CheckedChanged += (s, e) => UpdateRenderParams();
            grayscalePanel.Controls.Add(chkShowGrayscale, 0, 0);

            // Threshold controls with live update
            var thresholdPanel = new Panel { Dock = DockStyle.Fill, Height = 120 };

            lblMinThreshold = new Label { Text = "Min Threshold: 30", Location = new Point(0, 5), AutoSize = true };
            trkMinThreshold = new TrackBar
            {
                Minimum = 0,
                Maximum = 255,
                Value = 30,
                Width = 300,
                Location = new Point(0, 25),
                TickFrequency = 16,
                TickStyle = TickStyle.BottomRight
            };
            trkMinThreshold.Scroll += (s, e) =>
            {
                lblMinThreshold.Text = $"Min Threshold: {trkMinThreshold.Value}";
                if (trkMinThreshold.Value > trkMaxThreshold.Value)
                    trkMaxThreshold.Value = trkMinThreshold.Value;
                UpdateRenderParams();
            };

            lblMaxThreshold = new Label { Text = "Max Threshold: 200", Location = new Point(0, 60), AutoSize = true };
            trkMaxThreshold = new TrackBar
            {
                Minimum = 0,
                Maximum = 255,
                Value = 200,
                Width = 300,
                Location = new Point(0, 80),
                TickFrequency = 16,
                TickStyle = TickStyle.BottomRight
            };
            trkMaxThreshold.Scroll += (s, e) =>
            {
                lblMaxThreshold.Text = $"Max Threshold: {trkMaxThreshold.Value}";
                if (trkMaxThreshold.Value < trkMinThreshold.Value)
                    trkMinThreshold.Value = trkMaxThreshold.Value;
                UpdateRenderParams();
            };

            thresholdPanel.Controls.AddRange(new Control[] { lblMinThreshold, trkMinThreshold, lblMaxThreshold, trkMaxThreshold });
            grayscalePanel.Controls.Add(thresholdPanel, 0, 1);

            grayscaleGroup.Controls.Add(grayscalePanel);
            container.Controls.Add(grayscaleGroup, 0, 1);

            page.Controls.Add(container);
        }

        private void SetupMaterialsTab(TabPage page)
        {
            var splitContainer = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                Panel1MinSize = 200,
                Panel2MinSize = 150,
                
            };
            splitContainer.HandleCreated += (s, e) =>
            {
                int max = splitContainer.Height - splitContainer.Panel2MinSize;
                int desired = (int)(splitContainer.Height * 0.6);
                splitContainer.SplitterDistance = Math.Max(splitContainer.Panel1MinSize, Math.Min(desired, max));
            };
            // Top panel - Materials list
            var topPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
            var materialsGroup = CreateStyledGroupBox("Material Visibility");
            materialsGroup.Dock = DockStyle.Fill;

            lstMaterials = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                Font = new Font("Segoe UI", 9F),
                BorderStyle = BorderStyle.FixedSingle
            };
            lstMaterials.ItemCheck += LstMaterials_ItemCheck;
            lstMaterials.SelectedIndexChanged += LstMaterials_SelectedIndexChanged;

            materialsGroup.Controls.Add(lstMaterials);
            topPanel.Controls.Add(materialsGroup);

            // Bottom panel - Material properties
            var bottomPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
            var propertiesGroup = CreateStyledGroupBox("Material Properties");
            propertiesGroup.Dock = DockStyle.Fill;

            var propsPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4,
                RowStyles = {
                    new RowStyle(SizeType.AutoSize),
                    new RowStyle(SizeType.AutoSize),
                    new RowStyle(SizeType.AutoSize),
                    new RowStyle(SizeType.Percent, 100F)
                }
            };

            // Material name
            lblMaterialName = new Label
            {
                Text = "Select a material",
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                AutoSize = true
            };
            propsPanel.Controls.Add(lblMaterialName, 0, 0);
            propsPanel.SetColumnSpan(lblMaterialName, 2);

            // Material color
            propsPanel.Controls.Add(new Label { Text = "Color:", AutoSize = true }, 0, 1);
            materialColorPanel = new Panel
            {
                Size = new Size(60, 24),
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.White,
                Cursor = Cursors.Hand
            };
            materialColorPanel.Click += (s, e) =>
            {
                if (lstMaterials.SelectedIndex < 0) return;
                if (colorDialog == null) colorDialog = new ColorDialog();
                var material = mainForm.Materials[lstMaterials.SelectedIndex];
                colorDialog.Color = material.Color;
                if (colorDialog.ShowDialog() == DialogResult.OK)
                {
                    material.Color = colorDialog.Color;
                    materialColorPanel.BackColor = colorDialog.Color;
                    UpdateMaterialBuffer();
                }
            };
            propsPanel.Controls.Add(materialColorPanel, 1, 1);

            // Opacity slider
            propsPanel.Controls.Add(new Label { Text = "Opacity:", AutoSize = true }, 0, 2);
            var opacityContainer = new Panel { Dock = DockStyle.Fill, Height = 50 };

            lblOpacity = new Label
            {
                Text = "100%",
                Location = new Point(260, 5),
                AutoSize = true
            };
            trkOpacity = new TrackBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 100,
                Width = 250,
                Location = new Point(0, 0),
                TickFrequency = 10,
                TickStyle = TickStyle.BottomRight
            };
            trkOpacity.Scroll += (s, e) =>
            {
                lblOpacity.Text = $"{trkOpacity.Value}%";
                if (lstMaterials.SelectedIndex >= 0)
                {
                    var material = mainForm.Materials[lstMaterials.SelectedIndex];
                    material.SetOpacity(trkOpacity.Value / 100.0f);
                    UpdateMaterialBuffer();
                }
            };

            opacityContainer.Controls.AddRange(new Control[] { trkOpacity, lblOpacity });
            propsPanel.Controls.Add(opacityContainer, 1, 2);

            propertiesGroup.Controls.Add(propsPanel);
            bottomPanel.Controls.Add(propertiesGroup);

            splitContainer.Panel1.Controls.Add(topPanel);
            splitContainer.Panel2.Controls.Add(bottomPanel);
            page.Controls.Add(splitContainer);
        }

        private void SetupClippingTab(TabPage page)
        {
            var mainPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(10)
            };
            mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 180F));
            mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 100F));

            // Clipping planes list
            var planesGroup = CreateStyledGroupBox("Clipping Planes");

            var planesPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2
            };
            planesPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            planesPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));

            lstClippingPlanes = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                CheckBoxes = true,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F)
            };
            lstClippingPlanes.Columns.Add("Name", 180);
            lstClippingPlanes.Columns.Add("Type", 80);
            lstClippingPlanes.Columns.Add("Mirror", 60);
            lstClippingPlanes.ItemChecked += (s, e) =>
            {
                if (!updatingUI)
                {
                    clippingPlanes[e.Item.Index] = new ClippingPlane
                    {
                        Name = clippingPlanes[e.Item.Index].Name,
                        Normal = clippingPlanes[e.Item.Index].Normal,
                        Distance = clippingPlanes[e.Item.Index].Distance,
                        Enabled = e.Item.Checked,
                        Mirrored = clippingPlanes[e.Item.Index].Mirrored
                    };
                    UpdateClippingPlanes();
                }
            };
            lstClippingPlanes.SelectedIndexChanged += LstClippingPlanes_SelectedIndexChanged;

            var buttonPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                Dock = DockStyle.Fill,
                Height = 35
            };

            btnAddPlane = CreateStyledButton("Add", 70);
            btnRemovePlane = CreateStyledButton("Remove", 70);
            btnAddPlane.Click += BtnAddPlane_Click;
            btnRemovePlane.Click += BtnRemovePlane_Click;

            buttonPanel.Controls.AddRange(new Control[] { btnAddPlane, btnRemovePlane });

            planesPanel.Controls.Add(lstClippingPlanes, 0, 0);
            planesPanel.Controls.Add(buttonPanel, 0, 1);
            planesGroup.Controls.Add(planesPanel);
            mainPanel.Controls.Add(planesGroup, 0, 0);

            // Plane editor
            var editorGroup = CreateStyledGroupBox("Plane Properties");
            var editorPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 9,
                AutoScroll = true
            };

            // Enable and Mirror checkboxes
            chkEnablePlane = new CheckBox { Text = "Enable Plane", AutoSize = true };
            chkEnablePlane.CheckedChanged += (s, e) => UpdateSelectedPlane();
            editorPanel.Controls.Add(chkEnablePlane, 0, 0);

            chkMirrorPlane = new CheckBox { Text = "Mirror Plane", AutoSize = true };
            chkMirrorPlane.CheckedChanged += (s, e) => UpdateSelectedPlane();
            editorPanel.Controls.Add(chkMirrorPlane, 1, 0);

            // Preset dropdown
            editorPanel.Controls.Add(new Label { Text = "Preset:", AutoSize = true }, 0, 1);
            cmbPlanePresets = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbPlanePresets.Items.AddRange(new[] { "Custom", "X Axis", "Y Axis", "Z Axis", "XY Diagonal", "XZ Diagonal", "YZ Diagonal" });
            cmbPlanePresets.SelectedIndexChanged += CmbPresets_SelectedIndexChanged;
            editorPanel.Controls.Add(cmbPlanePresets, 1, 1);

            // Normal X
            lblNormalX = new Label { Text = "Normal X: 0.00", AutoSize = true };
            editorPanel.Controls.Add(lblNormalX, 0, 2);
            trkPlaneNormalX = new TrackBar { Minimum = -100, Maximum = 100, Value = 0, Dock = DockStyle.Fill };
            trkPlaneNormalX.Scroll += (s, e) => { lblNormalX.Text = $"Normal X: {trkPlaneNormalX.Value / 100.0f:F2}"; UpdateSelectedPlane(); };
            editorPanel.Controls.Add(trkPlaneNormalX, 1, 2);

            // Normal Y
            lblNormalY = new Label { Text = "Normal Y: 0.00", AutoSize = true };
            editorPanel.Controls.Add(lblNormalY, 0, 3);
            trkPlaneNormalY = new TrackBar { Minimum = -100, Maximum = 100, Value = 0, Dock = DockStyle.Fill };
            trkPlaneNormalY.Scroll += (s, e) => { lblNormalY.Text = $"Normal Y: {trkPlaneNormalY.Value / 100.0f:F2}"; UpdateSelectedPlane(); };
            editorPanel.Controls.Add(trkPlaneNormalY, 1, 3);

            // Normal Z
            lblNormalZ = new Label { Text = "Normal Z: 1.00", AutoSize = true };
            editorPanel.Controls.Add(lblNormalZ, 0, 4);
            trkPlaneNormalZ = new TrackBar { Minimum = -100, Maximum = 100, Value = 100, Dock = DockStyle.Fill };
            trkPlaneNormalZ.Scroll += (s, e) => { lblNormalZ.Text = $"Normal Z: {trkPlaneNormalZ.Value / 100.0f:F2}"; UpdateSelectedPlane(); };
            editorPanel.Controls.Add(trkPlaneNormalZ, 1, 4);

            // Rotation
            lblRotation = new Label { Text = "Rotation: 0°", AutoSize = true };
            editorPanel.Controls.Add(lblRotation, 0, 5);
            trkPlaneRotation = new TrackBar { Minimum = 0, Maximum = 360, Value = 0, Dock = DockStyle.Fill };
            trkPlaneRotation.Scroll += (s, e) => { lblRotation.Text = $"Rotation: {trkPlaneRotation.Value}°"; UpdateSelectedPlane(); };
            editorPanel.Controls.Add(trkPlaneRotation, 1, 5);

            // Distance
            lblDistance = new Label { Text = "Distance: 0.00", AutoSize = true };
            editorPanel.Controls.Add(lblDistance, 0, 6);
            trkPlaneDistance = new TrackBar { Minimum = -200, Maximum = 200, Value = 0, Dock = DockStyle.Fill };
            trkPlaneDistance.Scroll += (s, e) => { lblDistance.Text = $"Distance: {trkPlaneDistance.Value / 100.0f:F2}"; UpdateSelectedPlane(); };
            editorPanel.Controls.Add(trkPlaneDistance, 1, 6);

            editorGroup.Controls.Add(editorPanel);
            mainPanel.Controls.Add(editorGroup, 0, 1);

            // Plane preview
            var previewGroup = CreateStyledGroupBox("Preview");
            planePreviewPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 30, 30)
            };
            planePreviewPanel.Paint += PlanePreviewPanel_Paint;
            previewGroup.Controls.Add(planePreviewPanel);
            mainPanel.Controls.Add(previewGroup, 0, 2);

            page.Controls.Add(mainPanel);
        }

        private void SetupVisualizationTab(TabPage page)
        {
            var container = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(10),
                AutoScroll = true
            };
            container.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            container.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            container.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            // Scale Bar Section
            var scaleBarGroup = CreateStyledGroupBox("Scale Bar");
            var scalePanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 5,
                AutoSize = true
            };
            scalePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
            scalePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            chkShowScaleBar = new CheckBox
            {
                Text = "Show Scale Bar",
                AutoSize = true,
                Checked = true
            };
            chkShowScaleBar.CheckedChanged += (s, e) => UpdateRenderParams();
            scalePanel.Controls.Add(chkShowScaleBar, 0, 0);
            scalePanel.SetColumnSpan(chkShowScaleBar, 2);

            chkShowScaleText = new CheckBox
            {
                Text = "Show Scale Text",
                AutoSize = true,
                Checked = true
            };
            chkShowScaleText.CheckedChanged += (s, e) => UpdateRenderParams();
            scalePanel.Controls.Add(chkShowScaleText, 0, 1);
            scalePanel.SetColumnSpan(chkShowScaleText, 2);

            scalePanel.Controls.Add(new Label { Text = "Position:", AutoSize = true }, 0, 2);
            cmbScaleBarPosition = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            // Fixed: Corrected the order to match shader expectations
            cmbScaleBarPosition.Items.AddRange(new[] { "Bottom Left", "Bottom Right", "Top Left", "Top Right" });
            cmbScaleBarPosition.SelectedIndex = 0;
            cmbScaleBarPosition.SelectedIndexChanged += (s, e) => UpdateRenderParams();
            scalePanel.Controls.Add(cmbScaleBarPosition, 1, 2);

            scalePanel.Controls.Add(new Label { Text = "Length (mm):", AutoSize = true }, 0, 3);
            numScaleBarLength = new NumericUpDown
            {
                Dock = DockStyle.Fill,
                Minimum = 10,
                Maximum = 1000,
                Value = 100,
                Increment = 10,
                DecimalPlaces = 0
            };
            numScaleBarLength.ValueChanged += (s, e) => UpdateRenderParams();
            scalePanel.Controls.Add(numScaleBarLength, 1, 3);

            lblScaleBarUnits = new Label
            {
                Text = $"Pixel Size: {mainForm.pixelSize:F3} mm",
                AutoSize = true,
                Font = new Font("Segoe UI", 8F, FontStyle.Italic)
            };
            scalePanel.Controls.Add(lblScaleBarUnits, 0, 4);
            scalePanel.SetColumnSpan(lblScaleBarUnits, 2);

            scaleBarGroup.Controls.Add(scalePanel);
            container.Controls.Add(scaleBarGroup, 0, 0);

            // Background Section
            var bgGroup = CreateStyledGroupBox("Background");
            var bgPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                Height = 40
            };

            btnBackgroundColor = CreateStyledButton("Background Color...", 150);
            btnBackgroundColor.Click += (s, e) =>
            {
                if (colorDialog == null) colorDialog = new ColorDialog();
                if (colorDialog.ShowDialog() == DialogResult.OK)
                {
                    if (volumeRenderer != null && !volumeRenderer.IsDisposed)
                    {
                        volumeRenderer.SetBackgroundColor(colorDialog.Color);
                    }
                }
            };
            bgPanel.Controls.Add(btnBackgroundColor);

            bgGroup.Controls.Add(bgPanel);
            container.Controls.Add(bgGroup, 0, 1);

            page.Controls.Add(container);
        }

        private GroupBox CreateStyledGroupBox(string title)
        {
            var groupBox = new GroupBox
            {
                Text = title,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Padding = new Padding(8, 12, 8, 8),
                Margin = new Padding(0, 0, 0, 12),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            return groupBox;
        }

        private Button CreateStyledButton(string text, int width)
        {
            var btn = new Button
            {
                Text = text,
                Width = width,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9F),
                Cursor = Cursors.Hand
            };
            return btn;
        }

        private void ApplyDarkTheme()
        {
            var darkBg = Color.FromArgb(45, 45, 48);
            var darkControl = Color.FromArgb(60, 60, 65);
            var darkButton = Color.FromArgb(70, 70, 75);
            var lightText = Color.FromArgb(220, 220, 220);
            var accentColor = Color.FromArgb(0, 122, 204);

            this.BackColor = darkBg;
            this.ForeColor = lightText;

            ApplyThemeToControl(this, darkBg, darkControl, darkButton, lightText, accentColor);
        }

        private void ApplyThemeToControl(Control control, Color bg, Color controlBg, Color buttonBg, Color text, Color accent)
        {
            foreach (Control c in control.Controls)
            {
                if (c is GroupBox || c is Panel || c is TabPage)
                {
                    c.BackColor = bg;
                    c.ForeColor = text;
                }
                else if (c is Button btn)
                {
                    btn.BackColor = buttonBg;
                    btn.ForeColor = text;
                    btn.FlatAppearance.BorderColor = accent;
                    btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(80, 80, 85);
                    btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(60, 60, 65);
                }
                else if (c is TextBox || c is ComboBox || c is NumericUpDown || c is CheckedListBox || c is ListView)
                {
                    c.BackColor = controlBg;
                    c.ForeColor = text;
                }
                else if (c is CheckBox || c is Label)
                {
                    c.ForeColor = text;
                }
                else if (c is TrackBar tb)
                {
                    // TrackBar doesn't support BackColor well
                }
                else if (c is TabControl tc)
                {
                    tc.DrawMode = TabDrawMode.OwnerDrawFixed;
                    tc.DrawItem += (s, e) =>
                    {
                        var tab = tc.TabPages[e.Index];
                        var tabBounds = tc.GetTabRect(e.Index);
                        var textBrush = new SolidBrush(e.State == DrawItemState.Selected ? text : Color.Gray);
                        var backBrush = new SolidBrush(e.State == DrawItemState.Selected ? controlBg : bg);

                        e.Graphics.FillRectangle(backBrush, e.Bounds);
                        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        e.Graphics.DrawString(tab.Text, e.Font ?? tc.Font, textBrush, tabBounds, sf);

                        textBrush.Dispose();
                        backBrush.Dispose();
                    };
                }

                if (c.HasChildren)
                {
                    ApplyThemeToControl(c, bg, controlBg, buttonBg, text, accent);
                }
            }
        }

        private void InitializeClippingPlanes()
        {
            // Add default clipping planes (disabled)
            clippingPlanes.Add(new ClippingPlane { Name = "X-Axis Plane", Normal = new Vector3(1, 0, 0), Distance = 0, Enabled = false, Mirrored = false });
            clippingPlanes.Add(new ClippingPlane { Name = "Y-Axis Plane", Normal = new Vector3(0, 1, 0), Distance = 0, Enabled = false, Mirrored = false });
            clippingPlanes.Add(new ClippingPlane { Name = "Z-Axis Plane", Normal = new Vector3(0, 0, 1), Distance = 0, Enabled = false, Mirrored = false });

            RefreshClippingPlanesList();
        }

        private void RefreshClippingPlanesList()
        {
            if (lstClippingPlanes == null) return;

            updatingUI = true;
            lstClippingPlanes.Items.Clear();
            foreach (var plane in clippingPlanes)
            {
                var item = new ListViewItem(plane.Name);
                item.SubItems.Add(GetPlaneType(plane.Normal));
                item.SubItems.Add(plane.Mirrored ? "Yes" : "No");
                item.Checked = plane.Enabled;
                lstClippingPlanes.Items.Add(item);
            }
            updatingUI = false;
        }

        private string GetPlaneType(Vector3 normal)
        {
            var n = Vector3.Normalize(normal);
            if (Math.Abs(n.X) > 0.9f) return "X";
            if (Math.Abs(n.Y) > 0.9f) return "Y";
            if (Math.Abs(n.Z) > 0.9f) return "Z";
            return "Custom";
        }

        private void PlanePreviewPanel_Paint(object sender, PaintEventArgs e)
        {
            if (selectedPlaneIndex < 0 || selectedPlaneIndex >= clippingPlanes.Count) return;

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = planePreviewPanel.ClientRectangle;

            // Draw axes
            var center = new PointF(bounds.Width / 2f, bounds.Height / 2f);
            var scale = Math.Min(bounds.Width, bounds.Height) * 0.4f;

            using (var axisPen = new Pen(Color.Gray, 1))
            {
                // X axis (red)
                g.DrawLine(new Pen(Color.Red, 2), center, new PointF(center.X + scale * 0.8f, center.Y));
                g.DrawString("X", Font, Brushes.Red, center.X + scale * 0.8f + 5, center.Y - 8);

                // Y axis (green) - inverted for screen coordinates
                g.DrawLine(new Pen(Color.Green, 2), center, new PointF(center.X, center.Y - scale * 0.8f));
                g.DrawString("Y", Font, Brushes.Green, center.X - 8, center.Y - scale * 0.8f - 15);

                // Z axis (blue) - projected
                var zProj = new PointF(center.X - scale * 0.4f, center.Y + scale * 0.4f);
                g.DrawLine(new Pen(Color.Blue, 2), center, zProj);
                g.DrawString("Z", Font, Brushes.Blue, zProj.X - 15, zProj.Y);
            }

            // Draw plane
            var plane = clippingPlanes[selectedPlaneIndex];
            var normal = Vector3.Normalize(plane.Normal);

            // Project normal to 2D
            var projX = normal.X - normal.Z * 0.5f;
            var projY = -normal.Y + normal.Z * 0.5f;
            var normalEnd = new PointF(
                center.X + projX * scale * 0.6f,
                center.Y + projY * scale * 0.6f
            );

            using (var normalPen = new Pen(Color.Yellow, 3))
            {
                normalPen.EndCap = LineCap.ArrowAnchor;
                g.DrawLine(normalPen, center, normalEnd);
            }

            // Draw plane representation
            using (var planeBrush = new SolidBrush(Color.FromArgb(64, 100, 100, 255)))
            {
                // Simple rectangle representing the plane
                var planeSize = scale * 0.5f;
                g.FillRectangle(planeBrush, center.X - planeSize / 2, center.Y - planeSize / 2, planeSize, planeSize);
            }
        }

        private void BtnAddPlane_Click(object sender, EventArgs e)
        {
            var plane = new ClippingPlane
            {
                Name = $"Plane {clippingPlanes.Count + 1}",
                Normal = new Vector3(0, 0, 1),
                Distance = 0,
                Enabled = true,
                Mirrored = false
            };
            clippingPlanes.Add(plane);
            RefreshClippingPlanesList();
            lstClippingPlanes.Items[lstClippingPlanes.Items.Count - 1].Selected = true;
            UpdateClippingPlanes();
        }

        private void BtnRemovePlane_Click(object sender, EventArgs e)
        {
            if (selectedPlaneIndex >= 0 && selectedPlaneIndex < clippingPlanes.Count)
            {
                clippingPlanes.RemoveAt(selectedPlaneIndex);
                RefreshClippingPlanesList();
                UpdateClippingPlanes();
            }
        }

        private void LstClippingPlanes_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (lstClippingPlanes.SelectedIndices.Count > 0)
            {
                selectedPlaneIndex = lstClippingPlanes.SelectedIndices[0];
                UpdatePlaneEditor();
            }
            else
            {
                selectedPlaneIndex = -1;
            }
            planePreviewPanel?.Invalidate();
        }

        private void UpdatePlaneEditor()
        {
            if (selectedPlaneIndex >= 0 && selectedPlaneIndex < clippingPlanes.Count)
            {
                updatingUI = true;
                var plane = clippingPlanes[selectedPlaneIndex];
                var n = Vector3.Normalize(plane.Normal);

                chkEnablePlane.Checked = plane.Enabled;
                chkMirrorPlane.Checked = plane.Mirrored;

                trkPlaneNormalX.Value = (int)(n.X * 100);
                trkPlaneNormalY.Value = (int)(n.Y * 100);
                trkPlaneNormalZ.Value = (int)(n.Z * 100);
                trkPlaneDistance.Value = (int)(plane.Distance * 100 / Math.Max(1, mainForm.GetWidth()));

                lblNormalX.Text = $"Normal X: {n.X:F2}";
                lblNormalY.Text = $"Normal Y: {n.Y:F2}";
                lblNormalZ.Text = $"Normal Z: {n.Z:F2}";
                lblDistance.Text = $"Distance: {trkPlaneDistance.Value / 100.0f:F2}";

                updatingUI = false;
            }
        }

        private void UpdateSelectedPlane()
        {
            if (updatingUI || selectedPlaneIndex < 0 || selectedPlaneIndex >= clippingPlanes.Count) return;

            var normal = new Vector3(
                trkPlaneNormalX.Value / 100.0f,
                trkPlaneNormalY.Value / 100.0f,
                trkPlaneNormalZ.Value / 100.0f
            );

            if (normal.LengthSquared() > 0.01f)
            {
                normal = Vector3.Normalize(normal);
            }
            else
            {
                normal = new Vector3(0, 0, 1);
            }

            // Apply rotation if needed
            if (trkPlaneRotation.Value > 0)
            {
                float angle = trkPlaneRotation.Value * (float)Math.PI / 180f;
                // Rotate around the axis perpendicular to the normal
                // This is a simplified rotation - you may want to implement more sophisticated rotation
            }

            clippingPlanes[selectedPlaneIndex] = new ClippingPlane
            {
                Name = clippingPlanes[selectedPlaneIndex].Name,
                Normal = normal,
                Distance = trkPlaneDistance.Value / 100.0f * Math.Max(1, mainForm.GetWidth()),
                Enabled = chkEnablePlane.Checked,
                Mirrored = chkMirrorPlane.Checked
            };

            RefreshClippingPlanesList();
            UpdateClippingPlanes();
            planePreviewPanel?.Invalidate();
        }

        private void CmbPresets_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (selectedPlaneIndex < 0 || updatingUI) return;

            Vector3 normal = new Vector3(0, 0, 1);
            switch (cmbPlanePresets.SelectedIndex)
            {
                case 1: normal = new Vector3(1, 0, 0); break; // X
                case 2: normal = new Vector3(0, 1, 0); break; // Y
                case 3: normal = new Vector3(0, 0, 1); break; // Z
                case 4: normal = Vector3.Normalize(new Vector3(1, 1, 0)); break; // XY
                case 5: normal = Vector3.Normalize(new Vector3(1, 0, 1)); break; // XZ
                case 6: normal = Vector3.Normalize(new Vector3(0, 1, 1)); break; // YZ
            }

            trkPlaneNormalX.Value = (int)(normal.X * 100);
            trkPlaneNormalY.Value = (int)(normal.Y * 100);
            trkPlaneNormalZ.Value = (int)(normal.Z * 100);
            UpdateSelectedPlane();
        }

        private void UpdateClippingPlanes()
        {
            UpdateRenderParams();
        }

        private void PopulateMaterials()
        {
            if (mainForm?.Materials == null || lstMaterials == null) return;

            lstMaterials.Items.Clear();
            foreach (var mat in mainForm.Materials)
            {
                lstMaterials.Items.Add($"{mat.Name} [{mat.Min}-{mat.Max}]", mat.IsVisible);
            }
            if (lstMaterials.Items.Count > 0)
                lstMaterials.SelectedIndex = 0;
        }

        private void LstMaterials_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (mainForm?.Materials == null || e.Index < 0 || e.Index >= mainForm.Materials.Count) return;

            var material = mainForm.Materials[e.Index];
            material.IsVisible = e.NewValue == CheckState.Checked;
            UpdateMaterialBuffer();
        }

        private void LstMaterials_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (lstMaterials == null || mainForm?.Materials == null || lstMaterials.SelectedIndex < 0) return;

            var material = mainForm.Materials[lstMaterials.SelectedIndex];
            lblMaterialName.Text = material.Name;
            materialColorPanel.BackColor = material.Color;
            trkOpacity.Value = (int)(material.GetOpacity() * 100);
            lblOpacity.Text = $"{trkOpacity.Value}%";
        }

        private void UpdateMaterialBuffer()
        {
            if (volumeRenderer != null && !volumeRenderer.IsDisposed)
            {
                volumeRenderer.UpdateMaterialsBuffer();
                volumeRenderer.NeedsRender = true;
            }
        }

        private void UpdateRenderParams()
        {
            if (volumeRenderer == null || volumeRenderer.IsDisposed) return;

            var p = new RenderParameters();

            // Threshold
            p.Threshold = new Vector2(
                trkMinThreshold?.Value ?? 30,
                trkMaxThreshold?.Value ?? 200
            );

            // Quality
            p.Quality = cmbQuality?.SelectedIndex ?? 1;

            // Grayscale visibility
            p.ShowGrayscale = (chkShowGrayscale?.Checked ?? true) ? 1.0f : 0.0f;

            // Scale bar
            p.ShowScaleBar = (chkShowScaleBar?.Checked ?? true) ? 1.0f : 0.0f;

            // Fixed: Map combo box index to correct shader positions
            // Combo: 0=BottomLeft, 1=BottomRight, 2=TopLeft, 3=TopRight
            // Shader expects: 0=BottomLeft, 1=BottomRight, 2=TopLeft, 3=TopRight
            p.ScaleBarPosition = cmbScaleBarPosition?.SelectedIndex ?? 0;

            // Pass scale bar settings
            p.ShowScaleText = (chkShowScaleText?.Checked ?? true) ? 1.0f : 0.0f;
            p.ScaleBarLength = (float)(numScaleBarLength?.Value ?? 100);
            p.PixelSize = (float)mainForm.pixelSize;

            // Slices - determine which slices are enabled
            p.SlicePositions = new Vector3(-1, -1, -1); // Default all disabled

            // Clipping planes
            p.ClippingPlanes = new List<Vector4>();
            foreach (var plane in clippingPlanes.Where(planeItem => planeItem.Enabled))
            {
                p.ClippingPlanes.Add(new Vector4(plane.Normal, plane.Distance));

                // If mirrored, add the opposite plane
                if (plane.Mirrored)
                {
                    p.ClippingPlanes.Add(new Vector4(-plane.Normal, -plane.Distance));
                }
            }

            volumeRenderer.SetRenderParams(p);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // Update scale bar units label
            if (lblScaleBarUnits != null)
            {
                lblScaleBarUnits.Text = $"Pixel Size: {mainForm.pixelSize:F3} mm";
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                colorDialog?.Dispose();

                // Clear references
                volumeRenderer = null;
                viewerForm = null;
                mainForm = null;
            }
            base.Dispose(disposing);
        }
    }
}