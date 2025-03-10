using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTSegmenter
{
    // ------------------------------------------------------------------------
    // ControlForm – provides UI controls for loading, segmentation, materials etc.
    // ------------------------------------------------------------------------

    public partial class ControlForm : Form
    {
        // Current segmentation tool. Default is Pan.
        private SegmentationTool currentTool = SegmentationTool.Pan;

        // New Tools menu items.
        private ToolStripMenuItem toolsMenu;
        private ToolStripMenuItem panMenuItem;
        private ToolStripMenuItem eraserMenuItem;
        private ToolStripMenuItem brushMenuItem;
        private ToolStripMenuItem thresholdingMenuItem;

        // New UI elements in the left panel.
        private Button btnInterpolate;
        private TrackBar toolSizeSlider;
        private Label toolSizeLabel;
        private Timer brushOverlayTimer;

        private MainForm mainForm;
        private ListBox lstMaterials;
        private Button btnAddMaterial;
        private Button btnRemoveMaterial;
        private Button btnRenameMaterial;
        private Label lblThreshold;
        private TrackBar trkMin, trkMax;
        private NumericUpDown numThresholdMin, numThresholdMax;
        private Button btnAddSelection;
        private Button btnSubSelection;
        private Button btnSegmentAnything;
        private Button btnRefresh;
        private Label lblSlice;
        private TrackBar sliceSlider;
        private NumericUpDown numSlice;
        private Label lblXz;
        private TrackBar sliderXZ;
        private NumericUpDown numXz;
        private Label lblYz;
        private TrackBar sliderYZ;
        private NumericUpDown numYz;
        private PictureBox histogramPictureBox;
        private CheckBox chkLoadFull;
        private Timer xySliceUpdateTimer;
        private int pendingXySliceValue;
        private System.Windows.Forms.Timer thresholdUpdateTimer;
        private int pendingMin = -1;
        private int pendingMax = -1;
        private MenuStrip menuStrip;
        private ToolStripMenuItem fileMenu;
        private ToolStripMenuItem loadFolderMenuItem;
        private ToolStripMenuItem importBinMenuItem;
        private ToolStripSeparator fileSep1;
        private ToolStripMenuItem saveBinMenuItem;
        private ToolStripMenuItem exportImagesMenuItem;
        private ToolStripMenuItem closeDatasetMenuItem;
        private ToolStripMenuItem exitMenuItem;
        private ToolStripMenuItem editMenu;
        private ToolStripMenuItem addMaterialMenuItem;
        private ToolStripMenuItem deleteMaterialMenuItem;
        private ToolStripMenuItem renameMaterialMenuItem;
        private ToolStripSeparator editSep1;
        private ToolStripMenuItem addThresholdedMenuItem;
        private ToolStripMenuItem subtractThresholdedMenuItem;
        private ToolStripSeparator editSep2;
        private ToolStripMenuItem segmentAnythingMenuItem;
        private ToolStripMenuItem viewMenu;
        private ToolStripMenuItem showMaskMenuItem;
        private ToolStripMenuItem enableThresholdMaskMenuItem; // This now is "Render Materials"
        private ToolStripMenuItem showHistogramMenuItem;
        private ToolStripMenuItem showOrthoviewsMenuItem;
        private ToolStripMenuItem resetZoomMenuItem;
        private ToolStripMenuItem helpMenu;
        private ToolStripMenuItem dbgConsole;
        private ToolStripMenuItem about;
        private bool thresholdMaskEnabled = true;
        private bool isUpdatingHistogram = false;

        //Annotations for SAM2
        AnnotationManager sharedAnnotationManager = new AnnotationManager();
        

        public ControlForm(MainForm form)
        {
            mainForm = form;
            mainForm.FormClosed += (s, e) =>
            {
                this.Close();
                Application.Exit();
            };
            mainForm.AnnotationMgr = sharedAnnotationManager;
            InitializeComponent();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Logger.ShuttingDown = true;
            if (Logger.LogWindowInstance != null && !Logger.LogWindowInstance.IsDisposed)
            {
                Logger.LogWindowInstance.Invoke(new Action(() => Logger.LogWindowInstance.Close()));
            }
            Application.Exit();
        }

        private void InitializeComponent()
        {
            this.TopMost = true;
            this.Text = "Controls";
            this.Size = new Size(700, 645);
            try
            {
                string iconPath = Path.Combine(Application.StartupPath, "favicon.ico");
                if (File.Exists(iconPath))
                    this.Icon = new Icon(iconPath);
            }
            catch { }

            menuStrip = new MenuStrip();
            fileMenu = new ToolStripMenuItem("File");
            loadFolderMenuItem = new ToolStripMenuItem("Load Folder");
            loadFolderMenuItem.Click += async (s, e) => await OnLoadFolderClicked();
            importBinMenuItem = new ToolStripMenuItem("Import .bin");
            importBinMenuItem.Click += async (s, e) => await OnImportClicked();
            fileSep1 = new ToolStripSeparator();
            saveBinMenuItem = new ToolStripMenuItem("Save .bin");
            saveBinMenuItem.Click += (s, e) => OnSaveClicked();
            exportImagesMenuItem = new ToolStripMenuItem("Export Images");
            exportImagesMenuItem.Click += (s, e) => mainForm.ExportImages();
            closeDatasetMenuItem = new ToolStripMenuItem("Close B/W Dataset");
            closeDatasetMenuItem.Click += (s, e) => OnCloseDataset();
            exitMenuItem = new ToolStripMenuItem("Exit");
            exitMenuItem.Click += (s, e) =>
            {
                mainForm.Close();
                // Force termination even if windows remain open.
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            };
            fileMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
                loadFolderMenuItem, importBinMenuItem, fileSep1, saveBinMenuItem,
                exportImagesMenuItem, closeDatasetMenuItem, exitMenuItem
            });
            menuStrip.Items.Add(fileMenu);

            editMenu = new ToolStripMenuItem("Edit");
            addMaterialMenuItem = new ToolStripMenuItem("Add Material");
            addMaterialMenuItem.Click += (s, e) => OnAddMaterial();
            deleteMaterialMenuItem = new ToolStripMenuItem("Delete Material");
            deleteMaterialMenuItem.Click += (s, e) => OnRemoveMaterial();
            renameMaterialMenuItem = new ToolStripMenuItem("Rename Material");
            renameMaterialMenuItem.Click += (s, e) => OnRenameMaterial();
            editSep1 = new ToolStripSeparator();
            addThresholdedMenuItem = new ToolStripMenuItem("Add Thresholded");
            addThresholdedMenuItem.Click += (s, e) => AddThresholdedSelection();
            subtractThresholdedMenuItem = new ToolStripMenuItem("Subtract Thresholded");
            subtractThresholdedMenuItem.Click += (s, e) => SubThresholdedSelection();
            editSep2 = new ToolStripSeparator();
            segmentAnythingMenuItem = new ToolStripMenuItem("Segment Anything");
            segmentAnythingMenuItem.Click += (s, e) =>
            {
                Logger.Log("[ControlForm] Opening Segment Anything - ONNX Processor");
                SAMForm samForm = new SAMForm(mainForm, sharedAnnotationManager, mainForm.Materials);
                mainForm.SetSegmentationTool(SegmentationTool.Point);
                samForm.Show();
            };
            editMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
                addMaterialMenuItem, deleteMaterialMenuItem, renameMaterialMenuItem, editSep1,
                addThresholdedMenuItem, subtractThresholdedMenuItem, editSep2, segmentAnythingMenuItem
            });
            menuStrip.Items.Add(editMenu);

            viewMenu = new ToolStripMenuItem("View");
            showMaskMenuItem = new ToolStripMenuItem("Show Mask")
            {
                CheckOnClick = true,
                Checked = false
            };
            showMaskMenuItem.CheckedChanged += (s, e) =>
            {
                mainForm.ShowMask = showMaskMenuItem.Checked;
                mainForm.RenderViews();
                _ = mainForm.RenderOrthoViewsAsync();
            };

            // This menu item now toggles RenderMaterials.
            enableThresholdMaskMenuItem = new ToolStripMenuItem("Render Materials")
            {

                CheckOnClick = true,
                Checked = false
            };
            enableThresholdMaskMenuItem.CheckedChanged += (s, e) =>
            {
                mainForm.RenderMaterials = enableThresholdMaskMenuItem.Checked;
                // Do not change the text – keep it always "Render Materials".
                _ = mainForm.RenderOrthoViewsAsync();
                mainForm.RenderViews();
            };

            showHistogramMenuItem = new ToolStripMenuItem("Show Histogram")
            {
                CheckOnClick = true,
                Checked = false
            };
            showHistogramMenuItem.CheckedChanged += (s, e) =>
            {
                histogramPictureBox.Visible = showHistogramMenuItem.Checked;
                if (showHistogramMenuItem.Checked)
                    UpdateHistogram(histogramPictureBox);
            };

            showOrthoviewsMenuItem = new ToolStripMenuItem("Show Orthoviews")
            {
                CheckOnClick = true,
                Checked = false
            };
            showOrthoviewsMenuItem.CheckedChanged += async (s, e) =>
            {
                mainForm.SetShowProjections(showOrthoviewsMenuItem.Checked);
                if (showOrthoviewsMenuItem.Checked)
                    await mainForm.RenderOrthoViewsAsync();
            };

            resetZoomMenuItem = new ToolStripMenuItem("Reset Zoom");
            resetZoomMenuItem.Click += (s, e) => mainForm.ResetView();

            viewMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
                showMaskMenuItem, enableThresholdMaskMenuItem, showHistogramMenuItem,
                showOrthoviewsMenuItem, resetZoomMenuItem
            });
            menuStrip.Items.Add(viewMenu);

            helpMenu = new ToolStripMenuItem("Help");
            dbgConsole = new ToolStripMenuItem("Log Window");
            about = new ToolStripMenuItem("About");
            // ---- New: Create Tools menu and insert before Help.
            toolsMenu = new ToolStripMenuItem("Tools");
            panMenuItem = new ToolStripMenuItem("Pan") { CheckOnClick = true, Checked = true };
            eraserMenuItem = new ToolStripMenuItem("Eraser") { CheckOnClick = true };
            brushMenuItem = new ToolStripMenuItem("Brush") { CheckOnClick = true };
            thresholdingMenuItem = new ToolStripMenuItem("Thresholding") { CheckOnClick = true };
            // Attach a common click handler.
            panMenuItem.Click += ToolsMenuItem_Click;
            eraserMenuItem.Click += ToolsMenuItem_Click;
            brushMenuItem.Click += ToolsMenuItem_Click;
            thresholdingMenuItem.Click += ToolsMenuItem_Click;
            toolsMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
                panMenuItem, eraserMenuItem, brushMenuItem, thresholdingMenuItem
            });
            dbgConsole.Click += (s, e) =>
            {
                if (Logger.LogWindowInstance == null || Logger.LogWindowInstance.IsDisposed)
                    Logger.RestartLogWindow();
                else
                {
                    Logger.LogWindowInstance.Invoke(new Action(() =>
                    {
                        if (!Logger.LogWindowInstance.Visible)
                            Logger.LogWindowInstance.Show();
                        Logger.LogWindowInstance.BringToFront();
                    }));
                }
            };
            helpMenu.DropDownItems.AddRange(new ToolStripItem[] { dbgConsole, about });

            menuStrip.Items.Add(helpMenu);
            menuStrip.Items.Insert(menuStrip.Items.IndexOf(helpMenu), toolsMenu);
            menuStrip.Dock = DockStyle.Top;
            this.Controls.Add(menuStrip);
            this.MainMenuStrip = menuStrip;

            TableLayoutPanel table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                AutoSize = true,
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            this.Controls.Add(table);

            FlowLayoutPanel leftPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                AutoScroll = true,
                WrapContents = false,
                Padding = new Padding(30),
            };
            btnInterpolate = new Button { Text = "Interpolate", Width = 120, Enabled=false };
            btnInterpolate.Click += (s, e) =>
            {
                // Make sure a valid non-Exterior material is selected.
                int idx = lstMaterials.SelectedIndex;
                if (idx < 0 || idx >= mainForm.Materials.Count)
                {
                    MessageBox.Show("No material selected for interpolation.");
                    return;
                }
                Material mat = mainForm.Materials[idx];
                if (mat.IsExterior)
                {
                    MessageBox.Show("Cannot interpolate for the Exterior material.");
                    return;
                }

                // Disable the button while processing.
                btnInterpolate.Enabled = false;
                // Run the interpolation on a background thread.
                Task.Run(() =>
                {
                    mainForm.InterpolateSelection(mat.ID);
                }).ContinueWith(t =>
                {
                    // Re-enable the button on the UI thread once complete.
                    this.Invoke(new Action(() =>
                    {
                        btnInterpolate.Enabled = true;
                    }));
                });
            };


            chkLoadFull = new CheckBox
            {
                Text = "Load Full (no mapping)",
                AutoSize = true,
                Checked = false
            };
            chkLoadFull.CheckedChanged += (s, e) =>
            {
                mainForm.SetUseMemoryMapping(!chkLoadFull.Checked);
                Logger.Log($"[ControlForm] chkLoadFull changed. Now useMemoryMapping = {!chkLoadFull.Checked}");
            };
            leftPanel.Controls.Add(chkLoadFull);

            FlowLayoutPanel materialPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false
            };
            btnAddMaterial = new Button { Text = "Add", Width = 70, Height = 25 };
            btnAddMaterial.Click += (s, e) => OnAddMaterial();
            btnRemoveMaterial = new Button { Text = "Delete", Width = 70, Height = 25 };
            btnRemoveMaterial.Click += (s, e) => OnRemoveMaterial();
            btnRenameMaterial = new Button { Text = "Rename", Width = 70, Height = 25 };
            btnRenameMaterial.Click += (s, e) => OnRenameMaterial();
            materialPanel.Controls.Add(btnAddMaterial);
            materialPanel.Controls.Add(btnRemoveMaterial);
            materialPanel.Controls.Add(btnRenameMaterial);
            leftPanel.Controls.Add(materialPanel);

            Label lblMaterials = new Label { Text = "Materials:", AutoSize = true };
            leftPanel.Controls.Add(lblMaterials);
            lstMaterials = new ListBox
            {
                Width = 260,
                Height = 200,
                DrawMode = DrawMode.OwnerDrawFixed
            };
            lstMaterials.DrawItem += LstMaterials_DrawItem;
            lstMaterials.SelectedIndexChanged += (s, e) => UpdateThresholdSliders();
            lstMaterials.MouseDown += LstMaterials_MouseDown;
            leftPanel.Controls.Add(lstMaterials);

            lblThreshold = new Label { Text = "Threshold [min..max]", AutoSize = true };
            leftPanel.Controls.Add(lblThreshold);

            Panel thresholdPanel = new Panel { Width = 260, Height = 80 };
            trkMin = new TrackBar { Width = 150, Minimum = 0, Maximum = 255, TickFrequency = 32, Value = 1, Location = new Point(0, 0) };
            thresholdPanel.Controls.Add(trkMin);
            numThresholdMin = new NumericUpDown { Width = 80, Minimum = 0, Maximum = 255, Value = trkMin.Value, Location = new Point(155, 0) };
            numThresholdMin.ValueChanged += (s, e) => { trkMin.Value = (int)numThresholdMin.Value; UpdateSelectedMaterialRange(); };
            thresholdPanel.Controls.Add(numThresholdMin);
            trkMax = new TrackBar { Width = 150, Minimum = 0, Maximum = 255, TickFrequency = 32, Value = 255, Location = new Point(0, 40) };
            thresholdPanel.Controls.Add(trkMax);
            numThresholdMax = new NumericUpDown { Width = 80, Minimum = 0, Maximum = 255, Value = trkMax.Value, Location = new Point(155, 40) };
            numThresholdMax.ValueChanged += (s, e) => { trkMax.Value = (int)numThresholdMax.Value; UpdateSelectedMaterialRange(); };
            thresholdPanel.Controls.Add(numThresholdMax);
            leftPanel.Controls.Add(thresholdPanel);

            FlowLayoutPanel thresholdButtonsPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
            btnAddSelection = new Button { Text = "+", Width = 50, Height = 25 };

            btnAddSelection = new Button { Text = "+", Width = 50, Height = 25 };
            btnAddSelection.Click += (s, e) =>
            {
                int idx = lstMaterials.SelectedIndex;
                if (idx <= 0 || idx >= mainForm.Materials.Count)
                {
                    MessageBox.Show("Select a valid material (not the Exterior).");
                    return;
                }
                Material mat = mainForm.Materials[idx];

                // If the current tool is Brush, then apply the 2D (current slice) selection.
                if (mainForm.currentTool == SegmentationTool.Brush)
                {
                    mainForm.ApplyCurrentSelection();
                    mainForm.ApplyOrthoSelections(); // Also apply selections from orthoviews if needed.
                }
                // Otherwise, if an interpolated (full volume) mask is available, apply that.
                else if (mainForm.interpolatedMask != null)
                {
                    mainForm.ApplyInterpolatedSelection(mat.ID);
                }
                // Otherwise, fallback to the threshold-based selection.
                else
                {
                    mainForm.AddThresholdSelection(mat.Min, mat.Max, (byte)mat.ID);
                }
                mainForm.SaveLabelsChk();
            };
            btnSubSelection = new Button { Text = "-", Width = 50, Height = 25 };
            btnSubSelection.Click += (s, e) =>
            {
                int idx = lstMaterials.SelectedIndex;
                if (idx <= 0 || idx >= mainForm.Materials.Count)
                {
                    MessageBox.Show("Select a valid material (not the Exterior).");
                    return;
                }
                Material mat = mainForm.Materials[idx];

                if (mainForm.currentTool == SegmentationTool.Brush)
                {
                    mainForm.SubtractCurrentSelection();
                    mainForm.SubtractOrthoSelections();
                }
                else if (mainForm.interpolatedMask != null)
                {
                    mainForm.SubtractInterpolatedSelection(mat.ID);
                }
                else
                {
                    // Fallback: use your brush-based subtraction routines.
                    mainForm.SubtractCurrentSelection();
                    mainForm.SubtractOrthoSelections();
                }
                mainForm.SaveLabelsChk();
            };
            Button btnClearSelection = new Button { Text = "Clear", Width = 50, Height = 25 };
            btnClearSelection.Click += (s, e) =>
            {
                // Clear the 2D temporary selection.
                mainForm.currentSelection = new byte[mainForm.GetWidth(), mainForm.GetHeight()];
                // Optionally, clear the 3D interpolated mask too.
                mainForm.interpolatedMask = null;

                Logger.Log("[ClearSelection] Cleared current selection.");
                // Refresh views so that the cleared selection is no longer shown.
                mainForm.RenderViews();
                _ = mainForm.RenderOrthoViewsAsync();
            };
            Button btnApply = new Button { Text = "Apply", Width = 50, Height = 25 };
            btnApply.Click += (s, e) =>
            {
                // For brush tool, commit the current (2D) selection and the orthoview (XZ/YZ) selections.
                if (mainForm.currentTool == SegmentationTool.Brush)
                {
                    mainForm.ApplyCurrentSelection();
                    mainForm.ApplyOrthoSelections();
                }
                // You could later extend this for other tools if needed.
                // Refresh the views so the changes are visible.
                mainForm.RenderViews();
                _ = mainForm.RenderOrthoViewsAsync();
            };
            thresholdButtonsPanel.Controls.Add(btnAddSelection);
            thresholdButtonsPanel.Controls.Add(btnSubSelection);
            thresholdButtonsPanel.Controls.Add(btnClearSelection);
            thresholdButtonsPanel.Controls.Add(btnApply);
            leftPanel.Controls.Add(thresholdButtonsPanel);
            toolSizeLabel = new Label { Text = "Tool Size: 50px", AutoSize = true };
            leftPanel.Controls.Add(toolSizeLabel);
            toolSizeSlider = new TrackBar
            {
                Minimum = 1,
                Maximum = 1000,
                Value = 50,
                Width = 260,
                TickFrequency = 50,
                Enabled = false // initially disabled
            };
            toolSizeSlider.Scroll += ToolSizeSlider_Scroll;
            leftPanel.Controls.Add(toolSizeSlider);

            btnRefresh = new Button { Text = "Refresh Render", Width = 120 };
            btnRefresh.Click += (s, e) => mainForm.RenderViews();
            leftPanel.Controls.Add(btnRefresh);

            btnSegmentAnything = new Button { Text = "Segment Anything", Width = 120 };
            btnSegmentAnything.Click += (s, e) =>
            {
                mainForm.SetSegmentationTool(SegmentationTool.Point);
                Logger.Log("[ControlForm] Opening Segment Anything - ONNX Processor");
                SAMForm samForm = new SAMForm(mainForm, sharedAnnotationManager,mainForm.Materials);
                mainForm.SamFormInstance = samForm;
                panMenuItem.Enabled = false;
                eraserMenuItem.Enabled = false;
                brushMenuItem.Enabled = false;
                thresholdingMenuItem.Enabled = false;
                // When SAM closes, re-enable the buttons.
                samForm.Disposed += (sender, args) =>
                {
                    panMenuItem.Enabled = true;
                    eraserMenuItem.Enabled = true;
                    brushMenuItem.Enabled = true;
                    thresholdingMenuItem.Enabled = true;
                };
                samForm.Show();
            };
            leftPanel.Controls.Add(btnSegmentAnything);
            leftPanel.Controls.Add(btnInterpolate);
            table.Controls.Add(leftPanel, 0, 0);

            FlowLayoutPanel rightPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                AutoScroll = true,
                WrapContents = false,
                Padding = new Padding(30),
            };
            lblSlice = new Label { Text = "XY Slice: 0 / 0", AutoSize = true };
            rightPanel.Controls.Add(lblSlice);
            sliceSlider = new TrackBar { Width = 260, Minimum = 0, Maximum = 0, TickStyle = TickStyle.None };
            sliceSlider.Scroll += (s, e) =>
            {
                pendingXySliceValue = sliceSlider.Value;
                lblSlice.Text = $"XY Slice: {pendingXySliceValue} / {sliceSlider.Maximum}";
                numSlice.Value = pendingXySliceValue;
                if (xySliceUpdateTimer == null)
                {
                    xySliceUpdateTimer = new Timer { Interval = 50 };
                    xySliceUpdateTimer.Tick += (sender, args) =>
                    {
                        xySliceUpdateTimer.Stop();
                        mainForm.CurrentSlice = pendingXySliceValue;
                    };
                }
                else xySliceUpdateTimer.Stop();
                xySliceUpdateTimer.Start();
            };
            rightPanel.Controls.Add(sliceSlider);
            numSlice = new NumericUpDown { Width = 80, Minimum = 0, Maximum = 0, Value = 0 };
            numSlice.ValueChanged += (s, e) =>
            {
                sliceSlider.Value = (int)numSlice.Value;
                mainForm.CurrentSlice = sliceSlider.Value;
                lblSlice.Text = $"XY Slice: {sliceSlider.Value} / {sliceSlider.Maximum}";
            };
            rightPanel.Controls.Add(numSlice);
            lblXz = new Label { Text = "XZ Projection Row: 0 / 0", AutoSize = true };
            rightPanel.Controls.Add(lblXz);
            sliderXZ = new TrackBar { Width = 260, Minimum = 0, Maximum = mainForm.GetHeight() > 0 ? mainForm.GetHeight() - 1 : 0, TickStyle = TickStyle.None };
            sliderXZ.Scroll += (s, e) =>
            {
                mainForm.XzSliceY = sliderXZ.Value;
                lblXz.Text = $"XZ Projection Row: {sliderXZ.Value} / {sliderXZ.Maximum}";
                numXz.Value = sliderXZ.Value;
                _ = mainForm.RenderOrthoViewsAsync();
            };
            rightPanel.Controls.Add(sliderXZ);
            numXz = new NumericUpDown { Width = 80, Minimum = 0, Maximum = sliderXZ.Maximum, Value = sliderXZ.Maximum > 0 ? sliderXZ.Maximum / 2 : 0 };
            numXz.ValueChanged += (s, e) =>
            {
                sliderXZ.Value = (int)numXz.Value;
                mainForm.XzSliceY = sliderXZ.Value;
                lblXz.Text = $"XZ Projection Row: {sliderXZ.Value} / {sliderXZ.Maximum}";
                _ = mainForm.RenderOrthoViewsAsync();
            };
            rightPanel.Controls.Add(numXz);
            lblYz = new Label { Text = "YZ Projection Col: 0 / 0", AutoSize = true };
            rightPanel.Controls.Add(lblYz);
            sliderYZ = new TrackBar { Width = 260, Minimum = 0, Maximum = mainForm.GetWidth() > 0 ? mainForm.GetWidth() - 1 : 0, TickStyle = TickStyle.None };
            sliderYZ.Scroll += (s, e) =>
            {
                mainForm.YzSliceX = sliderYZ.Value;
                lblYz.Text = $"YZ Projection Col: {sliderYZ.Value} / {sliderYZ.Maximum}";
                numYz.Value = sliderYZ.Value;
                _ = mainForm.RenderOrthoViewsAsync();
            };
            rightPanel.Controls.Add(sliderYZ);
            numYz = new NumericUpDown { Width = 80, Minimum = 0, Maximum = sliderYZ.Maximum, Value = sliderYZ.Maximum > 0 ? sliderYZ.Maximum / 2 : 0 };
            numYz.ValueChanged += (s, e) =>
            {
                sliderYZ.Value = (int)numYz.Value;
                mainForm.YzSliceX = sliderYZ.Value;
                lblYz.Text = $"YZ Projection Col: {sliderYZ.Value} / {sliderYZ.Maximum}";
                _ = mainForm.RenderOrthoViewsAsync();
            };
            rightPanel.Controls.Add(numYz);
            histogramPictureBox = new PictureBox { Width = 260, Height = 100, BorderStyle = BorderStyle.FixedSingle, Visible = false };
            rightPanel.Controls.Add(histogramPictureBox);

            Button btnScreenshot = new Button
            {
                Text = "Take Screenshot",
                Width = 120,
                Margin = new Padding(0, 10, 0, 0)
            };
            btnScreenshot.Click += (s, e) => mainForm.SaveScreenshot();
            rightPanel.Controls.Add(btnScreenshot);


            table.Controls.Add(rightPanel, 1, 0);
            RefreshMaterialList();
            trkMin.Scroll += (s, e) =>
            {
                numThresholdMin.Value = trkMin.Value;
                UpdateSelectedMaterialRange();
                if (histogramPictureBox.Visible)
                    UpdateHistogram(histogramPictureBox);
            };
            trkMax.Scroll += (s, e) =>
            {
                numThresholdMax.Value = trkMax.Value;
                UpdateSelectedMaterialRange();
                if (histogramPictureBox.Visible)
                    UpdateHistogram(histogramPictureBox);
            };
            trkMin.Enabled = false;
            trkMax.Enabled = false;
            numThresholdMin.Enabled = false;
            numThresholdMax.Enabled = false;
            brushOverlayTimer = new Timer { Interval = 500 };
            brushOverlayTimer.Tick += (s, e) =>
            {
                mainForm.HideBrushOverlay();
                brushOverlayTimer.Stop();
            };
            this.ActiveControl = menuStrip;
        }
        private void ToolsMenuItem_Click(object sender, EventArgs e)
        {
            // Uncheck all tool menu items.
            panMenuItem.Checked = false;
            eraserMenuItem.Checked = false;
            brushMenuItem.Checked = false;
            thresholdingMenuItem.Checked = false;
            // Check the clicked item.
            var item = sender as ToolStripMenuItem;
            item.Checked = true;

            // Additional logic for clearing overlays on tool switch.
            if (item == brushMenuItem || item == eraserMenuItem || item == panMenuItem)
            {
                //enableThresholdMaskMenuItem.Checked = true;
                
                // When switching to brush, eraser, or pan, clear any threshold overlay.
                mainForm.PreviewMin = 0;
                mainForm.PreviewMax = 0;
                // Optionally disable the threshold overlay.
                mainForm.EnableThresholdMask = false;
                // Also clear any temporary brush selection if needed.
                mainForm.currentSelection = new byte[mainForm.GetWidth(), mainForm.GetHeight()];
                mainForm.RenderViews();
                _ = mainForm.RenderOrthoViewsAsync();
            }
            else if (item == thresholdingMenuItem)
            {
                // When switching to thresholding, clear any existing brush selection.
                mainForm.currentSelection = new byte[mainForm.GetWidth(), mainForm.GetHeight()];
                // Enable the threshold overlay.
                mainForm.EnableThresholdMask = true;
                mainForm.RenderViews();
                _ = mainForm.RenderOrthoViewsAsync();
            }

            // Set the tool and enable/disable UI controls accordingly.
            if (item == panMenuItem)
            {
                currentTool = SegmentationTool.Pan;
                toolSizeSlider.Enabled = false;
                trkMin.Enabled = false;
                trkMax.Enabled = false;
                numThresholdMin.Enabled = false;
                numThresholdMax.Enabled = false;
                btnInterpolate.Enabled = false;
            }
            else if (item == eraserMenuItem)
            {
                currentTool = SegmentationTool.Eraser;
                toolSizeSlider.Enabled = true;
                trkMin.Enabled = false;
                trkMax.Enabled = false;
                numThresholdMin.Enabled = false;
                numThresholdMax.Enabled = false;
                btnInterpolate.Enabled = true;
            }
            else if (item == brushMenuItem)
            {
                currentTool = SegmentationTool.Brush;
                toolSizeSlider.Enabled = true;
                trkMin.Enabled = false;
                trkMax.Enabled = false;
                numThresholdMin.Enabled = false;
                numThresholdMax.Enabled = false;
                btnInterpolate.Enabled = true;
            }
            else if (item == thresholdingMenuItem)
            {
                currentTool = SegmentationTool.Thresholding;
                toolSizeSlider.Enabled = false;
                // Enable threshold controls when in thresholding mode.
                trkMin.Enabled = true;
                trkMax.Enabled = true;
                numThresholdMin.Enabled = true;
                numThresholdMax.Enabled = true;
                btnInterpolate.Enabled = false;
            }
            // Inform MainForm of the current tool.
            mainForm.SetSegmentationTool(currentTool);
        }


        private void ToolSizeSlider_Scroll(object sender, EventArgs e)
        {
            int size = toolSizeSlider.Value;
            toolSizeLabel.Text = $"Tool Size: {size}px";
            // Simply show the overlay – MainForm will handle its own timer.
            mainForm.ShowBrushOverlay(size);
        }


        private void OnCloseDataset()
        {
            if (mainForm.volumeData != null)
            {
                mainForm.volumeData.Dispose();
                mainForm.volumeData = null;
                Logger.Log("[OnCloseDataset] Grayscale dataset closed.");
                MessageBox.Show("Dataset successfully closed.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
                MessageBox.Show("No dataset is currently loaded.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private async Task OnLoadFolderClicked()
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    await mainForm.LoadDatasetAsync(fbd.SelectedPath);
                    sliceSlider.Maximum = Math.Max(0, mainForm.GetDepth() - 1);
                    sliceSlider.Value = sliceSlider.Maximum / 2;
                    numSlice.Maximum = sliceSlider.Maximum;
                    numSlice.Value = sliceSlider.Value;
                    lblSlice.Text = $"XY Slice: {sliceSlider.Value} / {sliceSlider.Maximum}";
                    sliderXZ.Maximum = mainForm.GetHeight() > 0 ? mainForm.GetHeight() - 1 : 0;
                    numXz.Maximum = sliderXZ.Maximum;
                    numXz.Value = sliderXZ.Maximum > 0 ? sliderXZ.Maximum / 2 : 0;
                    lblXz.Text = $"XZ Projection Row: {sliderXZ.Value} / {sliderXZ.Maximum}";
                    sliderYZ.Maximum = mainForm.GetWidth() > 0 ? mainForm.GetWidth() - 1 : 0;
                    numYz.Maximum = sliderYZ.Maximum;
                    numYz.Value = sliderYZ.Maximum > 0 ? sliderYZ.Maximum / 2 : 0;
                    lblYz.Text = $"YZ Projection Col: {sliderYZ.Value} / {sliderYZ.Maximum}";
                    RefreshMaterialList();
                }
            }
        }

        private async Task OnImportClicked()
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = "Binary Volume|*.bin";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    await mainForm.LoadDatasetAsync(ofd.FileName);
                    sliceSlider.Maximum = Math.Max(0, mainForm.GetDepth() - 1);
                    sliceSlider.Value = 0;
                    numSlice.Maximum = sliceSlider.Maximum;
                    lblSlice.Text = $"XY Slice: 0 / {sliceSlider.Maximum}";
                    sliderXZ.Maximum = mainForm.GetHeight() > 0 ? mainForm.GetHeight() - 1 : 0;
                    numXz.Maximum = sliderXZ.Maximum;
                    numXz.Value = sliderXZ.Maximum > 0 ? sliderXZ.Maximum / 2 : 0;
                    lblXz.Text = $"XZ Projection Row: {sliderXZ.Value} / {sliderXZ.Maximum}";
                    sliderYZ.Maximum = mainForm.GetWidth() > 0 ? mainForm.GetWidth() - 1 : 0;
                    numYz.Maximum = sliderYZ.Maximum;
                    numYz.Value = sliderYZ.Maximum > 0 ? sliderYZ.Maximum / 2 : 0;
                    lblYz.Text = $"YZ Projection Col: {sliderYZ.Value} / {sliderYZ.Maximum}";
                    RefreshMaterialList();
                }
            }
        }

        private void OnSaveClicked()
        {
            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Filter = "Binary Volume|*.bin";
                if (sfd.ShowDialog() == DialogResult.OK)
                    mainForm.SaveBinary(sfd.FileName);
            }
        }

        private void LstMaterials_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= lstMaterials.Items.Count)
                return;
            e.DrawBackground();
            Material mat = mainForm.Materials[e.Index];
            if ((e.State & DrawItemState.Selected) == DrawItemState.Selected)
                e.Graphics.FillRectangle(Brushes.LightBlue, e.Bounds);
            else
            {
                using (SolidBrush b = new SolidBrush(mat.Color))
                    e.Graphics.FillRectangle(b, e.Bounds);
            }
            Color textColor = mat.IsExterior ? Color.Red : (mat.Color.GetBrightness() < 0.4f ? Color.White : Color.Black);
            using (SolidBrush textBrush = new SolidBrush(textColor))
                e.Graphics.DrawString(mat.Name, e.Font, textBrush, e.Bounds);
            e.DrawFocusRectangle();
        }

        private void LstMaterials_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                int index = lstMaterials.IndexFromPoint(e.Location);
                if (index < 0 || index >= mainForm.Materials.Count)
                    return;
                Material mat = mainForm.Materials[index];
                if (mat.IsExterior)
                {
                    MessageBox.Show("Cannot change color of the Exterior material.");
                    return;
                }
                using (ColorDialog dlg = new ColorDialog())
                {
                    dlg.Color = mat.Color;
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        mat.Color = dlg.Color;
                        RefreshMaterialList();
                        mainForm.RenderViews();
                    }
                }
            }
        }

        private void OnAddMaterial()
        {
            using (ColorDialog dlg = new ColorDialog())
            {
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    string name = Prompt.ShowDialog("Enter material name:", "New Material");
                    if (string.IsNullOrWhiteSpace(name))
                        name = "MaterialX";
                    byte newID = mainForm.GetNextMaterialID();
                    Material mat = new Material(name, dlg.Color, 0, 0, newID);
                    mainForm.Materials.Add(mat);
                    RefreshMaterialList();
                    lstMaterials.SelectedIndex = lstMaterials.Items.Count - 1;
                }
            }
            mainForm.SaveLabelsChk();
        }

        private void OnRemoveMaterial()
        {
            int idx = lstMaterials.SelectedIndex;
            // Do not allow deletion if index is 0 (Exterior) or invalid.
            if (idx <= 0 || idx >= mainForm.Materials.Count)
            {
                MessageBox.Show("Invalid selection. Cannot remove the Exterior material or invalid index.");
                return;
            }
            // Get the material using the list index, then remove it using its ID.
            Material mat = mainForm.Materials[idx];
            mainForm.RemoveMaterialAndReindex(mat.ID);
            RefreshMaterialList();
            mainForm.RenderViews();
            mainForm.SaveLabelsChk();
        }

        private void OnRenameMaterial()
        {
            int idx = lstMaterials.SelectedIndex;
            if (idx < 0 || idx >= mainForm.Materials.Count)
                return;
            if (idx == 0)
            {
                MessageBox.Show("Cannot rename the Exterior material.");
                return;
            }
            Material mat = mainForm.Materials[idx];
            string newName = Prompt.ShowDialog("Enter new material name:", "Rename Material");
            if (!string.IsNullOrWhiteSpace(newName))
            {
                mat.Name = newName;
                RefreshMaterialList();
            }
            mainForm.SaveLabelsChk();
        }

        private void UpdateThresholdSliders()
        {
            int idx = lstMaterials.SelectedIndex;
            if (idx < 0 || idx >= mainForm.Materials.Count)
                return;
            mainForm.SelectedMaterialIndex = idx;
            Material mat = mainForm.Materials[idx];
            trkMin.Value = mat.Min;
            numThresholdMin.Value = mat.Min;
            trkMax.Value = mat.Max;
            numThresholdMax.Value = mat.Max;
        }

        private void UpdateSelectedMaterialRange()
        {
            int idx = lstMaterials.SelectedIndex;
            if (idx < 0 || idx >= mainForm.Materials.Count)
                return;
            mainForm.SelectedMaterialIndex = idx;
            Material mat = mainForm.Materials[idx];
            if (mat.IsExterior)
                return;
            mat.Min = (byte)trkMin.Value;
            mat.Max = (byte)trkMax.Value;
            mainForm.PreviewMin = mat.Min;
            mainForm.PreviewMax = mat.Max;
            if (histogramPictureBox.Visible)
                UpdateHistogram(histogramPictureBox);
            mainForm.RenderViews();
            _ = mainForm.RenderOrthoViewsAsync();
            //mainForm.SaveLabelsChk();
        }

        private void AddThresholdedSelection()
        {
            if (mainForm.currentTool == SegmentationTool.Brush)
            {
                mainForm.ApplyCurrentSelection();     // Apply XY selection.
                mainForm.ApplyOrthoSelections();        // Apply selections from XZ and YZ views.
            }
            else
            {
                int idx = lstMaterials.SelectedIndex;
                if (idx <= 0 || idx >= mainForm.Materials.Count)
                {
                    MessageBox.Show("Select a valid material (not the Exterior).");
                    return;
                }
                Material mat = mainForm.Materials[idx];
                mainForm.AddThresholdSelection(mat.Min, mat.Max, (byte)mat.ID);
            }
            mainForm.RenderViews();
            _ = mainForm.RenderOrthoViewsAsync();
            mainForm.SaveLabelsChk();
        }

        private void SubThresholdedSelection()
        {
            if (mainForm.currentTool == SegmentationTool.Brush)
            {
                mainForm.SubtractCurrentSelection();
                mainForm.SubtractOrthoSelections();
            }
            else
            {
                int idx = lstMaterials.SelectedIndex;
                if (idx <= 0 || idx >= mainForm.Materials.Count)
                {
                    MessageBox.Show("Select a valid material (not the Exterior).");
                    return;
                }
                Material mat = mainForm.Materials[idx];
                mainForm.RemoveThresholdSelection(mat.Min, mat.Max, (byte)mat.ID);
            }
            mainForm.RenderViews();
            _ = mainForm.RenderOrthoViewsAsync();
            mainForm.SaveLabelsChk();
        }

        private void UpdateHistogram(PictureBox histBox)
        {
            if (isUpdatingHistogram)
                return;
            isUpdatingHistogram = true;

            try
            {
                if (mainForm.volumeData == null || histBox.ClientSize.Width <= 0 || histBox.ClientSize.Height <= 0)
                {
                    histBox.Image?.Dispose();
                    histBox.Image = null;
                    return;
                }

                int w = mainForm.GetWidth();
                int h = mainForm.GetHeight();
                int slice = mainForm.CurrentSlice;

                if (w <= 0 || h <= 0)
                {
                    histBox.Image?.Dispose();
                    histBox.Image = null;
                    return;
                }

                byte[] graySlice = new byte[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        graySlice[y * w + x] = mainForm.volumeData[x, y, slice];

                int[] hist = new int[256];
                foreach (byte b in graySlice)
                    hist[b]++;

                int maxCount = hist.Max();
                int histWidth = 256, histHeight = 100;

                using (Bitmap bmp = new Bitmap(histWidth, histHeight + 15))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                        g.Clear(Color.Black);

                        for (int i = 0; i < 256; i++)
                        {
                            float binHeight = (maxCount > 0) ? (hist[i] / (float)maxCount) * histHeight : 0;
                            g.DrawLine(Pens.White, i, histHeight, i, histHeight - binHeight);
                        }

                        int minThreshold = (numThresholdMin != null) ? Math.Max(0, Math.Min(255, (int)numThresholdMin.Value)) : 0;
                        int maxThreshold = (numThresholdMax != null) ? Math.Max(0, Math.Min(255, (int)numThresholdMax.Value)) : 255;

                        using (Pen redPen = new Pen(Color.Red, 2))
                        using (Pen bluePen = new Pen(Color.Blue, 2))
                        {
                            g.DrawLine(redPen, minThreshold, 0, minThreshold, histHeight);
                            g.DrawLine(bluePen, maxThreshold, 0, maxThreshold, histHeight);
                        }

                        // Define fixed rectangles for drawing text labels.
                        // The left rectangle is at (0, histHeight) with width 50 and height 15.
                        // The right rectangle is at (histWidth - 50, histHeight) with width 50 and height 15.
                        RectangleF rectLeft = new RectangleF(0, histHeight, 50, 15);
                        RectangleF rectRight = new RectangleF(histWidth - 50, histHeight, 50, 15);

                       
                    }

                    histBox.Image?.Dispose();
                    histBox.Image = (Bitmap)bmp.Clone();
                }
            }
            catch (Exception ex)
            {
                Logger.Log("[UpdateHistogram] Exception: " + ex.Message);
            }
            finally
            {
                isUpdatingHistogram = false;
            }
        }







        public void RefreshMaterialList()
        {
            lstMaterials.Items.Clear();
            for (int i = 0; i < mainForm.Materials.Count; i++)
            {
                Material m = mainForm.Materials[i];
                lstMaterials.Items.Add($"{i}: {m.Name} [Range {m.Min}..{m.Max}]");
            }
            if (lstMaterials.Items.Count > 1)
                lstMaterials.SelectedIndex = 1;
            else if (lstMaterials.Items.Count > 0)
                lstMaterials.SelectedIndex = 0;
        }
    }
}
