// Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
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
                ItemSize = new Size(110, 32),
                SizeMode = TabSizeMode.Fixed
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
            var container = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10),
                AutoScroll = true
            };

            // Quality Section
            var qualityGroup = CreateStyledGroupBox("Rendering Quality");
            qualityGroup.Location = new Point(10, 10);
            qualityGroup.Size = new Size(container.ClientSize.Width - 40, 80);
            qualityGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            var qualityLabel = new Label
            {
                Text = "Quality:",
                Location = new Point(10, 30),
                AutoSize = true
            };

            cmbQuality = new ComboBox
            {
                Location = new Point(100, 27),
                Size = new Size(200, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9F)
            };
            cmbQuality.Items.AddRange(new[] { "Fast (Draft)", "Balanced", "High Quality" });
            cmbQuality.SelectedIndex = 1;
            cmbQuality.SelectedIndexChanged += (s, e) => UpdateRenderParams();

            qualityGroup.Controls.Add(qualityLabel);
            qualityGroup.Controls.Add(cmbQuality);
            container.Controls.Add(qualityGroup);

            // Grayscale Section
            var grayscaleGroup = CreateStyledGroupBox("Grayscale Volume");
            grayscaleGroup.Location = new Point(10, 100);
            grayscaleGroup.Size = new Size(container.ClientSize.Width - 40, 220);
            grayscaleGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            chkShowGrayscale = new CheckBox
            {
                Text = "Show Grayscale Data",
                Location = new Point(10, 30),
                AutoSize = true,
                Checked = true,
                Font = new Font("Segoe UI", 9F)
            };
            chkShowGrayscale.CheckedChanged += (s, e) => UpdateRenderParams();

            lblMinThreshold = new Label
            {
                Text = "Min Threshold: 30",
                Location = new Point(10, 65),
                AutoSize = true
            };

            trkMinThreshold = new TrackBar
            {
                Location = new Point(10, 85),
                Size = new Size(grayscaleGroup.ClientSize.Width - 30, 45),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Minimum = 0,
                Maximum = 255,
                Value = 30,
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

            lblMaxThreshold = new Label
            {
                Text = "Max Threshold: 200",
                Location = new Point(10, 135),
                AutoSize = true
            };

            trkMaxThreshold = new TrackBar
            {
                Location = new Point(10, 155),
                Size = new Size(grayscaleGroup.ClientSize.Width - 30, 45),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Minimum = 0,
                Maximum = 255,
                Value = 200,
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

            grayscaleGroup.Controls.Add(chkShowGrayscale);
            grayscaleGroup.Controls.Add(lblMinThreshold);
            grayscaleGroup.Controls.Add(trkMinThreshold);
            grayscaleGroup.Controls.Add(lblMaxThreshold);
            grayscaleGroup.Controls.Add(trkMaxThreshold);
            container.Controls.Add(grayscaleGroup);

            page.Controls.Add(container);
        }

        private void SetupMaterialsTab(TabPage page)
        {
            var container = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10)
            };

            // Materials list
            var materialsGroup = CreateStyledGroupBox("Material Visibility");
            materialsGroup.Location = new Point(10, 10);
            materialsGroup.Size = new Size(container.ClientSize.Width - 20, 300);
            materialsGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            lstMaterials = new CheckedListBox
            {
                Location = new Point(10, 25),
                Size = new Size(materialsGroup.ClientSize.Width - 20, 260),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                CheckOnClick = true,
                Font = new Font("Segoe UI", 9F),
                BorderStyle = BorderStyle.FixedSingle
            };
            lstMaterials.ItemCheck += LstMaterials_ItemCheck;
            lstMaterials.SelectedIndexChanged += LstMaterials_SelectedIndexChanged;

            materialsGroup.Controls.Add(lstMaterials);
            container.Controls.Add(materialsGroup);

            // Material properties
            var propertiesGroup = CreateStyledGroupBox("Material Properties");
            propertiesGroup.Location = new Point(10, 320);
            propertiesGroup.Size = new Size(container.ClientSize.Width - 20, 200);
            propertiesGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            lblMaterialName = new Label
            {
                Text = "Select a material",
                Location = new Point(10, 25),
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                AutoSize = true
            };

            var lblColor = new Label
            {
                Text = "Color:",
                Location = new Point(10, 55),
                AutoSize = true
            };

            materialColorPanel = new Panel
            {
                Location = new Point(60, 53),
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

            var lblOpacityText = new Label
            {
                Text = "Opacity:",
                Location = new Point(10, 95),
                AutoSize = true
            };

            trkOpacity = new TrackBar
            {
                Location = new Point(10, 115),
                Size = new Size(250, 45),
                Minimum = 0,
                Maximum = 100,
                Value = 100,
                TickFrequency = 10,
                TickStyle = TickStyle.BottomRight
            };

            lblOpacity = new Label
            {
                Text = "100%",
                Location = new Point(270, 120),
                AutoSize = true
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

            propertiesGroup.Controls.Add(lblMaterialName);
            propertiesGroup.Controls.Add(lblColor);
            propertiesGroup.Controls.Add(materialColorPanel);
            propertiesGroup.Controls.Add(lblOpacityText);
            propertiesGroup.Controls.Add(trkOpacity);
            propertiesGroup.Controls.Add(lblOpacity);
            container.Controls.Add(propertiesGroup);

            page.Controls.Add(container);
        }

        private void SetupClippingTab(TabPage page)
        {
            var container = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10),
                AutoScroll = true
            };

            // Clipping planes list
            var planesGroup = CreateStyledGroupBox("Clipping Planes");
            planesGroup.Location = new Point(10, 10);
            planesGroup.Size = new Size(container.ClientSize.Width - 20, 180);
            planesGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            lstClippingPlanes = new ListView
            {
                Location = new Point(10, 25),
                Size = new Size(planesGroup.ClientSize.Width - 20, 110),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                CheckBoxes = true,
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

            btnAddPlane = CreateStyledButton("Add", 70);
            btnAddPlane.Location = new Point(10, 145);
            btnAddPlane.Click += BtnAddPlane_Click;

            btnRemovePlane = CreateStyledButton("Remove", 70);
            btnRemovePlane.Location = new Point(90, 145);
            btnRemovePlane.Click += BtnRemovePlane_Click;

            planesGroup.Controls.Add(lstClippingPlanes);
            planesGroup.Controls.Add(btnAddPlane);
            planesGroup.Controls.Add(btnRemovePlane);
            container.Controls.Add(planesGroup);

            // Plane editor
            var editorGroup = CreateStyledGroupBox("Plane Properties");
            editorGroup.Location = new Point(10, 200);
            editorGroup.Size = new Size(container.ClientSize.Width - 20, 350);
            editorGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            chkEnablePlane = new CheckBox
            {
                Text = "Enable Plane",
                Location = new Point(10, 25),
                AutoSize = true
            };
            chkEnablePlane.CheckedChanged += (s, e) => UpdateSelectedPlane();

            chkMirrorPlane = new CheckBox
            {
                Text = "Mirror Plane",
                Location = new Point(150, 25),
                AutoSize = true
            };
            chkMirrorPlane.CheckedChanged += (s, e) => UpdateSelectedPlane();

            var lblPreset = new Label
            {
                Text = "Preset:",
                Location = new Point(10, 55),
                AutoSize = true
            };

            cmbPlanePresets = new ComboBox
            {
                Location = new Point(60, 52),
                Size = new Size(150, 24),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbPlanePresets.Items.AddRange(new[] { "Custom", "X Axis", "Y Axis", "Z Axis", "XY Diagonal", "XZ Diagonal", "YZ Diagonal" });
            cmbPlanePresets.SelectedIndexChanged += CmbPresets_SelectedIndexChanged;

            // Normal controls
            lblNormalX = new Label { Text = "Normal X: 0.00", Location = new Point(10, 90), AutoSize = true };
            trkPlaneNormalX = new TrackBar
            {
                Location = new Point(10, 110),
                Size = new Size(300, 45),
                Minimum = -100,
                Maximum = 100,
                Value = 0,
                TickFrequency = 20
            };
            trkPlaneNormalX.Scroll += (s, e) => { lblNormalX.Text = $"Normal X: {trkPlaneNormalX.Value / 100.0f:F2}"; UpdateSelectedPlane(); };

            lblNormalY = new Label { Text = "Normal Y: 0.00", Location = new Point(10, 155), AutoSize = true };
            trkPlaneNormalY = new TrackBar
            {
                Location = new Point(10, 175),
                Size = new Size(300, 45),
                Minimum = -100,
                Maximum = 100,
                Value = 0,
                TickFrequency = 20
            };
            trkPlaneNormalY.Scroll += (s, e) => { lblNormalY.Text = $"Normal Y: {trkPlaneNormalY.Value / 100.0f:F2}"; UpdateSelectedPlane(); };

            lblNormalZ = new Label { Text = "Normal Z: 1.00", Location = new Point(10, 220), AutoSize = true };
            trkPlaneNormalZ = new TrackBar
            {
                Location = new Point(10, 240),
                Size = new Size(300, 45),
                Minimum = -100,
                Maximum = 100,
                Value = 100,
                TickFrequency = 20
            };
            trkPlaneNormalZ.Scroll += (s, e) => { lblNormalZ.Text = $"Normal Z: {trkPlaneNormalZ.Value / 100.0f:F2}"; UpdateSelectedPlane(); };

            lblDistance = new Label { Text = "Distance: 0.00", Location = new Point(10, 285), AutoSize = true };
            trkPlaneDistance = new TrackBar
            {
                Location = new Point(10, 305),
                Size = new Size(300, 45),
                Minimum = -200,
                Maximum = 200,
                Value = 0,
                TickFrequency = 20
            };
            trkPlaneDistance.Scroll += (s, e) => { lblDistance.Text = $"Distance: {trkPlaneDistance.Value / 100.0f:F2}"; UpdateSelectedPlane(); };

            editorGroup.Controls.Add(chkEnablePlane);
            editorGroup.Controls.Add(chkMirrorPlane);
            editorGroup.Controls.Add(lblPreset);
            editorGroup.Controls.Add(cmbPlanePresets);
            editorGroup.Controls.Add(lblNormalX);
            editorGroup.Controls.Add(trkPlaneNormalX);
            editorGroup.Controls.Add(lblNormalY);
            editorGroup.Controls.Add(trkPlaneNormalY);
            editorGroup.Controls.Add(lblNormalZ);
            editorGroup.Controls.Add(trkPlaneNormalZ);
            editorGroup.Controls.Add(lblDistance);
            editorGroup.Controls.Add(trkPlaneDistance);
            container.Controls.Add(editorGroup);

            // Plane preview
            var previewGroup = CreateStyledGroupBox("Preview");
            previewGroup.Location = new Point(10, 560);
            previewGroup.Size = new Size(container.ClientSize.Width - 20, 100);
            previewGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            planePreviewPanel = new Panel
            {
                Location = new Point(10, 25),
                Size = new Size(previewGroup.ClientSize.Width - 20, 65),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.FromArgb(30, 30, 30)
            };
            planePreviewPanel.Paint += PlanePreviewPanel_Paint;

            previewGroup.Controls.Add(planePreviewPanel);
            container.Controls.Add(previewGroup);

            page.Controls.Add(container);
        }

        private void SetupVisualizationTab(TabPage page)
        {
            var container = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10),
                AutoScroll = true
            };

            // Scale Bar Section
            var scaleBarGroup = CreateStyledGroupBox("Scale Bar");
            scaleBarGroup.Location = new Point(10, 10);
            scaleBarGroup.Size = new Size(container.ClientSize.Width - 20, 200);
            scaleBarGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            chkShowScaleBar = new CheckBox
            {
                Text = "Show Scale Bar",
                Location = new Point(10, 30),
                AutoSize = true,
                Checked = true
            };
            chkShowScaleBar.CheckedChanged += (s, e) => UpdateRenderParams();

            chkShowScaleText = new CheckBox
            {
                Text = "Show Scale Text",
                Location = new Point(10, 55),
                AutoSize = true,
                Checked = true
            };
            chkShowScaleText.CheckedChanged += (s, e) => UpdateRenderParams();

            var lblPosition = new Label
            {
                Text = "Position:",
                Location = new Point(10, 85),
                AutoSize = true
            };

            cmbScaleBarPosition = new ComboBox
            {
                Location = new Point(80, 82),
                Size = new Size(150, 24),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbScaleBarPosition.Items.AddRange(new[] { "Bottom Left", "Bottom Right", "Top Left", "Top Right" });
            cmbScaleBarPosition.SelectedIndex = 0;
            cmbScaleBarPosition.SelectedIndexChanged += (s, e) => UpdateRenderParams();

            var lblLength = new Label
            {
                Text = "Length:",
                Location = new Point(10, 115),
                AutoSize = true
            };

            numScaleBarLength = new NumericUpDown
            {
                Location = new Point(100, 113),
                Size = new Size(100, 24),
                Minimum = 1,
                Maximum = 10000,
                Value = 100,
                Increment = 10,
                DecimalPlaces = 0
            };
            numScaleBarLength.ValueChanged += (s, e) => UpdateRenderParams();

            // Add a label to show the units
            var lblUnits = new Label
            {
                Text = "Unit",
                Location = new Point(205, 115),
                AutoSize = true
            };

            lblScaleBarUnits = new Label
            {
                Text = GetPixelSizeText(),
                Location = new Point(10, 145),
                AutoSize = true,
                Font = new Font("Segoe UI", 8F, FontStyle.Italic)
            };

            scaleBarGroup.Controls.Add(chkShowScaleBar);
            scaleBarGroup.Controls.Add(chkShowScaleText);
            scaleBarGroup.Controls.Add(lblPosition);
            scaleBarGroup.Controls.Add(cmbScaleBarPosition);
            scaleBarGroup.Controls.Add(lblLength);
            scaleBarGroup.Controls.Add(numScaleBarLength);
            scaleBarGroup.Controls.Add(lblUnits);
            scaleBarGroup.Controls.Add(lblScaleBarUnits);
            container.Controls.Add(scaleBarGroup);

            // Background Section
            var bgGroup = CreateStyledGroupBox("Background");
            bgGroup.Location = new Point(10, 220);
            bgGroup.Size = new Size(container.ClientSize.Width - 20, 80);
            bgGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            btnBackgroundColor = CreateStyledButton("Background Color...", 150);
            btnBackgroundColor.Location = new Point(10, 30);
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

            bgGroup.Controls.Add(btnBackgroundColor);
            container.Controls.Add(bgGroup);

            // Screenshot Section
            var screenshotGroup = CreateStyledGroupBox("Screenshot");
            screenshotGroup.Location = new Point(10, 310);
            screenshotGroup.Size = new Size(container.ClientSize.Width - 20, 80);
            screenshotGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            Button btnScreenshot = CreateStyledButton("Take Screenshot", 150);
            btnScreenshot.Location = new Point(10, 30);
            btnScreenshot.Click += (s, e) => TakeScreenshot();

            screenshotGroup.Controls.Add(btnScreenshot);
            container.Controls.Add(screenshotGroup);

            page.Controls.Add(container);

            // Set initial scale bar length based on pixel size
            SetDefaultScaleBarLength();
        }
        private string GetPixelSizeText()
        {
            double pixelSizeMicrometers = mainForm.pixelSize * 1e6;
            if (pixelSizeMicrometers < 1000)
            {
                return $"Pixel Size: {pixelSizeMicrometers:F1} µm";
            }
            else
            {
                double pixelSizeMillimeters = mainForm.pixelSize * 1e3;
                return $"Pixel Size: {pixelSizeMillimeters:F3} mm";
            }
        }
        private void SetDefaultScaleBarLength()
        {
            if (numScaleBarLength == null) return;

            double pixelSizeMicrometers = mainForm.pixelSize * 1e6;

            // Set scale bar length based on pixel size
            if (pixelSizeMicrometers < 10) // Less than 10 micrometers
            {
                numScaleBarLength.Value = 10; // 10 mm = 10,000 micrometers
            }
            else if (pixelSizeMicrometers < 100) // 10-100 micrometers
            {
                numScaleBarLength.Value = 50; // 50 mm
            }
            else if (pixelSizeMicrometers < 1000) // 100-1000 micrometers
            {
                numScaleBarLength.Value = 100; // 100 mm
            }
            else // millimeter scale
            {
                numScaleBarLength.Value = 200; // 200 mm
            }
        }
        public void TakeScreenshot()
        {
            if (viewerForm == null || viewerForm.IsDisposed || volumeRenderer == null || volumeRenderer.IsDisposed)
            {
                MessageBox.Show("No active 3D view to capture.", "Screenshot Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                // Create a bitmap of the viewer form's client area
                Rectangle bounds = viewerForm.ClientRectangle;
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    MessageBox.Show("Invalid window size for screenshot.", "Screenshot Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                using (Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
                {
                    // Capture the viewer form's content
                    viewerForm.DrawToBitmap(bitmap, bounds);

                    // Show save dialog
                    using (SaveFileDialog sfd = new SaveFileDialog())
                    {
                        sfd.Filter = "PNG Image|*.png|JPEG Image|*.jpg|BMP Image|*.bmp|All Files|*.*";
                        sfd.Title = "Save 3D View Screenshot";
                        sfd.DefaultExt = "png";
                        sfd.FileName = $"3DView_{DateTime.Now:yyyyMMdd_HHmmss}";

                        if (sfd.ShowDialog() == DialogResult.OK)
                        {
                            // Determine the image format based on extension
                            ImageFormat format = ImageFormat.Png;
                            string ext = Path.GetExtension(sfd.FileName).ToLower();

                            switch (ext)
                            {
                                case ".jpg":
                                case ".jpeg":
                                    format = ImageFormat.Jpeg;
                                    break;
                                case ".bmp":
                                    format = ImageFormat.Bmp;
                                    break;
                                case ".gif":
                                    format = ImageFormat.Gif;
                                    break;
                                case ".tiff":
                                case ".tif":
                                    format = ImageFormat.Tiff;
                                    break;
                            }

                            // Save the image
                            bitmap.Save(sfd.FileName, format);

                            Logger.Log($"[D3D11ControlPanel] Screenshot saved to: {sfd.FileName}");

                            // Show confirmation
                            MessageBox.Show($"Screenshot saved successfully to:\n{sfd.FileName}",
                                "Screenshot Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[D3D11ControlPanel] Screenshot error: {ex.Message}");
                MessageBox.Show($"Error taking screenshot: {ex.Message}", "Screenshot Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private GroupBox CreateStyledGroupBox(string title)
        {
            var groupBox = new GroupBox
            {
                Text = title,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Padding = new Padding(8, 12, 8, 8)
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
                lblScaleBarUnits.Text = GetPixelSizeText();
            }

            // Set appropriate default scale bar length
            SetDefaultScaleBarLength();

            // Force initial render parameters update
            UpdateRenderParams();

            // Force layout update
            PerformLayout();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);

            // Force relayout of all controls
            PerformLayout();
            Invalidate(true);
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