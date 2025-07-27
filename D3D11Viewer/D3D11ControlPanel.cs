// Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
        public bool SlabMode; // Renamed from Mirrored
        public float SlabTranslation; // New field for translation
        public string Name;
    }

    public abstract class MeasurementObject
    {
        private static int counter = 0;
        public string Name { get; set; }
        protected MeasurementObject() { Name = $"Item {++counter}"; }
        public abstract string GetDetails(float pixelSize);
    }

    public class MeasurementPoint : MeasurementObject
    {
        public Vector3 Position;
        public override string GetDetails(float pixelSize) => $"Point at ({Position.X:F1}, {Position.Y:F1}, {Position.Z:F1})";
    }

    public class MeasurementLine : MeasurementObject
    {
        public Vector3 Start;
        public Vector3 End;
        public override string GetDetails(float pixelSize)
        {
            float lengthMeters = Vector3.Distance(Start, End) * pixelSize;
            string unit = "m";
            if (lengthMeters < 1) { lengthMeters *= 1000; unit = "mm"; }
            if (lengthMeters < 1 && unit == "mm") { lengthMeters *= 1000; unit = "µm"; }
            return $"Line, Length: {lengthMeters:F2} {unit}";
        }
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
        private ComboBox cmbExportQuality;


        // Materials tab
        private CheckedListBox lstMaterials;
        private TrackBar trkOpacity;
        private Label lblOpacity;
        private Label lblMaterialName;
        private Panel materialColorPanel;

        // Clipping tab
        private ListView lstClippingPlanes;
        private Button btnAddPlane, btnRemovePlane;
        private CheckBox chkEnablePlane, chkSlabMode, chkDrawPlanes;
        private TrackBar trkPlaneNormalX, trkPlaneNormalY, trkPlaneNormalZ;
        private TrackBar trkPlaneDistance, trkSlabTranslation;
        private Label lblNormalX, lblNormalY, lblNormalZ;
        private Label lblDistance, lblSlabTranslation;
        private Label lblDistanceLabel; // To change text between Distance/Thickness
        private ComboBox cmbPlanePresets;
        private Panel planePreviewPanel;

        // Measurement Tab
        private ListView lstMeasurements;
        private Button btnPlacePoint, btnDrawLine, btnRemoveMeasurement, btnClearMeasurements;
        private List<MeasurementObject> measurements = new List<MeasurementObject>();
        private Vector3? lineStartPoint = null;


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
            viewerForm.PointPicked += ViewerForm_PointPicked;

            InitializeComponent();
            PopulateMaterials();
            InitializeClippingPlanes();
            ApplyDarkTheme();
            PopulateInfoTab(); // Populate the new tab
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
            var tabMeasurement = new TabPage("Measurement");
            var tabVisualization = new TabPage("Display");
            var tabInfo = new TabPage("Info"); // New Info tab

            SetupRenderingTab(tabRendering);
            SetupMaterialsTab(tabMaterials);
            SetupClippingTab(tabClipping);
            SetupMeasurementTab(tabMeasurement);
            SetupVisualizationTab(tabVisualization);
            SetupInfoTab(tabInfo); // Setup the new tab

            mainTabControl.TabPages.AddRange(new TabPage[] { tabRendering, tabMaterials, tabClipping, tabMeasurement, tabVisualization, tabInfo });
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

            // Export Section
            var exportGroup = CreateStyledGroupBox("Export");
            exportGroup.Location = new Point(10, grayscaleGroup.Bottom + 10);
            exportGroup.Size = new Size(container.ClientSize.Width - 40, 120);
            exportGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            var exportQualityLabel = new Label
            {
                Text = "Export Quality:",
                Location = new Point(10, 30),
                AutoSize = true
            };

            cmbExportQuality = new ComboBox
            {
                Location = new Point(120, 27),
                Size = new Size(150, 24),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbExportQuality.Items.AddRange(new[] { "Low (1/4 Res)", "Medium (1/2 Res)", "High (Full Res)" });
            cmbExportQuality.SelectedIndex = 1; // Default to Medium

            var btnExport = CreateStyledButton("Export as STL...", 150);
            btnExport.Location = new Point(10, 65);
            btnExport.Click += BtnExport_Click;

            exportGroup.Controls.Add(exportQualityLabel);
            exportGroup.Controls.Add(cmbExportQuality);
            exportGroup.Controls.Add(btnExport);
            container.Controls.Add(exportGroup);

            page.Controls.Add(container);
        }

        private async void BtnExport_Click(object sender, EventArgs e)
        {
            if (volumeRenderer == null || mainForm.volumeData == null || mainForm.volumeLabels == null)
            {
                MessageBox.Show("Renderer or volume data not available for export.", "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            using (var sfd = new SaveFileDialog())
            {
                sfd.Filter = "STL Binary File|*.stl";
                sfd.Title = "Export Visible Materials";
                sfd.FileName = $"CTS_Export_{DateTime.Now:yyyyMMdd_HHmmss}.stl";

                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    var quality = (VolumeExporter.ExportQuality)cmbExportQuality.SelectedIndex;
                    var parameters = GetCurrentRenderParameters();

                    var progressForm = new Form
                    {
                        Text = "Exporting...",
                        Size = new Size(300, 100),
                        StartPosition = FormStartPosition.CenterParent,
                        FormBorderStyle = FormBorderStyle.FixedDialog,
                        ControlBox = false
                    };
                    var progressLabel = new Label
                    {
                        Text = "Generating mesh, please wait...\nThis may take several minutes.",
                        Dock = DockStyle.Fill,
                        TextAlign = ContentAlignment.MiddleCenter
                    };
                    progressForm.Controls.Add(progressLabel);

                    try
                    {
                        this.Enabled = false;
                        viewerForm.Enabled = false;
                        progressForm.Show(this);
                        Application.DoEvents(); // Ensure the progress form is displayed

                        var exporter = new VolumeExporter(mainForm.volumeData, mainForm.volumeLabels, mainForm.Materials, parameters);
                        await exporter.ExportToStlAsync(sfd.FileName, quality);

                        progressForm.Close();
                        MessageBox.Show($"Successfully exported to:\n{sfd.FileName}", "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        progressForm.Close();
                        Logger.Log($"[STL Export] Error: {ex}");
                        MessageBox.Show($"An error occurred during export:\n{ex.Message}", "Export Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    finally
                    {
                        this.Enabled = true;
                        viewerForm.Enabled = true;
                    }
                }
            }
        }

        private RenderParameters GetCurrentRenderParameters()
        {
            var p = new RenderParameters();

            p.Threshold = new Vector2(
                trkMinThreshold?.Value ?? 30,
                trkMaxThreshold?.Value ?? 200
            );
            p.ShowGrayscale = (chkShowGrayscale?.Checked ?? true) ? 1.0f : 0.0f;
            p.ClippingPlanes = new List<Vector4>();
            foreach (var plane in clippingPlanes.Where(planeItem => planeItem.Enabled))
            {
                if (plane.SlabMode)
                {
                    float thickness = plane.Distance;
                    float position = plane.SlabTranslation;
                    float lowerBound = position - thickness / 2.0f;
                    float upperBound = position + thickness / 2.0f;
                    p.ClippingPlanes.Add(new Vector4(plane.Normal, upperBound));
                    p.ClippingPlanes.Add(new Vector4(-plane.Normal, -lowerBound));
                }
                else
                {
                    p.ClippingPlanes.Add(new Vector4(plane.Normal, plane.Distance));
                }
            }
            return p;
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
                if (lstMaterials.SelectedIndex >= 0)
                {
                    var material = mainForm.Materials[lstMaterials.SelectedIndex];
                    // Use linear mapping instead of quadratic
                    float opacity = trkOpacity.Value / 100.0f;
                    material.SetOpacity(opacity);
                    lblOpacity.Text = $"{trkOpacity.Value}%";
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
            lstClippingPlanes.Columns.Add("Slab", 60);
            lstClippingPlanes.ItemChecked += (s, e) =>
            {
                if (!updatingUI && e.Item.Index < clippingPlanes.Count)
                {
                    var plane = clippingPlanes[e.Item.Index];
                    plane.Enabled = e.Item.Checked;
                    clippingPlanes[e.Item.Index] = plane;
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
            editorGroup.Size = new Size(container.ClientSize.Width - 20, 420); // Increased height
            editorGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            chkEnablePlane = new CheckBox
            {
                Text = "Enable Plane",
                Location = new Point(10, 25),
                AutoSize = true
            };
            chkEnablePlane.CheckedChanged += ChkEnablePlane_CheckedChanged;

            chkSlabMode = new CheckBox
            {
                Text = "Slab Mode", // Renamed
                Location = new Point(120, 25),
                AutoSize = true
            };
            chkSlabMode.CheckedChanged += ChkSlabMode_CheckedChanged;

            chkDrawPlanes = new CheckBox
            {
                Text = "Show Visual Aid",
                Location = new Point(230, 25),
                AutoSize = true,
                Checked = true
            };
            chkDrawPlanes.CheckedChanged += (s, e) => UpdateRenderParams();

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
            trkPlaneNormalX.Scroll += (s, e) => {
                if (updatingUI) return;
                lblNormalX.Text = $"Normal X: {trkPlaneNormalX.Value / 100.0f:F2}";
                UpdateSelectedPlaneFromAllControls();
                planePreviewPanel?.Invalidate();
            };

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
            trkPlaneNormalY.Scroll += (s, e) => {
                if (updatingUI) return;
                lblNormalY.Text = $"Normal Y: {trkPlaneNormalY.Value / 100.0f:F2}";
                UpdateSelectedPlaneFromAllControls();
                planePreviewPanel?.Invalidate();
            };

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
            trkPlaneNormalZ.Scroll += (s, e) => {
                if (updatingUI) return;
                lblNormalZ.Text = $"Normal Z: {trkPlaneNormalZ.Value / 100.0f:F2}";
                UpdateSelectedPlaneFromAllControls();
                planePreviewPanel?.Invalidate();
            };

            // Distance / Thickness controls
            lblDistanceLabel = new Label { Text = "Distance:", Location = new Point(10, 285), AutoSize = true };
            lblDistance = new Label { Text = "0.00", Location = new Point(120, 285), AutoSize = true };
            trkPlaneDistance = new TrackBar
            {
                Location = new Point(10, 305),
                Size = new Size(300, 45),
                Minimum = -200,
                Maximum = 200,
                Value = 0,
                TickFrequency = 20
            };
            trkPlaneDistance.Scroll += (s, e) => {
                if (updatingUI) return;
                lblDistance.Text = $"{trkPlaneDistance.Value / 100.0f:F2}";
                UpdateSelectedPlaneFromAllControls();
                planePreviewPanel?.Invalidate();
            };

            // New Slab Translation controls
            lblSlabTranslation = new Label { Text = "Slab Position: 0.00", Location = new Point(10, 350), AutoSize = true, Visible = false };
            trkSlabTranslation = new TrackBar
            {
                Location = new Point(10, 370),
                Size = new Size(300, 45),
                Minimum = -200,
                Maximum = 200,
                Value = 0,
                TickFrequency = 20,
                Visible = false
            };
            trkSlabTranslation.Scroll += (s, e) => {
                if (updatingUI) return;
                lblSlabTranslation.Text = $"Slab Position: {trkSlabTranslation.Value / 100.0f:F2}";
                UpdateSelectedPlaneFromAllControls();
            };


            editorGroup.Controls.Add(chkEnablePlane);
            editorGroup.Controls.Add(chkSlabMode);
            editorGroup.Controls.Add(chkDrawPlanes);
            editorGroup.Controls.Add(lblPreset);
            editorGroup.Controls.Add(cmbPlanePresets);
            editorGroup.Controls.Add(lblNormalX);
            editorGroup.Controls.Add(trkPlaneNormalX);
            editorGroup.Controls.Add(lblNormalY);
            editorGroup.Controls.Add(trkPlaneNormalY);
            editorGroup.Controls.Add(lblNormalZ);
            editorGroup.Controls.Add(trkPlaneNormalZ);
            editorGroup.Controls.Add(lblDistanceLabel);
            editorGroup.Controls.Add(lblDistance);
            editorGroup.Controls.Add(trkPlaneDistance);
            editorGroup.Controls.Add(lblSlabTranslation);
            editorGroup.Controls.Add(trkSlabTranslation);
            container.Controls.Add(editorGroup);


            // Plane preview
            var previewGroup = CreateStyledGroupBox("Preview");
            previewGroup.Location = new Point(10, 630); // Moved down
            previewGroup.Size = new Size(container.ClientSize.Width - 20, 100);
            previewGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            planePreviewPanel = new Panel
            {
                Location = new Point(10, 25),
                Size = new Size(previewGroup.ClientSize.Width - 20, 65),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.FromArgb(30, 30, 30),
                BorderStyle = BorderStyle.FixedSingle
            };
            planePreviewPanel.Paint += PlanePreviewPanel_Paint;

            previewGroup.Controls.Add(planePreviewPanel);
            container.Controls.Add(previewGroup);

            page.Controls.Add(container);
        }

        private void SetupMeasurementTab(TabPage page)
        {
            var container = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10),
                AutoScroll = true
            };

            // Tools group
            var toolsGroup = CreateStyledGroupBox("Tools");
            toolsGroup.Location = new Point(10, 10);
            toolsGroup.Size = new Size(container.ClientSize.Width - 20, 80);
            toolsGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            btnPlacePoint = CreateStyledButton("Place Point", 100);
            btnPlacePoint.Location = new Point(10, 30);
            btnPlacePoint.Click += (s, e) => viewerForm.CurrentMeasurementMode = MeasurementMode.PlacePoint;

            btnDrawLine = CreateStyledButton("Draw Line", 100);
            btnDrawLine.Location = new Point(120, 30);
            btnDrawLine.Click += (s, e) => viewerForm.CurrentMeasurementMode = MeasurementMode.DrawLineStart;

            toolsGroup.Controls.Add(btnPlacePoint);
            toolsGroup.Controls.Add(btnDrawLine);
            container.Controls.Add(toolsGroup);

            // Measurements list
            var listGroup = CreateStyledGroupBox("Measurements");
            listGroup.Location = new Point(10, 100);
            listGroup.Size = new Size(container.ClientSize.Width - 20, 300);
            listGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            lstMeasurements = new ListView
            {
                Location = new Point(10, 25),
                Size = new Size(listGroup.ClientSize.Width - 20, 220),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Font = new Font("Segoe UI", 9F)
            };
            lstMeasurements.Columns.Add("Name", 120);
            lstMeasurements.Columns.Add("Details", 200);

            btnRemoveMeasurement = CreateStyledButton("Remove", 80);
            btnRemoveMeasurement.Location = new Point(10, 255);
            btnRemoveMeasurement.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnRemoveMeasurement.Click += BtnRemoveMeasurement_Click;

            btnClearMeasurements = CreateStyledButton("Clear All", 80);
            btnClearMeasurements.Location = new Point(100, 255);
            btnClearMeasurements.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnClearMeasurements.Click += BtnClearMeasurements_Click;

            listGroup.Controls.Add(lstMeasurements);
            listGroup.Controls.Add(btnRemoveMeasurement);
            listGroup.Controls.Add(btnClearMeasurements);
            container.Controls.Add(listGroup);

            page.Controls.Add(container);
        }

        // --- NEW METHOD: Setup for the Info tab ---
        private void SetupInfoTab(TabPage page)
        {
            var container = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10),
                AutoScroll = true
            };

            // System Info
            var systemGroup = CreateStyledGroupBox("System Information");
            systemGroup.Location = new Point(10, 10);
            systemGroup.Size = new Size(container.ClientSize.Width - 20, 80);
            systemGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            var lblGpu = new Label { Text = "GPU:", Location = new Point(10, 30), AutoSize = true };
            var lblGpuNameValue = new Label { Name = "lblGpuNameValue", Location = new Point(120, 30), Size = new Size(280, 40) };
            systemGroup.Controls.Add(lblGpu);
            systemGroup.Controls.Add(lblGpuNameValue);
            container.Controls.Add(systemGroup);

            // Volume Info
            var volumeGroup = CreateStyledGroupBox("Volume Information");
            volumeGroup.Location = new Point(10, 100);
            volumeGroup.Size = new Size(container.ClientSize.Width - 20, 120);
            volumeGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            var lblDims = new Label { Text = "Dimensions:", Location = new Point(10, 30), AutoSize = true };
            var lblVolumeDimsValue = new Label { Name = "lblVolumeDimsValue", Location = new Point(120, 30), AutoSize = true };
            var lblChunk = new Label { Text = "Chunking:", Location = new Point(10, 55), AutoSize = true };
            var lblChunkInfoValue = new Label { Name = "lblChunkInfoValue", Location = new Point(120, 55), AutoSize = true };
            var lblCache = new Label { Text = "GPU Cache:", Location = new Point(10, 80), AutoSize = true };
            var lblCacheSizeValue = new Label { Name = "lblCacheSizeValue", Location = new Point(120, 80), AutoSize = true };

            volumeGroup.Controls.Add(lblDims);
            volumeGroup.Controls.Add(lblVolumeDimsValue);
            volumeGroup.Controls.Add(lblChunk);
            volumeGroup.Controls.Add(lblChunkInfoValue);
            volumeGroup.Controls.Add(lblCache);
            volumeGroup.Controls.Add(lblCacheSizeValue);
            container.Controls.Add(volumeGroup);

            // Material Info
            var materialGroup = CreateStyledGroupBox("Material List");
            materialGroup.Location = new Point(10, 230);
            materialGroup.Size = new Size(container.ClientSize.Width - 20, 400);
            materialGroup.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            var lstMaterialInfo = new ListView
            {
                Name = "lstMaterialInfo",
                Location = new Point(10, 25),
                Size = new Size(materialGroup.ClientSize.Width - 20, materialGroup.ClientSize.Height - 40),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Font = new Font("Segoe UI", 9F)
            };
            lstMaterialInfo.Columns.Add("Color", 40);
            lstMaterialInfo.Columns.Add("ID", 40);
            lstMaterialInfo.Columns.Add("Name", 180);

            materialGroup.Controls.Add(lstMaterialInfo);
            container.Controls.Add(materialGroup);

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

        // --- NEW METHOD: Populates the Info tab with data ---
        private void PopulateInfoTab()
        {
            if (volumeRenderer == null || mainForm == null) return;

            var infoTab = mainTabControl.TabPages
                             .Cast<TabPage>()
                             .FirstOrDefault(tp => tp.Text == "Info");
            if (infoTab == null) return;

            // System Info
            infoTab.Controls.Find("lblGpuNameValue", true).FirstOrDefault().Text = volumeRenderer.GpuDescription;

            // Volume Info
            infoTab.Controls.Find("lblVolumeDimsValue", true).FirstOrDefault().Text =
                $"{mainForm.GetWidth()} x {mainForm.GetHeight()} x {mainForm.GetDepth()} voxels";

            if (mainForm.volumeData != null)
            {
                infoTab.Controls.Find("lblChunkInfoValue", true).FirstOrDefault().Text =
                    $"{mainForm.volumeData.ChunkDim}^3 voxels ({mainForm.volumeData.ChunkCountX}x{mainForm.volumeData.ChunkCountY}x{mainForm.volumeData.ChunkCountZ} chunks)";
            }
            infoTab.Controls.Find("lblCacheSizeValue", true).FirstOrDefault().Text =
                $"{volumeRenderer.GpuCacheSizeInChunks} chunks";

            // Material Info
            var lstMaterialInfo = infoTab.Controls.Find("lstMaterialInfo", true).FirstOrDefault() as ListView;
            if (lstMaterialInfo == null) return;

            var colorImageList = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };
            lstMaterialInfo.SmallImageList = colorImageList;
            lstMaterialInfo.Items.Clear();

            for (int i = 0; i < mainForm.Materials.Count; i++)
            {
                var material = mainForm.Materials[i];
                var bmp = new Bitmap(16, 16);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(material.Color);
                }
                colorImageList.Images.Add(bmp);

                var item = new ListViewItem("", i);
                item.SubItems.Add(i.ToString());
                item.SubItems.Add(material.Name);
                lstMaterialInfo.Items.Add(item);
            }
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
            clippingPlanes.Add(new ClippingPlane { Name = "X-Axis Plane", Normal = new Vector3(1, 0, 0), Distance = 0, Enabled = false, SlabMode = false });
            clippingPlanes.Add(new ClippingPlane { Name = "Y-Axis Plane", Normal = new Vector3(0, 1, 0), Distance = 0, Enabled = false, SlabMode = false });
            clippingPlanes.Add(new ClippingPlane { Name = "Z-Axis Plane", Normal = new Vector3(0, 0, 1), Distance = 0, Enabled = false, SlabMode = false });

            RefreshClippingPlanesList();
        }

        private void RefreshClippingPlanesList()
        {
            if (lstClippingPlanes == null) return;

            int previouslySelectedIndex = -1;
            if (lstClippingPlanes.SelectedIndices.Count > 0)
            {
                previouslySelectedIndex = lstClippingPlanes.SelectedIndices[0];
            }

            updatingUI = true;
            lstClippingPlanes.Items.Clear();
            foreach (var plane in clippingPlanes)
            {
                var item = new ListViewItem(plane.Name);
                item.SubItems.Add(GetPlaneType(plane.Normal));
                item.SubItems.Add(plane.SlabMode ? "Yes" : "No");
                item.Checked = plane.Enabled;
                lstClippingPlanes.Items.Add(item);
            }
            updatingUI = false;

            if (previouslySelectedIndex != -1 && previouslySelectedIndex < lstClippingPlanes.Items.Count)
            {
                lstClippingPlanes.Items[previouslySelectedIndex].Selected = true;
                lstClippingPlanes.Focus();
            }
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
            g.Clear(Color.FromArgb(30, 30, 30));

            var bounds = planePreviewPanel.ClientRectangle;
            var center = new PointF(bounds.Width / 2f, bounds.Height / 2f);
            var scale = Math.Min(bounds.Width, bounds.Height) * 0.3f;

            // Draw coordinate axes
            using (var xPen = new Pen(Color.Red, 2))
            using (var yPen = new Pen(Color.Green, 2))
            using (var zPen = new Pen(Color.Blue, 2))
            using (var font = new Font("Arial", 10, FontStyle.Bold))
            {
                // X axis
                g.DrawLine(xPen, center, new PointF(center.X + scale, center.Y));
                g.DrawString("X", font, Brushes.Red, center.X + scale + 5, center.Y - 10);

                // Y axis (inverted for screen coordinates)
                g.DrawLine(yPen, center, new PointF(center.X, center.Y - scale));
                g.DrawString("Y", font, Brushes.Green, center.X - 10, center.Y - scale - 15);

                // Z axis (projected at 45 degrees)
                float zProjX = scale * 0.7f;
                float zProjY = scale * 0.7f;
                g.DrawLine(zPen, center, new PointF(center.X - zProjX, center.Y + zProjY));
                g.DrawString("Z", font, Brushes.Blue, center.X - zProjX - 15, center.Y + zProjY);
            }

            // Draw the clipping plane
            var plane = clippingPlanes[selectedPlaneIndex];
            var normal = plane.Normal.LengthSquared() > 0.01f ? Vector3.Normalize(plane.Normal) : new Vector3(0, 0, 1);

            // Draw normal vector
            float projX = normal.X - normal.Z * 0.5f;
            float projY = -normal.Y + normal.Z * 0.5f;
            var normalEnd = new PointF(
                center.X + projX * scale * 0.8f,
                center.Y + projY * scale * 0.8f
            );

            using (var normalPen = new Pen(Color.Yellow, 3))
            {
                normalPen.EndCap = LineCap.ArrowAnchor;
                g.DrawLine(normalPen, center, normalEnd);
            }

            // Draw plane representation as a semi-transparent rectangle
            using (var planeBrush = new SolidBrush(Color.FromArgb(80, 100, 150, 255)))
            using (var planePen = new Pen(Color.FromArgb(200, 100, 150, 255), 2))
            {
                // Calculate plane orientation
                float angle = (float)Math.Atan2(projY, projX);

                g.TranslateTransform(center.X, center.Y);
                g.RotateTransform(angle * 180f / (float)Math.PI);

                float planeSize = scale * 0.6f;
                var planeRect = new RectangleF(-planeSize / 2, -planeSize / 4, planeSize, planeSize / 2);

                g.FillRectangle(planeBrush, planeRect);
                g.DrawRectangle(planePen, planeRect.X, planeRect.Y, planeRect.Width, planeRect.Height);

                g.ResetTransform();
            }

            // Draw distance indicator if non-zero
            if (Math.Abs(plane.Distance) > 0.01f)
            {
                using (var distPen = new Pen(Color.Orange, 2) { DashStyle = DashStyle.Dash })
                using (var font = new Font("Arial", 8))
                {
                    float distOffset = plane.Distance * scale / (float)Math.Max(mainForm.GetWidth(), 1);
                    var offsetPoint = new PointF(
                        center.X + projX * distOffset,
                        center.Y + projY * distOffset
                    );

                    g.DrawLine(distPen, center, offsetPoint);
                    g.DrawString($"d={plane.Distance:F1}", font, Brushes.Orange, offsetPoint.X + 5, offsetPoint.Y);
                }
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
                SlabMode = false,
                SlabTranslation = 0
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

        private void ViewerForm_PointPicked(object sender, Vector3 pickedPosition)
        {
            if (viewerForm.CurrentMeasurementMode == MeasurementMode.PlacePoint)
            {
                var newPoint = new MeasurementPoint { Position = pickedPosition };
                measurements.Add(newPoint);
                viewerForm.CurrentMeasurementMode = MeasurementMode.None; // Reset mode
            }
            else if (viewerForm.CurrentMeasurementMode == MeasurementMode.DrawLineStart)
            {
                if (!lineStartPoint.HasValue)
                {
                    lineStartPoint = pickedPosition;
                }
                else
                {
                    var newLine = new MeasurementLine { Start = lineStartPoint.Value, End = pickedPosition };
                    measurements.Add(newLine);
                    lineStartPoint = null;
                    viewerForm.CurrentMeasurementMode = MeasurementMode.None; // Reset mode
                }
            }
            RefreshMeasurementsList();
            viewerForm.UpdateMeasurementData(measurements);
        }

        private void RefreshMeasurementsList()
        {
            lstMeasurements.Items.Clear();
            foreach (var m in measurements)
            {
                var item = new ListViewItem(m.Name);
                item.SubItems.Add(m.GetDetails((float)mainForm.pixelSize));
                item.Tag = m;
                lstMeasurements.Items.Add(item);
            }
        }

        private void BtnRemoveMeasurement_Click(object sender, EventArgs e)
        {
            if (lstMeasurements.SelectedItems.Count > 0)
            {
                var selectedObject = (MeasurementObject)lstMeasurements.SelectedItems[0].Tag;
                measurements.Remove(selectedObject);
                RefreshMeasurementsList();
                viewerForm.UpdateMeasurementData(measurements);
            }
        }

        private void BtnClearMeasurements_Click(object sender, EventArgs e)
        {
            measurements.Clear();
            lineStartPoint = null;
            viewerForm.CurrentMeasurementMode = MeasurementMode.None;
            RefreshMeasurementsList();
            viewerForm.UpdateMeasurementData(measurements);
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
                var n = plane.Normal.LengthSquared() > 0.01f ? Vector3.Normalize(plane.Normal) : new Vector3(0, 0, 1);

                chkEnablePlane.Checked = plane.Enabled;
                chkSlabMode.Checked = plane.SlabMode;

                trkPlaneNormalX.Value = (int)(n.X * 100);
                trkPlaneNormalY.Value = (int)(n.Y * 100);
                trkPlaneNormalZ.Value = (int)(n.Z * 100);

                float maxDim = Math.Max(1f, mainForm.GetWidth());
                trkPlaneDistance.Value = (int)(plane.Distance * 200 / maxDim);
                trkSlabTranslation.Value = (int)(plane.SlabTranslation * 200 / maxDim);


                lblNormalX.Text = $"Normal X: {n.X:F2}";
                lblNormalY.Text = $"Normal Y: {n.Y:F2}";
                lblNormalZ.Text = $"Normal Z: {n.Z:F2}";
                lblDistance.Text = $"{plane.Distance:F2}";
                lblSlabTranslation.Text = $"Slab Position: {plane.SlabTranslation:F2}";

                UpdateSlabControlsVisibility();
                updatingUI = false;
            }
        }

        private void ChkEnablePlane_CheckedChanged(object sender, EventArgs e)
        {
            if (updatingUI || selectedPlaneIndex < 0 || selectedPlaneIndex >= clippingPlanes.Count) return;

            var plane = clippingPlanes[selectedPlaneIndex];
            plane.Enabled = chkEnablePlane.Checked;
            clippingPlanes[selectedPlaneIndex] = plane;

            RefreshClippingPlanesList();
            UpdateClippingPlanes();
        }

        private void ChkSlabMode_CheckedChanged(object sender, EventArgs e)
        {
            if (updatingUI || selectedPlaneIndex < 0 || selectedPlaneIndex >= clippingPlanes.Count) return;

            var plane = clippingPlanes[selectedPlaneIndex];
            plane.SlabMode = chkSlabMode.Checked;
            clippingPlanes[selectedPlaneIndex] = plane;

            UpdateSlabControlsVisibility();
            RefreshClippingPlanesList();
            UpdateClippingPlanes();
        }


        private void UpdateSelectedPlaneFromAllControls()
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

            var plane = clippingPlanes[selectedPlaneIndex];
            plane.Normal = normal;

            float maxDim = Math.Max(1f, mainForm.GetWidth());
            plane.Distance = trkPlaneDistance.Value * maxDim / 200.0f;
            plane.SlabTranslation = trkSlabTranslation.Value * maxDim / 200.0f;

            plane.Enabled = chkEnablePlane.Checked;
            plane.SlabMode = chkSlabMode.Checked;
            clippingPlanes[selectedPlaneIndex] = plane;

            RefreshClippingPlanesList();
            UpdateClippingPlanes();
            UpdatePlaneEditor(); // Refresh labels
        }

        private void UpdateSlabControlsVisibility()
        {
            bool isSlab = chkSlabMode.Checked;
            lblSlabTranslation.Visible = isSlab;
            trkSlabTranslation.Visible = isSlab;

            if (isSlab)
            {
                lblDistanceLabel.Text = "Slab Thickness:";
                trkPlaneDistance.Minimum = 0; // Thickness cannot be negative
            }
            else
            {
                lblDistanceLabel.Text = "Distance:";
                trkPlaneDistance.Minimum = -200;
            }
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
            UpdateSelectedPlaneFromAllControls();
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

            float opacity = material.GetOpacity();
            trkOpacity.Value = (int)(opacity * 100.0);
            lblOpacity.Text = $"{(int)(opacity * 100)}%";
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
                if (plane.SlabMode)
                {
                    float thickness = plane.Distance;
                    float position = plane.SlabTranslation;

                    float lowerBound = position - thickness / 2.0f;
                    float upperBound = position + thickness / 2.0f;

                    p.ClippingPlanes.Add(new Vector4(plane.Normal, upperBound));
                    p.ClippingPlanes.Add(new Vector4(-plane.Normal, -lowerBound));
                }
                else
                {
                    p.ClippingPlanes.Add(new Vector4(plane.Normal, plane.Distance));
                }
            }


            p.DrawClippingPlanes = (chkDrawPlanes?.Checked ?? true) ? 1.0f : 0.0f;

            volumeRenderer.SetRenderParams(p);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            if (lblScaleBarUnits != null)
            {
                lblScaleBarUnits.Text = GetPixelSizeText();
            }

            SetDefaultScaleBarLength();
            UpdateRenderParams();
            PerformLayout();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            PerformLayout();
            Invalidate(true);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (viewerForm != null)
                {
                    viewerForm.PointPicked -= ViewerForm_PointPicked;
                }
                colorDialog?.Dispose();
                volumeRenderer = null;
                viewerForm = null;
                mainForm = null;
            }
            base.Dispose(disposing);
        }
    }
}