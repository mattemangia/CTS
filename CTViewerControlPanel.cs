using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace CTSegmenter.SharpDXIntegration
{
    public partial class SharpDXControlPanel : Form
    {
        private CheckBox chkDebugMode;
        private SharpDXViewerForm viewerForm;
        private MainForm mainForm;
        private SharpDXVolumeRenderer volumeRenderer;

        // UI elements
        private TrackBar trkMinThreshold;
        private TrackBar trkMaxThreshold;
        private NumericUpDown numMinThreshold;
        private NumericUpDown numMaxThreshold;
        private CheckBox chkShowGrayscale;
        private ComboBox cmbQuality;
        private ComboBox cmbColorMap;
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
        private TabPage tabCutting;
        private TabPage tabInfo;

        // Material controls
        private CheckedListBox lstMaterials;
        private TrackBar trkOpacity;
        private Label lblOpacity;
        private Button btnDebugTest;

        // Cutting plane controls
        private CheckBox chkCutX, chkCutY, chkCutZ;
        private RadioButton radCutXForward, radCutXBackward;
        private RadioButton radCutYForward, radCutYBackward;
        private RadioButton radCutZForward, radCutZBackward;
        private TrackBar trkCutX, trkCutY, trkCutZ;

        // Info panel
        private Label lblVolumeInfo;
        private Label lblMaterialsInfo;
        private Label lblPixelSizeInfo;

        public SharpDXControlPanel(SharpDXViewerForm viewer, MainForm main, SharpDXVolumeRenderer renderer)
        {
            viewerForm = viewer;
            mainForm = main;
            volumeRenderer = renderer;

            InitializeComponent();
            InitializeRenderingTab();
            InitializeMaterialsTab();
            InitializeSlicesTab();
            InitializeCuttingTab();
            InitializeInfoTab();

            // Select the first tab
            tabControl.SelectedIndex = 0;
        }

        private void InitializeComponent()
        {
            this.Text = "3D Control Panel (SharpDX)";
            this.Size = new Size(400, 650);
            this.FormBorderStyle = FormBorderStyle.FixedToolWindow;
            this.StartPosition = FormStartPosition.Manual;

            tabControl = new TabControl();
            tabControl.Dock = DockStyle.Fill;

            tabRendering = new TabPage("Rendering");
            tabMaterials = new TabPage("Materials");
            tabSlices = new TabPage("Slices");
            tabCutting = new TabPage("Cutting");
            tabInfo = new TabPage("Info");

            tabControl.TabPages.Add(tabRendering);
            tabControl.TabPages.Add(tabMaterials);
            tabControl.TabPages.Add(tabSlices);
            tabControl.TabPages.Add(tabCutting);
            tabControl.TabPages.Add(tabInfo);

            this.Controls.Add(tabControl);
        }

        private void InitializeRenderingTab()
        {
            // Create panel to hold controls
            FlowLayoutPanel panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.FlowDirection = FlowDirection.TopDown;
            panel.AutoScroll = true;
            panel.Padding = new Padding(10);
            panel.WrapContents = false;

            // Add title label
            Label lblTitle = new Label();
            lblTitle.Text = "Volume Rendering Controls";
            lblTitle.Font = new Font(lblTitle.Font.FontFamily, 10, FontStyle.Bold);
            lblTitle.AutoSize = true;
            lblTitle.Margin = new Padding(0, 0, 0, 10);
            panel.Controls.Add(lblTitle);

            // Debug mode checkbox
            chkDebugMode = new CheckBox();
            chkDebugMode.Text = "Debug Mode (Wireframe)";
            chkDebugMode.Checked = false;
            chkDebugMode.CheckedChanged += (s, e) =>
            {
                viewerForm.SetDebugMode(chkDebugMode.Checked);
            };
            panel.Controls.Add(chkDebugMode);
            // LOD system checkbox
            CheckBox chkUseLod = new CheckBox();
            chkUseLod.Text = "Use LOD While Moving (Faster)";
            chkUseLod.Checked = true; // Default to enabled
            chkUseLod.CheckedChanged += (s, e) =>
            {
                viewerForm.SetLodEnabled(chkUseLod.Checked);
            };
            panel.Controls.Add(chkUseLod);
            // Show grayscale checkbox
            chkShowGrayscale = new CheckBox();
            chkShowGrayscale.Text = "Show Grayscale Volume";
            chkShowGrayscale.Checked = true;
            chkShowGrayscale.CheckedChanged += (s, e) =>
            {
                viewerForm.SetGrayscaleVisible(chkShowGrayscale.Checked);
            };
            panel.Controls.Add(chkShowGrayscale);

            // Quality dropdown
            Label lblQuality = new Label();
            lblQuality.Text = "Rendering Quality:";
            lblQuality.AutoSize = true;
            lblQuality.Margin = new Padding(0, 10, 0, 5);
            panel.Controls.Add(lblQuality);

            cmbQuality = new ComboBox();
            cmbQuality.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbQuality.Items.AddRange(new object[] { "Low (Faster)", "Medium", "High (Slower)" });
            cmbQuality.SelectedIndex = 1; // Default to medium
            cmbQuality.Width = 200;
            cmbQuality.SelectedIndexChanged += async (s, e) =>
            {
                await viewerForm.ApplyThresholdAndRender(
                    trkMinThreshold.Value,
                    trkMaxThreshold.Value,
                    cmbQuality.SelectedIndex);
            };
            panel.Controls.Add(cmbQuality);

            // Color map dropdown
            Label lblColorMap = new Label();
            lblColorMap.Text = "Color Map:";
            lblColorMap.AutoSize = true;
            lblColorMap.Margin = new Padding(0, 10, 0, 5);
            panel.Controls.Add(lblColorMap);

            cmbColorMap = new ComboBox();
            cmbColorMap.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbColorMap.Items.AddRange(new object[] { "Grayscale", "Hot", "Cool", "Rainbow" });
            cmbColorMap.SelectedIndex = 0; // Default to grayscale
            cmbColorMap.Width = 200;
            cmbColorMap.SelectedIndexChanged += (s, e) =>
            {
                // Set the color map index in the renderer
                viewerForm.SetColorMap(cmbColorMap.SelectedIndex);
            };
            panel.Controls.Add(cmbColorMap);

            // Threshold group
            GroupBox grpThreshold = new GroupBox();
            grpThreshold.Text = "Grayscale Threshold Range";
            grpThreshold.Width = 350;
            grpThreshold.Height = 160; // Increased height to avoid overlap
            grpThreshold.Margin = new Padding(0, 10, 0, 10);

            // Min threshold
            Label lblMinThreshold = new Label();
            lblMinThreshold.Text = "Min:";
            lblMinThreshold.AutoSize = true;
            lblMinThreshold.Location = new Point(10, 25);
            grpThreshold.Controls.Add(lblMinThreshold);

            trkMinThreshold = new TrackBar();
            trkMinThreshold.Minimum = 0;
            trkMinThreshold.Maximum = 255;
            trkMinThreshold.Value = 30;
            trkMinThreshold.TickFrequency = 16;
            trkMinThreshold.LargeChange = 16;
            trkMinThreshold.Width = 250;
            trkMinThreshold.Location = new Point(40, 20);
            trkMinThreshold.Scroll += (s, e) =>
            {
                numMinThreshold.Value = trkMinThreshold.Value;
                OnThresholdChanged();
            };
            grpThreshold.Controls.Add(trkMinThreshold);

            numMinThreshold = new NumericUpDown();
            numMinThreshold.Minimum = 0;
            numMinThreshold.Maximum = 255;
            numMinThreshold.Value = 30;
            numMinThreshold.Width = 55;
            numMinThreshold.Location = new Point(295, 25);
            numMinThreshold.ValueChanged += (s, e) =>
            {
                if (numMinThreshold.Value > numMaxThreshold.Value)
                    numMinThreshold.Value = numMaxThreshold.Value;

                trkMinThreshold.Value = (int)numMinThreshold.Value;
                OnThresholdChanged();
            };
            grpThreshold.Controls.Add(numMinThreshold);

            // Max threshold - moved lower to avoid overlap
            Label lblMaxThreshold = new Label();
            lblMaxThreshold.Text = "Max:";
            lblMaxThreshold.AutoSize = true;
            lblMaxThreshold.Location = new Point(10, 85); // Increased Y position
            grpThreshold.Controls.Add(lblMaxThreshold);

            trkMaxThreshold = new TrackBar();
            trkMaxThreshold.Minimum = 0;
            trkMaxThreshold.Maximum = 255;
            trkMaxThreshold.Value = 200;
            trkMaxThreshold.TickFrequency = 16;
            trkMaxThreshold.LargeChange = 16;
            trkMaxThreshold.Width = 250;
            trkMaxThreshold.Location = new Point(40, 80); // Increased Y position
            trkMaxThreshold.Scroll += (s, e) =>
            {
                numMaxThreshold.Value = trkMaxThreshold.Value;
                OnThresholdChanged();
            };
            grpThreshold.Controls.Add(trkMaxThreshold);

            numMaxThreshold = new NumericUpDown();
            numMaxThreshold.Minimum = 0;
            numMaxThreshold.Maximum = 255;
            numMaxThreshold.Value = 200;
            numMaxThreshold.Width = 55;
            numMaxThreshold.Location = new Point(295, 85); // Increased Y position
            numMaxThreshold.ValueChanged += (s, e) =>
            {
                if (numMaxThreshold.Value < numMinThreshold.Value)
                    numMaxThreshold.Value = numMinThreshold.Value;

                trkMaxThreshold.Value = (int)numMaxThreshold.Value;
                OnThresholdChanged();
            };
            grpThreshold.Controls.Add(numMaxThreshold);

            panel.Controls.Add(grpThreshold);

            // Export buttons
            btnScreenshot = new Button();
            btnScreenshot.Text = "Take Screenshot";
            btnScreenshot.Width = 200;
            btnScreenshot.Click += (s, e) =>
            {
                string fileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                string path = Path.Combine(Application.StartupPath, fileName);
                viewerForm.TakeScreenshot(path);
            };
            panel.Controls.Add(btnScreenshot);

            btnExportModel = new Button();
            btnExportModel.Text = "Export 3D Model...";
            btnExportModel.Width = 200;
            btnExportModel.Margin = new Padding(0, 10, 0, 10);
            btnExportModel.Click += async (s, e) =>
            {
                using (var saveDlg = new SaveFileDialog())
                {
                    saveDlg.Filter = "OBJ files (*.obj)|*.obj|STL files (*.stl)|*.stl";
                    saveDlg.FileName = "ct_model.obj";
                    if (saveDlg.ShowDialog(this) == DialogResult.OK)
                    {
                        bool isObj = Path.GetExtension(saveDlg.FileName).ToLower() == ".obj";

                        // Get export options
                        bool exportLabels = true;
                        bool exportGrayscaleSurface = true;
                        float isoLevel = 120.0f; // Default iso-level for grayscale

                        // Confirm with dialog
                        var result = MessageBox.Show(
                            "Export both segmented materials and grayscale volume?\n\n" +
                            "Yes - Export both\nNo - Export only materials\nCancel - Cancel export",
                            "Export Options",
                            MessageBoxButtons.YesNoCancel,
                            MessageBoxIcon.Question);

                        if (result == DialogResult.Cancel)
                            return;

                        exportGrayscaleSurface = (result == DialogResult.Yes);

                        // Update status before export
                        lblStatus.Text = "Exporting 3D model...";
                        progress.Value = 0;
                        progress.Visible = true;
                        Application.DoEvents();

                        // Disable UI during export
                        this.Enabled = false;

                        try
                        {
                            await viewerForm.ExportModelAsync(
                                exportLabels,
                                exportGrayscaleSurface,
                                saveDlg.FileName,
                                isoLevel);

                            lblStatus.Text = "Export complete.";
                        }
                        catch (Exception ex)
                        {
                            lblStatus.Text = "Export failed.";
                            MessageBox.Show($"Error exporting model: {ex.Message}", "Export Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        finally
                        {
                            this.Enabled = true;
                            progress.Visible = false;
                        }
                    }
                }
            };
            panel.Controls.Add(btnExportModel);

            // Progress bar and status label
            progress = new ProgressBar();
            progress.Width = 330;
            progress.Height = 20;
            progress.Visible = false;
            panel.Controls.Add(progress);

            lblStatus = new Label();
            lblStatus.Text = "Ready.";
            lblStatus.AutoSize = true;
            panel.Controls.Add(lblStatus);

            tabRendering.Controls.Add(panel);
        }

        private async void OnThresholdChanged()
        {
            lblStatus.Text = "Updating...";
            // Just queue up an async call to re-render with new thresholds
            await viewerForm.ApplyThresholdAndRender(
                trkMinThreshold.Value,
                trkMaxThreshold.Value,
                cmbQuality.SelectedIndex);
            lblStatus.Text = "Ready.";
        }

        private void InitializeMaterialsTab()
        {
            // Create panel to hold controls
            FlowLayoutPanel panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.FlowDirection = FlowDirection.TopDown;
            panel.AutoScroll = true;
            panel.Padding = new Padding(10);
            panel.WrapContents = false;

            // Add title label
            Label lblTitle = new Label();
            lblTitle.Text = "Materials Visibility & Opacity";
            lblTitle.Font = new Font(lblTitle.Font.FontFamily, 10, FontStyle.Bold);
            lblTitle.AutoSize = true;
            lblTitle.Margin = new Padding(0, 0, 0, 10);
            panel.Controls.Add(lblTitle);

            // Materials list
            Label lblMaterials = new Label();
            lblMaterials.Text = "Materials (Labels):";
            lblMaterials.AutoSize = true;
            panel.Controls.Add(lblMaterials);

            lstMaterials = new CheckedListBox();
            lstMaterials.CheckOnClick = true;
            lstMaterials.Width = 330;
            lstMaterials.Height = 220;
            lstMaterials.BorderStyle = BorderStyle.FixedSingle;

            // Enable owner-draw to show each material's color
            lstMaterials.DrawMode = DrawMode.OwnerDrawFixed;
            lstMaterials.DrawItem += LstMaterials_DrawItem;

            // Populate from mainForm.Materials
            if (mainForm.volumeLabels != null && mainForm.Materials != null)
            {
                for (int i = 0; i < mainForm.Materials.Count; i++)
                {
                    Material mat = mainForm.Materials[i];

                    // For visibility, you can ask the viewer if the material is currently visible:
                    bool currentlyVisible = viewerForm.GetMaterialVisibility(mat.ID);

                    // Add the actual 'Material' object as the item
                    lstMaterials.Items.Add(mat, currentlyVisible);
                }
            }

            // When user toggles a checkbox, update the material's visibility in the 3D viewer:
            lstMaterials.ItemCheck += (s, e) =>
            {
                if (e.Index < 0 || e.Index >= lstMaterials.Items.Count)
                    return;

                Material mat = (Material)lstMaterials.Items[e.Index];
                bool isChecked = (e.NewValue == CheckState.Checked);
                viewerForm.SetMaterialVisibility(mat.ID, isChecked);
            };

            // When user selects a material, update the opacity slider to show its current opacity
            lstMaterials.SelectedIndexChanged += (s, e) =>
            {
                int idx = lstMaterials.SelectedIndex;
                if (idx < 0 || idx >= lstMaterials.Items.Count) return;

                Material mat = (Material)lstMaterials.Items[idx];

                // Convert to 0..100 range for the slider
                float currentAlpha = viewerForm.GetMaterialOpacity(mat.ID);
                trkOpacity.Value = (int)Math.Round(currentAlpha * 100f);
            };

            panel.Controls.Add(lstMaterials);

            // Opacity controls
            lblOpacity = new Label();
            lblOpacity.Text = "Material Opacity:";
            lblOpacity.AutoSize = true;
            lblOpacity.Margin = new Padding(0, 10, 0, 5);
            panel.Controls.Add(lblOpacity);

            trkOpacity = new TrackBar();
            trkOpacity.Minimum = 0;
            trkOpacity.Maximum = 100;
            trkOpacity.Value = 100;
            trkOpacity.TickFrequency = 10;
            trkOpacity.Width = 330;
            trkOpacity.Scroll += (s, e) =>
            {
                // Adjust currently selected material's opacity
                int idx = lstMaterials.SelectedIndex;
                if (idx < 0) return;

                Material mat = (Material)lstMaterials.Items[idx];
                float alpha = trkOpacity.Value / 100f;
                viewerForm.SetMaterialOpacity(mat.ID, alpha);
            };
            panel.Controls.Add(trkOpacity);

            tabMaterials.Controls.Add(panel);
        }

        private void LstMaterials_DrawItem(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();

            // Validate the index
            if (e.Index < 0 || e.Index >= lstMaterials.Items.Count)
                return;

            // Get material from the item
            Material mat = (Material)lstMaterials.Items[e.Index];

            // Fill the background with the material's color
            using (SolidBrush b = new SolidBrush(mat.Color))
                e.Graphics.FillRectangle(b, e.Bounds);

            // Decide on a text color for contrast
            Color textColor = (mat.Color.GetBrightness() < 0.4f) ? Color.White : Color.Black;

            // Draw the text (the material's Name)
            TextRenderer.DrawText(
                e.Graphics,
                $"{mat.Name} (ID: {mat.ID})",
                e.Font,
                e.Bounds.Location,
                textColor);

            e.DrawFocusRectangle();
        }

        private void InitializeSlicesTab()
        {
            // Create panel to hold controls
            FlowLayoutPanel panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.FlowDirection = FlowDirection.TopDown;
            panel.AutoScroll = true;
            panel.Padding = new Padding(10);
            panel.WrapContents = false;

            // Add title label
            Label lblTitle = new Label();
            lblTitle.Text = "Orthogonal Slice Controls";
            lblTitle.Font = new Font(lblTitle.Font.FontFamily, 10, FontStyle.Bold);
            lblTitle.AutoSize = true;
            lblTitle.Margin = new Padding(0, 0, 0, 10);
            panel.Controls.Add(lblTitle);

            // Enable/disable slices
            chkSlices = new CheckBox();
            chkSlices.Text = "Show Orthogonal Slices";
            chkSlices.Checked = false;
            chkSlices.CheckedChanged += (s, e) =>
            {
                viewerForm.SetSlicesEnabled(chkSlices.Checked);
                // Enable/disable slice sliders
                trkXSlice.Enabled = chkSlices.Checked;
                trkYSlice.Enabled = chkSlices.Checked;
                trkZSlice.Enabled = chkSlices.Checked;
            };
            panel.Controls.Add(chkSlices);

            // Add colored indicators for each slice direction
            Label lblSliceColors = new Label();
            lblSliceColors.Text = "Slice Direction Colors:";
            lblSliceColors.AutoSize = true;
            lblSliceColors.Margin = new Padding(0, 10, 0, 5);
            panel.Controls.Add(lblSliceColors);

            // Create a panel to hold the color indicators
            TableLayoutPanel colorPanel = new TableLayoutPanel();
            colorPanel.ColumnCount = 3;
            colorPanel.RowCount = 1;
            colorPanel.Width = 330;
            colorPanel.Height = 30;
            colorPanel.Margin = new Padding(0, 0, 0, 10);

            // X slice indicator (red)
            Panel xColorPanel = new Panel();
            xColorPanel.BackColor = Color.Red;
            xColorPanel.Dock = DockStyle.Fill;
            Label xLabel = new Label();
            xLabel.Text = "X Slice";
            xLabel.ForeColor = Color.White;
            xLabel.TextAlign = ContentAlignment.MiddleCenter;
            xLabel.Dock = DockStyle.Fill;
            xColorPanel.Controls.Add(xLabel);
            colorPanel.Controls.Add(xColorPanel, 0, 0);

            // Y slice indicator (green)
            Panel yColorPanel = new Panel();
            yColorPanel.BackColor = Color.Green;
            yColorPanel.Dock = DockStyle.Fill;
            Label yLabel = new Label();
            yLabel.Text = "Y Slice";
            yLabel.ForeColor = Color.White;
            yLabel.TextAlign = ContentAlignment.MiddleCenter;
            yLabel.Dock = DockStyle.Fill;
            yColorPanel.Controls.Add(yLabel);
            colorPanel.Controls.Add(yColorPanel, 1, 0);

            // Z slice indicator (blue)
            Panel zColorPanel = new Panel();
            zColorPanel.BackColor = Color.Blue;
            zColorPanel.Dock = DockStyle.Fill;
            Label zLabel = new Label();
            zLabel.Text = "Z Slice";
            zLabel.ForeColor = Color.White;
            zLabel.TextAlign = ContentAlignment.MiddleCenter;
            zLabel.Dock = DockStyle.Fill;
            zColorPanel.Controls.Add(zLabel);
            colorPanel.Controls.Add(zColorPanel, 2, 0);

            panel.Controls.Add(colorPanel);

            // X Slice (YZ plane)
            Label lblXSlice = new Label();
            lblXSlice.Text = "X Slice (YZ plane - Red)";
            lblXSlice.AutoSize = true;
            lblXSlice.Margin = new Padding(0, 10, 0, 5);
            panel.Controls.Add(lblXSlice);

            trkXSlice = new TrackBar();
            trkXSlice.Minimum = 0;
            trkXSlice.Maximum = Math.Max(0, mainForm.GetWidth() - 1);
            trkXSlice.Value = trkXSlice.Maximum / 2;
            trkXSlice.TickFrequency = Math.Max(1, mainForm.GetWidth() / 20);
            trkXSlice.Width = 330;
            trkXSlice.Enabled = false;
            trkXSlice.Scroll += (s, e) => UpdateSlices();
            panel.Controls.Add(trkXSlice);

            // Y Slice (XZ plane)
            Label lblYSlice = new Label();
            lblYSlice.Text = "Y Slice (XZ plane - Green)";
            lblYSlice.AutoSize = true;
            lblYSlice.Margin = new Padding(0, 10, 0, 5);
            panel.Controls.Add(lblYSlice);

            trkYSlice = new TrackBar();
            trkYSlice.Minimum = 0;
            trkYSlice.Maximum = Math.Max(0, mainForm.GetHeight() - 1);
            trkYSlice.Value = trkYSlice.Maximum / 2;
            trkYSlice.TickFrequency = Math.Max(1, mainForm.GetHeight() / 20);
            trkYSlice.Width = 330;
            trkYSlice.Enabled = false;
            trkYSlice.Scroll += (s, e) => UpdateSlices();
            panel.Controls.Add(trkYSlice);

            // Z Slice (XY plane)
            Label lblZSlice = new Label();
            lblZSlice.Text = "Z Slice (XY plane - Blue)";
            lblZSlice.AutoSize = true;
            lblZSlice.Margin = new Padding(0, 10, 0, 5);
            panel.Controls.Add(lblZSlice);

            trkZSlice = new TrackBar();
            trkZSlice.Minimum = 0;
            trkZSlice.Maximum = Math.Max(0, mainForm.GetDepth() - 1);
            trkZSlice.Value = trkZSlice.Maximum / 2;
            trkZSlice.TickFrequency = Math.Max(1, mainForm.GetDepth() / 20);
            trkZSlice.Width = 330;
            trkZSlice.Enabled = false;
            trkZSlice.Scroll += (s, e) => UpdateSlices();
            panel.Controls.Add(trkZSlice);

            // "Reset to center" button
            Button btnResetSlices = new Button();
            btnResetSlices.Text = "Reset Slices to Center";
            btnResetSlices.Width = 180;
            btnResetSlices.Click += (s, e) =>
            {
                // Set each slider to its midpoint
                trkXSlice.Value = trkXSlice.Maximum / 2;
                trkYSlice.Value = trkYSlice.Maximum / 2;
                trkZSlice.Value = trkZSlice.Maximum / 2;
                UpdateSlices();
            };
            panel.Controls.Add(btnResetSlices);

            tabSlices.Controls.Add(panel);
        }

        private void InitializeCuttingTab()
        {
            // Create panel to hold controls
            FlowLayoutPanel panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.FlowDirection = FlowDirection.TopDown;
            panel.AutoScroll = true;
            panel.Padding = new Padding(10);
            panel.WrapContents = false;

            // Add title label
            Label lblTitle = new Label();
            lblTitle.Text = "Dataset Cutting Controls";
            lblTitle.Font = new Font(lblTitle.Font.FontFamily, 10, FontStyle.Bold);
            lblTitle.AutoSize = true;
            lblTitle.Margin = new Padding(0, 0, 0, 10);
            panel.Controls.Add(lblTitle);

            // Add description
            Label lblDescription = new Label();
            lblDescription.Text = "Use these controls to cut away parts of the dataset along each axis.";
            lblDescription.AutoSize = true;
            lblDescription.Width = 330;
            panel.Controls.Add(lblDescription);

            // X Cutting plane
            GroupBox grpCutX = new GroupBox();
            grpCutX.Text = "X Cutting Plane";
            grpCutX.Width = 330;
            grpCutX.Height = 120;
            grpCutX.Margin = new Padding(0, 10, 0, 5);

            // Enable checkbox
            chkCutX = new CheckBox();
            chkCutX.Text = "Enable X Cutting";
            chkCutX.Location = new Point(10, 20);
            chkCutX.CheckedChanged += (s, e) =>
            {
                bool enabled = chkCutX.Checked;
                viewerForm.SetCutXEnabled(enabled);
                radCutXForward.Enabled = enabled;
                radCutXBackward.Enabled = enabled;
                trkCutX.Enabled = enabled;
            };
            grpCutX.Controls.Add(chkCutX);

            // Direction radio buttons
            radCutXForward = new RadioButton();
            radCutXForward.Text = "Forward";
            radCutXForward.Checked = true;
            radCutXForward.Location = new Point(120, 20);
            radCutXForward.Enabled = false;
            radCutXForward.CheckedChanged += (s, e) =>
            {
                if (radCutXForward.Checked)
                    viewerForm.SetCutXDirection(1.0f);
            };
            grpCutX.Controls.Add(radCutXForward);

            radCutXBackward = new RadioButton();
            radCutXBackward.Text = "Backward";
            radCutXBackward.Location = new Point(220, 20);
            radCutXBackward.Enabled = false;
            radCutXBackward.CheckedChanged += (s, e) =>
            {
                if (radCutXBackward.Checked)
                    viewerForm.SetCutXDirection(-1.0f);
            };
            grpCutX.Controls.Add(radCutXBackward);

            // Position slider
            trkCutX = new TrackBar();
            trkCutX.Minimum = 0;
            trkCutX.Maximum = 100;
            trkCutX.Value = 50;
            trkCutX.TickFrequency = 10;
            trkCutX.Width = 310;
            trkCutX.Location = new Point(10, 50);
            trkCutX.Enabled = false;
            trkCutX.Scroll += (s, e) =>
            {
                viewerForm.SetCutXPosition(trkCutX.Value / 100.0f);
            };
            grpCutX.Controls.Add(trkCutX);

            panel.Controls.Add(grpCutX);

            // Y Cutting plane
            GroupBox grpCutY = new GroupBox();
            grpCutY.Text = "Y Cutting Plane";
            grpCutY.Width = 330;
            grpCutY.Height = 120;
            grpCutY.Margin = new Padding(0, 5, 0, 5);

            // Enable checkbox
            chkCutY = new CheckBox();
            chkCutY.Text = "Enable Y Cutting";
            chkCutY.Location = new Point(10, 20);
            chkCutY.CheckedChanged += (s, e) =>
            {
                bool enabled = chkCutY.Checked;
                viewerForm.SetCutYEnabled(enabled);
                radCutYForward.Enabled = enabled;
                radCutYBackward.Enabled = enabled;
                trkCutY.Enabled = enabled;
            };
            grpCutY.Controls.Add(chkCutY);

            // Direction radio buttons
            radCutYForward = new RadioButton();
            radCutYForward.Text = "Forward";
            radCutYForward.Checked = true;
            radCutYForward.Location = new Point(120, 20);
            radCutYForward.Enabled = false;
            radCutYForward.CheckedChanged += (s, e) =>
            {
                if (radCutYForward.Checked)
                    viewerForm.SetCutYDirection(1.0f);
            };
            grpCutY.Controls.Add(radCutYForward);

            radCutYBackward = new RadioButton();
            radCutYBackward.Text = "Backward";
            radCutYBackward.Location = new Point(220, 20);
            radCutYBackward.Enabled = false;
            radCutYBackward.CheckedChanged += (s, e) =>
            {
                if (radCutYBackward.Checked)
                    viewerForm.SetCutYDirection(-1.0f);
            };
            grpCutY.Controls.Add(radCutYBackward);

            // Position slider
            trkCutY = new TrackBar();
            trkCutY.Minimum = 0;
            trkCutY.Maximum = 100;
            trkCutY.Value = 50;
            trkCutY.TickFrequency = 10;
            trkCutY.Width = 310;
            trkCutY.Location = new Point(10, 50);
            trkCutY.Enabled = false;
            trkCutY.Scroll += (s, e) =>
            {
                viewerForm.SetCutYPosition(trkCutY.Value / 100.0f);
            };
            grpCutY.Controls.Add(trkCutY);

            panel.Controls.Add(grpCutY);

            // Z Cutting plane
            GroupBox grpCutZ = new GroupBox();
            grpCutZ.Text = "Z Cutting Plane";
            grpCutZ.Width = 330;
            grpCutZ.Height = 120;
            grpCutZ.Margin = new Padding(0, 5, 0, 5);

            // Enable checkbox
            chkCutZ = new CheckBox();
            chkCutZ.Text = "Enable Z Cutting";
            chkCutZ.Location = new Point(10, 20);
            chkCutZ.CheckedChanged += (s, e) =>
            {
                bool enabled = chkCutZ.Checked;
                viewerForm.SetCutZEnabled(enabled);
                radCutZForward.Enabled = enabled;
                radCutZBackward.Enabled = enabled;
                trkCutZ.Enabled = enabled;
            };
            grpCutZ.Controls.Add(chkCutZ);

            // Direction radio buttons
            radCutZForward = new RadioButton();
            radCutZForward.Text = "Forward";
            radCutZForward.Checked = true;
            radCutZForward.Location = new Point(120, 20);
            radCutZForward.Enabled = false;
            radCutZForward.CheckedChanged += (s, e) =>
            {
                if (radCutZForward.Checked)
                    viewerForm.SetCutZDirection(1.0f);
            };
            grpCutZ.Controls.Add(radCutZForward);

            radCutZBackward = new RadioButton();
            radCutZBackward.Text = "Backward";
            radCutZBackward.Location = new Point(220, 20);
            radCutZBackward.Enabled = false;
            radCutZBackward.CheckedChanged += (s, e) =>
            {
                if (radCutZBackward.Checked)
                    viewerForm.SetCutZDirection(-1.0f);
            };
            grpCutZ.Controls.Add(radCutZBackward);

            // Position slider
            trkCutZ = new TrackBar();
            trkCutZ.Minimum = 0;
            trkCutZ.Maximum = 100;
            trkCutZ.Value = 50;
            trkCutZ.TickFrequency = 10;
            trkCutZ.Width = 310;
            trkCutZ.Location = new Point(10, 50);
            trkCutZ.Enabled = false;
            trkCutZ.Scroll += (s, e) =>
            {
                viewerForm.SetCutZPosition(trkCutZ.Value / 100.0f);
            };
            grpCutZ.Controls.Add(trkCutZ);

            panel.Controls.Add(grpCutZ);

            // Reset button
            Button btnResetCuts = new Button();
            btnResetCuts.Text = "Reset All Cuts";
            btnResetCuts.Width = 150;
            btnResetCuts.Click += (s, e) =>
            {
                // Reset all cutting planes
                chkCutX.Checked = false;
                chkCutY.Checked = false;
                chkCutZ.Checked = false;
                trkCutX.Value = 50;
                trkCutY.Value = 50;
                trkCutZ.Value = 50;
                radCutXForward.Checked = true;
                radCutYForward.Checked = true;
                radCutZForward.Checked = true;
                viewerForm.ResetAllCuts();
            };
            panel.Controls.Add(btnResetCuts);

            tabCutting.Controls.Add(panel);
        }

        private void InitializeInfoTab()
        {
            // Create panel to hold controls
            FlowLayoutPanel panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.FlowDirection = FlowDirection.TopDown;
            panel.AutoScroll = true;
            panel.Padding = new Padding(10);
            panel.WrapContents = false;

            // Add title label
            Label lblTitle = new Label();
            lblTitle.Text = "Dataset Information";
            lblTitle.Font = new Font(lblTitle.Font.FontFamily, 10, FontStyle.Bold);
            lblTitle.AutoSize = true;
            lblTitle.Margin = new Padding(0, 0, 0, 15);
            panel.Controls.Add(lblTitle);

            // Volume info
            lblVolumeInfo = new Label();
            lblVolumeInfo.Text = "Volume Information:";
            lblVolumeInfo.AutoSize = true;
            panel.Controls.Add(lblVolumeInfo);

            // Get volume info
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();
            double pixelSize = mainForm.GetPixelSize();

            // Create infobox
            TextBox txtVolumeInfo = new TextBox();
            txtVolumeInfo.Multiline = true;
            txtVolumeInfo.ReadOnly = true;
            txtVolumeInfo.ScrollBars = ScrollBars.Vertical;
            txtVolumeInfo.Width = 330;
            txtVolumeInfo.Height = 120;
            txtVolumeInfo.Text = $"Dimensions: {width} × {height} × {depth} voxels\r\n";
            txtVolumeInfo.Text += $"Volume: {width * height * depth:N0} voxels total\r\n";

            // Calculate volume in real-world units
            double voxelVolume = pixelSize * pixelSize * pixelSize;
            double totalVolumeCubicMm = voxelVolume * width * height * depth * 1e9; // in mm³

            txtVolumeInfo.Text += $"Pixel Size: {FormatSize(pixelSize)}\r\n";
            txtVolumeInfo.Text += $"Physical Size: {FormatSize(width * pixelSize)} × " +
                                 $"{FormatSize(height * pixelSize)} × " +
                                 $"{FormatSize(depth * pixelSize)}\r\n";
            txtVolumeInfo.Text += $"Total Volume: {totalVolumeCubicMm:N2} mm³";

            panel.Controls.Add(txtVolumeInfo);

            // Material info
            lblMaterialsInfo = new Label();
            lblMaterialsInfo.Text = "Materials Information:";
            lblMaterialsInfo.AutoSize = true;
            lblMaterialsInfo.Margin = new Padding(0, 15, 0, 5);
            panel.Controls.Add(lblMaterialsInfo);

            // Create infobox
            ListBox lstMatInfo = new ListBox();
            lstMatInfo.Width = 330;
            lstMatInfo.Height = 150;
            lstMatInfo.DrawMode = DrawMode.OwnerDrawFixed;
            lstMatInfo.DrawItem += (s, e) => {
                e.DrawBackground();

                if (e.Index < 0 || e.Index >= mainForm.Materials.Count)
                    return;

                Material mat = mainForm.Materials[e.Index];

                // Draw color block
                Rectangle colorRect = new Rectangle(
                    e.Bounds.X + 2,
                    e.Bounds.Y + 2,
                    20,
                    e.Bounds.Height - 4);

                using (SolidBrush brush = new SolidBrush(mat.Color))
                {
                    e.Graphics.FillRectangle(brush, colorRect);
                    e.Graphics.DrawRectangle(Pens.Black, colorRect);
                }

                // Draw text
                string text = $"{mat.Name} (ID: {mat.ID})";
                if (mat.IsExterior)
                    text += " - Exterior";

                Rectangle textRect = new Rectangle(
                    colorRect.Right + 5,
                    e.Bounds.Y,
                    e.Bounds.Width - colorRect.Width - 7,
                    e.Bounds.Height);

                TextRenderer.DrawText(e.Graphics, text, e.Font, textRect, Color.Black);

                e.DrawFocusRectangle();
            };

            foreach (Material mat in mainForm.Materials)
            {
                lstMatInfo.Items.Add(mat);
            }

            panel.Controls.Add(lstMatInfo);

            // Viewing instructions
            Label lblInstructions = new Label();
            lblInstructions.Text = "Viewing Instructions:";
            lblInstructions.Font = new Font(lblInstructions.Font, FontStyle.Bold);
            lblInstructions.AutoSize = true;
            lblInstructions.Margin = new Padding(0, 15, 0, 5);
            panel.Controls.Add(lblInstructions);

            TextBox txtInstructions = new TextBox();
            txtInstructions.Multiline = true;
            txtInstructions.ReadOnly = true;
            txtInstructions.ScrollBars = ScrollBars.Vertical;
            txtInstructions.Width = 330;
            txtInstructions.Height = 100;
            txtInstructions.Text = "• Left drag: Rotate camera\r\n";
            txtInstructions.Text += "• Right drag: Pan camera\r\n";
            txtInstructions.Text += "• Mouse wheel: Zoom in/out\r\n";
            txtInstructions.Text += "• Use the Materials tab to control visibility and opacity\r\n";
            txtInstructions.Text += "• Use the Slices tab to enable and position orthogonal slice planes\r\n";
            txtInstructions.Text += "• Use the Cutting tab to cut away parts of the volume\r\n";
            panel.Controls.Add(txtInstructions);

            tabInfo.Controls.Add(panel);
        }

        private string FormatSize(double meters)
        {
            if (meters >= 1)
                return $"{meters:F3} m";
            if (meters >= 1e-3)
                return $"{meters * 1e3:F3} mm";
            if (meters >= 1e-6)
                return $"{meters * 1e6:F3} μm";
            return $"{meters * 1e9:F3} nm";
        }

        private void UpdateSlices()
        {
            viewerForm.SetSliceIndices(trkXSlice.Value, trkYSlice.Value, trkZSlice.Value);
        }
    }
}