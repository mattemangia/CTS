using CTS.SharpDXIntegration;
using Krypton.Toolkit;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS
{
    // ------------------------------------------------------------------------
    // ControlForm – provides UI controls for loading, segmentation, materials etc.
    // ------------------------------------------------------------------------

    public partial class ControlForm : KryptonPanel
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
        private Button btnExtractFromMaterial;
        private TrackBar toolSizeSlider;
        private Label toolSizeLabel;
        private Timer brushOverlayTimer;

        private MainForm mainForm;
        private ListBox lstMaterials;
        private Button btnAddMaterial;
        private Button btnRemoveMaterial;
        private Button btnRenameMaterial;
        private Label lblThreshold;
        private RangeSlider thresholdRangeSlider;
        private NumericUpDown numThresholdMin, numThresholdMax;
        private Button btnAddSelection;
        private Button btnSubSelection;
        //private Button btnSegmentAnything;

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
        private ToolStripMenuItem mergeMaterialMenuItem;
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

        //private ToolStripMenuItem showOrthoviewsMenuItem;
        private ToolStripMenuItem resetZoomMenuItem;

        private ToolStripMenuItem helpMenu;
        private ToolStripMenuItem dbgConsole;
        private ToolStripMenuItem about;
        private bool thresholdMaskEnabled = true;
        private bool isUpdatingHistogram = false;
        private ToolStripMenuItem simulationMenu;
        private ToolStripMenuItem stressAnalysisMenuItem;

        //Annotations for SAM2
        private AnnotationManager sharedAnnotationManager = new AnnotationManager();

        public ControlForm(MainForm form)
        {
            mainForm = form;
            mainForm.FormClosed += (s, e) =>
            {
                // Immediately kill the process to prevent hanging
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            };
            mainForm.AnnotationMgr = sharedAnnotationManager;
            InitializeComponent();
            PaletteMode = PaletteMode.Office2010Black;
            MakeEverythingDark(this);
        }

        private void MakeEverythingDark(Control root)
        {
            root.BackColor = Color.FromArgb(45, 45, 48);
            root.ForeColor = Color.Gainsboro;

            foreach (Control c in root.Controls) MakeEverythingDark(c);
        }

        /*protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Logger.ShuttingDown = true;
            if (Logger.LogWindowInstance != null && !Logger.LogWindowInstance.IsDisposed)
            {
                Logger.LogWindowInstance.Invoke(new Action(() => Logger.LogWindowInstance.Close()));
            }
            System.Diagnostics.Process.GetCurrentProcess().Kill();
            Application.Exit();
        }*/

        private void InitializeSliceControls()
        {
            // Calculate the maximum slice index
            int maxSlice = Math.Max(0, mainForm.GetDepth() - 1);

            // Set slider maximum
            sliceSlider.Maximum = maxSlice;
            numSlice.Maximum = maxSlice;

            // Set slider value to the middle of the dataset
            int middleSlice = maxSlice / 2;
            sliceSlider.Value = middleSlice;
            numSlice.Value = middleSlice;

            // Update the label text
            lblSlice.Text = $"XY Slice: {middleSlice} / {maxSlice}";

            // Important: Set the current slice in MainForm to the middle slice
            mainForm.CurrentSlice = middleSlice;

            // Update controls for XZ and YZ views too
            sliderXZ.Maximum = mainForm.GetHeight() > 0 ? mainForm.GetHeight() - 1 : 0;
            numXz.Maximum = sliderXZ.Maximum;
            int middleXZ = sliderXZ.Maximum / 2;
            sliderXZ.Value = middleXZ;
            numXz.Value = middleXZ;
            lblXz.Text = $"XZ Projection Row: {middleXZ} / {sliderXZ.Maximum}";
            mainForm.XzSliceY = middleXZ;

            sliderYZ.Maximum = mainForm.GetWidth() > 0 ? mainForm.GetWidth() - 1 : 0;
            numYz.Maximum = sliderYZ.Maximum;
            int middleYZ = sliderYZ.Maximum / 2;
            sliderYZ.Value = middleYZ;
            numYz.Value = middleYZ;
            lblYz.Text = $"YZ Projection Col: {middleYZ} / {sliderYZ.Maximum}";
            mainForm.YzSliceX = middleYZ;
        }

        private void InitializeComponent()
        {
            // ==== Form Setup ====
            //this.FormBorderStyle = FormBorderStyle.None;
          
            //this.CloseBox = false;
            //this.TopMost = false;
            this.Text = "Controls";
            this.Size = new Size(700, 645);

            // Try to load the icon if it exists
            /*try
            {
                string iconPath = Path.Combine(Application.StartupPath, "favicon.ico");
                if (File.Exists(iconPath))
                    this.Icon = new Icon(iconPath);
            }
            catch { }*/

            // ==== Menu Setup ====
            InitializeMenus();

            // ==== Main Layout ====
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

            // ==== Left Panel (Tools & Materials) ====
            FlowLayoutPanel leftPanel = CreateLeftPanel();
            table.Controls.Add(leftPanel, 0, 0);

            // ==== Right Panel (Slices & Views) ====
            FlowLayoutPanel rightPanel = CreateRightPanel();
            table.Controls.Add(rightPanel, 1, 0);

            // ==== Final Setup ====
            RefreshMaterialList();

            // Initialize the threshold slider as disabled
            thresholdRangeSlider.Enabled = false;
            numThresholdMin.Enabled = false;
            numThresholdMax.Enabled = false;

            // Setup brush overlay timer
            brushOverlayTimer = new Timer { Interval = 500 };
            brushOverlayTimer.Tick += (s, e) =>
            {
                mainForm.HideBrushOverlay();
                brushOverlayTimer.Stop();
            };

            //this.ActiveControl = menuStrip;
        }
        private void InitializeMenus()
        {
            // Create the main menu strip
            menuStrip = new MenuStrip();
            menuStrip.Dock = DockStyle.Top;
            this.Controls.Add(menuStrip);
            //this.MainMenuStrip = menuStrip;

            // ==== File Menu ====
            CreateFileMenu();
            menuStrip.Items.Add(fileMenu);

            // ==== Edit Menu ====
            CreateEditMenu();
            menuStrip.Items.Add(editMenu);

            // ==== View Menu ====
            CreateViewMenu();
            menuStrip.Items.Add(viewMenu);

            // ==== Tools Menu ====
            CreateToolsMenu();

            // ==== Simulation Menu ====
            CreateSimulationMenu();
            menuStrip.Items.Add(simulationMenu);

            // ==== Help Menu ====
            CreateHelpMenu();
            menuStrip.Items.Add(helpMenu);

            // Insert Tools menu before Help
            menuStrip.Items.Insert(menuStrip.Items.IndexOf(helpMenu), toolsMenu);
        }
        private void CreateFileMenu()
        {
            fileMenu = new ToolStripMenuItem("File");

            // If New then: APP RESTART
            var newMenuItem = new ToolStripMenuItem("New");
            newMenuItem.Click += (s, e) =>
            {
                var result = MessageBox.Show(
                    "Are you sure you want to restart the application?",
                    "Confirm Restart",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    // this will close and launch a fresh instance
                    Application.Restart();
                    mainForm.Close();
                }
            };
            fileMenu.DropDownItems.Add(newMenuItem);

            // Load Folder
            loadFolderMenuItem = new ToolStripMenuItem("Load Folder");
            loadFolderMenuItem.Click += async (s, e) => await OnLoadFolderClicked();

            // Import .bin
            importBinMenuItem = new ToolStripMenuItem("Import .bin");
            importBinMenuItem.Click += async (s, e) => await OnImportClicked();

            // Separator
            fileSep1 = new ToolStripSeparator();

            // Save .bin
            saveBinMenuItem = new ToolStripMenuItem("Save .bin");
            saveBinMenuItem.Click += (s, e) => OnSaveClicked();

            // Export Images
            exportImagesMenuItem = new ToolStripMenuItem("Export Images");
            exportImagesMenuItem.Click += (s, e) => mainForm.ExportImages();

            // Close Dataset
            closeDatasetMenuItem = new ToolStripMenuItem("Close Greyscale Dataset");
            closeDatasetMenuItem.Click += (s, e) => OnCloseDataset();

            // Exit
            exitMenuItem = new ToolStripMenuItem("Exit");
            exitMenuItem.Click += (s, e) =>
            {
                mainForm.Close();
                // Force termination even if windows remain open.
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            };

            // Add items to File menu
            fileMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
        loadFolderMenuItem, importBinMenuItem, fileSep1, saveBinMenuItem,
        exportImagesMenuItem, closeDatasetMenuItem, exitMenuItem
            });
        }

        private void CreateEditMenu()
        {
            editMenu = new ToolStripMenuItem("Edit");

            // Material management
            addMaterialMenuItem = new ToolStripMenuItem("Add Material");
            addMaterialMenuItem.Click += (s, e) => OnAddMaterial();

            deleteMaterialMenuItem = new ToolStripMenuItem("Delete Material");
            deleteMaterialMenuItem.Click += (s, e) => OnRemoveMaterial();

            renameMaterialMenuItem = new ToolStripMenuItem("Rename Material");
            renameMaterialMenuItem.Click += (s, e) => OnRenameMaterial();

            mergeMaterialMenuItem = new ToolStripMenuItem("Merge Material");
            mergeMaterialMenuItem.Click += (s, e) => OnMergeMaterial();

            ToolStripMenuItem extractFromMaterialMenuItem = new ToolStripMenuItem("Extract from Material");
            extractFromMaterialMenuItem.Click += async (s, e) => await OnExtractFromMaterialAsync();

            // Separator
            editSep1 = new ToolStripSeparator();

            // Threshold operations
            addThresholdedMenuItem = new ToolStripMenuItem("Add Thresholded");
            addThresholdedMenuItem.Click += (s, e) => AddThresholdedSelection();

            subtractThresholdedMenuItem = new ToolStripMenuItem("Subtract Thresholded");
            subtractThresholdedMenuItem.Click += (s, e) => SubThresholdedSelection();

            // Separator
            editSep2 = new ToolStripSeparator();

            // Segment Anything
            segmentAnythingMenuItem = new ToolStripMenuItem("Segment Anything");
            segmentAnythingMenuItem.Click += (s, e) =>
            {
                if (mainForm.volumeData == null)
                {
                    MessageBox.Show("Please load a dataset first.", "No Data",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Get the selected material
                Material selectedMaterial;
                if (lstMaterials.SelectedIndex > 0 && lstMaterials.SelectedIndex < mainForm.Materials.Count)
                {
                    selectedMaterial = mainForm.Materials[lstMaterials.SelectedIndex];
                }
                else
                {
                    // Default to the first non-exterior material
                    selectedMaterial = mainForm.Materials.Count > 1 ? mainForm.Materials[1] : mainForm.Materials[0];
                }

                Logger.Log("[ControlForm] Opening Segment Anything CT tool");
                SegmentAnythingCT segmentAnything = new SegmentAnythingCT(
                    mainForm,
                    selectedMaterial,
                    sharedAnnotationManager);

                segmentAnything.Show();
            };

            // Add items to Edit menu
            editMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
                addMaterialMenuItem, deleteMaterialMenuItem, renameMaterialMenuItem,
                mergeMaterialMenuItem, extractFromMaterialMenuItem, editSep1,
                addThresholdedMenuItem, subtractThresholdedMenuItem, editSep2,
                segmentAnythingMenuItem
            });
        }

        private void CreateViewMenu()
        {
            viewMenu = new ToolStripMenuItem("View");

            // Show Mask
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

            // Render Materials
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

            // Show Histogram
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

            // 3D Volume View
            ToolStripMenuItem view3DMenuItem = new ToolStripMenuItem("3D Volume View");
            view3DMenuItem.Click += (s, e) =>
            {
                if (mainForm.volumeData == null && mainForm.volumeLabels == null)
                {
                    MessageBox.Show("No volume data loaded. Please load a dataset first.",
                                  "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // Create and show the 3D viewer form
                SharpDXViewerForm viewer3DForm = new SharpDXViewerForm(mainForm);
                viewer3DForm.Show();
            };

            // Reset Zoom
            resetZoomMenuItem = new ToolStripMenuItem("Reset Zoom");
            resetZoomMenuItem.Click += (s, e) => mainForm.ResetView();

            // Add items to View menu
            viewMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
        showMaskMenuItem, enableThresholdMaskMenuItem, showHistogramMenuItem,
        view3DMenuItem, resetZoomMenuItem
            });
        }
        private void CreateToolsMenu()
        {
            // Tools menu
            toolsMenu = new ToolStripMenuItem("Tools");

            // Basic tools
            panMenuItem = new ToolStripMenuItem("Pan") { CheckOnClick = true, Checked = true };
            eraserMenuItem = new ToolStripMenuItem("Eraser") { CheckOnClick = true };
            brushMenuItem = new ToolStripMenuItem("Brush") { CheckOnClick = true };
            thresholdingMenuItem = new ToolStripMenuItem("Thresholding") { CheckOnClick = true };

            // Attach a common click handler
            panMenuItem.Click += ToolsMenuItem_Click;
            eraserMenuItem.Click += ToolsMenuItem_Click;
            brushMenuItem.Click += ToolsMenuItem_Click;
            thresholdingMenuItem.Click += ToolsMenuItem_Click;

            // Add basic tools to menu
            toolsMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
        panMenuItem, eraserMenuItem, brushMenuItem, thresholdingMenuItem
            });

            // Add statistics submenu
            AddStatisticsMenu();

            // Separator
            ToolStripSeparator toolsSeparator = new ToolStripSeparator();
            toolsMenu.DropDownItems.Add(toolsSeparator);
            ToolStripMenuItem nodeEditorMenuItem = new ToolStripMenuItem("Node Editor");
            nodeEditorMenuItem.Click += (s, e) =>
            {
                if (mainForm != null)
                {
                    mainForm.OpenNodeEditor();
                }
            };
            toolsMenu.DropDownItems.Add(nodeEditorMenuItem);
            ToolStripSeparator toolsSeparator2 = new ToolStripSeparator();
            toolsMenu.DropDownItems.Add(toolsSeparator2);

            // Brightness/Contrast
            AddBrightnessContrastMenu();

            // AI submenu
            ToolStripMenuItem aiSubmenu = new ToolStripMenuItem("Artificial Intelligence");

            // Segment Anything
            ToolStripMenuItem segmentAnythingToolMenuItem = new ToolStripMenuItem("Segment Anything");
            segmentAnythingToolMenuItem.Click += (s, e) =>
            {
                if (mainForm.volumeData == null)
                {
                    MessageBox.Show("Please load a dataset first.", "No Data",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Get the selected material
                Material selectedMaterial;
                if (lstMaterials.SelectedIndex > 0 && lstMaterials.SelectedIndex < mainForm.Materials.Count)
                {
                    selectedMaterial = mainForm.Materials[lstMaterials.SelectedIndex];
                }
                else
                {
                    // Default to the first non-exterior material
                    selectedMaterial = mainForm.Materials.Count > 1 ? mainForm.Materials[1] : mainForm.Materials[0];
                }

                Logger.Log("[ControlForm] Opening Segment Anything CT tool from Tools menu");
                SegmentAnythingCT segmentAnything = new SegmentAnythingCT(
                    mainForm,
                    selectedMaterial,
                    sharedAnnotationManager);

                segmentAnything.Show();
            };

            // MicroSAM
            ToolStripMenuItem microSamToolMenuItem = new ToolStripMenuItem("MicroSAM");
            microSamToolMenuItem.Click += (s, e) =>
            {
                if (mainForm.volumeData == null)
                {
                    MessageBox.Show("Please load a dataset first.", "No Data",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Get the selected material
                Material selectedMaterial;
                if (lstMaterials.SelectedIndex > 0 && lstMaterials.SelectedIndex < mainForm.Materials.Count)
                {
                    selectedMaterial = mainForm.Materials[lstMaterials.SelectedIndex];
                }
                else
                {
                    // Default to the first non-exterior material
                    selectedMaterial = mainForm.Materials.Count > 1 ? mainForm.Materials[1] : mainForm.Materials[0];
                }

                Logger.Log("[ControlForm] Opening MicroSAM tool");
                CTS.Modules.ArtificialIntelligence.MicroSAM.MicroSAM microSam =
                    new CTS.Modules.ArtificialIntelligence.MicroSAM.MicroSAM(
                        mainForm,
                        selectedMaterial,
                        sharedAnnotationManager);

                microSam.Show();
            };

            // Grounding DINO
            ToolStripMenuItem groundingDinoMenuItem = new ToolStripMenuItem("Grounding DINO");
            groundingDinoMenuItem.Click += (s, e) =>
            {
                if (mainForm.volumeData == null)
                {
                    MessageBox.Show("Please load a dataset first.", "No Data",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Get the selected material
                Material selectedMaterial;
                if (lstMaterials.SelectedIndex > 0 && lstMaterials.SelectedIndex < mainForm.Materials.Count)
                {
                    selectedMaterial = mainForm.Materials[lstMaterials.SelectedIndex];
                }
                else
                {
                    // Default to the first non-exterior material
                    selectedMaterial = mainForm.Materials.Count > 1 ? mainForm.Materials[1] : mainForm.Materials[0];
                }

                Logger.Log("[ControlForm] Opening Grounding DINO detector");
                CTS.Modules.ArtificialIntelligence.GroundingDINO.GroundingDINODetector groundingDino =
                    new CTS.Modules.ArtificialIntelligence.GroundingDINO.GroundingDINODetector(
                        mainForm,
                        selectedMaterial);

                groundingDino.Show();
            };

            // Texture Classifier
            ToolStripMenuItem textureClassifierMenuItem = new ToolStripMenuItem("Texture Classifier");
            textureClassifierMenuItem.Click += (s, e) => OpenTextureClassifier();

            // Add items to AI submenu
            aiSubmenu.DropDownItems.Add(microSamToolMenuItem);
            aiSubmenu.DropDownItems.Add(groundingDinoMenuItem);
            aiSubmenu.DropDownItems.Add(segmentAnythingToolMenuItem);
            aiSubmenu.DropDownItems.Add(textureClassifierMenuItem);

            // Integrate/Resample
            ToolStripMenuItem integrateResampleMenuItem = new ToolStripMenuItem("Integrate / Resample");
            integrateResampleMenuItem.Click += (s, e) =>
            {
                if (mainForm.volumeData == null)
                {
                    MessageBox.Show("Please load a dataset first.", "No Data",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                IntegrateResampleForm integrateForm = new IntegrateResampleForm(mainForm);
                integrateForm.Show();
            };

            // Band Detection
            ToolStripMenuItem bandDetectionMenuItem = new ToolStripMenuItem("Band Detection");
            bandDetectionMenuItem.Click += (s, e) =>
            {
                if (mainForm.volumeData == null)
                {
                    MessageBox.Show("Please load a dataset first.", "No Data",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                Logger.Log("[ControlForm] Opening Band Detection tool");
                BandDetectionForm bandDetectionForm = new BandDetectionForm(mainForm);
                bandDetectionForm.Show();
            };

            // Transform Dataset
            ToolStripMenuItem transformDatasetMenuItem = new ToolStripMenuItem("Transform Dataset");
            transformDatasetMenuItem.Click += (s, e) =>
            {
                if (mainForm.volumeData == null)
                {
                    MessageBox.Show("Please load a dataset first.", "No Data",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                try
                {
                    using (TransformDatasetForm transformForm = new TransformDatasetForm(mainForm))
                    {
                        transformForm.ShowDialog();
                    }

                    Logger.Log("[ControlForm] Transform Dataset dialog closed");
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ControlForm] Error opening Transform Dataset form: {ex.Message}");
                    MessageBox.Show($"Error opening Transform Dataset form: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            ToolStripMenuItem coreExtractionMenuItem = new ToolStripMenuItem("Core Extraction");
            coreExtractionMenuItem.Click += (s, e) =>
            {
                if (mainForm.volumeData == null)
                {
                    MessageBox.Show("Please load a dataset first.", "No Data",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                try
                {
                    // Create and show the core extraction form
                    CoreExtractionForm coreExtractionForm = new CoreExtractionForm(mainForm);
                    coreExtractionForm.Show();
                    Logger.Log("[ControlForm] Opened Core Extraction form");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error opening Core Extraction form: {ex.Message}",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Logger.Log($"[ControlForm] Error opening Core Extraction form: {ex.Message}");
                }
            };
            toolsMenu.DropDownItems.Add(coreExtractionMenuItem);
            // Filter Manager
            ToolStripMenuItem filterManagerMenuItem = new ToolStripMenuItem("Filter Manager");
            filterManagerMenuItem.Click += (s, e) =>
            {
                if (mainForm.volumeData == null)
                {
                    MessageBox.Show("Please load a dataset first.", "No Data",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Create and show the FilterManager form
                FilterManager filterManager = new FilterManager(mainForm);
                filterManager.Show();
            };

            // Add remaining items to Tools menu
            toolsMenu.DropDownItems.Add(integrateResampleMenuItem);
            toolsMenu.DropDownItems.Add(bandDetectionMenuItem);
            toolsMenu.DropDownItems.Add(transformDatasetMenuItem);
            toolsMenu.DropDownItems.Add(filterManagerMenuItem);
            toolsMenu.DropDownItems.Add(aiSubmenu);

            // Add Label Operations submenu
            AddLabelOperationsMenu();
        }
        private void CreateHelpMenu()
        {
            helpMenu = new ToolStripMenuItem("Help");

            // Log Window
            dbgConsole = new ToolStripMenuItem("Log Window");
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

            // Check For Updates
            ToolStripMenuItem checkUpdatesMenuItem = new ToolStripMenuItem("Check For Updates");
            checkUpdatesMenuItem.Click += (s, e) =>
            {
                try
                {
                    // Show the update progress form
                    Modules.AutoUpdater.UpdateProgressForm updateForm = new Modules.AutoUpdater.UpdateProgressForm();
                    updateForm.ShowDialog();
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ControlForm] Error checking for updates: {ex.Message}");
                    MessageBox.Show($"Error checking for updates: {ex.Message}",
                        "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            // Report Bug
            ToolStripMenuItem reportBugMenuItem = new ToolStripMenuItem("Report Bug");
            reportBugMenuItem.Click += (s, e) =>
            {
                try
                {
                    // Show the bug submission form
                    Modules.BugSubmission.BugSubmissionForm.ShowBugReportDialog();
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ControlForm] Error opening bug report form: {ex.Message}");
                    MessageBox.Show($"Error opening bug report form: {ex.Message}",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            // About
            about = new ToolStripMenuItem("About");

            // Add this click handler right after it:
            about.Click += (s, e) =>
            {
                try
                {
                    // Create and show the About form
                    About aboutForm = new About();
                    aboutForm.ShowDialog(this);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ControlForm] Error opening About form: {ex.Message}");
                    MessageBox.Show($"Error opening About form: {ex.Message}",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            // Add items to Help menu
            helpMenu.DropDownItems.AddRange(new ToolStripItem[] {
        dbgConsole,
        checkUpdatesMenuItem,
        reportBugMenuItem,
        about
    });
        }
        private void CreateSimulationMenu()
        {
            // Create the Simulation menu
            simulationMenu = new ToolStripMenuItem("Simulation");

            // Create Pore Network Modeling menu item
            ToolStripMenuItem poreNetworkMenuItem = new ToolStripMenuItem("Pore Network Modeling");
            poreNetworkMenuItem.Click += (s, e) =>
            {
                if (mainForm.volumeData == null || mainForm.volumeLabels == null)
                {
                    // Instead of showing an error, ask if the user wants to load a saved model
                    DialogResult result = MessageBox.Show(
                        "No dataset is currently loaded. Would you like to load a saved pore network model file?",
                        "Load Pore Network Model",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);

                    if (result == DialogResult.Yes)
                    {
                        // Show file dialog to select a .dat file
                        using (OpenFileDialog openDialog = new OpenFileDialog())
                        {
                            openDialog.Filter = "Pore Network Model|*.dat";
                            openDialog.Title = "Load Pore Network Model";

                            if (openDialog.ShowDialog() == DialogResult.OK)
                            {
                                try
                                {
                                    // Create and show the pore network modeling form with the selected file
                                    PoreNetworkModelingForm poreNetworkForm = new PoreNetworkModelingForm(mainForm, openDialog.FileName);
                                    poreNetworkForm.Show();
                                }
                                catch (Exception ex)
                                {
                                    MessageBox.Show($"Error loading pore network model: {ex.Message}",
                                        "Loading Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                    Logger.Log($"[ControlForm] Error loading pore network model: {ex.Message}");
                                }
                            }
                        }
                    }
                }
                else
                {
                    // Create and show the pore network modeling form with dataset as before
                    PoreNetworkModelingForm poreNetworkForm = new PoreNetworkModelingForm(mainForm);
                    poreNetworkForm.Show();
                }
            };

            // Acoustic Simulation
            ToolStripMenuItem acousticSimulationMenuItem = new ToolStripMenuItem("Acoustic Simulation");
            acousticSimulationMenuItem.Click += (s, e) =>
            {
                try
                {
                    // Create and show the acoustic simulation form
                    AcousticSimulationForm acousticSimulationForm = new AcousticSimulationForm(mainForm);
                    acousticSimulationForm.Show();
                    Logger.Log("[ControlForm] Opened Acoustic Simulation form");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error opening Acoustic Simulation form: {ex.Message}",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Logger.Log($"[ControlForm] Error opening Acoustic Simulation form: {ex.Message}");
                }
            };

            // Stress Analysis
            ToolStripMenuItem stressAnalysisMenuItem = new ToolStripMenuItem("Stress Analysis");
            stressAnalysisMenuItem.Click += (s, e) =>
            {
                if (mainForm.volumeData == null || mainForm.volumeLabels == null)
                {
                    MessageBox.Show("Please load a dataset first to perform stress analysis.",
                        "No Dataset", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                try
                {
                    // Create and show the stress analysis form
                    StressAnalysisForm stressAnalysisForm = new StressAnalysisForm(mainForm);
                    stressAnalysisForm.Show();
                    Logger.Log("[ControlForm] Opened Stress Analysis form");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error opening Stress Analysis form: {ex.Message}",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Logger.Log($"[ControlForm] Error opening Stress Analysis form: {ex.Message}");
                }
            };

            // Triaxial Simulation
            ToolStripMenuItem triaxialSimulationMenuItem = new ToolStripMenuItem("Triaxial Simulation");
            triaxialSimulationMenuItem.Click += (s, e) =>
            {
                if (mainForm.volumeData == null || mainForm.volumeLabels == null)
                {
                    MessageBox.Show("Please load a dataset first to perform triaxial simulation.",
                        "No Dataset", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                try
                {
                    // Create and show the triaxial simulation form
                    TriaxialSimulationForm triaxialSimulationForm =
                        new TriaxialSimulationForm(mainForm);
                    triaxialSimulationForm.Show();
                    Logger.Log("[ControlForm] Opened Triaxial Simulation form");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error opening Triaxial Simulation form: {ex.Message}",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Logger.Log($"[ControlForm] Error opening Triaxial Simulation form: {ex.Message}");
                }
            };

            // Add items to Simulation menu
            simulationMenu.DropDownItems.Add(poreNetworkMenuItem);
            simulationMenu.DropDownItems.Add(acousticSimulationMenuItem);
            simulationMenu.DropDownItems.Add(stressAnalysisMenuItem);
            simulationMenu.DropDownItems.Add(triaxialSimulationMenuItem);
        }
        private FlowLayoutPanel CreateLeftPanel()
        {
            FlowLayoutPanel leftPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                AutoScroll = true,
                WrapContents = false,
                Padding = new Padding(30),
            };

            // Load Full checkbox
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

            // Material buttons panel
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

            // Add the Extract from material button
            btnExtractFromMaterial = new Button { Text = "Extract", Width = 70, Height = 25 };
            btnExtractFromMaterial.Click += async (s, e) => await OnExtractFromMaterialAsync();

            materialPanel.Controls.Add(btnAddMaterial);
            materialPanel.Controls.Add(btnRemoveMaterial);
            materialPanel.Controls.Add(btnRenameMaterial);
            materialPanel.Controls.Add(btnExtractFromMaterial);
            leftPanel.Controls.Add(materialPanel);


            // Materials list
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

            // Threshold controls
            lblThreshold = new Label { Text = "Threshold [min..max]", AutoSize = true };
            leftPanel.Controls.Add(lblThreshold);

            Panel thresholdPanel = new Panel { Width = 260, Height = 100 };

            thresholdRangeSlider = new RangeSlider
            {
                Width = 260,
                Height = 40,
                Minimum = 0,
                Maximum = 255,
                RangeMinimum = 1,
                RangeMaximum = 255,
                Location = new Point(0, 0)
            };

            thresholdRangeSlider.RangeChanged += (s, e) =>
            {
                numThresholdMin.Value = thresholdRangeSlider.RangeMinimum;
                numThresholdMax.Value = thresholdRangeSlider.RangeMaximum;
                UpdateSelectedMaterialRange();
                mainForm.OnThresholdRangeChanged((byte)thresholdRangeSlider.RangeMinimum, (byte)thresholdRangeSlider.RangeMaximum);
                mainForm.RenderViews();
                _ = mainForm.RenderOrthoViewsAsync();
            };
            thresholdPanel.Controls.Add(thresholdRangeSlider);

            numThresholdMin = new NumericUpDown
            {
                Width = 80,
                Minimum = 0,
                Maximum = 255,
                Value = thresholdRangeSlider.RangeMinimum,
                Location = new Point(0, 50)
            };
            numThresholdMin.ValueChanged += (s, e) =>
            {
                if ((int)numThresholdMin.Value != thresholdRangeSlider.RangeMinimum)
                {
                    thresholdRangeSlider.RangeMinimum = (int)numThresholdMin.Value;
                    UpdateSelectedMaterialRange();
                    mainForm.OnThresholdRangeChanged((byte)thresholdRangeSlider.RangeMinimum, (byte)thresholdRangeSlider.RangeMaximum);
                    mainForm.RenderViews();
                    _ = mainForm.RenderOrthoViewsAsync();
                }
            };
            thresholdPanel.Controls.Add(numThresholdMin);

            numThresholdMax = new NumericUpDown
            {
                Width = 80,
                Minimum = 0,
                Maximum = 255,
                Value = thresholdRangeSlider.RangeMaximum,
                Location = new Point(180, 50)
            };
            numThresholdMax.ValueChanged += (s, e) =>
            {
                if ((int)numThresholdMax.Value != thresholdRangeSlider.RangeMaximum)
                {
                    thresholdRangeSlider.RangeMaximum = (int)numThresholdMax.Value;
                    UpdateSelectedMaterialRange();
                    mainForm.OnThresholdRangeChanged((byte)thresholdRangeSlider.RangeMinimum, (byte)thresholdRangeSlider.RangeMaximum);
                    mainForm.RenderViews();
                    _ = mainForm.RenderOrthoViewsAsync();
                }
            };
            thresholdPanel.Controls.Add(numThresholdMax);

            leftPanel.Controls.Add(thresholdPanel);

            // Threshold action buttons
            FlowLayoutPanel thresholdButtonsPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false
            };

            btnAddSelection = new Button { Text = "+", Width = 50, Height = 25 };
            btnSubSelection = new Button { Text = "-", Width = 50, Height = 25 };
            
            btnAddSelection.Click += OnAddSelectionAsync;
            btnSubSelection.Click += OnSubSelectionAsync;
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
                // Could later extend this for other tools if needed.
                // Refresh the views so the changes are visible.
                mainForm.RenderViews();
                _ = mainForm.RenderOrthoViewsAsync();
            };

            thresholdButtonsPanel.Controls.Add(btnAddSelection);
            thresholdButtonsPanel.Controls.Add(btnSubSelection);
            thresholdButtonsPanel.Controls.Add(btnClearSelection);
            thresholdButtonsPanel.Controls.Add(btnApply);
            leftPanel.Controls.Add(thresholdButtonsPanel);

            // Tool size controls
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

            // Refresh and interpolate buttons
            btnRefresh = new Button { Text = "Refresh Render", Width = 120 };
            btnRefresh.Click += (s, e) => mainForm.RenderViews();
            leftPanel.Controls.Add(btnRefresh);

            btnInterpolate = new Button { Text = "Interpolate", Width = 120, Enabled = false };
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
            leftPanel.Controls.Add(btnInterpolate);

            return leftPanel;
        }

        private FlowLayoutPanel CreateRightPanel()
        {
            FlowLayoutPanel rightPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                AutoScroll = true,
                WrapContents = false,
                Padding = new Padding(30),
            };

            // XY Slice controls
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

            // XZ Slice controls
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

            // YZ Slice controls
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

            // Histogram
            histogramPictureBox = new PictureBox { Width = 260, Height = 100, BorderStyle = BorderStyle.FixedSingle, Visible = false };
            rightPanel.Controls.Add(histogramPictureBox);

            // Screenshot button
            Button btnScreenshot = new Button
            {
                Text = "Take Screenshot",
                Width = 120,
                Margin = new Padding(0, 10, 0, 0)
            };
            btnScreenshot.Click += (s, e) => mainForm.SaveScreenshot();
            rightPanel.Controls.Add(btnScreenshot);

            return rightPanel;
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

            // Set the tool and enable/disable UI controls accordingly.
            if (item == panMenuItem)
            {
                currentTool = SegmentationTool.Pan;
                toolSizeSlider.Enabled = false;
                thresholdRangeSlider.Enabled = false;
                numThresholdMin.Enabled = false;
                numThresholdMax.Enabled = false;
                btnInterpolate.Enabled = false;
            }
            else if (item == eraserMenuItem)
            {
                currentTool = SegmentationTool.Eraser;
                toolSizeSlider.Enabled = true;
                thresholdRangeSlider.Enabled = false;
                numThresholdMin.Enabled = false;
                numThresholdMax.Enabled = false;
                btnInterpolate.Enabled = true;
            }
            else if (item == brushMenuItem)
            {
                currentTool = SegmentationTool.Brush;
                toolSizeSlider.Enabled = true;
                thresholdRangeSlider.Enabled = false;
                numThresholdMin.Enabled = false;
                numThresholdMax.Enabled = false;
                btnInterpolate.Enabled = true;
            }
            else if (item == thresholdingMenuItem)
            {
                currentTool = SegmentationTool.Thresholding;
                toolSizeSlider.Enabled = false;
                // Enable threshold controls when in thresholding mode.
                thresholdRangeSlider.Enabled = true;
                numThresholdMin.Enabled = true;
                numThresholdMax.Enabled = true;
                btnInterpolate.Enabled = false;
                // Ensure ShowMask is enabled
                showMaskMenuItem.Checked = true;
                mainForm.ShowMask = true;
            }

            // Inform MainForm of the current tool.
            mainForm.SetSegmentationTool(currentTool);
        }

        private void AddLabelOperationsMenu()
        {
            // Create Label Operations submenu
            ToolStripMenuItem labelOperationsMenu = new ToolStripMenuItem("Label Operations");

            // Create Separate Particles menu item
            ToolStripMenuItem separateParticlesMenuItem = new ToolStripMenuItem("Separate Particles");
            separateParticlesMenuItem.Click += (s, e) => OpenParticleSeparator();

            // Add menu item to submenu
            labelOperationsMenu.DropDownItems.Add(separateParticlesMenuItem);

            // Add submenu to Tools menu
            toolsMenu.DropDownItems.Add(labelOperationsMenu);
        }

        private void OpenParticleSeparator()
        {
            // Check if a material is selected
            if (lstMaterials.SelectedIndex < 0 || lstMaterials.SelectedIndex >= mainForm.Materials.Count)
            {
                MessageBox.Show("Please select a material first.", "No Material Selected",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Get the selected material
            Material selectedMaterial = mainForm.Materials[lstMaterials.SelectedIndex];

            // Open the particle separator form
            ParticleSeparatorForm separatorForm = new ParticleSeparatorForm(mainForm, selectedMaterial);
            separatorForm.Show();
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
                mainForm.RenderViews();
                _ = mainForm.RenderOrthoViewsAsync();
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
                    InitializeSliceControls(); // Use new helper method
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
                    InitializeSliceControls(); // Use new helper method
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
        /// Handles the Extract from Material button click event
        /// </summary>
        private async Task OnExtractFromMaterialAsync()
        {
            // Check if a valid source material is selected
            int sourceIdx = lstMaterials.SelectedIndex;
            if (sourceIdx <= 0 || sourceIdx >= mainForm.Materials.Count)
            {
                MessageBox.Show("Please select a valid source material (not the Exterior).",
                    "Invalid Selection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Material sourceMaterial = mainForm.Materials[sourceIdx];

            // Build a dialog to select target material
            using (Form dlg = new Form
            {
                Text = $"Extract from '{sourceMaterial.Name}'",
                Width = 350,
                Height = 170,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent
            })
            {
                Label lblTarget = new Label
                {
                    Text = "Target material:",
                    Left = 10,
                    Top = 15,
                    AutoSize = true
                };

                ComboBox cbTargetMaterial = new ComboBox
                {
                    Left = 110,
                    Top = 12,
                    Width = 200,
                    DropDownStyle = ComboBoxStyle.DropDownList
                };

                Label lblMode = new Label
                {
                    Text = "Extraction mode:",
                    Left = 10,
                    Top = 45,
                    AutoSize = true
                };

                RadioButton rbCurrent = new RadioButton
                {
                    Text = "Current selection/threshold",
                    Left = 110,
                    Top = 45,
                    Width = 200,
                    Checked = true
                };

                RadioButton rbAll = new RadioButton
                {
                    Text = "All voxels of this material",
                    Left = 110,
                    Top = 70,
                    Width = 200
                };

                Button btnOK = new Button
                {
                    Text = "Extract",
                    DialogResult = DialogResult.OK,
                    Left = 75,
                    Width = 90,
                    Top = 100
                };

                Button btnCancel = new Button
                {
                    Text = "Cancel",
                    DialogResult = DialogResult.Cancel,
                    Left = 175,
                    Width = 90,
                    Top = 100
                };

                // Populate with all other materials except source
                foreach (var m in mainForm.Materials.FindAll(m => m.ID != sourceMaterial.ID && !m.IsExterior))
                {
                    cbTargetMaterial.Items.Add(m);
                }

                // Add the exterior material at the beginning for emergency removal cases
                if (mainForm.Materials.Count > 0 && mainForm.Materials[0].IsExterior)
                {
                    cbTargetMaterial.Items.Insert(0, mainForm.Materials[0]);
                }

                if (cbTargetMaterial.Items.Count == 0)
                {
                    MessageBox.Show("No target materials available. Please create another material first.",
                                    "No Target Material", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                cbTargetMaterial.SelectedIndex = 0;

                dlg.Controls.AddRange(new Control[] { lblTarget, cbTargetMaterial, lblMode, rbCurrent, rbAll, btnOK, btnCancel });
                dlg.AcceptButton = btnOK;
                dlg.CancelButton = btnCancel;

                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    Material targetMaterial = (Material)cbTargetMaterial.SelectedItem;
                    bool currentSelectionOnly = rbCurrent.Checked;

                    // Create and show a progress form
                    using (ProgressForm progressForm = new ProgressForm($"Extracting from '{sourceMaterial.Name}' to '{targetMaterial.Name}'..."))
                    {
                        progressForm.Show();
                        this.Enabled = false;

                        try
                        {
                            // Run the extraction operation asynchronously
                            await Task.Run(() =>
                            {
                                ExtractFromMaterial(sourceMaterial, targetMaterial, currentSelectionOnly);
                            });

                            Logger.Log($"[ControlForm] Extracted voxels from material '{sourceMaterial.Name}' to '{targetMaterial.Name}'");

                            // Update UI and refresh views
                            mainForm.SaveLabelsChk();
                            mainForm.RenderViews();
                            await mainForm.RenderOrthoViewsAsync();
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[ControlForm] Error during material extraction: {ex.Message}");
                            MessageBox.Show($"Error during extraction: {ex.Message}",
                                "Extraction Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        finally
                        {
                            this.Enabled = true;
                        }
                    }
                }
            }
        }
        /// <summary>
        /// Handles the “＋” button – routes the action through the new asynchronous
        /// path in <see cref="MainForm"/>.
        /// </summary>
        private async void OnAddSelectionAsync(object sender, EventArgs e)
        {
            int idx = lstMaterials.SelectedIndex;
            if (idx <= 0 || idx >= mainForm.Materials.Count)
            {
                MessageBox.Show("Select a valid (non-Exterior) material.");
                return;
            }

            Material mat = mainForm.Materials[idx];

            if (mainForm.currentTool == SegmentationTool.Brush)
            {
                mainForm.ApplyCurrentSelection();
                mainForm.ApplyOrthoSelections();
            }
            else if (mainForm.interpolatedMask != null)
            {
                mainForm.ApplyInterpolatedSelection(mat.ID);
            }
            else
            {
                await mainForm.AddThresholdSelectionAsync(mat.Min, mat.Max, mat.ID);
            }

            mainForm.SaveLabelsChk();
        }

        /// <summary>
        /// Handles the “－” button – routes the action through the new asynchronous
        /// path in <see cref="MainForm"/>.
        /// </summary>
        private async void OnSubSelectionAsync(object sender, EventArgs e)
        {
            int idx = lstMaterials.SelectedIndex;
            if (idx <= 0 || idx >= mainForm.Materials.Count)
            {
                MessageBox.Show("Select a valid (non-Exterior) material.");
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
                await mainForm.RemoveThresholdSelectionAsync(mat.Min, mat.Max, mat.ID);
            }

            mainForm.SaveLabelsChk();
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

                    // Get the next available material ID from MaterialOperations
                    byte newID = mainForm.GetNextMaterialID();
                    Material mat = new Material(name, dlg.Color, 0, 0, newID);
                    mainForm.Materials.Add(mat);
                    RefreshMaterialList();
                    lstMaterials.SelectedIndex = lstMaterials.Items.Count - 1;
                }
            }
            mainForm.SaveLabelsChk();
        }

        private async void OnRemoveMaterial()
        {
            int idx = lstMaterials.SelectedIndex;
            // Do not allow deletion if index is 0 (Exterior) or invalid.
            if (idx <= 0 || idx >= mainForm.Materials.Count)
            {
                MessageBox.Show("Invalid selection. Cannot remove the Exterior material or invalid index.");
                return;
            }

            // Get the material using the list index
            Material mat = mainForm.Materials[idx];

            // Confirm removal
            var result = MessageBox.Show(
                $"Are you sure you want to remove material '{mat.Name}'?\n\nThis operation might take a while for large volumes.",
                "Confirm Removal",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result != DialogResult.Yes)
                return;

            // Create and show a progress form
            ProgressForm progressForm = new ProgressForm($"Removing material '{mat.Name}'...");
            progressForm.Show();

            // Disable UI controls during the operation
            btnAddMaterial.Enabled = false;
            btnRemoveMaterial.Enabled = false;
            btnRenameMaterial.Enabled = false;
            lstMaterials.Enabled = false;

            try
            {
                // Use MaterialOperations to remove the material through MainForm
                await Task.Run(() => mainForm.RemoveMaterialAndReindex(mat.ID));

                // Update UI after successful removal
                RefreshMaterialList();
                mainForm.SaveLabelsChk();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error removing material: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                // Re-enable UI controls
                btnAddMaterial.Enabled = true;
                btnRemoveMaterial.Enabled = true;
                btnRenameMaterial.Enabled = true;
                lstMaterials.Enabled = true;

                // Close the progress form
                progressForm.Close();
            }
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

        private void OpenTextureClassifier()
        {
            if (mainForm.volumeData == null)
            {
                MessageBox.Show("Please load a dataset first.", "No Data",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Get the selected material
            Material selectedMaterial;
            if (lstMaterials.SelectedIndex > 0 && lstMaterials.SelectedIndex < mainForm.Materials.Count)
            {
                selectedMaterial = mainForm.Materials[lstMaterials.SelectedIndex];
            }
            else
            {
                // Default to the first non-exterior material
                selectedMaterial = mainForm.Materials.Count > 1 ? mainForm.Materials[1] : mainForm.Materials[0];
            }

            Logger.Log("[ControlForm] Opening Texture Classifier tool");
            TextureClassifier textureClassifier = new TextureClassifier(mainForm, selectedMaterial);
            textureClassifier.Show();
        }

        private void UpdateThresholdSliders()
        {
            int idx = lstMaterials.SelectedIndex;
            if (idx < 0 || idx >= mainForm.Materials.Count)
                return;
            mainForm.SelectedMaterialIndex = idx;
            Material mat = mainForm.Materials[idx];

            // Update the RangeSlider with material's min/max values
            thresholdRangeSlider.RangeMinimum = mat.Min;
            thresholdRangeSlider.RangeMaximum = mat.Max;

            // Update the numeric controls
            numThresholdMin.Value = mat.Min;
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

            mat.Min = (byte)thresholdRangeSlider.RangeMinimum;
            mat.Max = (byte)thresholdRangeSlider.RangeMaximum;
            mainForm.PreviewMin = mat.Min;
            mainForm.PreviewMax = mat.Max;

            if (histogramPictureBox.Visible)
                UpdateHistogram(histogramPictureBox);

            mainForm.RenderViews();
            _ = mainForm.RenderOrthoViewsAsync();
        }

        private void AddThresholdedSelection()
        {
            if (mainForm.currentTool == SegmentationTool.Brush)
            {
                mainForm.ApplyCurrentSelection();     // Apply XY selection.
                mainForm.ApplyOrthoSelections();      // Apply selections from XZ and YZ views.
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
                mainForm.AddThresholdSelection(mat.Min, mat.Max, mat.ID);
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
                mainForm.RemoveThresholdSelection(mat.Min, mat.Max, mat.ID);
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

                        int minThreshold = thresholdRangeSlider.RangeMinimum;
                        int maxThreshold = thresholdRangeSlider.RangeMaximum;

                        using (Pen redPen = new Pen(Color.Red, 2))
                        using (Pen bluePen = new Pen(Color.Blue, 2))
                        {
                            g.DrawLine(redPen, minThreshold, 0, minThreshold, histHeight);
                            g.DrawLine(bluePen, maxThreshold, 0, maxThreshold, histHeight);
                        }

                        // Define fixed rectangles for drawing text labels.
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

        private void AddBrightnessContrastMenu()
        {
            // Create menu item for Brightness/Contrast tool
            ToolStripMenuItem brightnessContrastMenuItem = new ToolStripMenuItem("Brightness / Contrast");
            brightnessContrastMenuItem.Click += (s, e) =>
            {
                if (mainForm.volumeData == null)
                {
                    MessageBox.Show("Please load a dataset first.", "No Data",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Create and show the Brightness/Contrast adjustment form
                BrightnessContrastForm brightnessForm = new BrightnessContrastForm(mainForm);
                brightnessForm.Show();
            };

            // Add the menu item to the Tools menu
            toolsMenu.DropDownItems.Add(brightnessContrastMenuItem);
        }

        /// <summary>
        /// Adds the Statistics menu to the Tools menu
        /// </summary>
        private void AddStatisticsMenu()
        {
            // Create Statistics submenu
            ToolStripMenuItem statisticsMenu = new ToolStripMenuItem("Statistics");

            // Create Material Statistics menu item
            ToolStripMenuItem materialStatisticsMenuItem = new ToolStripMenuItem("Material Statistics");
            materialStatisticsMenuItem.Click += (s, e) => OpenMaterialStatistics();

            // Add menu item to submenu
            statisticsMenu.DropDownItems.Add(materialStatisticsMenuItem);

            // Add submenu to Tools menu
            toolsMenu.DropDownItems.Add(statisticsMenu);
        }

        /// <summary>
        /// Opens the Material Statistics form
        /// </summary>
        private void OpenMaterialStatistics()
        {
            if (mainForm.volumeData == null || mainForm.volumeLabels == null)
            {
                MessageBox.Show("Please load a dataset first.", "No Data",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Create and show the Material Statistics form
            MaterialStatisticsForm statsForm = new MaterialStatisticsForm(mainForm);
            statsForm.Show();
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

        /// <summary>
        /// Extracts voxels from source material to target material
        /// </summary>
        private void ExtractFromMaterial(Material sourceMaterial, Material targetMaterial, bool currentSelectionOnly)
        {
            if (mainForm.volumeLabels == null || mainForm.volumeData == null)
                return;

            // Check which tool is active and process accordingly
            if (currentSelectionOnly)
            {
                if (currentTool == SegmentationTool.Brush)
                {
                    // For brush tool, extract using the current 2D selections
                    ExtractUsingBrushSelection(sourceMaterial.ID, targetMaterial.ID);
                }
                else if (currentTool == SegmentationTool.Thresholding)
                {
                    // For thresholding tool, extract within the threshold range
                    ExtractUsingThreshold(sourceMaterial.ID, targetMaterial.ID);
                }
                else
                {
                    // Default to using the current material's threshold range
                    ExtractUsingMaterialThreshold(sourceMaterial, targetMaterial);
                }
            }
            else
            {
                // Extract all voxels of the source material to the target material
                ExtractAllVoxels(sourceMaterial.ID, targetMaterial.ID);
            }
        }

        /// <summary>
        /// Extracts voxels using the current brush selection
        /// </summary>
        private void ExtractUsingBrushSelection(byte sourceID, byte targetID)
        {
            // Handle extraction for the current XY slice
            if (mainForm.currentSelection != null)
            {
                int w = mainForm.GetWidth();
                int h = mainForm.GetHeight();
                int currentSlice = mainForm.CurrentSlice;

                // Process each voxel in the current slice
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        // If the voxel is selected (non-zero in selection) and matches source material
                        if (mainForm.currentSelection[x, y] != 0 && mainForm.volumeLabels[x, y, currentSlice] == sourceID)
                        {
                            // Transfer the voxel to the target material
                            mainForm.volumeLabels[x, y, currentSlice] = targetID;
                        }
                    }
                }
            }

            // Also process ortho selections if they exist
            if (mainForm.currentSelectionXZ != null)
            {
                int w = mainForm.GetWidth();
                int d = mainForm.GetDepth();
                int yFixed = mainForm.XzSliceY;

                for (int z = 0; z < d; z++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        if (x < mainForm.currentSelectionXZ.GetLength(0) && z < mainForm.currentSelectionXZ.GetLength(1) &&
                            mainForm.currentSelectionXZ[x, z] != 0 && mainForm.volumeLabels[x, yFixed, z] == sourceID)
                        {
                            mainForm.volumeLabels[x, yFixed, z] = targetID;
                        }
                    }
                }
            }

            if (mainForm.currentSelectionYZ != null)
            {
                int h = mainForm.GetHeight();
                int d = mainForm.GetDepth();
                int xFixed = mainForm.YzSliceX;

                for (int z = 0; z < d; z++)
                {
                    for (int y = 0; y < h; y++)
                    {
                        if (z < mainForm.currentSelectionYZ.GetLength(0) && y < mainForm.currentSelectionYZ.GetLength(1) &&
                            mainForm.currentSelectionYZ[z, y] != 0 && mainForm.volumeLabels[xFixed, y, z] == sourceID)
                        {
                            mainForm.volumeLabels[xFixed, y, z] = targetID;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Extracts voxels based on the current threshold settings
        /// </summary>
        private void ExtractUsingThreshold(byte sourceID, byte targetID)
        {
            int w = mainForm.GetWidth();
            int h = mainForm.GetHeight();
            int d = mainForm.GetDepth();

            byte minThreshold = (byte)thresholdRangeSlider.RangeMinimum;
            byte maxThreshold = (byte)thresholdRangeSlider.RangeMaximum;

            // Only extract voxels that are within the threshold range AND belong to the source material
            Parallel.For(0, d, z =>
            {
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        byte grayValue = mainForm.volumeData[x, y, z];
                        byte labelValue = mainForm.volumeLabels[x, y, z];

                        // Check if the voxel is within threshold range and belongs to source material
                        if (grayValue >= minThreshold && grayValue <= maxThreshold && labelValue == sourceID)
                        {
                            // Transfer to target material
                            mainForm.volumeLabels[x, y, z] = targetID;
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Extracts voxels using the source material's threshold range
        /// </summary>
        private void ExtractUsingMaterialThreshold(Material sourceMaterial, Material targetMaterial)
        {
            int w = mainForm.GetWidth();
            int h = mainForm.GetHeight();
            int d = mainForm.GetDepth();

            byte minThreshold = sourceMaterial.Min;
            byte maxThreshold = sourceMaterial.Max;

            // Extract voxels that are within the source material's threshold range AND belong to the source material
            Parallel.For(0, d, z =>
            {
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        byte grayValue = mainForm.volumeData[x, y, z];
                        byte labelValue = mainForm.volumeLabels[x, y, z];

                        // Check if the voxel is within threshold range and belongs to source material
                        if (grayValue >= minThreshold && grayValue <= maxThreshold && labelValue == sourceMaterial.ID)
                        {
                            // Transfer to target material
                            mainForm.volumeLabels[x, y, z] = targetMaterial.ID;
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Extracts all voxels from source material to target material
        /// </summary>
        private void ExtractAllVoxels(byte sourceID, byte targetID)
        {
            int w = mainForm.GetWidth();
            int h = mainForm.GetHeight();
            int d = mainForm.GetDepth();

            // Process the entire volume
            Parallel.For(0, d, z =>
            {
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        // If the voxel belongs to the source material
                        if (mainForm.volumeLabels[x, y, z] == sourceID)
                        {
                            // Transfer it to the target material
                            mainForm.volumeLabels[x, y, z] = targetID;
                        }
                    }
                }
            });
        }
        private void OnMergeMaterial()
        {
            int idxTarget = lstMaterials.SelectedIndex;
            if (idxTarget <= 0 || idxTarget >= mainForm.Materials.Count)
            {
                MessageBox.Show("Select a valid material (not the Exterior) to merge into.", "Invalid Selection",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var targetMat = mainForm.Materials[idxTarget];

            // Build a simple dialog with a ComboBox for the source material
            using (Form dlg = new Form { Text = $"Merge into '{targetMat.Name}'", Width = 350, Height = 150, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent })
            {
                var lbl = new Label { Text = "Merge material:", Left = 10, Top = 10, AutoSize = true };
                var cb = new ComboBox { Left = 110, Top = 8, Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
                // Populate with all other materials
                foreach (var m in mainForm.Materials.Where((m, i) => i != idxTarget))
                    cb.Items.Add(m);
                if (cb.Items.Count == 0)
                {
                    MessageBox.Show("No other materials available to merge.", "Nothing to Merge",
                                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                cb.SelectedIndex = 0;

                var btnOK = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 150, Width = 80, Top = 50 };
                dlg.Controls.AddRange(new Control[] { lbl, cb, btnOK });
                dlg.AcceptButton = btnOK;

                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    var sourceMat = (Material)cb.SelectedItem;
                    // perform the merge
                    mainForm.MergeMaterials(targetMat.ID, sourceMat.ID);
                    Logger.Log($"[ControlForm] Merged material '{sourceMat.Name}' into '{targetMat.Name}'");
                    mainForm.SaveLabelsChk();
                    RefreshMaterialList();
                    // re‑select the target in the refreshed list
                    lstMaterials.SelectedIndex = mainForm.Materials.FindIndex(m => m.ID == targetMat.ID);
                }
            }
        }
    }
}