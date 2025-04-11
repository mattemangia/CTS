using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTSegmenter
{
    public partial class CTViewerControlPanel : Form
    {
        private CTViewer3DForm viewerForm;
        private MainForm mainForm;
        private VolumeRenderer volumeRenderer;
        private Timer memoryUpdateTimer;

        // UI controls
        private TabControl tabControl;
        private TabPage tabRender;
        private TabPage tabMaterials;
        private TabPage tabSlices;

        // Render tab controls
        private Button btnResetView;
        private Button btnExportModel;
        private Button btnApplyRender;
        private Button btnQuickTest;
        private CheckBox chkShowGrayscale;
        private CheckBox chkUseLOD;
        private ComboBox cmbQuality;
        private Label lblQuality;
        private TrackBar trkMinThreshold;
        private TrackBar trkMaxThreshold;
        private Label lblMinThreshold;
        private Label lblMaxThreshold;
        private ProgressBar progressBar;
        private Label lblStatus;
        private Label lblMemory;

        // Materials tab
        private CheckedListBox lstMaterials;
        private TrackBar trkOpacity;
        private Label lblOpacity;

        // Slices tab
        private CheckBox chkEnableSlices;
        private CheckBox chkShowOrthoPlanes;
        private TrackBar trkXSlice;
        private TrackBar trkYSlice;
        private TrackBar trkZSlice;
        private Label lblXSlice;
        private Label lblYSlice;
        private Label lblZSlice;

        // Public properties for the main form to access
        public bool IsGrayscaleEnabled => chkShowGrayscale.Checked;
        public bool IsLodEnabled => chkUseLOD.Checked;
        public bool AreSlicesEnabled => chkEnableSlices.Checked;
        public bool AreOrthoplanesVisible => chkShowOrthoPlanes.Checked;
        public int XSliceValue => trkXSlice.Value;
        public int YSliceValue => trkYSlice.Value;
        public int ZSliceValue => trkZSlice.Value;

        public CTViewerControlPanel(CTViewer3DForm viewer, MainForm main, VolumeRenderer renderer)
        {
            try
            {
                this.viewerForm = viewer;
                this.mainForm = main;
                this.volumeRenderer = renderer;

                InitializeComponent();
                InitializeRenderTab();
                InitializeMaterialsTab();
                InitializeSlicesTab();
                ApplyStyles();

                memoryUpdateTimer = new Timer();
                memoryUpdateTimer.Interval = 1000;
                memoryUpdateTimer.Tick += (s, e) => {
                    long totalMem = GC.GetTotalMemory(false);
                    lblMemory.Text = $"Memory: {totalMem / (1024 * 1024)} MB";
                };
                memoryUpdateTimer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize control panel: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log("[CTViewerControlPanel] Error in constructor: " + ex.Message);
            }
        }

        private void InitializeComponent()
        {
            // Create controls
            this.tabControl = new TabControl();
            this.tabRender = new TabPage("Render");
            this.tabMaterials = new TabPage("Materials");
            this.tabSlices = new TabPage("Slices");

            // Form settings
            this.Text = "3D Viewer Controls";
            this.BackColor = Color.FromArgb(40, 40, 40);
            this.FormBorderStyle = FormBorderStyle.SizableToolWindow;
            this.StartPosition = FormStartPosition.Manual;
            this.ShowInTaskbar = false;
            this.MinimumSize = new Size(300, 500);

            // Setup tab control
            this.tabControl.Dock = DockStyle.Fill;
            this.tabControl.ItemSize = new Size(80, 24);
            this.tabControl.Controls.Add(this.tabRender);
            this.tabControl.Controls.Add(this.tabMaterials);
            this.tabControl.Controls.Add(this.tabSlices);

            this.Controls.Add(this.tabControl);
        }

        private void InitializeRenderTab()
        {
            // Setup the rendering tab with controls in proper order
            tabRender.BackColor = Color.FromArgb(50, 50, 50);
            tabRender.AutoScroll = true;

            // Create top panel for buttons and checkboxes
            Panel topPanel = new Panel();
            topPanel.Dock = DockStyle.Top;
            topPanel.Height = 130;
            topPanel.BackColor = Color.FromArgb(50, 50, 50);

            // Create buttons
            btnResetView = new Button();
            btnResetView.Text = "Reset View";
            btnResetView.Size = new Size(100, 30);
            btnResetView.Location = new Point(10, 10);
            btnResetView.Click += (s, e) => viewerForm.ResetView();
            topPanel.Controls.Add(btnResetView);

            btnExportModel = new Button();
            btnExportModel.Text = "Export 3D";
            btnExportModel.Size = new Size(100, 30);
            btnExportModel.Location = new Point(120, 10);
            btnExportModel.Click += async (s, e) => {
                using (SaveFileDialog dialog = new SaveFileDialog())
                {
                    dialog.Filter = "STL File (*.stl)|*.stl|OBJ File (*.obj)|*.obj|PLY File (*.ply)|*.ply";
                    dialog.Title = "Export 3D Model";
                    dialog.FileName = "VolumeExport";
                    if (dialog.ShowDialog() == DialogResult.OK)
                    {
                        await viewerForm.ExportModel(dialog.FileName);
                    }
                }
            };
            topPanel.Controls.Add(btnExportModel);

            btnApplyRender = new Button();
            btnApplyRender.Text = "Apply & Render";
            btnApplyRender.Size = new Size(110, 30);
            btnApplyRender.Location = new Point(10, 50);
            btnApplyRender.Click += async (s, e) => {
                await viewerForm.ApplyThresholdAndRender(
                    trkMinThreshold.Value,
                    trkMaxThreshold.Value,
                    cmbQuality.SelectedIndex);
            };
            topPanel.Controls.Add(btnApplyRender);

            btnQuickTest = new Button();
            btnQuickTest.Text = "Quick Test";
            btnQuickTest.Size = new Size(100, 30);
            btnQuickTest.Location = new Point(130, 50);
            btnQuickTest.Click += (s, e) => viewerForm.RunQuickTest();
            topPanel.Controls.Add(btnQuickTest);

            chkShowGrayscale = new CheckBox();
            chkShowGrayscale.Text = "Show Grayscale";
            chkShowGrayscale.Checked = true;
            chkShowGrayscale.AutoSize = true;
            chkShowGrayscale.Location = new Point(10, 90);
            topPanel.Controls.Add(chkShowGrayscale);

            chkUseLOD = new CheckBox();
            chkUseLOD.Text = "Adaptive LOD";
            chkUseLOD.Checked = true;
            chkUseLOD.AutoSize = true;
            chkUseLOD.Location = new Point(160, 90);
            topPanel.Controls.Add(chkUseLOD);

            tabRender.Controls.Add(topPanel);

            // Create panel for quality selection
            Panel qualityPanel = new Panel();
            qualityPanel.Dock = DockStyle.Top;
            qualityPanel.Top = 130;
            qualityPanel.Height = 50;
            qualityPanel.BackColor = Color.FromArgb(50, 50, 50);

            lblQuality = new Label();
            lblQuality.Text = "Quality:";
            lblQuality.AutoSize = true;
            lblQuality.Location = new Point(10, 15);
            qualityPanel.Controls.Add(lblQuality);

            cmbQuality = new ComboBox();
            cmbQuality.Items.AddRange(new string[] { "Low", "Medium", "High", "Ultra" });
            cmbQuality.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbQuality.Size = new Size(130, 25);
            cmbQuality.Location = new Point(70, 12);
            cmbQuality.SelectedIndex = 2; // Default to High
            qualityPanel.Controls.Add(cmbQuality);

            tabRender.Controls.Add(qualityPanel);

            // Create panel for thresholds
            Panel thresholdPanel = new Panel();
            thresholdPanel.Dock = DockStyle.Top;
            thresholdPanel.Top = 180;
            thresholdPanel.Height = 140;
            thresholdPanel.BackColor = Color.FromArgb(50, 50, 50);

            lblMinThreshold = new Label();
            lblMinThreshold.Text = "Min Threshold: 0";
            lblMinThreshold.AutoSize = true;
            lblMinThreshold.Location = new Point(10, 10);
            thresholdPanel.Controls.Add(lblMinThreshold);

            trkMinThreshold = new TrackBar();
            trkMinThreshold.Minimum = 0;
            trkMinThreshold.Maximum = 255;
            trkMinThreshold.Value = 0;
            trkMinThreshold.TickFrequency = 25;
            trkMinThreshold.Width = 260;
            trkMinThreshold.Location = new Point(10, 30);
            trkMinThreshold.ValueChanged += (s, e) => {
                lblMinThreshold.Text = $"Min Threshold: {trkMinThreshold.Value}";
            };
            thresholdPanel.Controls.Add(trkMinThreshold);

            lblMaxThreshold = new Label();
            lblMaxThreshold.Text = "Max Threshold: 255";
            lblMaxThreshold.AutoSize = true;
            lblMaxThreshold.Location = new Point(10, 80);
            thresholdPanel.Controls.Add(lblMaxThreshold);

            trkMaxThreshold = new TrackBar();
            trkMaxThreshold.Minimum = 0;
            trkMaxThreshold.Maximum = 255;
            trkMaxThreshold.Value = 255;
            trkMaxThreshold.TickFrequency = 25;
            trkMaxThreshold.Width = 260;
            trkMaxThreshold.Location = new Point(10, 100);
            trkMaxThreshold.ValueChanged += (s, e) => {
                lblMaxThreshold.Text = $"Max Threshold: {trkMaxThreshold.Value}";
            };
            thresholdPanel.Controls.Add(trkMaxThreshold);

            tabRender.Controls.Add(thresholdPanel);

            // Create status panel
            Panel statusPanel = new Panel();
            statusPanel.Dock = DockStyle.Top;
            statusPanel.Top = 320;
            statusPanel.Height = 80;
            statusPanel.BackColor = Color.FromArgb(50, 50, 50);

            progressBar = new ProgressBar();
            progressBar.Style = ProgressBarStyle.Marquee;
            progressBar.Visible = false;
            progressBar.Width = 250;
            progressBar.Location = new Point(10, 10);
            statusPanel.Controls.Add(progressBar);

            lblStatus = new Label();
            lblStatus.Text = "Ready";
            lblStatus.AutoSize = true;
            lblStatus.Location = new Point(10, 40);
            statusPanel.Controls.Add(lblStatus);

            lblMemory = new Label();
            lblMemory.Text = "Memory: 0 MB";
            lblMemory.AutoSize = true;
            lblMemory.Location = new Point(150, 40);
            statusPanel.Controls.Add(lblMemory);

            tabRender.Controls.Add(statusPanel);
        }

        private void InitializeMaterialsTab()
        {
            tabMaterials.BackColor = Color.FromArgb(50, 50, 50);
            tabMaterials.AutoScroll = true;

            // Materials list
            Panel listPanel = new Panel();
            listPanel.Dock = DockStyle.Top;
            listPanel.Height = 300;
            listPanel.BackColor = Color.FromArgb(50, 50, 50);

            Label lblTitle = new Label();
            lblTitle.Text = "Available Materials:";
            lblTitle.AutoSize = true;
            lblTitle.Location = new Point(10, 10);
            listPanel.Controls.Add(lblTitle);

            lstMaterials = new CheckedListBox();
            lstMaterials.CheckOnClick = true;
            lstMaterials.Size = new Size(260, 250);
            lstMaterials.Location = new Point(10, 35);
            lstMaterials.BackColor = Color.FromArgb(60, 60, 60);
            lstMaterials.ForeColor = Color.White;
            lstMaterials.BorderStyle = BorderStyle.FixedSingle;

            // Populate materials list
            foreach (var material in mainForm.Materials)
            {
                if (!material.IsExterior)
                {
                    int index = lstMaterials.Items.Add(material.Name, true);
                }
            }

            lstMaterials.ItemCheck += (s, e) => {
                this.BeginInvoke(new Action(() => {
                    string name = lstMaterials.Items[e.Index].ToString();
                    var mat = mainForm.Materials.Find(m => m.Name == name);
                    if (mat != null)
                    {
                        viewerForm.SetMaterialVisibility(mat.ID, lstMaterials.GetItemChecked(e.Index));
                    }
                }));
            };
            listPanel.Controls.Add(lstMaterials);

            tabMaterials.Controls.Add(listPanel);

            // Opacity controls
            Panel opacityPanel = new Panel();
            opacityPanel.Dock = DockStyle.Top;
            opacityPanel.Top = 300;
            opacityPanel.Height = 80;
            opacityPanel.BackColor = Color.FromArgb(50, 50, 50);

            lblOpacity = new Label();
            lblOpacity.Text = "Opacity: 100%";
            lblOpacity.AutoSize = true;
            lblOpacity.Location = new Point(10, 10);
            opacityPanel.Controls.Add(lblOpacity);

            trkOpacity = new TrackBar();
            trkOpacity.Minimum = 0;
            trkOpacity.Maximum = 100;
            trkOpacity.Value = 100;
            trkOpacity.TickFrequency = 10;
            trkOpacity.Width = 260;
            trkOpacity.Location = new Point(10, 30);
            trkOpacity.ValueChanged += (s, e) => {
                if (lstMaterials.SelectedItem != null)
                {
                    string name = lstMaterials.SelectedItem.ToString();
                    var mat = mainForm.Materials.Find(m => m.Name == name);
                    if (mat != null && !mat.IsExterior)
                    {
                        double opacity = trkOpacity.Value / 100.0;
                        viewerForm.SetMaterialOpacity(mat.ID, opacity);
                        lblOpacity.Text = $"Opacity: {trkOpacity.Value}%";
                    }
                }
            };
            opacityPanel.Controls.Add(trkOpacity);

            tabMaterials.Controls.Add(opacityPanel);

            // When a material is selected, update opacity slider
            lstMaterials.SelectedIndexChanged += (s, e) => {
                if (lstMaterials.SelectedItem != null)
                {
                    string name = lstMaterials.SelectedItem.ToString();
                    var mat = mainForm.Materials.Find(m => m.Name == name);
                    if (mat != null && !mat.IsExterior)
                    {
                        double op = viewerForm.GetMaterialOpacity(mat.ID);
                        trkOpacity.Value = (int)(op * 100);
                        lblOpacity.Text = $"Opacity: {trkOpacity.Value}%";
                    }
                }
            };
        }

        private void InitializeSlicesTab()
        {
            tabSlices.BackColor = Color.FromArgb(50, 50, 50);
            tabSlices.AutoScroll = true;

            // Checkboxes panel
            Panel checkboxPanel = new Panel();
            checkboxPanel.Dock = DockStyle.Top;
            checkboxPanel.Height = 80;
            checkboxPanel.BackColor = Color.FromArgb(50, 50, 50);

            chkEnableSlices = new CheckBox();
            chkEnableSlices.Text = "Enable Slice Controls";
            chkEnableSlices.Checked = true;
            chkEnableSlices.AutoSize = true;
            chkEnableSlices.Location = new Point(10, 15);
            chkEnableSlices.CheckedChanged += (s, e) => {
                bool enabled = chkEnableSlices.Checked;
                trkXSlice.Enabled = enabled;
                trkYSlice.Enabled = enabled;
                trkZSlice.Enabled = enabled;
                chkShowOrthoPlanes.Enabled = enabled;

                viewerForm.ShowSlicePlanes(enabled && chkShowOrthoPlanes.Checked);
            };
            checkboxPanel.Controls.Add(chkEnableSlices);

            chkShowOrthoPlanes = new CheckBox();
            chkShowOrthoPlanes.Text = "Show Ortho Planes";
            chkShowOrthoPlanes.Checked = true;
            chkShowOrthoPlanes.AutoSize = true;
            chkShowOrthoPlanes.Location = new Point(10, 45);
            chkShowOrthoPlanes.CheckedChanged += (s, e) => {
                if (chkEnableSlices.Checked)
                {
                    viewerForm.ShowSlicePlanes(chkShowOrthoPlanes.Checked);
                }
            };
            checkboxPanel.Controls.Add(chkShowOrthoPlanes);

            tabSlices.Controls.Add(checkboxPanel);

            // X Slice panel
            Panel xSlicePanel = new Panel();
            xSlicePanel.Dock = DockStyle.Top;
            xSlicePanel.Top = 80;
            xSlicePanel.Height = 80;
            xSlicePanel.BackColor = Color.FromArgb(50, 50, 50);

            lblXSlice = new Label();
            lblXSlice.Text = "X Slice:";
            lblXSlice.AutoSize = true;
            lblXSlice.Location = new Point(10, 10);
            xSlicePanel.Controls.Add(lblXSlice);

            trkXSlice = new TrackBar();
            trkXSlice.Minimum = 0;
            trkXSlice.Maximum = mainForm.GetWidth() - 1;
            trkXSlice.Value = trkXSlice.Maximum;
            trkXSlice.TickFrequency = Math.Max(1, trkXSlice.Maximum / 10);
            trkXSlice.Width = 260;
            trkXSlice.Location = new Point(10, 30);
            trkXSlice.ValueChanged += (s, e) => {
                if (chkEnableSlices.Checked)
                {
                    viewerForm.UpdateSlice(0, trkXSlice.Value);
                }
            };
            xSlicePanel.Controls.Add(trkXSlice);

            tabSlices.Controls.Add(xSlicePanel);

            // Y Slice panel
            Panel ySlicePanel = new Panel();
            ySlicePanel.Dock = DockStyle.Top;
            ySlicePanel.Top = 160;
            ySlicePanel.Height = 80;
            ySlicePanel.BackColor = Color.FromArgb(50, 50, 50);

            lblYSlice = new Label();
            lblYSlice.Text = "Y Slice:";
            lblYSlice.AutoSize = true;
            lblYSlice.Location = new Point(10, 10);
            ySlicePanel.Controls.Add(lblYSlice);

            trkYSlice = new TrackBar();
            trkYSlice.Minimum = 0;
            trkYSlice.Maximum = mainForm.GetHeight() - 1;
            trkYSlice.Value = trkYSlice.Maximum;
            trkYSlice.TickFrequency = Math.Max(1, trkYSlice.Maximum / 10);
            trkYSlice.Width = 260;
            trkYSlice.Location = new Point(10, 30);
            trkYSlice.ValueChanged += (s, e) => {
                if (chkEnableSlices.Checked)
                {
                    viewerForm.UpdateSlice(1, trkYSlice.Value);
                }
            };
            ySlicePanel.Controls.Add(trkYSlice);

            tabSlices.Controls.Add(ySlicePanel);

            // Z Slice panel
            Panel zSlicePanel = new Panel();
            zSlicePanel.Dock = DockStyle.Top;
            zSlicePanel.Top = 240;
            zSlicePanel.Height = 80;
            zSlicePanel.BackColor = Color.FromArgb(50, 50, 50);

            lblZSlice = new Label();
            lblZSlice.Text = "Z Slice:";
            lblZSlice.AutoSize = true;
            lblZSlice.Location = new Point(10, 10);
            zSlicePanel.Controls.Add(lblZSlice);

            trkZSlice = new TrackBar();
            trkZSlice.Minimum = 0;
            trkZSlice.Maximum = mainForm.GetDepth() - 1;
            trkZSlice.Value = trkZSlice.Maximum;
            trkZSlice.TickFrequency = Math.Max(1, trkZSlice.Maximum / 10);
            trkZSlice.Width = 260;
            trkZSlice.Location = new Point(10, 30);
            trkZSlice.ValueChanged += (s, e) => {
                if (chkEnableSlices.Checked)
                {
                    viewerForm.UpdateSlice(2, trkZSlice.Value);
                }
            };
            zSlicePanel.Controls.Add(trkZSlice);

            tabSlices.Controls.Add(zSlicePanel);
        }

        private void ApplyStyles()
        {
            // Apply dark theme to all controls
            ApplyStyleToControl(this);

            // Custom tab control styling
            tabControl.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabControl.DrawItem += (sender, e) => {
                TabPage page = tabControl.TabPages[e.Index];
                Rectangle tabBounds = tabControl.GetTabRect(e.Index);

                using (SolidBrush brush = new SolidBrush(Color.FromArgb(60, 60, 60)))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    e.Graphics.FillRectangle(brush, tabBounds);

                    StringFormat stringFormat = new StringFormat();
                    stringFormat.Alignment = StringAlignment.Center;
                    stringFormat.LineAlignment = StringAlignment.Center;
                    e.Graphics.DrawString(page.Text, tabControl.Font, textBrush, tabBounds, stringFormat);
                }
            };
        }

        private void ApplyStyleToControl(Control control)
        {
            if (control is Button btn)
            {
                btn.BackColor = Color.FromArgb(60, 60, 60);
                btn.ForeColor = Color.White;
                btn.FlatStyle = FlatStyle.Flat;
                btn.FlatAppearance.BorderColor = Color.DarkGray;
                btn.Font = new Font(btn.Font.FontFamily, 9f, FontStyle.Regular);
            }
            else if (control is Label lbl)
            {
                lbl.ForeColor = Color.White;
            }
            else if (control is CheckBox chk)
            {
                chk.ForeColor = Color.White;
            }
            else if (control is ComboBox cmb)
            {
                cmb.BackColor = Color.FromArgb(60, 60, 60);
                cmb.ForeColor = Color.White;
                cmb.FlatStyle = FlatStyle.Flat;
            }
            else if (control is TrackBar)
            {
                // TrackBars don't need special styling
            }
            else if (control is TabPage)
            {
                control.BackColor = Color.FromArgb(50, 50, 50);
                control.ForeColor = Color.White;
            }
            else if (control is Panel)
            {
                control.BackColor = Color.FromArgb(50, 50, 50);
            }

            // Apply style to child controls
            foreach (Control child in control.Controls)
            {
                ApplyStyleToControl(child);
            }
        }

        public void SetStatus(string statusText, bool showProgress)
        {
            lblStatus.Text = statusText;
            progressBar.Visible = showProgress;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (memoryUpdateTimer != null)
            {
                memoryUpdateTimer.Stop();
                memoryUpdateTimer = null;
            }

            base.OnFormClosing(e);
        }
    }
}
