using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTSegmenter.SharpDXIntegration
{
    public partial class SharpDXControlPanel : Form
    {
        private SharpDXViewerForm viewerForm;
        private MainForm mainForm;
        private SharpDXVolumeRenderer volumeRenderer;

        // UI elements
        private TrackBar trkMinThreshold;
        private TrackBar trkMaxThreshold;
        private CheckBox chkShowGrayscale;
        private ComboBox cmbQuality;
        private CheckBox chkSlices;
        private TrackBar trkXSlice, trkYSlice, trkZSlice;
        private Button btnScreenshot;
        private Button btnExportModel;
        private ProgressBar progress;
        private Label lblStatus;
        private TabControl tabControl;
        private TabPage tabRendering;
        private TabPage tabMaterials;
        private TabPage tabSlices;

        // Material controls
        private CheckedListBox lstMaterials;
        private TrackBar trkOpacity;
        private Label lblOpacity;

        public SharpDXControlPanel(SharpDXViewerForm viewer, MainForm main, SharpDXVolumeRenderer renderer)
        {
            viewerForm = viewer;
            mainForm = main;
            volumeRenderer = renderer;

            InitializeComponent();
            InitializeRenderingTab();
            InitializeMaterialsTab();
            InitializeSlicesTab();
        }

        private void InitializeComponent()
        {
            this.Text = "3D Control Panel (SharpDX)";
            this.Size = new Size(400, 600);
            this.FormBorderStyle = FormBorderStyle.FixedToolWindow;

            tabControl = new TabControl();
            tabControl.Dock = DockStyle.Fill;

            tabRendering = new TabPage("Rendering");
            tabMaterials = new TabPage("Materials");
            tabSlices = new TabPage("Slices");

            tabControl.TabPages.Add(tabRendering);
            tabControl.TabPages.Add(tabMaterials);
            tabControl.TabPages.Add(tabSlices);

            this.Controls.Add(tabControl);
        }

        private void InitializeRenderingTab()
        {
            // min/max threshold
            trkMinThreshold = new TrackBar();
            trkMinThreshold.Minimum = 0;
            trkMinThreshold.Maximum = 255;
            trkMinThreshold.Value = 30;
            trkMinThreshold.TickFrequency = 10;
            trkMinThreshold.Scroll += (s, e) => OnThresholdChanged();

            trkMaxThreshold = new TrackBar();
            trkMaxThreshold.Minimum = 0;
            trkMaxThreshold.Maximum = 255;
            trkMaxThreshold.Value = 255;
            trkMaxThreshold.TickFrequency = 10;
            trkMaxThreshold.Scroll += (s, e) => OnThresholdChanged();

            chkShowGrayscale = new CheckBox();
            chkShowGrayscale.Text = "Show Grayscale";
            chkShowGrayscale.Checked = true;
            chkShowGrayscale.CheckedChanged += (s, e) =>
            {
                viewerForm.SetGrayscaleVisible(chkShowGrayscale.Checked);
            };

            Label lblQuality = new Label();
            lblQuality.Text = "Quality:";
            cmbQuality = new ComboBox();
            cmbQuality.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbQuality.Items.AddRange(new object[] { "Low", "Medium", "High" });
            cmbQuality.SelectedIndex = 1; // default medium
            cmbQuality.SelectedIndexChanged += async (s, e) =>
            {
                // re‐apply threshold to trigger a re-render with new quality
                await viewerForm.ApplyThresholdAndRender(trkMinThreshold.Value, trkMaxThreshold.Value, cmbQuality.SelectedIndex);
            };

            btnScreenshot = new Button();
            btnScreenshot.Text = "Screenshot";
            btnScreenshot.Click += (s, e) =>
            {
                string fileName = "screenshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                string path = Path.Combine(Application.StartupPath, fileName);
                viewerForm.TakeScreenshot(path);
            };

            btnExportModel = new Button();
            btnExportModel.Text = "Export Model (OBJ)";
            btnExportModel.Click += async (s, e) =>
            {
                using (var saveDlg = new SaveFileDialog())
                {
                    saveDlg.Filter = "OBJ file|*.obj|STL file|*.stl";
                    saveDlg.FileName = "export.obj";
                    if (saveDlg.ShowDialog(this) == DialogResult.OK)
                    {
                        bool isObj = Path.GetExtension(saveDlg.FileName).ToLower().Contains("obj");
                        // For grayscale surfaces, pick an iso-level. For label surfaces, user can decide
                        float isoLevel = 80.0f; // example
                        bool exportLabels = true; // or prompt user
                        bool exportGrayscaleSurface = true;
                        await viewerForm.ExportModelAsync(exportLabels, exportGrayscaleSurface, saveDlg.FileName, isoLevel);
                    }
                }
            };

            progress = new ProgressBar();
            progress.Minimum = 0;
            progress.Maximum = 100;
            progress.Value = 0;
            progress.Style = ProgressBarStyle.Blocks;
            lblStatus = new Label();
            lblStatus.Text = "Ready.";

            // Layout in tabRendering
            var pnl = new FlowLayoutPanel();
            pnl.Dock = DockStyle.Fill;
            pnl.FlowDirection = FlowDirection.TopDown;
            pnl.WrapContents = false;

            pnl.Controls.Add(new Label() { Text = "Min Threshold:" });
            pnl.Controls.Add(trkMinThreshold);
            pnl.Controls.Add(new Label() { Text = "Max Threshold:" });
            pnl.Controls.Add(trkMaxThreshold);
            pnl.Controls.Add(chkShowGrayscale);
            pnl.Controls.Add(lblQuality);
            pnl.Controls.Add(cmbQuality);
            pnl.Controls.Add(btnScreenshot);
            pnl.Controls.Add(btnExportModel);
            pnl.Controls.Add(progress);
            pnl.Controls.Add(lblStatus);

            tabRendering.Controls.Add(pnl);
        }

        private void OnThresholdChanged()
        {
            // Just queue up an async call to re-render with new thresholds
            _ = viewerForm.ApplyThresholdAndRender(trkMinThreshold.Value, trkMaxThreshold.Value, cmbQuality.SelectedIndex);
        }

        private void InitializeMaterialsTab()
        {
            // Materials: checkboxes for “visibility” and a slider for “opacity”
            lstMaterials = new CheckedListBox();
            lstMaterials.CheckOnClick = true;
            lstMaterials.Width = 200;
            lstMaterials.Height = 120;

            // Enable owner-draw to show each material’s color
            lstMaterials.DrawMode = DrawMode.OwnerDrawFixed;
            lstMaterials.DrawItem += LstMaterials_DrawItem;

            // Populate from mainForm.Materials
            // Here, we assume mainForm.Materials[0] might be "Exterior." 
            // Decide whether to skip or include it. Example: we skip exterior:
            if (mainForm.volumeLabels != null && mainForm.Materials != null)
            {
                for (int i = 0; i < mainForm.Materials.Count; i++)
                {
                    Material mat = mainForm.Materials[i];
                    // Optional: skip if it's the exterior
                    // if (mat.IsExterior) continue;

                    // For visibility, you can ask the viewer if the material is currently visible:
                    bool currentlyVisible = viewerForm.GetMaterialVisibility(mat.ID);
                    // Add the actual 'Material' object as the item
                    lstMaterials.Items.Add(mat, currentlyVisible);
                }
            }

            // When user toggles a checkbox, update the material’s visibility in the 3D viewer:
            lstMaterials.ItemCheck += (s, e) =>
            {
                if (e.Index < 0 || e.Index >= lstMaterials.Items.Count)
                    return;
                Material mat = (Material)lstMaterials.Items[e.Index];
                bool isChecked = (e.NewValue == CheckState.Checked);
                viewerForm.SetMaterialVisibility(mat.ID, isChecked);
            };

            // When user selects a material, update the trackbar to show its current opacity
            lstMaterials.SelectedIndexChanged += (s, e) =>
            {
                int idx = lstMaterials.SelectedIndex;
                if (idx < 0 || idx >= lstMaterials.Items.Count) return;
                Material mat = (Material)lstMaterials.Items[idx];

                // Convert to 0..100 range
                float currentAlpha = viewerForm.GetMaterialOpacity(mat.ID);
                trkOpacity.Value = (int)Math.Round(currentAlpha * 100f);
            };

            // Opacity trackbar
            trkOpacity = new TrackBar();
            trkOpacity.Minimum = 0;
            trkOpacity.Maximum = 100;
            trkOpacity.Value = 100;
            trkOpacity.TickFrequency = 10;
            trkOpacity.Scroll += (s, e) =>
            {
                // Adjust currently selected material's opacity
                int idx = lstMaterials.SelectedIndex;
                if (idx < 0) return;

                Material mat = (Material)lstMaterials.Items[idx];
                float alpha = trkOpacity.Value / 100f;
                viewerForm.SetMaterialOpacity(mat.ID, alpha);
            };

            lblOpacity = new Label();
            lblOpacity.Text = "Material Opacity:";

            var pnl = new FlowLayoutPanel();
            pnl.Dock = DockStyle.Fill;
            pnl.FlowDirection = FlowDirection.TopDown;
            pnl.WrapContents = false;

            pnl.Controls.Add(new Label() { Text = "Materials (Labels)" });
            pnl.Controls.Add(lstMaterials);
            pnl.Controls.Add(lblOpacity);
            pnl.Controls.Add(trkOpacity);

            tabMaterials.Controls.Add(pnl);
        }
        private void LstMaterials_DrawItem(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();

            // Validate the index
            if (e.Index < 0 || e.Index >= lstMaterials.Items.Count)
                return;

            // We stored actual Material objects in the Items collection
            Material mat = (Material)lstMaterials.Items[e.Index];

            // Fill the background with the material’s color
            using (SolidBrush b = new SolidBrush(mat.Color))
                e.Graphics.FillRectangle(b, e.Bounds);

            // Decide on a text color for contrast
            Color textColor = (mat.Color.GetBrightness() < 0.4f) ? Color.White : Color.Black;

            // Draw the text (the material’s Name)
            TextRenderer.DrawText(
                e.Graphics,
                mat.Name,
                e.Font,
                e.Bounds.Location,
                textColor);

            e.DrawFocusRectangle();
        }

        private void InitializeSlicesTab()
        {
            chkSlices = new CheckBox();
            chkSlices.Text = "Enable Slices";
            chkSlices.Checked = false;
            chkSlices.CheckedChanged += (s, e) =>
            {
                viewerForm.SetSlicesEnabled(chkSlices.Checked);
            };

            trkXSlice = new TrackBar();
            trkXSlice.Minimum = 0;
            trkXSlice.Maximum = mainForm.GetWidth() - 1;
            trkXSlice.Value = trkXSlice.Maximum / 2;
            trkXSlice.TickFrequency = mainForm.GetWidth() / 10;
            trkXSlice.Scroll += (s, e) => UpdateSlices();

            trkYSlice = new TrackBar();
            trkYSlice.Minimum = 0;
            trkYSlice.Maximum = mainForm.GetHeight() - 1;
            trkYSlice.Value = trkYSlice.Maximum / 2;
            trkYSlice.TickFrequency = mainForm.GetHeight() / 10;
            trkYSlice.Scroll += (s, e) => UpdateSlices();

            trkZSlice = new TrackBar();
            trkZSlice.Minimum = 0;
            trkZSlice.Maximum = mainForm.GetDepth() - 1;
            trkZSlice.Value = trkZSlice.Maximum / 2;
            trkZSlice.TickFrequency = Math.Max(1, mainForm.GetDepth() / 10);
            trkZSlice.Scroll += (s, e) => UpdateSlices();

            var pnl = new FlowLayoutPanel();
            pnl.Dock = DockStyle.Fill;
            pnl.FlowDirection = FlowDirection.TopDown;
            pnl.WrapContents = false;

            pnl.Controls.Add(chkSlices);
            pnl.Controls.Add(new Label() { Text = "X Slice:" });
            pnl.Controls.Add(trkXSlice);
            pnl.Controls.Add(new Label() { Text = "Y Slice:" });
            pnl.Controls.Add(trkYSlice);
            pnl.Controls.Add(new Label() { Text = "Z Slice:" });
            pnl.Controls.Add(trkZSlice);

            tabSlices.Controls.Add(pnl);
        }

        private void UpdateSlices()
        {
            viewerForm.SetSliceIndices(trkXSlice.Value, trkYSlice.Value, trkZSlice.Value);
        }
    }
}
