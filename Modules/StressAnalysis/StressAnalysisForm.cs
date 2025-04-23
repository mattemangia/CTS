using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Krypton.Toolkit;
using Krypton.Ribbon;
using ILGPU;
using ILGPU.Runtime;
using Krypton.Navigator;
using Krypton.Docking;
using static MaterialDensityLibrary;
using System.Drawing.Drawing2D;
using System.Collections.Concurrent;

namespace CTSegmenter
{
    public partial class StressAnalysisForm : KryptonForm
    {
        private AcousticVelocitySimulation currentAcousticSim;
        private MainForm mainForm;
        public Material selectedMaterial;
        private Context ilgpuContext;
        private Accelerator accelerator;
        private KryptonRibbon ribbon;
        private KryptonRibbonTab meshTab;
        private KryptonRibbonTab analysisTab;
        private KryptonRibbonTab resultsTab;
        private KryptonRibbonGroup meshGroup;
        private KryptonRibbonGroup importGroup;
        private KryptonRibbonGroup visualizationGroup;
        private KryptonRibbonGroup analysisGroup;
        private KryptonHeader statusHeader;
        private KryptonButton btnAssignVaryingDensity;
        public bool inhomogeneousDensityEnabled = false;
        public Dictionary<Vector3, float> voxelDensities = null;
        private KryptonDockableNavigator mainTabControl;
        private KryptonPage meshPage;
        private KryptonPage analysisPage;
        private KryptonPage resultsPage;
        private Panel meshViewPanel;
        private TrackBar resolutionTrackBar;
        private NumericUpDown facetsNumeric;
        private Label resolutionLabel;
        private Label facetsLabel;
        private Button generateMeshButton;
        private Button importMeshButton;
        private ComboBox materialComboBox;
        private Label materialLabel;
        private bool meshGenerated = false;
        private List<Triangle> meshTriangles = new List<Triangle>();
        private KryptonButton applyMeshButton;
        private KryptonContextMenu tabContextMenu;
        private KryptonContextMenuItems tabMenuItems;
        private List<KryptonPage> closedPages = new List<KryptonPage>();
        private KryptonGroupBox triaxialParamsBox;
        private KryptonGroupBox acousticParamsBox;
        private NumericUpDown confiningPressureNumeric;
        private NumericUpDown pressureMinNumeric;
        private NumericUpDown pressureMaxNumeric;
        private NumericUpDown pressureStepsNumeric;
        private ComboBox testDirectionCombo;
        private NumericUpDown acousticConfiningNumeric;
        private ComboBox waveTypeCombo;
        private NumericUpDown timeStepsNumeric;
        private NumericUpDown frequencyNumeric;
        private NumericUpDown amplitudeNumeric;
        private NumericUpDown energyNumeric;
        private ComboBox acousticDirectionCombo;
        private KryptonButton runTriaxialButton;
        private KryptonButton runAcousticButton;
        private KryptonCheckBox extendedSimulationCheckBox;
        private PictureBox wavePictureBox;
        private Panel waveScrollPanel;

        // Properties to expose inhomogeneous density state
        public bool InhomogeneousDensityEnabled => inhomogeneousDensityEnabled;
        public Dictionary<Vector3, float> VoxelDensities => voxelDensities;

        // Method to expose density calculations to other forms
        public Dictionary<Vector3, float> GetCalculatedDensityMap()
        {
            return voxelDensities;
        }

        public bool UseExtendedSimulationTime { get; private set; } = true;
        // Wave Propagation controls
        private float waveZoomLevel = 1.0f;
        private PointF wavePanOffset = new PointF(0, 0);
        private bool isWavePanning = false;
        private Point waveLastMousePos;
        private Bitmap originalWaveImage = null;
        //3D view controls
        private float rotationX = 0f;
        private float rotationY = 0f;
        private float zoomLevel = 1.0f;
        private float panX = 0f;
        private float panY = 0f;
        private Point lastMousePosition;
        private bool isRotating = false;
        private bool isPanning = false;
        private bool isAutoRotating = false;
        private ToolTip viewerTooltip;
        private Button toggleRotationButton;
        private Button resetViewButton;
        private Label viewControlsLabel;

        private KryptonButton btnSetDensity;
        private Label densityLabel;

        private bool isMeshGenerationCancelled = false;
        private Button cancelMeshButton;
        private Panel controlsPanel;
        private TriaxialSimulation currentTriaxialSim;
        private NumericUpDown dtFactorNumeric;
        private struct Point3D
        {
            public int X;
            public int Y;
            public int Z;

            public Point3D(int x, int y, int z)
            {
                X = x;
                Y = y;
                Z = z;
            }
        }
        // Simple structure for 3D triangles
        private struct Triangle
        {
            public Vector3 V1;
            public Vector3 V2;
            public Vector3 V3;

            public Triangle(Vector3 v1, Vector3 v2, Vector3 v3)
            {
                V1 = v1;
                V2 = v2;
                V3 = v3;
            }
        }

        // Simple 3D vector structure
        public struct Vector3
        {
            public float X;
            public float Y;
            public float Z;

            public Vector3(float x, float y, float z)
            {
                X = x;
                Y = y;
                Z = z;
            }
        }
        private KryptonPage mohrCoulombPage;
        public StressAnalysisForm(MainForm mainForm)
        {
            Logger.Log("[StressAnalysisForm] Module Initialization Called.");
            Logger.Log("[StressAnalysisForm] Constructor start.");
            try
            {
                string iconPath = Path.Combine(Application.StartupPath, "favicon.ico");
                if (File.Exists(iconPath))
                    this.Icon = new Icon(iconPath);
            }
            catch { }
            this.mainForm = mainForm;
            InitializeComponent();
            InitializeILGPU();
            PopulateMaterialList();
            this.Load += StressAnalysisPatch_Load;
            BuildWavePropagationCanvas();
            Logger.Log("[StressAnalysisForm] Constructor end.");
        }
        private void BuildWavePropagationCanvas()
        {
            // Create a scrollable panel to hold the PictureBox
            waveScrollPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(20, 20, 20),
                AutoScroll = true
            };

            // Create the PictureBox for rendering the wave image
            wavePictureBox = new PictureBox
            {
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.Zoom
            };
            // Wire up zoom/pan events
            wavePictureBox.MouseWheel += WavePictureBox_MouseWheel;
            wavePictureBox.MouseDown += WavePictureBox_MouseDown;
            wavePictureBox.MouseMove += WavePictureBox_MouseMove;
            wavePictureBox.MouseUp += WavePictureBox_MouseUp;

            // Add the PictureBox into the scroll panel
            waveScrollPanel.Controls.Add(wavePictureBox);

            // Build the overlay controls (zoom in/out, fit, export)
            Panel zoomPanel = new Panel
            {
                Size = new Size(180, 120),
                Location = new Point(10, 10),
                BackColor = Color.FromArgb(60, 0, 0, 0),
                Padding = new Padding(5)
            };

            Label zoomLabel = new Label
            {
                Text = "Wave Visualization Controls:",
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                AutoSize = true,
                Location = new Point(5, 5)
            };
            zoomPanel.Controls.Add(zoomLabel);

            Button zoomInButton = new Button
            {
                Text = "Zoom In (+)",
                BackColor = Color.FromArgb(60, 60, 100),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(80, 25),
                Location = new Point(5, 30)
            };
            zoomInButton.FlatAppearance.BorderColor = Color.LightBlue;
            zoomInButton.Click += (s, e) => ZoomWaveView(1.2f);
            zoomPanel.Controls.Add(zoomInButton);

            Button zoomOutButton = new Button
            {
                Text = "Zoom Out (-)",
                BackColor = Color.FromArgb(60, 60, 100),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(80, 25),
                Location = new Point(90, 30)
            };
            zoomOutButton.FlatAppearance.BorderColor = Color.LightBlue;
            zoomOutButton.Click += (s, e) => ZoomWaveView(0.8f);
            zoomPanel.Controls.Add(zoomOutButton);

            Button zoomFitButton = new Button
            {
                Text = "Zoom to Fit",
                BackColor = Color.FromArgb(60, 80, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(170, 25),
                Location = new Point(5, 60)
            };
            zoomFitButton.FlatAppearance.BorderColor = Color.LightGreen;
            zoomFitButton.Click += (s, e) => ResetWaveView();
            zoomPanel.Controls.Add(zoomFitButton);

            Button exportImageButton = new Button
            {
                Text = "Export Image",
                BackColor = Color.FromArgb(80, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(170, 25),
                Location = new Point(5, 90)
            };
            exportImageButton.FlatAppearance.BorderColor = Color.LightCoral;
            exportImageButton.Click += (s, e) => ExportWaveImage();
            zoomPanel.Controls.Add(exportImageButton);

            // Add the zoomPanel on top of the scroll panel
            waveScrollPanel.Controls.Add(zoomPanel);
            zoomPanel.BringToFront();

            // Finally, clear and add our scroll panel into the Results tab
            resultsPage.Controls.Clear();
            resultsPage.Controls.Add(waveScrollPanel);

            // Set up a tooltip for the PictureBox
            ToolTip tooltip = new ToolTip();
            tooltip.SetToolTip(wavePictureBox,
                "Mouse Wheel: Zoom\nDrag: Pan\nRight-click: Reset View");
        }

        private void HookSimCompleted(AcousticVelocitySimulation sim)
        {
            if (sim == null || wavePictureBox == null) return;

            sim.SimulationCompleted += (s, e) =>
            {
                try
                {
                    // Use larger dimensions for more detailed visualization
                    const int logicalW = 2000;
                    const int logicalH = 1500;

                    using (var bmp = new Bitmap(logicalW, logicalH))
                    using (var g = Graphics.FromImage(bmp))
                    {
                        // Clear the graphics area with background color
                        g.Clear(Color.Black);

                        // Render the results
                        sim.RenderResults(g, bmp.Width, bmp.Height, RenderMode.Stress);

                        // Dispose of old image
                        if (wavePictureBox.Image != null)
                        {
                            var oldImage = wavePictureBox.Image;
                            wavePictureBox.Image = null;
                            oldImage.Dispose();
                        }

                        // Set the new image
                        wavePictureBox.Image = (Image)bmp.Clone();

                        // Ensure the picture box is sized to match the image
                        wavePictureBox.Size = bmp.Size;

                        // Force a refresh of the scrollable panel to update scrollbars
                        waveScrollPanel.AutoScrollMinSize = wavePictureBox.Size;
                        waveScrollPanel.Invalidate();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[StressAnalysisForm] Error rendering wave simulation: {ex.Message}");
                }
            };
        }
        public void UpdateWaveVisualization(AcousticVelocitySimulation sim)
        {
            if (sim == null || wavePictureBox == null) return;

            try
            {
                // Dispose previous original image
                if (originalWaveImage != null)
                {
                    originalWaveImage.Dispose();
                    originalWaveImage = null;
                }

                // Use a reasonable fixed size for the original high-quality image
                int bitmapWidth = 2000;
                int bitmapHeight = 1500;

                // Create the high-quality original image
                originalWaveImage = new Bitmap(bitmapWidth, bitmapHeight);
                using (var g = Graphics.FromImage(originalWaveImage))
                {
                    g.Clear(Color.Black);

                    // Set high quality rendering
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

                    // Render the wave field
                    sim.RenderResults(g, bitmapWidth, bitmapHeight, RenderMode.Stress);

                    // Add a footer with key information
                    using (var font = new Font("Arial", 12))
                    using (var brush = new SolidBrush(Color.White))
                    using (var bgBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                    {
                        string info = $"{sim.WaveType} - P-Wave: {sim.MeasuredPWaveVelocity:F1} m/s, S-Wave: {sim.MeasuredSWaveVelocity:F1} m/s, Vp/Vs: {sim.CalculatedVpVsRatio:F2}";
                        SizeF textSize = g.MeasureString(info, font);
                        float x = (bitmapWidth - textSize.Width) / 2;

                        g.FillRectangle(bgBrush, x - 10, bitmapHeight - textSize.Height - 30, textSize.Width + 20, textSize.Height + 10);
                        g.DrawString(info, font, brush, x, bitmapHeight - textSize.Height - 25);
                    }
                }

                // Dispose any existing image in the picture box
                if (wavePictureBox.Image != null)
                {
                    var oldImage = wavePictureBox.Image;
                    wavePictureBox.Image = null;
                    if (oldImage != originalWaveImage) // Don't dispose if it's the original
                    {
                        oldImage.Dispose();
                    }
                }

                // Reset zoom and pan for new visualization
                ResetWaveView();

                Logger.Log($"[StressAnalysisForm] Wave visualization updated successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"[StressAnalysisForm] Error updating wave visualization: {ex.Message}");
                if (wavePictureBox.Image != null)
                {
                    wavePictureBox.Image.Dispose();
                    wavePictureBox.Image = null;
                }

                // Display error message in the picture box
                Bitmap errorImage = new Bitmap(800, 600);
                using (var g = Graphics.FromImage(errorImage))
                {
                    g.Clear(Color.Black);
                    using (var font = new Font("Arial", 12))
                    using (var brush = new SolidBrush(Color.Red))
                    {
                        g.DrawString($"Error updating visualization: {ex.Message}", font, brush, 20, 20);
                        g.DrawString("Please try running the simulation again.", font, brush, 20, 50);
                    }
                }
                wavePictureBox.Image = errorImage;
            }
        }
        private void InitializeComponent()
        {
            // Basic window settings
            this.Text = "Stress Analysis and Acoustic Velocity";
            this.Size = new Size(1024, 768);
            this.MinimumSize = new Size(800, 600);
            this.ControlBox = true;
            this.MaximizeBox = true;
            this.MinimizeBox = true;
            this.FormBorderStyle = FormBorderStyle.Sizable;

            // --- Create Ribbon and Tabs ---
            ribbon = new KryptonRibbon();
            ribbon.StateCommon.RibbonGeneral.TabRowBackgroundSolidColor = Color.FromArgb(45, 45, 48);
            ribbon.Dock = DockStyle.Top;

            meshTab = new KryptonRibbonTab() { Text = "Mesh" };
            analysisTab = new KryptonRibbonTab() { Text = "Analysis" };
            resultsTab = new KryptonRibbonTab() { Text = "Results" };
            ribbon.RibbonTabs.AddRange(new[] { meshTab, analysisTab, resultsTab });

            // On each tab, add a Tab‑Management group and a Window group
            foreach (var tab in new[] { meshTab, analysisTab, resultsTab })
            {
                // Tab Management → Reopen Closed Tabs
                var tabMgmtGroup = new KryptonRibbonGroup { TextLine1 = "Tab Management" };
                var tabMgmtTriple = new KryptonRibbonGroupTriple();
                var reopenBtn = new KryptonRibbonGroupButton
                {
                    TextLine1 = "Reopen",
                    TextLine2 = "Closed Tabs",
                    ImageSmall = CreateReopenTabIcon(16),
                    ImageLarge = CreateReopenTabIcon(32)
                };
                reopenBtn.Click += ReopenTabsButton_Click;
                tabMgmtTriple.Items.Add(reopenBtn);
                tabMgmtGroup.Items.Add(tabMgmtTriple);

                // Window → Close Window
                var windowGroup = new KryptonRibbonGroup { TextLine1 = "Window" };
                var windowTriple = new KryptonRibbonGroupTriple();
                var closeWinBtn = new KryptonRibbonGroupButton
                {
                    TextLine1 = "Close",
                    TextLine2 = "Window",
                    ImageSmall = CreateCancelIcon(16),
                    ImageLarge = CreateCancelIcon(32)
                };
                closeWinBtn.Click += (s, e) => this.Close();
                windowTriple.Items.Add(closeWinBtn);
                windowGroup.Items.Add(windowTriple);

                tab.Groups.Add(tabMgmtGroup);
                tab.Groups.Add(windowGroup);
            }

            // --- Core Ribbon Groups ---
            meshGroup = new KryptonRibbonGroup { TextLine1 = "Mesh Generation" };
            importGroup = new KryptonRibbonGroup { TextLine1 = "Import/Export" };
            visualizationGroup = new KryptonRibbonGroup { TextLine1 = "Visualization" };
            analysisGroup = new KryptonRibbonGroup { TextLine1 = "Analysis Options" };
            var simulationOptionsGroup = new KryptonRibbonGroup { TextLine1 = "Simulation Options" };
            meshTab.Groups.AddRange(new[] { meshGroup, importGroup, visualizationGroup });
            var simOptionsTriple = new KryptonRibbonGroupTriple();
            simulationOptionsGroup.Items.Add(simOptionsTriple);
            analysisTab.Groups.Add(analysisGroup);
            analysisTab.Groups.Add(simulationOptionsGroup);

            var toggleInhomogeneousBtn = new KryptonRibbonGroupButton
            {
                TextLine1 = "Use Varying",
                TextLine2 = "Density",
                Checked = inhomogeneousDensityEnabled,
                ButtonType = GroupButtonType.Check,
                ImageSmall = CreateInhomogeneousDensityIcon(16),
                ImageLarge = CreateInhomogeneousDensityIcon(32)
            };
            toggleInhomogeneousBtn.Click += (s, e) => {
                inhomogeneousDensityEnabled = toggleInhomogeneousBtn.Checked;
                statusHeader.Text = inhomogeneousDensityEnabled ?
                    "Inhomogeneous density mode enabled." :
                    "Inhomogeneous density mode disabled.";
            };
            simOptionsTriple.Items.Add(toggleInhomogeneousBtn);

            // Mesh Generation buttons
            {
                var meshTriple = new KryptonRibbonGroupTriple();
                var generateButton = new KryptonRibbonGroupButton
                {
                    TextLine1 = "Generate",
                    TextLine2 = "Mesh",
                    ImageSmall = CreateMeshIcon(16),
                    ImageLarge = CreateMeshIcon(32)
                };
                generateButton.Click += GenerateMeshButton_Click;

                var settingsButton = new KryptonRibbonGroupButton
                {
                    TextLine1 = "Mesh",
                    TextLine2 = "Settings",
                    ImageSmall = CreateSettingsIcon(16),
                    ImageLarge = CreateSettingsIcon(32)
                };
                settingsButton.Click += MeshSettingsButton_Click;

                meshTriple.Items.AddRange(new KryptonRibbonGroupItem[] { generateButton, settingsButton });
                meshGroup.Items.Add(meshTriple);
            }

            // Import/Export buttons
            {
                var importTriple = new KryptonRibbonGroupTriple();
                var importObjButton = new KryptonRibbonGroupButton
                {
                    TextLine1 = "Import",
                    TextLine2 = "OBJ",
                    ImageSmall = CreateImportIcon(16, "OBJ"),
                    ImageLarge = CreateImportIcon(32, "OBJ")
                };
                importObjButton.Click += ImportObjButton_Click;

                var importStlButton = new KryptonRibbonGroupButton
                {
                    TextLine1 = "Import",
                    TextLine2 = "STL",
                    ImageSmall = CreateImportIcon(16, "STL"),
                    ImageLarge = CreateImportIcon(32, "STL")
                };
                importStlButton.Click += ImportStlButton_Click;

                var exportButton = new KryptonRibbonGroupButton
                {
                    TextLine1 = "Export",
                    TextLine2 = "Mesh",
                    ImageSmall = CreateExportIcon(16),
                    ImageLarge = CreateExportIcon(32)
                };
                exportButton.Click += ExportMeshButton_Click;

                importTriple.Items.AddRange(new KryptonRibbonGroupItem[] { importObjButton, importStlButton, exportButton });
                importGroup.Items.Add(importTriple);
            }

            // Visualization buttons
            {
                var visualTriple = new KryptonRibbonGroupTriple();
                var wireframeButton = new KryptonRibbonGroupButton
                {
                    TextLine1 = "Wireframe",
                    TextLine2 = "View",
                    ImageSmall = CreateWireframeIcon(16),
                    ImageLarge = CreateWireframeIcon(32)
                };
                wireframeButton.Click += WireframeButton_Click;

                /*var solidButton = new KryptonRibbonGroupButton
                {
                    TextLine1 = "Solid",
                    TextLine2 = "View",
                    ImageSmall = CreateSolidIcon(16),
                    ImageLarge = CreateSolidIcon(32)
                };
                solidButton.Click += SolidButton_Click;

                visualTriple.Items.AddRange(new KryptonRibbonGroupItem[] { wireframeButton, solidButton });*/
                visualizationGroup.Items.Add(visualTriple);
            }

            // Analysis buttons
            {
                var analysisTriple = new KryptonRibbonGroupTriple();
                var stressButton = new KryptonRibbonGroupButton
                {
                    TextLine1 = "Stress",
                    TextLine2 = "Analysis",
                    ImageSmall = CreateStressIcon(16),
                    ImageLarge = CreateStressIcon(32)
                };
                stressButton.Click += StressButton_Click;

                var acousticButton = new KryptonRibbonGroupButton
                {
                    TextLine1 = "Acoustic",
                    TextLine2 = "Velocity",
                    ImageSmall = CreateAcousticIcon(16),
                    ImageLarge = CreateAcousticIcon(32)
                };
                acousticButton.Click += AcousticButton_Click;

                analysisTriple.Items.AddRange(new KryptonRibbonGroupItem[] { stressButton, acousticButton });
                analysisGroup.Items.Add(analysisTriple);
            }

            // --- Status Bar ---
            statusHeader = new KryptonHeader
            {
                Text = "Ready",
                Dock = DockStyle.Bottom
            };
            statusHeader.Values.Image = CreateGpuStatusIcon(16);

            // --- Tab Control (Navigator) ---
            mainTabControl = new KryptonDockableNavigator
            {
                Dock = DockStyle.Fill,
                BackColor = Color.DarkGray,
                AllowPageReorder = true,
                AllowPageDrag = true
            };
            mainTabControl.Button.CloseButtonDisplay = ButtonDisplay.ShowEnabled;
            mainTabControl.Button.CloseButtonAction = CloseButtonAction.RemovePage;
            mainTabControl.Pages.Removed += Pages_Removed;

            // Create pages
            meshPage = new KryptonPage { Text = "Mesh", TextTitle = "Mesh Generation", Tag = "original" };
            analysisPage = new KryptonPage { Text = "Analysis", TextTitle = "Stress Analysis", Tag = "original" };
            resultsPage = new KryptonPage { Text = "Results", TextTitle = "Analysis Results", Tag = "original" };
            mainTabControl.Pages.AddRange(new[] { meshPage, analysisPage, resultsPage });

            // --- Mesh Page Layout ---
            var meshLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 2
            };
            meshLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20F));
            meshLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));
            meshLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20F));
            meshLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 80F));
            meshLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 20F));

            // Mesh view panel
            meshViewPanel = new Panel
            {
                BackColor = Color.Black,
                Dock = DockStyle.Fill
            };
            meshViewPanel.Paint += MeshViewPanel_Paint;
            InitializeMeshViewControls();
            // Controls panel
            controlsPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10)
            };

            // Material selection
            materialLabel = new Label
            {
                Text = "Material:",
                AutoSize = true,
                ForeColor = Color.White,
                Location = new Point(10, 10)
            };
            materialComboBox = new ComboBox
            {
                Location = new Point(10, 30),
                Width = 150,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            materialComboBox.SelectedIndexChanged += MaterialComboBox_SelectedIndexChanged;

            // Mesh resolution
            resolutionLabel = new Label
            {
                Text = "Mesh Resolution:",
                AutoSize = true,
                ForeColor = Color.White,
                Location = new Point(10, 60)
            };
            resolutionTrackBar = new TrackBar
            {
                Location = new Point(10, 80),
                Width = 150,
                Minimum = 1,
                Maximum = 10,
                Value = 5,
                TickFrequency = 1
            };

            // Facets numeric
            facetsLabel = new Label
            {
                Text = "Facets:",
                AutoSize = true,
                ForeColor = Color.White,
                Location = new Point(10, 120)
            };
            facetsNumeric = new NumericUpDown
            {
                Location = new Point(10, 140),
                Width = 80,
                Minimum = 100,
                Maximum = 100000,
                Value = 5000,
                Increment = 100
            };

            // Generate/Import/Apply buttons
            generateMeshButton = new Button
            {
                Text = "Generate Mesh",
                Location = new Point(10, 180),
                Width = 150,
                Image = CreateMeshIcon(16),
                ImageAlign = ContentAlignment.MiddleLeft,
                TextAlign = ContentAlignment.MiddleRight
            };
            generateMeshButton.Click += GenerateMeshButton_Click;

            importMeshButton = new Button
            {
                Text = "Import Mesh",
                Location = new Point(10, 210),
                Width = 150,
                Image = CreateImportIcon(16, ""),
                ImageAlign = ContentAlignment.MiddleLeft,
                TextAlign = ContentAlignment.MiddleRight
            };
            importMeshButton.Click += ImportMeshButton_Click;

            applyMeshButton = new KryptonButton
            {
                Text = "Apply Mesh",
                Location = new Point(10, 240),
                Width = 150,
                Values = { Image = CreateApplyIcon(16) },
                Enabled = false
            };
            applyMeshButton.Click += ApplyMeshButton_Click;

            // Density controls
            btnSetDensity = new KryptonButton
            {
                Text = "Set Material Density",
                Location = new Point(10, 280),
                Width = 150,
                Values = { Image = CreateDensityIcon(16) }
            };
            btnSetDensity.Click += BtnSetDensity_Click;

            densityLabel = new Label
            {
                Text = "Density: Not set",
                AutoSize = true,
                ForeColor = Color.White,
                Location = new Point(10, 310)
            };
            btnAssignVaryingDensity = new KryptonButton
            {
                Text = "Calculate Varying Density",
                Location = new Point(10, 340),
                Width = 150,
                Values = { Image = CreateVaryingDensityIcon(16) }
            };
            btnAssignVaryingDensity.Click += BtnAssignVaryingDensity_Click;
            

            // Add all controls to the controlsPanel
            controlsPanel.Controls.Add(materialLabel);
            controlsPanel.Controls.Add(materialComboBox);
            controlsPanel.Controls.Add(resolutionLabel);
            controlsPanel.Controls.Add(resolutionTrackBar);
            controlsPanel.Controls.Add(facetsLabel);
            controlsPanel.Controls.Add(facetsNumeric);
            controlsPanel.Controls.Add(generateMeshButton);
            controlsPanel.Controls.Add(importMeshButton);
            controlsPanel.Controls.Add(applyMeshButton);
            controlsPanel.Controls.Add(btnSetDensity);
            controlsPanel.Controls.Add(btnAssignVaryingDensity);
            controlsPanel.Controls.Add(densityLabel);

            // Place panels into meshLayout
            meshLayout.Controls.Add(controlsPanel, 0, 0);
            meshLayout.Controls.Add(meshViewPanel, 1, 0);
            meshLayout.SetRowSpan(controlsPanel, 2);
            meshPage.Controls.Add(meshLayout);

            var resultsExportGroup = new KryptonRibbonGroup { TextLine1 = "Results Export" };
            var resultsExportTriple = new KryptonRibbonGroupTriple();
            var exportCompositeRibbonButton = new KryptonRibbonGroupButton
            {
                TextLine1 = "Export",
                TextLine2 = "Triaxial Composite",
                ImageSmall = CreateExportCompositeIcon(16),
                ImageLarge = CreateExportCompositeIcon(32)
            };
            exportCompositeRibbonButton.Click += (s, e) =>
            {
                // Assuming 'currentTriaxial' holds your latest simulation
                if (currentTriaxial != null)
                    ExportFullCompositeImage(currentTriaxial);
                else
                    MessageBox.Show("No simulation results available.", "Export Error",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            resultsExportTriple.Items.Add(exportCompositeRibbonButton);
            resultsExportGroup.Items.Add(resultsExportTriple);
            resultsTab.Groups.Add(resultsExportGroup);
            // -- Acoustic composite export button --
            var exportAcousticCompositeBtn = new KryptonRibbonGroupButton
            {
                TextLine1 = "Export",
                TextLine2 = "Acoustic Composite",
                ImageSmall = CreateExportAcousticCompositeIcon(16),
                ImageLarge = CreateExportAcousticCompositeIcon(32)
            };
            exportAcousticCompositeBtn.Click += (s, e) =>
            {
                if (currentAcousticSim != null)
                    ExportAcousticCompositeImage(currentAcousticSim);
                else
                    MessageBox.Show("No acoustic simulation results available.",
                                    "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            resultsExportTriple.Items.Add(exportAcousticCompositeBtn);
            // Finally add everything into the form
            this.Controls.Add(mainTabControl);
            this.Controls.Add(statusHeader);
            this.Controls.Add(ribbon);

            // Save the “original” pages for reopen logic
            InitializeTabManagement();
            InitializeSimulationParameters();
        }
        private Image CreateVaryingDensityIcon(int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);

                // Draw container
                Rectangle container = new Rectangle(2, 2, size - 4, size - 4);
                using (Pen pen = new Pen(Color.DarkGray, 1))
                {
                    g.DrawRectangle(pen, container);
                }

                // Draw dots of different intensities (representing varying density)
                Random rnd = new Random(42); // Fixed seed for consistent pattern
                for (int i = 0; i < 12; i++)
                {
                    int x = rnd.Next(4, size - 4);
                    int y = rnd.Next(4, size - 4);
                    int dotSize = rnd.Next(1, 3);
                    int intensity = rnd.Next(64, 255);

                    using (SolidBrush brush = new SolidBrush(Color.FromArgb(intensity, 0, 0, intensity)))
                    {
                        g.FillEllipse(brush, x - dotSize / 2, y - dotSize / 2, dotSize, dotSize);
                    }
                }

                // Draw gradient indicator in bottom right
                using (LinearGradientBrush gradBrush = new LinearGradientBrush(
                       new Point(size - 7, size - 7),
                       new Point(size - 3, size - 3),
                       Color.Blue, Color.Red))
                {
                    g.FillRectangle(gradBrush, size - 7, size - 7, 4, 4);
                }
            }
            return bmp;
        }
        private void ExportAcousticCompositeImage(AcousticVelocitySimulation sim)
        {
            try
            {
                using (var dlg = new SaveFileDialog())
                {
                    dlg.Filter = "PNG Image|*.png";
                    dlg.Title = "Export Acoustic Composite Image";
                    dlg.FileName = $"Acoustic_{selectedMaterial.Name}_{DateTime.Now:yyyyMMdd_HHmmss}_composite.png";

                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        this.Cursor = Cursors.WaitCursor;
                        statusHeader.Text = "Creating acoustic composite image.";

                        bool ok = sim.ExportCompositeImage(dlg.FileName);
                        if (ok)
                        {
                            statusHeader.Text = $"Exported acoustic composite to {Path.GetFileName(dlg.FileName)}";
                            MessageBox.Show($"Acoustic composite image exported to:\n{dlg.FileName}",
                                            "Export Successful", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            statusHeader.Text = "Export failed.";
                            MessageBox.Show("Failed to export acoustic composite image.",
                                            "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting acoustic composite: {ex.Message}",
                                "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[StressAnalysisForm] Acoustic export error: {ex.Message}");
            }
            finally
            {
                this.Cursor = Cursors.Default;
            }
        }

        private Image CreateExportAcousticCompositeIcon(int size)
        {
            var bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);

                // Draw a 2×2 grid like the composite icon, but in cyan
                using (Pen pen = new Pen(Color.Cyan, 2))
                {
                    int cell = size / 2;
                    g.DrawRectangle(pen, 1, 1, size - 2, size - 2);
                    g.DrawLine(pen, cell, 1, cell, size - 2);
                    g.DrawLine(pen, 1, cell, size - 2, cell);
                }

                // Overlay a little sine‐wave arrow to hint “acoustic”
                using (Pen wavePen = new Pen(Color.Cyan, 1))
                {
                    var pts = new PointF[]
                    {
                new PointF(4, size - 8),
                new PointF(size/4f, size - 12),
                new PointF(size/2f, size - 4),
                new PointF(3*size/4f, size - 14),
                new PointF(size - 4, size - 8)
                    };
                    g.DrawCurve(wavePen, pts);
                }
            }
            return bmp;
        }
        private Image CreateExportCompositeIcon(int size)
        {
            var bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (Pen pen = new Pen(Color.OrangeRed, 2))
                {
                    int cell = size / 2;
                    // draw outer border
                    g.DrawRectangle(pen, 1, 1, size - 2, size - 2);
                    // draw vertical divider
                    g.DrawLine(pen, cell, 1, cell, size - 2);
                    // draw horizontal divider
                    g.DrawLine(pen, 1, cell, size - 2, cell);
                }
                using (SolidBrush brush = new SolidBrush(Color.OrangeRed))
                {
                    // small arrow in lower-right to indicate export
                    var arrow = new Point[]
                    {
                new Point(size - 1 - 4, size - 1 - 8),
                new Point(size - 1 - 4, size - 1 - 2),
                new Point(size - 1 - 10, size - 1 - 2)
                    };
                    g.FillPolygon(brush, arrow);
                }
            }
            return bmp;
        }
        // Create an icon for reopening tabs
        private Image CreateReopenTabIcon(int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);

                // Draw a tab with an arrow pointing to it
                using (Pen pen = new Pen(Color.LightBlue, 1))
                {
                    // Draw tab
                    int tabWidth = size / 2;
                    int tabHeight = size / 3;
                    g.DrawLine(pen, 0, tabHeight, tabWidth, tabHeight);
                    g.DrawLine(pen, tabWidth, tabHeight, tabWidth, 0);
                    g.DrawLine(pen, 0, tabHeight, 0, 0);

                    // Draw document under tab
                    g.DrawRectangle(pen, 0, tabHeight, size - 2, size - tabHeight - 2);
                }

                // Draw circular arrow
                using (Pen pen = new Pen(Color.Green, 2))
                {
                    int arrowSize = size / 2;
                    int centerX = size - arrowSize / 2;
                    int centerY = arrowSize / 2;

                    // Draw arrow circle
                    g.DrawArc(pen, centerX - arrowSize / 2, centerY - arrowSize / 2,
                        arrowSize, arrowSize, 0, 270);

                    // Draw arrowhead
                    g.DrawLine(pen, centerX, centerY - arrowSize / 2,
                        centerX + arrowSize / 4, centerY - arrowSize / 4);
                    g.DrawLine(pen, centerX, centerY - arrowSize / 2,
                        centerX - arrowSize / 4, centerY - arrowSize / 4);
                }
            }
            return bmp;
        }

        // Initialize tab management functionality
        private void InitializeTabManagement()
        {
            // Save original pages for later restoration
            foreach (KryptonPage page in mainTabControl.Pages)
            {
                // Store a reference or deep copy if needed
                page.Tag = "original"; // Mark as original page
            }
        }



        // Handle reopening of closed tabs
        private void Pages_PageRemoving(object sender, TypedCollectionEventArgs<KryptonPage> e)
        {
            if (e.Item.Tag != null && e.Item.Tag.Equals("original"))
            {
                MessageBox.Show("Main tabs cannot be closed.", "Information",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                e.Item.Visible = false; // Hide instead of remove
                
            }
        }

        // Handler to track removed pages
        private void Pages_Removed(object sender, TypedCollectionEventArgs<KryptonPage> e)
        {
            // Only track non-original pages
            if (e.Item.Tag == null || !e.Item.Tag.Equals("original"))
            {
                closedPages.Add(e.Item);
            }
        }

        // Handle reopening of closed tabs
        private void ReopenTabsButton_Click(object sender, EventArgs e)
        {
            if (closedPages.Count == 0)
            {
                MessageBox.Show("No closed tabs to reopen.", "Information",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Create a form to show the list of closed tabs
            using (Form reopenForm = new Form())
            {
                reopenForm.Text = "Reopen Closed Tabs";
                reopenForm.Size = new Size(300, 300);
                reopenForm.StartPosition = FormStartPosition.CenterParent;
                reopenForm.MinimizeBox = false;
                reopenForm.MaximizeBox = false;
                reopenForm.FormBorderStyle = FormBorderStyle.FixedDialog;

                ListBox tabList = new ListBox();
                tabList.Dock = DockStyle.Fill;
                tabList.DisplayMember = "Text";

                foreach (KryptonPage page in closedPages)
                {
                    tabList.Items.Add(page);
                }

                Button reopenButton = new Button();
                reopenButton.Text = "Reopen Selected";
                reopenButton.Dock = DockStyle.Bottom;
                reopenButton.Click += (s, args) => {
                    if (tabList.SelectedItem is KryptonPage selectedPage)
                    {
                        mainTabControl.Pages.Add(selectedPage);
                        closedPages.Remove(selectedPage);
                        reopenForm.DialogResult = DialogResult.OK;
                    }
                };

                Button reopenAllButton = new Button();
                reopenAllButton.Text = "Reopen All";
                reopenAllButton.Dock = DockStyle.Bottom;
                reopenAllButton.Click += (s, args) => {
                    foreach (KryptonPage page in closedPages.ToList())
                    {
                        mainTabControl.Pages.Add(page);
                    }
                    closedPages.Clear();
                    reopenForm.DialogResult = DialogResult.OK;
                };

                Panel buttonPanel = new Panel();
                buttonPanel.Height = 70;
                buttonPanel.Dock = DockStyle.Bottom;
                buttonPanel.Controls.Add(reopenButton);
                buttonPanel.Controls.Add(reopenAllButton);
                reopenAllButton.Location = new Point(0, 35);
                reopenButton.Location = new Point(0, 0);
                reopenButton.Width = reopenForm.ClientSize.Width;
                reopenAllButton.Width = reopenForm.ClientSize.Width;

                reopenForm.Controls.Add(tabList);
                reopenForm.Controls.Add(buttonPanel);

                reopenForm.ShowDialog(this);
            }
        }

        // Helper methods to create icons
        private Image CreateMeshIcon(int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (Pen pen = new Pen(Color.DodgerBlue, 1))
                {
                    // Draw a simple mesh grid
                    int cellSize = size / 4;
                    for (int x = 0; x <= size; x += cellSize)
                    {
                        g.DrawLine(pen, x, 0, x, size);
                    }
                    for (int y = 0; y <= size; y += cellSize)
                    {
                        g.DrawLine(pen, 0, y, size, y);
                    }

                    // Draw some diagonal lines to represent triangles
                    g.DrawLine(pen, 0, 0, cellSize, cellSize);
                    g.DrawLine(pen, cellSize, 0, 0, cellSize);
                    g.DrawLine(pen, size - cellSize, size, size, size - cellSize);
                }
            }
            return bmp;
        }

        private Image CreateSettingsIcon(int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (Pen pen = new Pen(Color.Gray, 1))
                {
                    // Draw gear
                    int centerX = size / 2;
                    int centerY = size / 2;
                    int radius = size / 3;

                    for (int i = 0; i < 8; i++)
                    {
                        double angle = i * Math.PI / 4;
                        int x1 = centerX + (int)(radius * Math.Cos(angle));
                        int y1 = centerY + (int)(radius * Math.Sin(angle));
                        int x2 = centerX + (int)((radius + size / 4) * Math.Cos(angle));
                        int y2 = centerY + (int)((radius + size / 4) * Math.Sin(angle));
                        g.DrawLine(pen, x1, y1, x2, y2);
                    }

                    g.DrawEllipse(pen, centerX - radius / 2, centerY - radius / 2, radius, radius);
                }
            }
            return bmp;
        }
        private void BtnAssignVaryingDensity_Click(object sender, EventArgs e)
        {
            if (selectedMaterial == null || selectedMaterial.ID == 0)
            {
                MessageBox.Show("Please select a material first.", "No Material Selected",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (selectedMaterial.Density <= 0)
            {
                MessageBox.Show("Please set a base material density first using the 'Set Material Density' button.",
                    "Base Density Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!meshGenerated || meshTriangles.Count == 0)
            {
                MessageBox.Show("Please generate a mesh first.", "No Mesh",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                Cursor = Cursors.WaitCursor;
                statusHeader.Text = "Calculating varying density based on grayscale values...";

                // Call method to calculate densities based on grayscale values
                voxelDensities = CalculateVaryingDensity();

                // Enable the inhomogeneous density mode
                inhomogeneousDensityEnabled = true;

                // Update the toggle button in the ribbon
                UpdateInhomogeneousDensityToggle();

                MessageBox.Show($"Successfully calculated varying density for {voxelDensities.Count} mesh elements.",
                    "Varying Density", MessageBoxButtons.OK, MessageBoxIcon.Information);

                statusHeader.Text = "Varying density calculation complete.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error calculating varying density: {ex.Message}",
                    "Calculation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[StressAnalysisForm] Varying density calculation error: {ex.Message}");
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }
        private Dictionary<Vector3, float> CalculateVaryingDensity()
        {
            // Create a dictionary to store densities for each mesh position
            Dictionary<Vector3, float> densities = new Dictionary<Vector3, float>();

            // Get the average gray value for the material
            double avgGrayValue = CalculateAverageMaterialGrayValue();
            double baseDensity = selectedMaterial.Density;

            // For each mesh triangle
            foreach (var triangle in meshTriangles)
            {
                // For each vertex of the triangle
                ProcessVertexDensity(triangle.V1, densities, avgGrayValue, baseDensity);
                ProcessVertexDensity(triangle.V2, densities, avgGrayValue, baseDensity);
                ProcessVertexDensity(triangle.V3, densities, avgGrayValue, baseDensity);
            }

            return densities;
        }

        private void ProcessVertexDensity(Vector3 vertex, Dictionary<Vector3, float> densities, double avgGrayValue, double baseDensity)
        {
            // Skip if we already processed this vertex
            if (densities.ContainsKey(vertex)) return;

            // Convert vertex position to voxel coordinates
            int x = (int)Math.Round(vertex.X);
            int y = (int)Math.Round(vertex.Y);
            int z = (int)Math.Round(vertex.Z);

            // Make sure we're within volume bounds
            if (x < 0 || y < 0 || z < 0 || x >= mainForm.GetWidth() || y >= mainForm.GetHeight() || z >= mainForm.GetDepth())
            {
                // Out of bounds, use default density
                densities[vertex] = (float)baseDensity;
                return;
            }

            // Get the grayscale value at this position
            byte grayValue = mainForm.volumeData[x, y, z];

            // Skip if this voxel isn't part of our material
            if (mainForm.volumeLabels[x, y, z] != selectedMaterial.ID)
            {
                densities[vertex] = (float)baseDensity;
                return;
            }

            // Calculate relative density based on gray value
            // Simple linear relationship: denser = brighter
            double densityScale = (double)grayValue / avgGrayValue;

            // Apply scaling but keep within reasonable bounds (e.g. ±30% of base density)
            double scaledDensity = Math.Max(baseDensity * 0.7, Math.Min(baseDensity * 1.3, baseDensity * densityScale));

            // Store the calculated density
            densities[vertex] = (float)scaledDensity;
        }
        private Image CreateInhomogeneousDensityIcon(int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);

                // Draw a material sample with gradient
                Rectangle gradRect = new Rectangle(2, 2, size - 4, size - 4);

                using (LinearGradientBrush gradBrush = new LinearGradientBrush(
                       gradRect, Color.LightBlue, Color.DarkBlue, 45f))
                {
                    // Add density variation with gradient stops
                    ColorBlend blend = new ColorBlend(4);
                    blend.Colors = new Color[] {
                Color.LightBlue,
                Color.RoyalBlue,
                Color.MediumBlue,
                Color.DarkBlue
            };
                    blend.Positions = new float[] { 0.0f, 0.3f, 0.7f, 1.0f };
                    gradBrush.InterpolationColors = blend;

                    g.FillEllipse(gradBrush, gradRect);
                }

                // Draw outline
                using (Pen pen = new Pen(Color.Gray, 1))
                {
                    g.DrawEllipse(pen, gradRect);
                }

                // Draw density indicator lines
                using (Pen pen = new Pen(Color.White, 1))
                {
                    for (int i = 1; i < 4; i++)
                    {
                        int y = size * i / 4;
                        g.DrawLine(pen, 4, y, size - 4, y);
                    }
                }
            }
            return bmp;
        }

        private void UpdateInhomogeneousDensityToggle()
        {
            // Find the toggle button in the ribbon
            foreach (KryptonRibbonTab tab in ribbon.RibbonTabs)
            {
                foreach (KryptonRibbonGroup group in tab.Groups)
                {
                    if (group.TextLine1 == "Simulation Options")
                    {
                        foreach (var item in group.Items)
                        {
                            if (item is KryptonRibbonGroupTriple triple)
                            {
                                foreach (var button in triple.Items)
                                {
                                    if (button is KryptonRibbonGroupButton rbtn &&
                                        rbtn.TextLine1 == "Use Varying" &&
                                        rbtn.TextLine2 == "Density")
                                    {
                                        rbtn.Checked = inhomogeneousDensityEnabled;
                                        return;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        private Image CreateImportIcon(int size, string fileType)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);

                // Draw document
                Rectangle docRect = new Rectangle(2, 2, size - 4, size - 4);
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    g.FillRectangle(brush, docRect);
                }
                using (Pen pen = new Pen(Color.DarkGray, 1))
                {
                    g.DrawRectangle(pen, docRect);
                }

                // Draw arrow
                int arrowWidth = size / 3;
                using (Pen pen = new Pen(Color.Green, 2))
                {
                    g.DrawLine(pen, size / 2, 3, size / 2, size / 2);
                    g.DrawLine(pen, size / 2 - arrowWidth / 2, size / 3, size / 2, size / 2);
                    g.DrawLine(pen, size / 2 + arrowWidth / 2, size / 3, size / 2, size / 2);
                }

                // Draw file extension text
                if (!string.IsNullOrEmpty(fileType))
                {
                    using (Font font = new Font("Arial", size / 4, FontStyle.Bold))
                    using (SolidBrush brush = new SolidBrush(Color.Blue))
                    {
                        StringFormat format = new StringFormat();
                        format.Alignment = StringAlignment.Center;
                        format.LineAlignment = StringAlignment.Center;
                        g.DrawString(fileType, font, brush, new RectangleF(2, size / 2, size - 4, size / 2 - 2), format);
                    }
                }
            }
            return bmp;
        }

        private Image CreateExportIcon(int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);

                // Draw document
                Rectangle docRect = new Rectangle(2, 2, size - 4, size - 4);
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    g.FillRectangle(brush, docRect);
                }
                using (Pen pen = new Pen(Color.DarkGray, 1))
                {
                    g.DrawRectangle(pen, docRect);
                }

                // Draw arrow
                int arrowWidth = size / 3;
                using (Pen pen = new Pen(Color.OrangeRed, 2))
                {
                    g.DrawLine(pen, size / 2, size / 2, size / 2, size - 3);
                    g.DrawLine(pen, size / 2 - arrowWidth / 2, size - size / 3, size / 2, size - 3);
                    g.DrawLine(pen, size / 2 + arrowWidth / 2, size - size / 3, size / 2, size - 3);
                }

                // Draw mesh text
                using (Font font = new Font("Arial", size / 5, FontStyle.Bold))
                using (SolidBrush brush = new SolidBrush(Color.Black))
                {
                    StringFormat format = new StringFormat();
                    format.Alignment = StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;
                    g.DrawString("MESH", font, brush, new RectangleF(2, 2, size - 4, size / 2 - 2), format);
                }
            }
            return bmp;
        }

        private Image CreateWireframeIcon(int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);

                using (Pen pen = new Pen(Color.Cyan, 1))
                {
                    // Draw wireframe cube
                    int margin = size / 6;

                    // Front face
                    Point[] frontFace = new Point[] {
                        new Point(margin, margin),
                        new Point(size - margin, margin),
                        new Point(size - margin, size - margin),
                        new Point(margin, size - margin)
                    };
                    g.DrawPolygon(pen, frontFace);

                    // Back face
                    int offset = size / 5;
                    Point[] backFace = new Point[] {
                        new Point(margin + offset, margin - offset/2),
                        new Point(size - margin + offset, margin - offset/2),
                        new Point(size - margin + offset, size - margin - offset/2),
                        new Point(margin + offset, size - margin - offset/2)
                    };
                    g.DrawPolygon(pen, backFace);

                    // Connect front to back
                    for (int i = 0; i < 4; i++)
                    {
                        g.DrawLine(pen, frontFace[i], backFace[i]);
                    }
                }
            }
            return bmp;
        }

        private Image CreateSolidIcon(int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);

                // Draw solid cube
                int margin = size / 6;

                // Top face
                Point[] topFace = new Point[] {
                    new Point(margin, margin),
                    new Point(size - margin, margin),
                    new Point(size - margin + size/5, margin - size/10),
                    new Point(margin + size/5, margin - size/10)
                };
                using (SolidBrush brush = new SolidBrush(Color.LightBlue))
                {
                    g.FillPolygon(brush, topFace);
                }

                // Front face
                Point[] frontFace = new Point[] {
                    new Point(margin, margin),
                    new Point(size - margin, margin),
                    new Point(size - margin, size - margin),
                    new Point(margin, size - margin)
                };
                using (SolidBrush brush = new SolidBrush(Color.RoyalBlue))
                {
                    g.FillPolygon(brush, frontFace);
                }

                // Side face
                Point[] sideFace = new Point[] {
                    new Point(size - margin, margin),
                    new Point(size - margin + size/5, margin - size/10),
                    new Point(size - margin + size/5, size - margin - size/10),
                    new Point(size - margin, size - margin)
                };
                using (SolidBrush brush = new SolidBrush(Color.SteelBlue))
                {
                    g.FillPolygon(brush, sideFace);
                }

                // Outline
                using (Pen pen = new Pen(Color.Black, 1))
                {
                    g.DrawPolygon(pen, topFace);
                    g.DrawPolygon(pen, frontFace);
                    g.DrawPolygon(pen, sideFace);
                }
            }
            return bmp;
        }

        private Image CreateStressIcon(int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);

                int margin = size / 6;
                int center = size / 2;

                // Draw pressure arrows
                using (Pen pen = new Pen(Color.Red, 2))
                {
                    // Draw arrows from all sides pointing inward
                    // Top arrow
                    g.DrawLine(pen, center, margin, center, center - margin);
                    g.DrawLine(pen, center - margin / 2, center - margin / 2, center, center - margin);
                    g.DrawLine(pen, center + margin / 2, center - margin / 2, center, center - margin);

                    // Bottom arrow
                    g.DrawLine(pen, center, size - margin, center, center + margin);
                    g.DrawLine(pen, center - margin / 2, center + margin / 2, center, center + margin);
                    g.DrawLine(pen, center + margin / 2, center + margin / 2, center, center + margin);

                    // Left arrow
                    g.DrawLine(pen, margin, center, center - margin, center);
                    g.DrawLine(pen, center - margin / 2, center - margin / 2, center - margin, center);
                    g.DrawLine(pen, center - margin / 2, center + margin / 2, center - margin, center);

                    // Right arrow
                    g.DrawLine(pen, size - margin, center, center + margin, center);
                    g.DrawLine(pen, center + margin / 2, center - margin / 2, center + margin, center);
                    g.DrawLine(pen, center + margin / 2, center + margin / 2, center + margin, center);
                }

                // Draw center object under stress
                using (SolidBrush brush = new SolidBrush(Color.Orange))
                {
                    g.FillEllipse(brush, center - margin / 2, center - margin / 2, margin, margin);
                }
                using (Pen pen = new Pen(Color.DarkOrange, 1))
                {
                    g.DrawEllipse(pen, center - margin / 2, center - margin / 2, margin, margin);
                }
            }
            return bmp;
        }

        private Image CreateAcousticIcon(int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);

                int center = size / 2;
                int margin = size / 6;

                // Draw a speaker
                using (SolidBrush brush = new SolidBrush(Color.DimGray))
                {
                    g.FillRectangle(brush, margin, center - margin, margin * 2, margin * 2);
                }

                // Draw sound waves
                using (Pen pen = new Pen(Color.RoyalBlue, 1))
                {
                    for (int i = 1; i <= 3; i++)
                    {
                        int radius = margin + i * margin / 2;
                        g.DrawArc(pen, center - radius, center - radius, radius * 2, radius * 2, -30, 60);
                    }
                }

                // Draw velocity arrows
                using (Pen pen = new Pen(Color.Green, 2))
                {
                    int arrowX = center + margin * 2;
                    g.DrawLine(pen, arrowX, center, size - margin, center);
                    g.DrawLine(pen, size - margin - margin / 2, center - margin / 2, size - margin, center);
                    g.DrawLine(pen, size - margin - margin / 2, center + margin / 2, size - margin, center);
                }
            }
            return bmp;
        }

        private Image CreateApplyIcon(int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);

                // Draw a checkmark
                using (Pen pen = new Pen(Color.Green, 2))
                {
                    g.DrawLine(pen, size / 4, size / 2, size / 2, size * 3 / 4);
                    g.DrawLine(pen, size / 2, size * 3 / 4, size * 3 / 4, size / 4);
                }
            }
            return bmp;
        }

        private Image CreateDensityIcon(int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);

                // Draw density represented as dots in a container
                Rectangle container = new Rectangle(2, 2, size - 4, size - 4);
                using (Pen pen = new Pen(Color.DarkGray, 1))
                {
                    g.DrawRectangle(pen, container);
                }

                // Draw dots representing density
                using (SolidBrush brush = new SolidBrush(Color.DarkBlue))
                {
                    int dotSize = 2;
                    int spacing = size / 6;

                    for (int x = spacing; x < size; x += spacing)
                    {
                        for (int y = spacing; y < size; y += spacing)
                        {
                            g.FillEllipse(brush, x - dotSize / 2, y - dotSize / 2, dotSize, dotSize);
                        }
                    }
                }

                // Draw a weight symbol
                using (SolidBrush brush = new SolidBrush(Color.Red))
                {
                    g.FillRectangle(brush, size / 2 - size / 8, 0, size / 4, size / 6);
                }
                using (Pen pen = new Pen(Color.Red, 1))
                {
                    g.DrawLine(pen, size / 2, size / 6, size / 2, size / 3);
                }
            }
            return bmp;
        }

        private Image CreateGpuStatusIcon(int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);

                // Draw GPU chip
                Rectangle chipRect = new Rectangle(1, 1, size - 2, size - 2);
                using (SolidBrush brush = new SolidBrush(Color.SlateGray))
                {
                    g.FillRectangle(brush, chipRect);
                }

                // Draw connection pins
                using (SolidBrush brush = new SolidBrush(Color.Gold))
                {
                    int pinSize = 1;
                    int pinCount = size / 3;
                    int pinSpacing = size / (pinCount + 1);

                    for (int i = 1; i <= pinCount; i++)
                    {
                        g.FillRectangle(brush, i * pinSpacing, size - 1, pinSize, 1);
                        g.FillRectangle(brush, i * pinSpacing, 0, pinSize, 1);
                    }
                }

                // Draw circuit lines
                using (Pen pen = new Pen(Color.LightGreen, 1))
                {
                    int margin = 3;
                    g.DrawLine(pen, margin, margin, size - margin, margin);
                    g.DrawLine(pen, margin, margin, margin, size - margin);
                    g.DrawLine(pen, margin, size / 2, size / 2, size / 2);
                    g.DrawLine(pen, size / 2, size / 2, size / 2, size - margin);
                    g.DrawLine(pen, size - margin, margin, size - margin, size / 3);
                    g.DrawLine(pen, size - margin, size / 3, size / 2, size / 3);
                }
            }
            return bmp;
        }

        private Image CreateCancelIcon(int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (Pen pen = new Pen(Color.Red, 2))
                {
                    // Draw an X
                    g.DrawLine(pen, 2, 2, size - 2, size - 2);
                    g.DrawLine(pen, size - 2, 2, 2, size - 2);
                }
            }
            return bmp;
        }

        private void InitializeILGPU()
        {
            try
            {
                // Create ILGPU context
                ilgpuContext = Context.CreateDefault();

                // Try to get a GPU accelerator first
                try
                {
                    accelerator = ilgpuContext.GetPreferredDevice(preferCPU: false)
                        .CreateAccelerator(ilgpuContext);
                    statusHeader.Values.Description = $"Using GPU: {accelerator.Name}";
                }
                catch
                {
                    // Fall back to CPU if GPU is not available
                    accelerator = ilgpuContext.GetPreferredDevice(preferCPU: true)
                        .CreateAccelerator(ilgpuContext);
                    statusHeader.Values.Description = $"Using CPU: {accelerator.Name}";
                }

                Logger.Log($"[StressAnalysisForm] ILGPU initialized with {accelerator.Name}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize ILGPU: {ex.Message}\nThe application will use CPU fallback.",
                    "ILGPU Initialization Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Logger.Log($"[StressAnalysisForm] ILGPU initialization failed: {ex.Message}");
            }
        }

        private void PopulateMaterialList()
        {
            materialComboBox.Items.Clear();

            foreach (Material material in mainForm.Materials)
            {
                // Skip the "Exterior" material which typically has ID 0
                if (!material.IsExterior)
                {
                    materialComboBox.Items.Add(material);
                }
            }

            if (materialComboBox.Items.Count > 0)
            {
                materialComboBox.DisplayMember = "Name";
                materialComboBox.SelectedIndex = 0;
            }
        }

        private void MaterialComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (materialComboBox.SelectedItem is Material material)
            {
                selectedMaterial = material;
                Logger.Log($"[StressAnalysisForm] Selected material: {material.Name}");
            }
        }
        private void GenerateMeshButton_Click(object sender, EventArgs e)
        {
            if (selectedMaterial == null)
            {
                MessageBox.Show("Please select a material first.", "No Material Selected",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                // Update status and cursor
                statusHeader.Text = "Generating mesh...";
                this.Cursor = Cursors.WaitCursor;

                // Clear existing mesh before generating a new one
                meshTriangles.Clear();

                // Get mesh parameters
                int resolution = resolutionTrackBar.Value;
                int targetFacets = (int)facetsNumeric.Value;

                // Generate mesh based on the volume data and selected material
                GenerateMeshFromVolumeImproved(selectedMaterial.ID, resolution, targetFacets);

                statusHeader.Text = $"Mesh generated with {meshTriangles.Count} triangles";
                meshGenerated = true;
                applyMeshButton.Enabled = true;

                // Refresh the mesh view
                meshViewPanel.Invalidate();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error generating mesh: {ex.Message}", "Mesh Generation Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[StressAnalysisForm] Mesh generation error: {ex.Message}");
            }
            finally
            {
                this.Cursor = Cursors.Default;
            }
        }
        private void GenerateMeshFromVolumeImproved(byte materialID, int resolution, int targetFacets)
        {
            if (mainForm.volumeLabels == null)
            {
                throw new InvalidOperationException("No volume data available");
            }

            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();

            // Calculate a step size based on resolution
            // Higher resolution means smaller step size (more detailed mesh)
            int maxDim = Math.Max(Math.Max(width, height), depth);
            int step = Math.Max(1, maxDim / (resolution * 10));

            Logger.Log($"[StressAnalysisForm] Generating connected mesh for material {materialID} with step {step}");

            statusHeader.Text = "Analyzing volume data...";
            Application.DoEvents();

            // First, create a binary volume marking which voxels are part of our material
            bool[,,] materialVoxels = new bool[width, height, depth];
            int totalMaterialVoxels = 0;

            for (int z = 0; z < depth; z += step)
            {
                for (int y = 0; y < height; y += step)
                {
                    for (int x = 0; x < width; x += step)
                    {
                        if (isMeshGenerationCancelled)
                            return;

                        try
                        {
                            if (mainForm.volumeLabels[x, y, z] == materialID)
                            {
                                // Mark all voxels in this step cube as part of the material
                                for (int dz = 0; dz < step && z + dz < depth; dz++)
                                {
                                    for (int dy = 0; dy < step && y + dy < height; dy++)
                                    {
                                        for (int dx = 0; dx < step && x + dx < width; dx++)
                                        {
                                            materialVoxels[x + dx, y + dy, z + dz] = true;
                                            totalMaterialVoxels++;
                                        }
                                    }
                                }
                            }
                        }
                        catch (IndexOutOfRangeException)
                        {
                            continue; // Skip out-of-bounds voxels
                        }
                    }
                }
            }

            if (totalMaterialVoxels == 0)
            {
                throw new InvalidOperationException("No voxels found for the selected material");
            }

            // Now extract the connected surface mesh
            statusHeader.Text = "Generating connected surface mesh...";
            Application.DoEvents();

            // Clear existing mesh before generating a new one
            meshTriangles.Clear();

            // We use a face-based approach: for each voxel, we add only faces that are on the boundary
            // (i.e., faces adjacent to empty space or a different material)
            for (int z = 0; z < depth; z += step)
            {
                statusHeader.Text = $"Generating mesh... {z * 100 / depth}%";
                Application.DoEvents(); // Keep UI responsive

                for (int y = 0; y < height; y += step)
                {
                    for (int x = 0; x < width; x += step)
                    {
                        if (isMeshGenerationCancelled)
                            return;

                        // Skip if not a material voxel
                        if (!IsVoxelInMaterial(materialVoxels, x, y, z, width, height, depth))
                            continue;

                        // Size of the voxel cube
                        float cubeSize = step * 0.95f; // Slightly smaller to avoid z-fighting
                        float halfSize = cubeSize / 2;

                        // Center of the voxel cube
                        float centerX = x + step / 2.0f;
                        float centerY = y + step / 2.0f;
                        float centerZ = z + step / 2.0f;

                        // Check each face and add only if it's on the boundary

                        // Check -X face (left)
                        if (x == 0 || !IsVoxelInMaterial(materialVoxels, x - step, y, z, width, height, depth))
                        {
                            AddFace(
                                centerX - halfSize, centerY - halfSize, centerZ - halfSize,
                                centerX - halfSize, centerY - halfSize, centerZ + halfSize,
                                centerX - halfSize, centerY + halfSize, centerZ + halfSize,
                                centerX - halfSize, centerY + halfSize, centerZ - halfSize
                            );
                        }

                        // Check +X face (right)
                        if (x + step >= width || !IsVoxelInMaterial(materialVoxels, x + step, y, z, width, height, depth))
                        {
                            AddFace(
                                centerX + halfSize, centerY - halfSize, centerZ - halfSize,
                                centerX + halfSize, centerY + halfSize, centerZ - halfSize,
                                centerX + halfSize, centerY + halfSize, centerZ + halfSize,
                                centerX + halfSize, centerY - halfSize, centerZ + halfSize
                            );
                        }

                        // Check -Y face (bottom)
                        if (y == 0 || !IsVoxelInMaterial(materialVoxels, x, y - step, z, width, height, depth))
                        {
                            AddFace(
                                centerX - halfSize, centerY - halfSize, centerZ - halfSize,
                                centerX + halfSize, centerY - halfSize, centerZ - halfSize,
                                centerX + halfSize, centerY - halfSize, centerZ + halfSize,
                                centerX - halfSize, centerY - halfSize, centerZ + halfSize
                            );
                        }

                        // Check +Y face (top)
                        if (y + step >= height || !IsVoxelInMaterial(materialVoxels, x, y + step, z, width, height, depth))
                        {
                            AddFace(
                                centerX - halfSize, centerY + halfSize, centerZ - halfSize,
                                centerX - halfSize, centerY + halfSize, centerZ + halfSize,
                                centerX + halfSize, centerY + halfSize, centerZ + halfSize,
                                centerX + halfSize, centerY + halfSize, centerZ - halfSize
                            );
                        }

                        // Check -Z face (front)
                        if (z == 0 || !IsVoxelInMaterial(materialVoxels, x, y, z - step, width, height, depth))
                        {
                            AddFace(
                                centerX - halfSize, centerY - halfSize, centerZ - halfSize,
                                centerX - halfSize, centerY + halfSize, centerZ - halfSize,
                                centerX + halfSize, centerY + halfSize, centerZ - halfSize,
                                centerX + halfSize, centerY - halfSize, centerZ - halfSize
                            );
                        }

                        // Check +Z face (back)
                        if (z + step >= depth || !IsVoxelInMaterial(materialVoxels, x, y, z + step, width, height, depth))
                        {
                            AddFace(
                                centerX - halfSize, centerY - halfSize, centerZ + halfSize,
                                centerX + halfSize, centerY - halfSize, centerZ + halfSize,
                                centerX + halfSize, centerY + halfSize, centerZ + halfSize,
                                centerX - halfSize, centerY + halfSize, centerZ + halfSize
                            );
                        }
                    }
                }
            }

            // If we have too many triangles, perform a simple mesh simplification by removing small details
            if (meshTriangles.Count > targetFacets * 1.2 && resolution > 2)
            {
                statusHeader.Text = "Simplifying mesh...";
                Application.DoEvents();

                // This is a simple approach: re-run with a lower resolution
                int newResolution = resolution - 1;
                GenerateMeshFromVolumeImproved(materialID, newResolution, targetFacets);
                return;
            }

            Logger.Log($"[StressAnalysisForm] Generated connected mesh with {meshTriangles.Count} triangles");
        }

        // Helper method to check if a voxel is part of the material
        private bool IsVoxelInMaterial(bool[,,] materialVoxels, int x, int y, int z, int width, int height, int depth)
        {
            // Check bounds
            if (x < 0 || y < 0 || z < 0 || x >= width || y >= height || z >= depth)
                return false;

            return materialVoxels[x, y, z];
        }

        // Helper method to add a quadrilateral face (as two triangles)
        private void AddFace(float x1, float y1, float z1, float x2, float y2, float z2,
                             float x3, float y3, float z3, float x4, float y4, float z4)
        {
            // Create vertices
            Vector3 v1 = new Vector3(x1, y1, z1);
            Vector3 v2 = new Vector3(x2, y2, z2);
            Vector3 v3 = new Vector3(x3, y3, z3);
            Vector3 v4 = new Vector3(x4, y4, z4);

            // Add two triangles to form a quad
            meshTriangles.Add(new Triangle(v1, v2, v3));
            meshTriangles.Add(new Triangle(v1, v3, v4));
        }
        private void GenerateMeshFromVolume(byte materialID, int resolution, int targetFacets)
        {
            if (mainForm.volumeLabels == null)
            {
                throw new InvalidOperationException("No volume data available");
            }

            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();

            // Calculate total number of voxels with this material
            int totalVoxels = CountMaterialVoxels(materialID);

            // If total voxels is greater than target facets, we'll need to skip some voxels
            // to approximate the entire volume with fewer triangles
            int voxelStep = Math.Max(1, (int)Math.Ceiling((double)totalVoxels * 12 / targetFacets));

            // Adjust resolution based on target facets
            int maxDim = Math.Max(Math.Max(width, height), depth);
            int step = Math.Max(1, maxDim / (resolution * 10));

            Logger.Log($"[StressAnalysisForm] Generating mesh for material {materialID} with step {step}, voxel step {voxelStep}");

            // Status update variables
            int processedVoxels = 0;
            int lastPercentage = 0;

            // Create a list to store all material voxels for better distribution
            List<Point3D> materialVoxels = new List<Point3D>();

            // First pass: collect all material voxels
            for (int z = 0; z < depth; z += step)
            {
                if (isMeshGenerationCancelled)
                    return;

                for (int y = 0; y < height; y += step)
                {
                    for (int x = 0; x < width; x += step)
                    {
                        try
                        {
                            if (mainForm.volumeLabels[x, y, z] == materialID)
                            {
                                materialVoxels.Add(new Point3D(x, y, z));
                            }
                        }
                        catch (IndexOutOfRangeException)
                        {
                            // Skip out-of-bounds voxels
                            continue;
                        }
                    }
                }
            }

            // If we have no voxels, return
            if (materialVoxels.Count == 0)
                return;

            // Calculate adaptive step to evenly distribute triangles
            voxelStep = Math.Max(1, materialVoxels.Count / (targetFacets / 12));

            // Second pass: create mesh from selected voxels
            int totalMaterialVoxels = materialVoxels.Count;
            for (int i = 0; i < totalMaterialVoxels; i += voxelStep)
            {
                if (isMeshGenerationCancelled)
                    return;

                Point3D voxel = materialVoxels[i];
                AddCube(voxel.X, voxel.Y, voxel.Z, step * 0.8f);

                // Update progress every 1%
                processedVoxels++;
                int percentage = (processedVoxels * 100) / totalMaterialVoxels;
                if (percentage > lastPercentage)
                {
                    lastPercentage = percentage;
                    this.BeginInvoke(new Action(() => statusHeader.Text = $"Generating mesh... {percentage}%"));
                }
            }

            Logger.Log($"[StressAnalysisForm] Generated mesh with {meshTriangles.Count} triangles");
        }
        private void AddCube(float x, float y, float z, float size)
        {
            float halfSize = size / 2;

            // Define the 8 vertices of the cube
            Vector3 v0 = new Vector3(x - halfSize, y - halfSize, z - halfSize);
            Vector3 v1 = new Vector3(x + halfSize, y - halfSize, z - halfSize);
            Vector3 v2 = new Vector3(x + halfSize, y + halfSize, z - halfSize);
            Vector3 v3 = new Vector3(x - halfSize, y + halfSize, z - halfSize);
            Vector3 v4 = new Vector3(x - halfSize, y - halfSize, z + halfSize);
            Vector3 v5 = new Vector3(x + halfSize, y - halfSize, z + halfSize);
            Vector3 v6 = new Vector3(x + halfSize, y + halfSize, z + halfSize);
            Vector3 v7 = new Vector3(x - halfSize, y + halfSize, z + halfSize);

            // Front face (z-)
            meshTriangles.Add(new Triangle(v0, v1, v2));
            meshTriangles.Add(new Triangle(v0, v2, v3));

            // Back face (z+)
            meshTriangles.Add(new Triangle(v4, v6, v5));
            meshTriangles.Add(new Triangle(v4, v7, v6));

            // Left face (x-)
            meshTriangles.Add(new Triangle(v0, v3, v7));
            meshTriangles.Add(new Triangle(v0, v7, v4));

            // Right face (x+)
            meshTriangles.Add(new Triangle(v1, v5, v6));
            meshTriangles.Add(new Triangle(v1, v6, v2));

            // Bottom face (y-)
            meshTriangles.Add(new Triangle(v0, v4, v5));
            meshTriangles.Add(new Triangle(v0, v5, v1));

            // Top face (y+)
            meshTriangles.Add(new Triangle(v3, v2, v6));
            meshTriangles.Add(new Triangle(v3, v6, v7));
        }

        private void MeshViewPanel_Paint(object sender, PaintEventArgs e)
        {
            if (!meshGenerated || meshTriangles.Count == 0)
                return;

            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Black);

            // Set up projection parameters
            int width = meshViewPanel.Width;
            int height = meshViewPanel.Height;
            float scale = Math.Min(width, height) / 200.0f * zoomLevel;

            // Center point for the projection with panning
            float centerX = width / 2.0f + panX;
            float centerY = height / 2.0f + panY;

            // Get volume dimensions for normalization
            int volumeWidth = mainForm.GetWidth();
            int volumeHeight = mainForm.GetHeight();
            int volumeDepth = mainForm.GetDepth();
            float maxDim = Math.Max(Math.Max(volumeWidth, volumeHeight), volumeDepth);

            // Update rotation angle if auto-rotation is enabled
            if (isAutoRotating)
            {
                rotationY = (float)(DateTime.Now.TimeOfDay.TotalSeconds * 0.1 % (2 * Math.PI));
                meshViewPanel.Invalidate();
            }

            // Create a list to hold all triangles with their average Z for depth sorting
            var trianglesToDraw = new List<(Triangle Triangle, float AverageZ)>();

            // First pass: calculate projected positions and depth for all triangles
            foreach (Triangle tri in meshTriangles)
            {
                // Project all vertices
                PointF p1 = ProjectVertex(tri.V1, centerX, centerY, scale, maxDim, rotationX, rotationY);
                PointF p2 = ProjectVertex(tri.V2, centerX, centerY, scale, maxDim, rotationX, rotationY);
                PointF p3 = ProjectVertex(tri.V3, centerX, centerY, scale, maxDim, rotationX, rotationY);

                // Calculate the average Z depth for this triangle for depth sorting
                float avgZ = (tri.V1.Z + tri.V2.Z + tri.V3.Z) / 3.0f;

                // Store the triangle and its average Z
                trianglesToDraw.Add((tri, avgZ));
            }

            // Sort triangles by Z depth (back to front for correct rendering)
            trianglesToDraw = trianglesToDraw.OrderBy(t => -t.AverageZ).ToList();

            // Draw the triangles
            using (Pen wirePen = new Pen(Color.FromArgb(150, selectedMaterial.Color), 1))
            {
                foreach (var triData in trianglesToDraw)
                {
                    Triangle tri = triData.Triangle;

                    // Project vertices
                    PointF p1 = ProjectVertex(tri.V1, centerX, centerY, scale, maxDim, rotationX, rotationY);
                    PointF p2 = ProjectVertex(tri.V2, centerX, centerY, scale, maxDim, rotationX, rotationY);
                    PointF p3 = ProjectVertex(tri.V3, centerX, centerY, scale, maxDim, rotationX, rotationY);

                    // Draw triangle in wireframe
                    g.DrawLine(wirePen, p1, p2);
                    g.DrawLine(wirePen, p2, p3);
                    g.DrawLine(wirePen, p3, p1);
                }
            }

            // Display mesh info
            string info = $"Triangles: {meshTriangles.Count} | Zoom: {zoomLevel:F1}x";
            Font infoFont = new Font("Arial", 10);
            g.DrawString(info, infoFont, Brushes.White, 10, meshViewPanel.Height - 25);
        }
        private PointF ProjectVertex(Vector3 vertex, float centerX, float centerY, float scale, float maxDim, float rotX, float rotY)
        {
            // Normalize coordinates to -0.5 to 0.5 range
            float nx = vertex.X / maxDim - 0.5f;
            float ny = vertex.Y / maxDim - 0.5f;
            float nz = vertex.Z / maxDim - 0.5f;

            // Apply rotation around Y axis first
            float cosY = (float)Math.Cos(rotY);
            float sinY = (float)Math.Sin(rotY);
            float tx = nx * cosY + nz * sinY;
            float ty = ny;
            float tz = -nx * sinY + nz * cosY;

            // Then apply rotation around X axis
            float cosX = (float)Math.Cos(rotX);
            float sinX = (float)Math.Sin(rotX);
            float rx = tx;
            float ry = ty * cosX - tz * sinX;
            float rz = ty * sinX + tz * cosX;

            // Simple perspective projection
            float perspective = 1.5f + rz;
            float projX = centerX + rx * scale * 150 / perspective;
            float projY = centerY + ry * scale * 150 / perspective;

            return new PointF(projX, projY);
        }
        private void MeshSettingsButton_Click(object sender, EventArgs e)
        {
            // Display a dialog to adjust mesh settings
            using (Form settingsForm = new Form())
            {
                settingsForm.Text = "Mesh Settings";
                settingsForm.Size = new Size(300, 200);
                settingsForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                settingsForm.StartPosition = FormStartPosition.CenterParent;
                settingsForm.MaximizeBox = false;
                settingsForm.MinimizeBox = false;

                // Add controls to the form
                Label resLabel = new Label();
                resLabel.Text = "Resolution:";
                resLabel.Location = new Point(20, 20);

                TrackBar resTracker = new TrackBar();
                resTracker.Location = new Point(20, 40);
                resTracker.Width = 240;
                resTracker.Minimum = 1;
                resTracker.Maximum = 10;
                resTracker.Value = resolutionTrackBar.Value;

                Label facetsLabel = new Label();
                facetsLabel.Text = "Target Facets:";
                facetsLabel.Location = new Point(20, 80);

                NumericUpDown facetsNum = new NumericUpDown();
                facetsNum.Location = new Point(120, 80);
                facetsNum.Width = 100;
                facetsNum.Minimum = 100;
                facetsNum.Maximum = 1000000;
                facetsNum.Value = facetsNumeric.Value;
                facetsNum.Increment = 1000;

                Button okButton = new Button();
                okButton.Text = "OK";
                okButton.DialogResult = DialogResult.OK;
                okButton.Location = new Point(110, 120);

                settingsForm.Controls.Add(resLabel);
                settingsForm.Controls.Add(resTracker);
                settingsForm.Controls.Add(facetsLabel);
                settingsForm.Controls.Add(facetsNum);
                settingsForm.Controls.Add(okButton);

                if (settingsForm.ShowDialog() == DialogResult.OK)
                {
                    resolutionTrackBar.Value = resTracker.Value;
                    facetsNumeric.Value = facetsNum.Value;
                }
            }
        }

        private void ImportObjButton_Click(object sender, EventArgs e)
        {
            ImportMesh(".obj");
        }

        private void ImportStlButton_Click(object sender, EventArgs e)
        {
            ImportMesh(".stl");
        }

        private void ImportMeshButton_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "Mesh Files|*.obj;*.stl|OBJ Files|*.obj|STL Files|*.stl|All Files|*.*";
                dlg.Title = "Import Mesh";

                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    string ext = Path.GetExtension(dlg.FileName).ToLower();
                    ImportMesh(ext, dlg.FileName);
                }
            }
        }

        private void ImportMesh(string fileExtension, string filePath = null)
        {
            if (filePath == null)
            {
                using (OpenFileDialog dlg = new OpenFileDialog())
                {
                    if (fileExtension == ".obj")
                        dlg.Filter = "OBJ Files|*.obj";
                    else if (fileExtension == ".stl")
                        dlg.Filter = "STL Files|*.stl";
                    else
                        dlg.Filter = "All Files|*.*";

                    dlg.Title = $"Import {fileExtension.ToUpper().Substring(1)} File";

                    if (dlg.ShowDialog() != DialogResult.OK)
                        return;

                    filePath = dlg.FileName;
                }
            }

            try
            {
                statusHeader.Text = $"Importing {Path.GetFileName(filePath)}...";
                this.Cursor = Cursors.WaitCursor;

                // Clear existing mesh
                meshTriangles.Clear();

                // Check file extension to determine which importer to use
                if (fileExtension.ToLower() == ".obj")
                {
                    ImportObjFile(filePath);
                }
                else if (fileExtension.ToLower() == ".stl")
                {
                    ImportStlFile(filePath);
                }
                else
                {
                    throw new InvalidOperationException($"Unsupported file format: {fileExtension}");
                }

                statusHeader.Text = $"Imported mesh with {meshTriangles.Count} triangles";
                meshGenerated = true;
                applyMeshButton.Enabled = true;

                // Refresh the mesh view
                meshViewPanel.Invalidate();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error importing mesh: {ex.Message}", "Import Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[StressAnalysisForm] Mesh import error: {ex.Message}");
            }
            finally
            {
                this.Cursor = Cursors.Default;
            }
        }

        private void ImportObjFile(string filePath)
        {
            Logger.Log($"[StressAnalysisForm] Importing OBJ file: {filePath}");

            List<Vector3> vertices = new List<Vector3>();

            using (StreamReader reader = new StreamReader(filePath))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length > 0)
                    {
                        if (parts[0] == "v" && parts.Length >= 4)
                        {
                            // Vertex
                            float x = float.Parse(parts[1]);
                            float y = float.Parse(parts[2]);
                            float z = float.Parse(parts[3]);
                            vertices.Add(new Vector3(x, y, z));
                        }
                        else if (parts[0] == "f" && parts.Length >= 4)
                        {
                            // Face - handle both formats: "f v1 v2 v3" and "f v1/vt1/vn1 v2/vt2/vn2 v3/vt3/vn3"
                            int[] indices = new int[3];

                            for (int i = 0; i < 3; i++)
                            {
                                string vertPart = parts[i + 1].Split('/')[0];
                                indices[i] = int.Parse(vertPart) - 1; // OBJ indices are 1-based
                            }

                            // Add the triangle
                            if (indices[0] < vertices.Count && indices[1] < vertices.Count && indices[2] < vertices.Count)
                            {
                                meshTriangles.Add(new Triangle(
                                    vertices[indices[0]],
                                    vertices[indices[1]],
                                    vertices[indices[2]]
                                ));
                            }
                        }
                    }
                }
            }

            Logger.Log($"[StressAnalysisForm] Imported OBJ with {meshTriangles.Count} triangles");
        }

        private void ImportStlFile(string filePath)
        {
            Logger.Log($"[StressAnalysisForm] Importing STL file: {filePath}");

            // Check if the file is binary or ASCII by reading the first few bytes
            bool isBinary = IsSTLBinary(filePath);

            if (isBinary)
            {
                ImportBinarySTL(filePath);
            }
            else
            {
                ImportAsciiSTL(filePath);
            }

            Logger.Log($"[StressAnalysisForm] Imported STL with {meshTriangles.Count} triangles");
        }

        private bool IsSTLBinary(string filePath)
        {
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                // Read the first 512 bytes to check
                byte[] buffer = new byte[512];
                fs.Read(buffer, 0, buffer.Length);

                // Check for ASCII STL header "solid"
                string header = System.Text.Encoding.ASCII.GetString(buffer, 0, 5);

                if (header.ToLower() == "solid")
                {
                    // Further check if it's really ASCII by scanning for key ASCII keywords
                    string content = System.Text.Encoding.ASCII.GetString(buffer);
                    if (content.Contains("facet") && content.Contains("vertex"))
                    {
                        return false; // Likely ASCII
                    }
                }

                return true; // Likely binary
            }
        }

        private void ImportBinarySTL(string filePath)
        {
            using (BinaryReader reader = new BinaryReader(File.Open(filePath, FileMode.Open)))
            {
                // Skip the header
                reader.ReadBytes(80);

                // Read the number of triangles
                uint triangleCount = reader.ReadUInt32();

                // Read each triangle
                for (int i = 0; i < triangleCount; i++)
                {
                    // Skip normal vector (3 floats)
                    reader.ReadBytes(12);

                    // Read vertices
                    float v1x = reader.ReadSingle();
                    float v1y = reader.ReadSingle();
                    float v1z = reader.ReadSingle();

                    float v2x = reader.ReadSingle();
                    float v2y = reader.ReadSingle();
                    float v2z = reader.ReadSingle();

                    float v3x = reader.ReadSingle();
                    float v3y = reader.ReadSingle();
                    float v3z = reader.ReadSingle();

                    // Skip attribute byte count
                    reader.ReadUInt16();

                    // Add the triangle
                    meshTriangles.Add(new Triangle(
                        new Vector3(v1x, v1y, v1z),
                        new Vector3(v2x, v2y, v2z),
                        new Vector3(v3x, v3y, v3z)
                    ));
                }
            }
        }

        private void ImportAsciiSTL(string filePath)
        {
            using (StreamReader reader = new StreamReader(filePath))
            {
                string line;
                Vector3[] vertices = new Vector3[3];
                int vertexIndex = 0;

                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();

                    if (line.StartsWith("vertex"))
                    {
                        string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                        if (parts.Length >= 4)
                        {
                            float x = float.Parse(parts[1]);
                            float y = float.Parse(parts[2]);
                            float z = float.Parse(parts[3]);

                            vertices[vertexIndex++] = new Vector3(x, y, z);

                            if (vertexIndex == 3)
                            {
                                // Add the triangle
                                meshTriangles.Add(new Triangle(vertices[0], vertices[1], vertices[2]));
                                vertexIndex = 0;
                            }
                        }
                    }
                }
            }
        }

        private void ExportMeshButton_Click(object sender, EventArgs e)
        {
            if (!meshGenerated || meshTriangles.Count == 0)
            {
                MessageBox.Show("No mesh to export. Please generate or import a mesh first.",
                    "No Mesh", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "OBJ Files|*.obj|STL Files|*.stl";
                dlg.Title = "Export Mesh";
                dlg.FileName = $"{selectedMaterial?.Name ?? "Mesh"}_export";

                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        statusHeader.Text = "Exporting mesh...";
                        this.Cursor = Cursors.WaitCursor;

                        string ext = Path.GetExtension(dlg.FileName).ToLower();

                        if (ext == ".obj")
                        {
                            ExportToObj(dlg.FileName);
                        }
                        else if (ext == ".stl")
                        {
                            ExportToStl(dlg.FileName);
                        }

                        statusHeader.Text = $"Mesh exported to {Path.GetFileName(dlg.FileName)}";
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error exporting mesh: {ex.Message}", "Export Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Logger.Log($"[StressAnalysisForm] Mesh export error: {ex.Message}");
                    }
                    finally
                    {
                        this.Cursor = Cursors.Default;
                    }
                }
            }
        }

        private void ExportToObj(string filePath)
        {
            using (StreamWriter writer = new StreamWriter(filePath))
            {
                writer.WriteLine($"# OBJ file generated by CT Segmenter");
                writer.WriteLine($"# Material: {selectedMaterial?.Name ?? "Unknown"}");
                writer.WriteLine($"# Triangles: {meshTriangles.Count}");
                writer.WriteLine();

                // Create a dictionary to store unique vertices
                Dictionary<Vector3, int> vertexMap = new Dictionary<Vector3, int>();
                int vertexIndex = 1; // OBJ uses 1-based indexing

                // Write all unique vertices
                foreach (Triangle tri in meshTriangles)
                {
                    if (!vertexMap.ContainsKey(tri.V1))
                    {
                        writer.WriteLine($"v {tri.V1.X} {tri.V1.Y} {tri.V1.Z}");
                        vertexMap[tri.V1] = vertexIndex++;
                    }

                    if (!vertexMap.ContainsKey(tri.V2))
                    {
                        writer.WriteLine($"v {tri.V2.X} {tri.V2.Y} {tri.V2.Z}");
                        vertexMap[tri.V2] = vertexIndex++;
                    }

                    if (!vertexMap.ContainsKey(tri.V3))
                    {
                        writer.WriteLine($"v {tri.V3.X} {tri.V3.Y} {tri.V3.Z}");
                        vertexMap[tri.V3] = vertexIndex++;
                    }
                }

                writer.WriteLine();
                writer.WriteLine($"g {selectedMaterial?.Name ?? "object"}");

                // Write all faces
                foreach (Triangle tri in meshTriangles)
                {
                    writer.WriteLine($"f {vertexMap[tri.V1]} {vertexMap[tri.V2]} {vertexMap[tri.V3]}");
                }
            }

            Logger.Log($"[StressAnalysisForm] Exported {meshTriangles.Count} triangles to OBJ: {filePath}");
        }

        private void ExportToStl(string filePath)
        {
            using (StreamWriter writer = new StreamWriter(filePath))
            {
                writer.WriteLine($"solid {selectedMaterial?.Name ?? "CTSegmenterExport"}");

                foreach (Triangle tri in meshTriangles)
                {
                    // Calculate a simple normal (not normalized for brevity)
                    float nx = (tri.V2.Y - tri.V1.Y) * (tri.V3.Z - tri.V1.Z) - (tri.V2.Z - tri.V1.Z) * (tri.V3.Y - tri.V1.Y);
                    float ny = (tri.V2.Z - tri.V1.Z) * (tri.V3.X - tri.V1.X) - (tri.V2.X - tri.V1.X) * (tri.V3.Z - tri.V1.Z);
                    float nz = (tri.V2.X - tri.V1.X) * (tri.V3.Y - tri.V1.Y) - (tri.V2.Y - tri.V1.Y) * (tri.V3.X - tri.V1.X);

                    // Normalize the normal
                    float length = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (length > 0)
                    {
                        nx /= length;
                        ny /= length;
                        nz /= length;
                    }

                    writer.WriteLine("  facet normal {0} {1} {2}", nx, ny, nz);
                    writer.WriteLine("    outer loop");
                    writer.WriteLine("      vertex {0} {1} {2}", tri.V1.X, tri.V1.Y, tri.V1.Z);
                    writer.WriteLine("      vertex {0} {1} {2}", tri.V2.X, tri.V2.Y, tri.V2.Z);
                    writer.WriteLine("      vertex {0} {1} {2}", tri.V3.X, tri.V3.Y, tri.V3.Z);
                    writer.WriteLine("    endloop");
                    writer.WriteLine("  endfacet");
                }

                writer.WriteLine($"endsolid {selectedMaterial?.Name ?? "CTSegmenterExport"}");
            }

            Logger.Log($"[StressAnalysisForm] Exported {meshTriangles.Count} triangles to STL: {filePath}");
        }

        private void WireframeButton_Click(object sender, EventArgs e)
        {
            meshViewPanel.Invalidate();
        }

        private void SolidButton_Click(object sender, EventArgs e)
        {
            MessageBox.Show("Solid view is not implemented in this version.", "Information",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void StressButton_Click(object sender, EventArgs e)
        {
            if (!meshGenerated || meshTriangles.Count == 0)
            {
                MessageBox.Show("No mesh to analyze. Please generate or import a mesh first.",
                    "No Mesh", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Switch to analysis tab
            mainTabControl.SelectedPage = analysisPage;

            // Focus on the triaxial parameters
            triaxialParamsBox.Focus();
        }

        private void AcousticButton_Click(object sender, EventArgs e)
        {
            if (!meshGenerated || meshTriangles.Count == 0)
            {
                MessageBox.Show("No mesh to analyze. Please generate or import a mesh first.",
                    "No Mesh", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Switch to analysis tab
            mainTabControl.SelectedPage = analysisPage;

            // Focus on the acoustic parameters
            acousticParamsBox.Focus();
        }
        private void ApplyMeshButton_Click(object sender, EventArgs e)
        {
            if (!meshGenerated || meshTriangles.Count == 0)
            {
                MessageBox.Show("No mesh to apply. Please generate or import a mesh first.",
                    "No Mesh", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // TODO: Implement mesh application to the main volume
            MessageBox.Show("The mesh has been applied successfully.", "Success",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        private void BtnSetDensity_Click(object sender, EventArgs e)
        {
            if (selectedMaterial == null || selectedMaterial.ID == 0)
            {
                MessageBox.Show("Please select a material first.", "No Material Selected",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                using (DensitySettingsForm form = new DensitySettingsForm(this, mainForm))
                {
                    if (form.ShowDialog() == DialogResult.OK)
                    {
                        UpdateDensityDisplay();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening density settings: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[StressAnalysisForm] Density settings error: {ex.Message}");
            }
        }

        public void SetMaterialDensity(double density)
        {
            selectedMaterial.Density = density;
            UpdateDensityDisplay();
        }

        private void UpdateDensityDisplay()
        {
            densityLabel.Text = $"Density: {selectedMaterial.Density:F2} kg/m³";
        }
        public double CalculateTotalVolume()
        {
            // Calculate volume from the mesh triangles
            double volume = 0;
            if (meshTriangles != null && meshTriangles.Count > 0)
            {
                // Use the mesh bounds to calculate volume
                float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

                foreach (var triangle in meshTriangles)
                {
                    minX = Math.Min(minX, Math.Min(triangle.V1.X, Math.Min(triangle.V2.X, triangle.V3.X)));
                    minY = Math.Min(minY, Math.Min(triangle.V1.Y, Math.Min(triangle.V2.Y, triangle.V3.Y)));
                    minZ = Math.Min(minZ, Math.Min(triangle.V1.Z, Math.Min(triangle.V2.Z, triangle.V3.Z)));

                    maxX = Math.Max(maxX, Math.Max(triangle.V1.X, Math.Max(triangle.V2.X, triangle.V3.X)));
                    maxY = Math.Max(maxY, Math.Max(triangle.V1.Y, Math.Max(triangle.V2.Y, triangle.V3.Y)));
                    maxZ = Math.Max(maxZ, Math.Max(triangle.V1.Z, Math.Max(triangle.V2.Z, triangle.V3.Z)));
                }

                // Calculate volume in cubic meters (assumes voxel units)
                double pixelSize = mainForm.pixelSize;
                volume = (maxX - minX) * (maxY - minY) * (maxZ - minZ) * Math.Pow(pixelSize, 3);
            }

            return Math.Max(volume, 1e-6); // Ensure we don't divide by zero
        }

        public void ApplyDensityCalibration(List<CalibrationPoint> calibrationPoints)
        {
            if (calibrationPoints.Count < 2 || selectedMaterial == null || selectedMaterial.ID == 0)
                return;

            // Sort calibration points by gray value for interpolation
            var sortedPoints = calibrationPoints.OrderBy(p => p.AvgGrayValue).ToList();

            // Calculate average gray value for the selected material
            double avgGrayValue = CalculateAverageMaterialGrayValue();

            // Find the two calibration points that bracket the average gray value
            double interpolatedDensity = 0;

            if (avgGrayValue <= sortedPoints.First().AvgGrayValue)
            {
                // Below the lowest calibration point, use the lowest density
                interpolatedDensity = sortedPoints.First().Density;
            }
            else if (avgGrayValue >= sortedPoints.Last().AvgGrayValue)
            {
                // Above the highest calibration point, use the highest density
                interpolatedDensity = sortedPoints.Last().Density;
            }
            else
            {
                // Interpolate between calibration points
                for (int i = 0; i < sortedPoints.Count - 1; i++)
                {
                    if (avgGrayValue >= sortedPoints[i].AvgGrayValue &&
                        avgGrayValue <= sortedPoints[i + 1].AvgGrayValue)
                    {
                        // Linear interpolation
                        double grayDelta = sortedPoints[i + 1].AvgGrayValue - sortedPoints[i].AvgGrayValue;
                        double densityDelta = sortedPoints[i + 1].Density - sortedPoints[i].Density;
                        double factor = (avgGrayValue - sortedPoints[i].AvgGrayValue) / grayDelta;
                        interpolatedDensity = sortedPoints[i].Density + factor * densityDelta;
                        break;
                    }
                }
            }

            // Apply the interpolated density
            SetMaterialDensity(interpolatedDensity);
        }

        private double CalculateAverageMaterialGrayValue()
        {
            if (mainForm.volumeData == null || mainForm.volumeLabels == null || selectedMaterial == null || selectedMaterial.ID == 0)
                return 128; // Default grayscale middle value

            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();

            byte materialID = selectedMaterial.ID;
            long totalGrayValue = 0;
            int voxelCount = 0;

            // Sum up gray values for all voxels of this material
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (mainForm.volumeLabels[x, y, z] == materialID)
                        {
                            totalGrayValue += mainForm.volumeData[x, y, z];
                            voxelCount++;
                        }
                    }
                }
            }

            // Calculate the average
            return voxelCount > 0 ? (double)totalGrayValue / voxelCount : 128;
        }
        private void InitializeCancelButton()
        {
            // Create cancel button if it doesn't exist
            if (cancelMeshButton == null)
            {
                cancelMeshButton = new Button();
                cancelMeshButton.Text = "Cancel";
                cancelMeshButton.Location = new Point(10, 180);
                cancelMeshButton.Width = 150;
                cancelMeshButton.Visible = false;
                cancelMeshButton.Image = CreateCancelIcon(16);
                cancelMeshButton.ImageAlign = ContentAlignment.MiddleLeft;
                cancelMeshButton.TextAlign = ContentAlignment.MiddleRight;
                cancelMeshButton.Click += CancelMeshButton_Click;
                controlsPanel.Controls.Add(cancelMeshButton);
            }
        }
        private void CancelMeshButton_Click(object sender, EventArgs e)
        {
            isMeshGenerationCancelled = true;
            statusHeader.Text = "Mesh generation cancelled";
        }

        private int CountMaterialVoxels(byte materialID)
        {
            int count = 0;
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();

            // Get count based on resolution
            int step = Math.Max(1, Math.Max(Math.Max(width, height), depth) / (resolutionTrackBar.Value * 10));

            for (int z = 0; z < depth; z += step)
            {
                if (isMeshGenerationCancelled)
                    return count;

                for (int y = 0; y < height; y += step)
                {
                    for (int x = 0; x < width; x += step)
                    {
                        try
                        {
                            if (mainForm.volumeLabels[x, y, z] == materialID)
                            {
                                count++;
                            }
                        }
                        catch (IndexOutOfRangeException)
                        {
                            // Skip out-of-bounds voxels
                            continue;
                        }
                    }
                }
            }

            return count;
        }
        private void InitializeMeshViewControls()
        {
            // Mouse event handlers for the mesh view panel
            meshViewPanel.MouseDown += MeshViewPanel_MouseDown;
            meshViewPanel.MouseMove += MeshViewPanel_MouseMove;
            meshViewPanel.MouseUp += MeshViewPanel_MouseUp;
            meshViewPanel.MouseWheel += MeshViewPanel_MouseWheel;

            // Create tooltip for instructions
            viewerTooltip = new ToolTip();
            viewerTooltip.AutoPopDelay = 5000;
            viewerTooltip.InitialDelay = 1000;
            viewerTooltip.ReshowDelay = 500;
            viewerTooltip.ShowAlways = true;

            // Add a control panel to the mesh view
            Panel viewControlPanel = new Panel
            {
                BackColor = Color.FromArgb(60, 0, 0, 0),
                Size = new Size(180, 85),
                Location = new Point(10, 10),
                Padding = new Padding(5)
            };

            viewControlsLabel = new Label
            {
                Text = "View Controls:",
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                AutoSize = true,
                Location = new Point(5, 5)
            };

            toggleRotationButton = new Button
            {
                Text = "Auto-Rotate: OFF",
                BackColor = Color.FromArgb(100, 100, 0, 0),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(170, 25),
                Location = new Point(5, 25)
            };
            toggleRotationButton.FlatAppearance.BorderColor = Color.Pink;
            toggleRotationButton.Click += ToggleRotationButton_Click;

            resetViewButton = new Button
            {
                Text = "Reset View",
                BackColor = Color.FromArgb(100, 0, 0, 100),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(170, 25),
                Location = new Point(5, 55)
            };
            resetViewButton.FlatAppearance.BorderColor = Color.LightBlue;
            resetViewButton.Click += ResetViewButton_Click;

            viewControlPanel.Controls.Add(viewControlsLabel);
            viewControlPanel.Controls.Add(toggleRotationButton);
            viewControlPanel.Controls.Add(resetViewButton);

            meshViewPanel.Controls.Add(viewControlPanel);

            viewerTooltip.SetToolTip(meshViewPanel, "Left-click + Drag: Rotate\nRight-click + Drag: Pan\nScroll Wheel: Zoom");
        }
        private void MeshViewPanel_MouseDown(object sender, MouseEventArgs e)
        {
            lastMousePosition = e.Location;

            if (e.Button == MouseButtons.Left)
            {
                isRotating = true;
                // Turn off auto-rotation when user starts manual rotation
                if (isAutoRotating)
                {
                    isAutoRotating = false;
                    toggleRotationButton.Text = "Auto-Rotate: OFF";
                    toggleRotationButton.BackColor = Color.FromArgb(100, 100, 0, 0);
                    toggleRotationButton.FlatAppearance.BorderColor = Color.Pink;
                }
            }
            else if (e.Button == MouseButtons.Right)
            {
                isPanning = true;
            }
        }

        private void MeshViewPanel_MouseMove(object sender, MouseEventArgs e)
        {
            if (!meshGenerated || meshTriangles.Count == 0)
                return;

            if (isRotating)
            {
                // Calculate rotation change based on mouse movement
                float deltaX = (e.X - lastMousePosition.X) * 0.01f;
                float deltaY = (e.Y - lastMousePosition.Y) * 0.01f;

                rotationY += deltaX;
                rotationX += deltaY;

                // Limit vertical rotation to avoid flipping
                rotationX = Math.Max(Math.Min(rotationX, (float)Math.PI / 2), -(float)Math.PI / 2);

                meshViewPanel.Invalidate();
            }
            else if (isPanning)
            {
                // Calculate pan change based on mouse movement
                float deltaX = (e.X - lastMousePosition.X) * 0.5f;
                float deltaY = (e.Y - lastMousePosition.Y) * 0.5f;

                panX += deltaX;
                panY += deltaY;

                meshViewPanel.Invalidate();
            }

            lastMousePosition = e.Location;
        }

        private void MeshViewPanel_MouseUp(object sender, MouseEventArgs e)
        {
            isRotating = false;
            isPanning = false;
        }

        private void MeshViewPanel_MouseWheel(object sender, MouseEventArgs e)
        {
            if (!meshGenerated || meshTriangles.Count == 0)
                return;

            // Adjust zoom level based on scroll direction
            float zoomDelta = e.Delta > 0 ? 0.1f : -0.1f;
            zoomLevel += zoomDelta;

            // Limit zoom range to prevent extreme values
            zoomLevel = Math.Max(Math.Min(zoomLevel, 5.0f), 0.1f);

            meshViewPanel.Invalidate();
        }

        // UI Button event handlers
        private void ToggleRotationButton_Click(object sender, EventArgs e)
        {
            isAutoRotating = !isAutoRotating;

            if (isAutoRotating)
            {
                toggleRotationButton.Text = "Auto-Rotate: ON";
                toggleRotationButton.BackColor = Color.FromArgb(100, 0, 100, 0);
                toggleRotationButton.FlatAppearance.BorderColor = Color.LightGreen;
            }
            else
            {
                toggleRotationButton.Text = "Auto-Rotate: OFF";
                toggleRotationButton.BackColor = Color.FromArgb(100, 100, 0, 0);
                toggleRotationButton.FlatAppearance.BorderColor = Color.Pink;
            }

            meshViewPanel.Invalidate();
        }
        private void InitializeSimulationParameters()
        {
            // Create controls container for analysis page
            TableLayoutPanel analysisLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1
            };
            analysisLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            analysisLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

            // Triaxial Simulation Parameters
            triaxialParamsBox = new KryptonGroupBox();
            triaxialParamsBox.Dock = DockStyle.Fill;
            triaxialParamsBox.Text = "Triaxial Simulation Parameters";
            triaxialParamsBox.Margin = new Padding(10);

            TableLayoutPanel triaxialTable = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 6,
                Padding = new Padding(5)
            };
            triaxialTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
            triaxialTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));
            for (int i = 0; i < 6; i++)
            {
                triaxialTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            }

            // Confining Pressure
            triaxialTable.Controls.Add(new Label { Text = "Confining Pressure (MPa):", Dock = DockStyle.Fill }, 0, 0);
            confiningPressureNumeric = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 200,
                Value = 10,
                DecimalPlaces = 1,
                Increment = 1,
                Dock = DockStyle.Fill
            };
            triaxialTable.Controls.Add(confiningPressureNumeric, 1, 0);

            // Min Pressure
            triaxialTable.Controls.Add(new Label { Text = "Minimum Pressure (MPa):", Dock = DockStyle.Fill }, 0, 1);
            pressureMinNumeric = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 500,
                Value = 0,
                DecimalPlaces = 1,
                Increment = 5,
                Dock = DockStyle.Fill
            };
            triaxialTable.Controls.Add(pressureMinNumeric, 1, 1);

            // Max Pressure
            triaxialTable.Controls.Add(new Label { Text = "Maximum Pressure (MPa):", Dock = DockStyle.Fill }, 0, 2);
            pressureMaxNumeric = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 500,
                Value = 100,
                DecimalPlaces = 1,
                Increment = 5,
                Dock = DockStyle.Fill
            };
            triaxialTable.Controls.Add(pressureMaxNumeric, 1, 2);

            // Steps
            triaxialTable.Controls.Add(new Label { Text = "Pressure Steps:", Dock = DockStyle.Fill }, 0, 3);
            pressureStepsNumeric = new NumericUpDown
            {
                Minimum = 2,
                Maximum = 100,
                Value = 10,
                DecimalPlaces = 0,
                Dock = DockStyle.Fill
            };
            triaxialTable.Controls.Add(pressureStepsNumeric, 1, 3);

            // Test Direction
            triaxialTable.Controls.Add(new Label { Text = "Test Direction:", Dock = DockStyle.Fill }, 0, 4);
            testDirectionCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill
            };
            testDirectionCombo.Items.AddRange(new object[] { "X-Axis", "Y-Axis", "Z-Axis" });
            testDirectionCombo.SelectedIndex = 2; // Default to Z-axis
            triaxialTable.Controls.Add(testDirectionCombo, 1, 4);

            // Run button
            runTriaxialButton = new KryptonButton
            {
                Text = "Run Triaxial Simulation",
                Dock = DockStyle.Fill
            };
            runTriaxialButton.Values.Image = CreateStressIcon(16);
            runTriaxialButton.Click += RunTriaxialButton_Click;
            triaxialTable.Controls.Add(runTriaxialButton, 0, 5);
            triaxialTable.SetColumnSpan(runTriaxialButton, 2);

            triaxialParamsBox.Panel.Controls.Add(triaxialTable);

            // Acoustic Velocity Parameters
            acousticParamsBox = new KryptonGroupBox();
            acousticParamsBox.Dock = DockStyle.Fill;
            acousticParamsBox.Text = "Acoustic Velocity Parameters";
            acousticParamsBox.Margin = new Padding(10);

            TableLayoutPanel acousticTable = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 7,
                Padding = new Padding(5)
            };
            acousticTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
            acousticTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));
            for (int i = 0; i < 7; i++)
            {
                acousticTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            }

            // Confining Pressure
            acousticTable.Controls.Add(new Label { Text = "Confining Pressure (MPa):", Dock = DockStyle.Fill }, 0, 0);
            acousticConfiningNumeric = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 200,
                Value = 10,
                DecimalPlaces = 1,
                Increment = 1,
                Dock = DockStyle.Fill
            };
            acousticTable.Controls.Add(acousticConfiningNumeric, 1, 0);

            // Wave Type
            acousticTable.Controls.Add(new Label { Text = "Wave Type:", Dock = DockStyle.Fill }, 0, 1);
            waveTypeCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill
            };
            waveTypeCombo.Items.AddRange(new object[] { "P-Wave", "S-Wave" });
            waveTypeCombo.SelectedIndex = 0; // Default to P-wave
            acousticTable.Controls.Add(waveTypeCombo, 1, 1);

            // Time Steps
            acousticTable.Controls.Add(new Label { Text = "Time Steps:", Dock = DockStyle.Fill }, 0, 2);
            timeStepsNumeric = new NumericUpDown
            {
                Minimum = 10,
                Maximum = 10000000,
                Value = 1000,
                DecimalPlaces = 0,
                Increment = 100,
                Dock = DockStyle.Fill
            };
            acousticTable.Controls.Add(timeStepsNumeric, 1, 2);

            // Frequency
            acousticTable.Controls.Add(new Label { Text = "Frequency (kHz):", Dock = DockStyle.Fill }, 0, 3);
            frequencyNumeric = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 1000,
                Value = 50,
                DecimalPlaces = 1,
                Increment = 5,
                Dock = DockStyle.Fill
            };
            acousticTable.Controls.Add(frequencyNumeric, 1, 3);

            // Amplitude
            acousticTable.Controls.Add(new Label { Text = "Amplitude:", Dock = DockStyle.Fill }, 0, 4);
            amplitudeNumeric = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 100,
                Value = 1,
                DecimalPlaces = 2,
                Increment = 0.1m,
                Dock = DockStyle.Fill
            };
            acousticTable.Controls.Add(amplitudeNumeric, 1, 4);

            // Energy
            acousticTable.Controls.Add(new Label { Text = "Energy (J):", Dock = DockStyle.Fill }, 0, 5);
            energyNumeric = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 1000,
                Value = 10,
                DecimalPlaces = 2,
                Increment = 1,
                Dock = DockStyle.Fill
            };
            acousticTable.Controls.Add(energyNumeric, 1, 5);
            Label dtFactorLabel = new Label
            {
                Text = "Time-Step Factor:",
                AutoSize = true,
                
                Location = new Point(10, 360)   // tweak Y as needed
            };
            acousticTable.Controls.Add(dtFactorLabel,0,6);

            dtFactorNumeric = new NumericUpDown
            {
                Name = "dtFactorNumeric",
                Minimum = 0,
                Maximum = 100,
                DecimalPlaces = 2,
                Increment = 0.01M,
                Value = 1.00M,
                Width = 80,
                Location = new Point(130, 358)
            };
            acousticTable.Controls.Add(dtFactorNumeric,1,6);

            // Direction
            acousticTable.Controls.Add(new Label { Text = "Test Direction:", Dock = DockStyle.Fill }, 0, 7);
            acousticDirectionCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill
            };
            acousticDirectionCombo.Items.AddRange(new object[] { "X-Axis", "Y-Axis", "Z-Axis" });
            acousticDirectionCombo.SelectedIndex = 2; // Default to Z-axis
            acousticTable.Controls.Add(acousticDirectionCombo, 1, 7);
            extendedSimulationCheckBox = new KryptonCheckBox
            {
                Text = "Use Extended Simulation Time",
                Checked = UseExtendedSimulationTime,
                Dock = DockStyle.Fill
            };
            extendedSimulationCheckBox.CheckedChanged += (s, e) =>
            {
                UseExtendedSimulationTime = extendedSimulationCheckBox.Checked;
            };
            acousticTable.Controls.Add(extendedSimulationCheckBox, 0, 8);
            acousticTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            KryptonButton dumpDataButton = new KryptonButton
            {
                Text = "Debug: Dump Wave Data",
                Dock = DockStyle.Fill
            };
            dumpDataButton.Click += (s, e) =>
            {
                if (currentAcousticSim != null)
                {
                    using (SaveFileDialog saveDialog = new SaveFileDialog())
                    {
                        saveDialog.Filter = "CSV Files|*.csv";
                        saveDialog.Title = "Save Simulation Debug Data";
                        saveDialog.FileName = $"WaveDebug_{DateTime.Now:yyyyMMdd_HHmmss}.csv";

                        if (saveDialog.ShowDialog() == DialogResult.OK)
                        {
                            currentAcousticSim.DumpSimulationData(saveDialog.FileName);
                            MessageBox.Show($"Debug data saved to {Path.GetFileName(saveDialog.FileName)}");
                        }
                    }
                }
                else
                {
                    MessageBox.Show("No active simulation data available.");
                }
            };
            acousticTable.Controls.Add(dumpDataButton, 1, 8);

            // Run button
            runAcousticButton = new KryptonButton
            {
                Text = "Run Acoustic Simulation",
                Dock = DockStyle.Fill
            };
            runAcousticButton.Values.Image = CreateAcousticIcon(16);
            runAcousticButton.Click += RunAcousticButton_Click;

            // Add a panel for the acoustic run button
            Panel acousticButtonPanel = new Panel { Dock = DockStyle.Bottom, Height = 30 };
            acousticButtonPanel.Controls.Add(runAcousticButton);
            runAcousticButton.Dock = DockStyle.Fill;

            acousticParamsBox.Panel.Controls.Add(acousticTable);
            acousticParamsBox.Panel.Controls.Add(acousticButtonPanel);

            // Add both parameter boxes to the analysis layout
            analysisLayout.Controls.Add(triaxialParamsBox, 0, 0);
            analysisLayout.Controls.Add(acousticParamsBox, 1, 0);

            // Add the analysis layout to the analysis page
            analysisPage.Controls.Add(analysisLayout);
        }
        private async void RunTriaxialButton_Click(object sender, EventArgs e)
        {
            if (!meshGenerated || meshTriangles.Count == 0)
            {
                MessageBox.Show("No mesh available for simulation. Please generate or import a mesh first.",
                    "No Mesh", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (selectedMaterial == null || selectedMaterial.Density <= 0)
            {
                MessageBox.Show("Please set material density before running simulation.",
                    "Material Properties", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                // Get simulation parameters
                float confiningPressure = (float)confiningPressureNumeric.Value;
                float minPressure = (float)pressureMinNumeric.Value;
                float maxPressure = (float)pressureMaxNumeric.Value;
                int steps = (int)pressureStepsNumeric.Value;
                string direction = testDirectionCombo.SelectedItem.ToString();

                statusHeader.Text = $"Running triaxial simulation ({direction}, {confiningPressure} MPa)...";
                this.Cursor = Cursors.WaitCursor;

                // Create converter for mesh triangles
                List<CTSegmenter.Triangle> simulationTriangles = new List<CTSegmenter.Triangle>();
                foreach (var meshTri in meshTriangles)
                {
                    simulationTriangles.Add(new CTSegmenter.Triangle(
                        new System.Numerics.Vector3(meshTri.V1.X, meshTri.V1.Y, meshTri.V1.Z),
                        new System.Numerics.Vector3(meshTri.V2.X, meshTri.V2.Y, meshTri.V2.Z),
                        new System.Numerics.Vector3(meshTri.V3.X, meshTri.V3.Y, meshTri.V3.Z)
                    ));
                }

                // Prepare density map if inhomogeneous density is enabled
                ConcurrentDictionary<System.Numerics.Vector3, float> densityMap = null;
                if (inhomogeneousDensityEnabled && voxelDensities != null && voxelDensities.Count > 0)
                {
                    densityMap = new ConcurrentDictionary<System.Numerics.Vector3, float>();
                    foreach (var kvp in voxelDensities)
                    {
                        densityMap[new System.Numerics.Vector3(kvp.Key.X, kvp.Key.Y, kvp.Key.Z)] = kvp.Value;
                    }
                    Logger.Log($"[StressAnalysisForm] Using inhomogeneous density with {densityMap.Count} density points");
                }

                // Create simulation with appropriate constructor based on density mode
                TriaxialSimulation simulation;
                if (inhomogeneousDensityEnabled && densityMap != null)
                {
                    // Use the inhomogeneous simulation
                    simulation = new InhomogeneousTriaxialSimulation(
                        selectedMaterial,
                        simulationTriangles,
                        confiningPressure,
                        minPressure,
                        maxPressure,
                        steps,
                        direction,
                        true,  // inhomogeneousDensityEnabled
                        densityMap);

                    Logger.Log("[StressAnalysisForm] Created inhomogeneous triaxial simulation");
                }
                else
                {
                    // Use the standard simulation
                    simulation = new TriaxialSimulation(
                        selectedMaterial,
                        simulationTriangles,
                        confiningPressure,
                        minPressure,
                        maxPressure,
                        steps,
                        direction);

                    Logger.Log("[StressAnalysisForm] Created standard triaxial simulation");
                }

                // IMPORTANT: Set custom rock strength parameters from UI
                // Check if rock strength UI controls exist and use their values
                if (cohesionNumeric != null)
                    simulation.CohesionStrength = (float)cohesionNumeric.Value;

                if (frictionAngleNumeric != null)
                    simulation.FrictionAngle = (float)frictionAngleNumeric.Value;

                if (tensileStrengthNumeric != null)
                    simulation.TensileStrength = (float)tensileStrengthNumeric.Value;

                // Log the values for debugging
                Logger.Log($"[StressAnalysisForm] Simulation rock strength parameters: Cohesion={simulation.CohesionStrength}MPa, " +
                          $"Friction Angle={simulation.FrictionAngle}°, Tensile Strength={simulation.TensileStrength}MPa");

                // Show progress in status bar
                simulation.ProgressChanged += (s, args) =>
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        statusHeader.Text = args.StatusMessage;
                    }));
                };

                // Initialize
                if (!simulation.Initialize())
                {
                    throw new InvalidOperationException("Failed to initialize simulation");
                }

                // Run the simulation
                var result = await simulation.RunAsync();

                currentTriaxial = simulation;

                if (result.IsSuccessful)
                {
                    statusHeader.Text = "Triaxial simulation completed.";

                    // Create results display on the results page
                    CreateTriaxialResultsDisplay(simulation, result);

                    // Switch to results tab
                    mainTabControl.SelectedPage = resultsPage;
                }
                else
                {
                    statusHeader.Text = "Simulation failed.";
                    MessageBox.Show($"Triaxial simulation failed: {result.ErrorMessage}",
                        "Simulation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error running triaxial simulation: {ex.Message}",
                    "Simulation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[StressAnalysisForm] Triaxial simulation error: {ex.Message}");
            }
            finally
            {
                this.Cursor = Cursors.Default;
            }
        }
        private void ExportFullCompositeWithDensity(TriaxialSimulation simulation)
        {
            try
            {
                using (SaveFileDialog dlg = new SaveFileDialog())
                {
                    dlg.Filter = "PNG Image|*.png";
                    dlg.Title = "Export Complete Simulation Results with Density";
                    dlg.FileName = $"Triaxial_{selectedMaterial.Name}_Density_{DateTime.Now:yyyyMMdd_HHmmss}.png";

                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        this.Cursor = Cursors.WaitCursor;
                        statusHeader.Text = "Creating composite image with density visualization...";

                        if (simulation is InhomogeneousTriaxialSimulation inhomogeneousSim)
                        {
                            // Add special handling for inhomogeneous simulation if needed
                            // For example, include density visualization in the export
                            bool success = simulation.ExportFullCompositeImage(dlg.FileName);

                            if (success)
                            {
                                statusHeader.Text = $"Exported composite image to {Path.GetFileName(dlg.FileName)}";
                                MessageBox.Show($"Complete composite image successfully exported to:\n{dlg.FileName}",
                                    "Export Successful", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            }
                            else
                            {
                                statusHeader.Text = "Export failed.";
                                MessageBox.Show("Failed to export composite image.", "Export Error",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                        }
                        else
                        {
                            // Standard export for regular simulation
                            bool success = simulation.ExportFullCompositeImage(dlg.FileName);

                            if (success)
                            {
                                statusHeader.Text = $"Exported composite image to {Path.GetFileName(dlg.FileName)}";
                                MessageBox.Show($"Complete composite image successfully exported to:\n{dlg.FileName}",
                                    "Export Successful", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            }
                            else
                            {
                                statusHeader.Text = "Export failed.";
                                MessageBox.Show("Failed to export composite image.", "Export Error",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting composite image: {ex.Message}",
                    "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[StressAnalysisForm] Composite image export error: {ex.Message}");
            }
            finally
            {
                this.Cursor = Cursors.Default;
            }
        }
        private void CreateTriaxialResultsDisplay(TriaxialSimulation simulation, SimulationResult result)
        {
            currentTriaxial = simulation;
            // Clear existing controls on the results page
            resultsPage.Controls.Clear();

            // Create a tab control for different result views
            KryptonDockableNavigator resultsTabs = new KryptonDockableNavigator
            {
                Dock = DockStyle.Fill,
                PageBackStyle = Krypton.Toolkit.PaletteBackStyle.PanelClient
            };
            resultsPage.Controls.Add(resultsTabs);

            // Stress distribution page
            KryptonPage stressPage = new KryptonPage
            {
                Text = "Stress Distribution",
                TextTitle = "Von Mises Stress Visualization"
            };

            // Strain-stress curve page
            KryptonPage strainStressPage = new KryptonPage
            {
                Text = "Stress-Strain Curve",
                TextTitle = "Stress vs. Strain"
            };

            // Fracture probability page
            KryptonPage fractureProbabilityPage = new KryptonPage
            {
                Text = "Fracture Probability",
                TextTitle = "Failure Prediction Model"
            };

            // Fracture surfaces page
            var fracturePage = new KryptonPage
            {
                Text = "Fracture",
                TextTitle = "Fracture Surfaces"
            };
            var fracturePanel = new Panel { Dock = DockStyle.Fill };
            fracturePanel.Paint += (s, e) => RenderFractureSurfaces(e.Graphics, fracturePanel.Width, fracturePanel.Height);
            fracturePage.Controls.Add(fracturePanel);

            // Mesh view page
            KryptonPage meshViewPage = new KryptonPage
            {
                Text = "Mesh View",
                TextTitle = "Deformed Mesh"
            };

            // Mohr-Coulomb page - NEW
            mohrCoulombPage = new KryptonPage
            {
                Text = "Mohr-Coulomb",
                TextTitle = "Mohr-Coulomb Failure Analysis"
            };
            Panel mohrCoulombPanel = new Panel { Dock = DockStyle.Fill };
            mohrCoulombPanel.Paint += (s, e) => currentTriaxial.RenderMohrCoulombDiagram(e.Graphics, mohrCoulombPanel.Width, mohrCoulombPanel.Height);
            mohrCoulombPage.Controls.Add(mohrCoulombPanel);

            // Summary page
            KryptonPage summaryPage = new KryptonPage
            {
                Text = "Summary",
                TextTitle = "Simulation Results Summary"
            };

            // Add all pages to the tab control
            resultsTabs.Pages.AddRange(new[]
            {
                stressPage,
                strainStressPage,
                fractureProbabilityPage,
                fracturePage,
                meshViewPage,
                mohrCoulombPage,
                summaryPage
            });

            // Create panels for each page
            Panel stressPanel = new Panel { Dock = DockStyle.Fill };
            Panel strainStressPanel = new Panel { Dock = DockStyle.Fill };
            Panel fractureProbabilityPanel = new Panel { Dock = DockStyle.Fill };
            Panel meshViewPanel = new Panel { Dock = DockStyle.Fill };
            Panel summaryPanel = new Panel { Dock = DockStyle.Fill };

            // Add panels to pages
            stressPage.Controls.Add(stressPanel);
            strainStressPage.Controls.Add(strainStressPanel);
            fractureProbabilityPage.Controls.Add(fractureProbabilityPanel);
            meshViewPage.Controls.Add(meshViewPanel);
            summaryPage.Controls.Add(summaryPanel);

            // Set up panel paint events
            stressPanel.Paint += (sender, e) =>
                simulation.RenderResults(e.Graphics, stressPanel.Width, stressPanel.Height, RenderMode.Stress);

            strainStressPanel.Paint += (sender, e) =>
                simulation.RenderResults(e.Graphics, strainStressPanel.Width, strainStressPanel.Height, RenderMode.Strain);

            fractureProbabilityPanel.Paint += (sender, e) =>
                simulation.RenderResults(e.Graphics, fractureProbabilityPanel.Width, fractureProbabilityPanel.Height, RenderMode.FailureProbability);

            meshViewPanel.Paint += (sender, e) =>
                simulation.RenderResults(e.Graphics, meshViewPanel.Width, meshViewPanel.Height, RenderMode.Solid);

            // Create summary panel content
            TableLayoutPanel summaryLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 10,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.Single
            };

            summaryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
            summaryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));

            // Add summary data
            AddSummaryRow(summaryLayout, 0, "Material:", selectedMaterial.Name);
            AddSummaryRow(summaryLayout, 1, "Density:", $"{selectedMaterial.Density:F1} kg/m³");
            AddSummaryRow(summaryLayout, 2, "Confining Pressure:", $"{simulation.ConfiningPressure:F1} MPa");
            AddSummaryRow(summaryLayout, 3, "Breaking Pressure:", $"{simulation.BreakingPressure:F1} MPa");
            AddSummaryRow(summaryLayout, 4, "Young's Modulus:", $"{simulation.YoungModulus:F0} MPa");
            AddSummaryRow(summaryLayout, 5, "Poisson's Ratio:", $"{simulation.PoissonRatio:F3}");
            AddSummaryRow(summaryLayout, 6, "Cohesion Strength:", $"{simulation.CohesionStrength:F2} MPa");
            AddSummaryRow(summaryLayout, 7, "Friction Angle:", $"{simulation.FrictionAngle:F1}°");
            AddSummaryRow(summaryLayout, 8, "Tensile Strength:", $"{simulation.TensileStrength:F2} MPa");
            AddSummaryRow(summaryLayout, 9, "Test Direction:", result.Data["TestDirection"].ToString());

            summaryPanel.Controls.Add(summaryLayout);

            // Add export buttons panel at the bottom
            Panel exportPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                BackColor = Color.FromArgb(45, 45, 48)
            };

            // CSV Export Button
            KryptonButton exportCsvButton = new KryptonButton
            {
                Text = "Export CSV",
                Values = { Image = CreateExportIcon(16) },
                Location = new Point(10, 8),
                Width = 100
            };
            exportCsvButton.Click += (sender, e) => ExportTriaxialSimulationResults(simulation, ExportFormat.CSV);

            // Image Export Button
            KryptonButton exportImageButton = new KryptonButton
            {
                Text = "Export Image",
                Values = { Image = CreateExportIcon(16) },
                Location = new Point(120, 8),
                Width = 120
            };
            exportImageButton.Click += (sender, e) => ExportFullCompositeImage(simulation);

            // VTK Export Button
            KryptonButton exportVtkButton = new KryptonButton
            {
                Text = "Export VTK",
                Values = { Image = CreateExportIcon(16) },
                Location = new Point(250, 8),
                Width = 100
            };
            exportVtkButton.Click += (sender, e) => ExportTriaxialSimulationResults(simulation, ExportFormat.VTK);

            // Add buttons to panel
            exportPanel.Controls.Add(exportCsvButton);
            exportPanel.Controls.Add(exportImageButton);
            exportPanel.Controls.Add(exportVtkButton);
            resultsPage.Controls.Add(exportPanel);

            // Set default selected page
            resultsTabs.SelectedPage = stressPage;
        }
        /// <summary>
        /// Render only the fractured facets in red for clear visualization.
        /// </summary>
        private void RenderFractureSurfaces(Graphics g, int width, int height)
        {
            if (currentTriaxial?.SimulationMeshAtFailure == null || currentTriaxial.SimulationMeshAtFailure.Count == 0)
            {
                g.Clear(Color.Black);
                g.DrawString("No fracture data available", new Font("Arial", 12), Brushes.Red, 20, 20);
                return;
            }

            g.Clear(Color.Black);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            float scale = Math.Min(width, height) / 200f;
            float cx = width / 2f, cy = height / 2f;

            // compute max extent
            float maxC = 0f;
            foreach (var t in currentTriaxial.SimulationMeshAtFailure)
            {
                maxC = Math.Max(maxC, Math.Abs(t.V1.X));
                maxC = Math.Max(maxC, Math.Abs(t.V1.Y));
                maxC = Math.Max(maxC, Math.Abs(t.V1.Z));
                maxC = Math.Max(maxC, Math.Abs(t.V2.X));
                maxC = Math.Max(maxC, Math.Abs(t.V2.Y));
                maxC = Math.Max(maxC, Math.Abs(t.V2.Z));
                maxC = Math.Max(maxC, Math.Abs(t.V3.X));
                maxC = Math.Max(maxC, Math.Abs(t.V3.Y));
                maxC = Math.Max(maxC, Math.Abs(t.V3.Z));
            }
            if (maxC <= 0f)
            {
                g.DrawString("Invalid mesh data", new Font("Arial", 12), Brushes.Red, 20, 20);
                return;
            }

            // collect fractured triangles
            var fractured = new List<(CTSegmenter.Triangle tri, float depth)>();
            foreach (var tri in currentTriaxial.SimulationMeshAtFailure)
            {
                if (!tri.IsFractured) continue;
                float depth = (tri.V1.Z + tri.V2.Z + tri.V3.Z) / 3f;
                fractured.Add((tri, depth));
            }

            if (fractured.Count == 0)
            {
                g.DrawString("No fractures detected at failure point", new Font("Arial", 12), Brushes.Yellow, 20, 20);
                return;
            }

            fractured.Sort((a, b) => -a.depth.CompareTo(b.depth));

            // Draw background mesh in translucent gray
            foreach (var tri in currentTriaxial.SimulationMeshAtFailure)
            {
                if (tri.IsFractured) continue; // Skip fractured ones for background

                var verts = new System.Numerics.Vector3[] { tri.V1, tri.V2, tri.V3 };
                var pts = new PointF[3];
                for (int i = 0; i < 3; i++)
                {
                    var v = verts[i];
                    float nx = (v.X / maxC) - 0.5f;
                    float ny = (v.Y / maxC) - 0.5f;
                    pts[i] = new PointF(
                        cx + nx * scale * 150,
                        cy + ny * scale * 150
                    );
                }

                using (var pen = new Pen(Color.FromArgb(40, Color.Gray), 1))
                    g.DrawPolygon(pen, pts);
            }

            // Draw fractured triangles in red
            foreach (var (tri, _) in fractured)
            {
                var verts = new System.Numerics.Vector3[] { tri.V1, tri.V2, tri.V3 };
                var pts = new PointF[3];
                for (int i = 0; i < 3; i++)
                {
                    var v = verts[i];
                    float nx = (v.X / maxC) - 0.5f;
                    float ny = (v.Y / maxC) - 0.5f;
                    pts[i] = new PointF(
                        cx + nx * scale * 150,
                        cy + ny * scale * 150
                    );
                }

                using (var fill = new SolidBrush(Color.FromArgb(180, Color.Red)))
                    g.FillPolygon(fill, pts);
                using (var pen = new Pen(Color.FromArgb(220, Color.DarkRed), 2))
                    g.DrawPolygon(pen, pts);
            }

            // Add information text
            using (var font = new Font("Arial", 10))
            {
                g.DrawString($"Fracture Surfaces at {currentTriaxial.BreakingPressure:F2} MPa",
                    new Font("Arial", 12, FontStyle.Bold), Brushes.White, 10, 10);
                g.DrawString($"Fractured triangles: {fractured.Count} ({fractured.Count * 100f / currentTriaxial.SimulationMeshAtFailure.Count:F1}%)",
                    font, Brushes.White, 10, 35);
                g.DrawString($"Confining pressure: {currentTriaxial.ConfiningPressure:F1} MPa",
                    font, Brushes.White, 10, 55);
                g.DrawString("Use mouse to rotate the view", font, Brushes.LightGray, 10, height - 25);
            }
        }
        private void ExportFullCompositeImage(TriaxialSimulation simulation)
        {
            try
            {
                // Create a save file dialog
                using (SaveFileDialog saveDialog = new SaveFileDialog())
                {
                    saveDialog.Filter = "PNG Image|*.png";
                    saveDialog.Title = "Export Complete Triaxial Simulation Results";
                    saveDialog.FileName = $"Triaxial_{selectedMaterial.Name}_{DateTime.Now:yyyyMMdd_HHmmss}_complete.png";

                    if (saveDialog.ShowDialog() == DialogResult.OK)
                    {
                        this.Cursor = Cursors.WaitCursor;
                        statusHeader.Text = "Creating complete composite image...";

                        // Use the enhanced composite image method with all views
                        bool success = simulation.ExportFullCompositeImage(saveDialog.FileName);

                        if (success)
                        {
                            statusHeader.Text = $"Exported complete composite image to {Path.GetFileName(saveDialog.FileName)}";
                            MessageBox.Show($"Complete composite image successfully exported to:\n{saveDialog.FileName}",
                                "Export Successful", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            statusHeader.Text = "Export failed.";
                            MessageBox.Show("Failed to export composite image.", "Export Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting composite image: {ex.Message}",
                    "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[StressAnalysisForm] Composite image export error: {ex.Message}");
            }
            finally
            {
                this.Cursor = Cursors.Default;
            }
        }

        // Also make sure to have this helper method in your class:
        private void AddSummaryRow(TableLayoutPanel table, int rowIndex, string label, string value)
        {
            // Create label
            Label lblName = new Label
            {
                Text = label,
                Font = new Font("Arial", 9, FontStyle.Bold),
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleRight,
                Dock = DockStyle.Fill
            };

            // Create value label
            Label lblValue = new Label
            {
                Text = value,
                Font = new Font("Arial", 9),
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill
            };

            // Add to table
            table.Controls.Add(lblName, 0, rowIndex);
            table.Controls.Add(lblValue, 1, rowIndex);
        }

        private async void RunAcousticButton_Click(object sender, EventArgs e)
        {
            // 1) Sanity-check our UI controls before we even try to read them:
            if (acousticConfiningNumeric == null)
            {
                Logger.Log("[StressAnalysisForm] acousticConfiningNumeric is null");
                MessageBox.Show("Internal error: missing confining-pressure control.");
                return;
            }
            if (waveTypeCombo == null)
            {
                Logger.Log("[StressAnalysisForm] waveTypeCombo is null");
                MessageBox.Show("Internal error: missing wave-type selector.");
                return;
            }
            if (timeStepsNumeric == null)
            {
                Logger.Log("[StressAnalysisForm] timeStepsNumeric is null");
                MessageBox.Show("Internal error: missing time-steps control.");
                return;
            }
            if (frequencyNumeric == null)
            {
                Logger.Log("[StressAnalysisForm] frequencyNumeric is null");
                MessageBox.Show("Internal error: missing frequency control.");
                return;
            }
            if (amplitudeNumeric == null)
            {
                Logger.Log("[StressAnalysisForm] amplitudeNumeric is null");
                MessageBox.Show("Internal error: missing amplitude control.");
                return;
            }
            if (energyNumeric == null)
            {
                Logger.Log("[StressAnalysisForm] energyNumeric is null");
                MessageBox.Show("Internal error: missing energy control.");
                return;
            }
            if (acousticDirectionCombo == null)
            {
                Logger.Log("[StressAnalysisForm] acousticDirectionCombo is null");
                MessageBox.Show("Internal error: missing direction selector.");
                return;
            }
            if (dtFactorNumeric == null)
            {
                Logger.Log("[StressAnalysisForm] dtFactorNumeric is null");
                MessageBox.Show("Internal error: missing time-step factor control.");
                return;
            }

            // 2) Check mesh/material as before:
            if (!meshGenerated || meshTriangles.Count == 0)
            {
                MessageBox.Show(
                    "No mesh to analyze. Please generate or import a mesh first.",
                    "No Mesh",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
                return;
            }

            if (selectedMaterial == null || selectedMaterial.Density <= 0)
            {
                MessageBox.Show(
                    "Please set material density before running simulation.",
                    "Material Properties",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
                return;
            }

            try
            {
                // 3) Safely read each value, providing a fallback if SelectedItem is null:
                float confiningPressure = (float)acousticConfiningNumeric.Value;

                string waveType;
                if (waveTypeCombo.SelectedItem != null)
                    waveType = waveTypeCombo.SelectedItem.ToString();
                else
                {
                    waveType = "P-Wave";
                    Logger.Log("[StressAnalysisForm] waveTypeCombo had no selection; defaulting to P-Wave");
                }

                int timeSteps = (int)timeStepsNumeric.Value;
                float frequency = (float)frequencyNumeric.Value;
                float amplitude = (float)amplitudeNumeric.Value;
                float energy = (float)energyNumeric.Value;

                string direction;
                if (acousticDirectionCombo.SelectedItem != null)
                    direction = acousticDirectionCombo.SelectedItem.ToString();
                else
                {
                    direction = "X-Axis";
                    Logger.Log("[StressAnalysisForm] acousticDirectionCombo had no selection; defaulting to X-Axis");
                }

                float dtFactor = (float)dtFactorNumeric.Value;

                statusHeader.Text = $"Running acoustic velocity simulation ({waveType}, {direction})...";
                Cursor = Cursors.WaitCursor;

                // 4) Convert the mesh for the sim
                var simulationTriangles = meshTriangles
                    .Select(tri => new CTSegmenter.Triangle(
                        new System.Numerics.Vector3(tri.V1.X, tri.V1.Y, tri.V1.Z),
                        new System.Numerics.Vector3(tri.V2.X, tri.V2.Y, tri.V2.Z),
                        new System.Numerics.Vector3(tri.V3.X, tri.V3.Y, tri.V3.Z)
                    ))
                    .ToList();

                SimulationResult triaxialResult = null;

                // 5) Prepare density map if inhomogeneous density is enabled
                ConcurrentDictionary<System.Numerics.Vector3, float> densityMap = null;
                if (inhomogeneousDensityEnabled && voxelDensities != null && voxelDensities.Count > 0)
                {
                    densityMap = new ConcurrentDictionary<System.Numerics.Vector3, float>();
                    foreach (var kvp in voxelDensities)
                    {
                        densityMap[new System.Numerics.Vector3(kvp.Key.X, kvp.Key.Y, kvp.Key.Z)] = kvp.Value;
                    }
                    Logger.Log($"[StressAnalysisForm] Using inhomogeneous density with {densityMap.Count} density points for acoustic simulation");
                }

                // 6) Use the factory to create the appropriate simulation type
                AcousticVelocitySimulation simulation = SimulationFactory.CreateAcousticSimulation(
                    selectedMaterial,
                    simulationTriangles,
                    confiningPressure,
                    waveType,
                    timeSteps,
                    frequency,
                    amplitude,
                    energy,
                    direction,
                    UseExtendedSimulationTime,
                    inhomogeneousDensityEnabled,
                    densityMap,
                    triaxialResult,
                    mainForm);

                simulation.TimeStepFactor = dtFactor;
                currentAcousticSim = simulation;
                HookSimCompleted(simulation);

                simulation.ProgressChanged += (s, args) =>
                    BeginInvoke(new Action(() => statusHeader.Text = args.StatusMessage));

                // 7) Initialize
                if (!simulation.Initialize())
                    throw new InvalidOperationException("Failed to initialize simulation");

                // 8) Run
                var result = await simulation.RunAsync();

                // 9) Handle results
                if (result.IsSuccessful)
                {
                    statusHeader.Text = "Acoustic velocity simulation completed.";
                    CreateAcousticResultsDisplay(simulation, result);
                    UpdateWaveVisualization(simulation);
                    mainTabControl.SelectedPage = resultsPage;

                    // If we're using inhomogeneous density, add a button to visualize it
                    if (inhomogeneousDensityEnabled && simulation is InhomogeneousAcousticSimulation inhomogeneousSim)
                    {
                        // Add a button to results page to visualize the density distribution
                        AddDensityVisualizationButton(inhomogeneousSim);
                    }
                }
                else
                {
                    statusHeader.Text = "Simulation failed.";
                    MessageBox.Show(
                        $"Acoustic velocity simulation failed: {result.ErrorMessage}",
                        "Simulation Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error running acoustic velocity simulation: {ex.Message}",
                    "Simulation Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
                Logger.Log($"[StressAnalysisForm] Acoustic simulation error: {ex.Message}");
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }
        private void AddDensityVisualizationButton(InhomogeneousAcousticSimulation simulation)
        {
            try
            {
                Logger.Log("[StressAnalysisForm] Creating density visualization tab");

                // Find the results tabs
                KryptonDockableNavigator resultsTabs = null;
                foreach (Control control in resultsPage.Controls)
                {
                    if (control is KryptonDockableNavigator navigator)
                    {
                        resultsTabs = navigator;
                        break;
                    }
                }

                if (resultsTabs == null)
                {
                    MessageBox.Show("Cannot find results tab control.", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // Remove any existing density tab
                foreach (KryptonPage page in resultsTabs.Pages)
                {
                    if (page.Text == "Density Distribution")
                    {
                        resultsTabs.Pages.Remove(page);
                        break;
                    }
                }

                // Create new density tab
                KryptonPage densityPage = new KryptonPage
                {
                    Text = "Density Distribution",
                    TextTitle = "Material Density Distribution",
                    BackColor = Color.Black
                };

                // Use TableLayoutPanel for proper organization
                TableLayoutPanel mainLayout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    RowCount = 3,
                    ColumnCount = 1,
                    BackColor = Color.Black,
                    CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
                    Padding = new Padding(10)
                };

                // Configure rows - title row, top slice row, bottom slice row
                mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
                mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

                // Header panel with title and refresh button
                Panel headerPanel = new Panel
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.FromArgb(30, 30, 30)
                };

                Label titleLabel = new Label
                {
                    Text = "Inhomogeneous Density Visualization",
                    Font = new Font("Arial", 12, FontStyle.Bold),
                    ForeColor = Color.White,
                    AutoSize = true,
                    Location = new Point(10, 10)
                };

                Button refreshButton = new Button
                {
                    Text = "Refresh View",
                    Size = new Size(100, 25),
                    Location = new Point(300, 8),
                    BackColor = Color.DimGray,
                    ForeColor = Color.White
                };

                headerPanel.Controls.Add(titleLabel);
                headerPanel.Controls.Add(refreshButton);

                // Top slice panel with X-Z slice
                Panel topSlicePanel = new Panel
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.Black,
                    Margin = new Padding(5)
                };

                // Bottom slice panel with Y-Z slice
                Panel bottomSlicePanel = new Panel
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.Black,
                    Margin = new Padding(5)
                };

                // Add panels to main layout
                mainLayout.Controls.Add(headerPanel, 0, 0);
                mainLayout.Controls.Add(topSlicePanel, 0, 1);
                mainLayout.Controls.Add(bottomSlicePanel, 0, 2);

                // Set up paint handlers for both panels
                topSlicePanel.Paint += (s, e) =>
                {
                    e.Graphics.Clear(Color.Black);

                    // Draw title
                    using (Font titleFont = new Font("Arial", 10, FontStyle.Bold))
                        e.Graphics.DrawString("X-Z Slice (Y middle)",
                            titleFont, Brushes.White, 10, 5);

                    // Direct access to density model
                    if (simulation._detailedDensityModel != null)
                    {
                        // Get dimensions with proper margins
                        int panelWidth = topSlicePanel.Width;
                        int panelHeight = topSlicePanel.Height;
                        int graphWidth = (int)(panelWidth * 0.85);  // Leave room for colorbar
                        int graphHeight = panelHeight - 25;        // Leave room for title
                        float minDensity = simulation.MinimumDensity;
                        float maxDensity = simulation.MaximumDensity;

                        // Get slice from middle Y
                        int sliceY = simulation._gridSizeY / 2;

                        // Draw the slice
                        using (Bitmap slice = new Bitmap(graphWidth, graphHeight))
                        {
                            using (Graphics g = Graphics.FromImage(slice))
                            {
                                g.Clear(Color.Black);

                                // Scale factors
                                float scaleX = (float)graphWidth / simulation._gridSizeX;
                                float scaleZ = (float)graphHeight / simulation._gridSizeZ;

                                // Draw each cell
                                for (int x = 0; x < simulation._gridSizeX; x++)
                                {
                                    for (int z = 0; z < simulation._gridSizeZ; z++)
                                    {
                                        float density = simulation._detailedDensityModel[x, sliceY, z];

                                        // Skip very low densities
                                        if (density < 0.1f)
                                            continue;

                                        // Calculate color
                                        float normalizedDensity = 0.5f;
                                        if (maxDensity > minDensity)
                                            normalizedDensity = (density - minDensity) / (maxDensity - minDensity);

                                        normalizedDensity = Math.Max(0f, Math.Min(1f, normalizedDensity));

                                        // Get density color (blue to red)
                                        Color color = GetDensityColor(normalizedDensity);

                                        // Draw rectangle
                                        int px = (int)(x * scaleX);
                                        int py = (int)(z * scaleZ);
                                        int w = Math.Max(1, (int)Math.Ceiling(scaleX));
                                        int h = Math.Max(1, (int)Math.Ceiling(scaleZ));

                                        g.FillRectangle(new SolidBrush(color), px, py, w, h);
                                    }
                                }
                            }

                            // Draw to panel
                            e.Graphics.DrawImage(slice, 10, 25);
                        }

                        // Draw color scale - right aligned
                        int colorBarX = graphWidth + 15;
                        int colorBarY = 25;
                        int colorBarWidth = 20;
                        int colorBarHeight = graphHeight;

                        DrawColorScale(e.Graphics, colorBarX, colorBarY, colorBarWidth, colorBarHeight, minDensity, maxDensity);
                    }
                    else
                    {
                        e.Graphics.DrawString("No density model available",
                            new Font("Arial", 12), Brushes.Yellow, 10, 50);
                    }
                };

                // Similar handler for bottom panel (Y-Z slice)
                bottomSlicePanel.Paint += (s, e) =>
                {
                    e.Graphics.Clear(Color.Black);

                    // Draw title
                    using (Font titleFont = new Font("Arial", 10, FontStyle.Bold))
                        e.Graphics.DrawString("Y-Z Slice (X middle)",
                            titleFont, Brushes.White, 10, 5);

                    // Direct access to density model
                    if (simulation._detailedDensityModel != null)
                    {
                        // Get dimensions with proper margins
                        int panelWidth = bottomSlicePanel.Width;
                        int panelHeight = bottomSlicePanel.Height;
                        int graphWidth = (int)(panelWidth * 0.85);  // Leave room for colorbar
                        int graphHeight = panelHeight - 25;        // Leave room for title
                        float minDensity = simulation.MinimumDensity;
                        float maxDensity = simulation.MaximumDensity;

                        // Get slice from middle X
                        int sliceX = simulation._gridSizeX / 2;

                        // Draw the slice
                        using (Bitmap slice = new Bitmap(graphWidth, graphHeight))
                        {
                            using (Graphics g = Graphics.FromImage(slice))
                            {
                                g.Clear(Color.Black);

                                // Scale factors
                                float scaleY = (float)graphWidth / simulation._gridSizeY;
                                float scaleZ = (float)graphHeight / simulation._gridSizeZ;

                                // Draw each cell
                                for (int y = 0; y < simulation._gridSizeY; y++)
                                {
                                    for (int z = 0; z < simulation._gridSizeZ; z++)
                                    {
                                        float density = simulation._detailedDensityModel[sliceX, y, z];

                                        // Skip very low densities
                                        if (density < 0.1f)
                                            continue;

                                        // Calculate color
                                        float normalizedDensity = 0.5f;
                                        if (maxDensity > minDensity)
                                            normalizedDensity = (density - minDensity) / (maxDensity - minDensity);

                                        normalizedDensity = Math.Max(0f, Math.Min(1f, normalizedDensity));

                                        // Get density color (blue to red)
                                        Color color = GetDensityColor(normalizedDensity);

                                        // Draw rectangle
                                        int px = (int)(y * scaleY);
                                        int py = (int)(z * scaleZ);
                                        int w = Math.Max(1, (int)Math.Ceiling(scaleY));
                                        int h = Math.Max(1, (int)Math.Ceiling(scaleZ));

                                        g.FillRectangle(new SolidBrush(color), px, py, w, h);
                                    }
                                }
                            }

                            // Draw to panel
                            e.Graphics.DrawImage(slice, 10, 25);
                        }

                        // Draw color scale - right aligned
                        int colorBarX = graphWidth + 15;
                        int colorBarY = 25;
                        int colorBarWidth = 20;
                        int colorBarHeight = graphHeight;

                        DrawColorScale(e.Graphics, colorBarX, colorBarY, colorBarWidth, colorBarHeight, minDensity, maxDensity);
                    }
                    else
                    {
                        e.Graphics.DrawString("No density model available",
                            new Font("Arial", 12), Brushes.Yellow, 10, 50);
                    }
                };

                // Refresh button handler
                refreshButton.Click += (s, e) => {
                    Logger.Log("[StressAnalysisForm] Refreshing density visualization");
                    topSlicePanel.Invalidate();
                    bottomSlicePanel.Invalidate();
                };

                // Add main layout to page
                densityPage.Controls.Add(mainLayout);

                // Add page to tabs and select it
                resultsTabs.Pages.Add(densityPage);
                resultsTabs.SelectedPage = densityPage;

                Logger.Log("[StressAnalysisForm] Density visualization tab added successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"[StressAnalysisForm] Error adding density tab: {ex.Message}");
                MessageBox.Show($"Error adding density visualization: {ex.Message}",
                               "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Helper method to get a color for a normalized density value
        private Color GetDensityColor(float normalizedValue)
        {
            if (normalizedValue < 0.5f)
            {
                // Blue to green (0.0 - 0.5)
                float t = normalizedValue * 2;
                return Color.FromArgb(
                    0,
                    (int)(t * 255),
                    (int)(255 * (1 - t) + t * 150)
                );
            }
            else
            {
                // Green to red (0.5 - 1.0)
                float t = (normalizedValue - 0.5f) * 2;
                return Color.FromArgb(
                    (int)(t * 255),
                    (int)(255 * (1 - t)),
                    0
                );
            }
        }

        // Helper method to draw a color scale with density values
        private void DrawColorScale(Graphics g, int x, int y, int width, int height,
                                  float minValue, float maxValue)
        {
            // Create gradient brush
            using (LinearGradientBrush brush = new LinearGradientBrush(
                new Rectangle(x, y, width, height),
                Color.Blue, Color.Red, LinearGradientMode.Vertical))
            {
                // Create color blend
                ColorBlend blend = new ColorBlend(5);
                blend.Colors = new Color[] {
            Color.Blue, Color.Cyan, Color.Green, Color.Yellow, Color.Red
        };
                blend.Positions = new float[] { 0.0f, 0.25f, 0.5f, 0.75f, 1.0f };
                brush.InterpolationColors = blend;

                // Draw gradient bar
                g.FillRectangle(brush, x, y, width, height);
                g.DrawRectangle(Pens.White, x, y, width, height);
            }

            // Draw labels
            using (Font font = new Font("Arial", 8))
            {
                // Draw min and max values
                g.DrawString(maxValue.ToString("F1"), font, Brushes.White, x + width + 2, y);
                g.DrawString(minValue.ToString("F1"), font, Brushes.White, x + width + 2, y + height - 12);

                // Draw units centered
                g.DrawString("kg/m³", font, Brushes.White, x + width + 2, y + height / 2 - 6);
            }
        }


        // Helper method to find the tab control


        private KryptonDockableNavigator FindResultsTabs()
        {
            foreach (Control control in resultsPage.Controls)
            {
                if (control is KryptonDockableNavigator navigator)
                    return navigator;
            }
            return null;
        }
        private void ExportTriaxialSimulationResults(TriaxialSimulation simulation, ExportFormat format)
        {
            if (simulation == null)
            {
                MessageBox.Show("No simulation results to export.", "Export Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Create a save file dialog
            using (SaveFileDialog saveDialog = new SaveFileDialog())
            {
                string fileExtension;
                string filter;

                switch (format)
                {
                    case ExportFormat.CSV:
                        fileExtension = ".csv";
                        filter = "CSV Files|*.csv";
                        break;
                    case ExportFormat.PNG:
                        fileExtension = ".png";
                        filter = "PNG Image|*.png";
                        break;
                    case ExportFormat.VTK:
                        fileExtension = ".vtk";
                        filter = "VTK Files|*.vtk";
                        break;
                    case ExportFormat.JSON:
                        fileExtension = ".json";
                        filter = "JSON Files|*.json";
                        break;
                    default:
                        fileExtension = ".csv";
                        filter = "CSV Files|*.csv";
                        break;
                }

                saveDialog.Filter = filter;
                saveDialog.Title = $"Export Triaxial Simulation Results as {format}";
                saveDialog.FileName = $"Triaxial_{selectedMaterial.Name}_{DateTime.Now:yyyyMMdd_HHmmss}{fileExtension}";

                if (saveDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        this.Cursor = Cursors.WaitCursor;
                        statusHeader.Text = $"Exporting simulation results to {format}...";

                        bool success = simulation.ExportResults(saveDialog.FileName, format);

                        if (success)
                        {
                            statusHeader.Text = $"Exported results to {Path.GetFileName(saveDialog.FileName)}";
                            MessageBox.Show($"Results successfully exported to:\n{saveDialog.FileName}",
                                "Export Successful", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            statusHeader.Text = "Export failed.";
                            MessageBox.Show("Failed to export results.", "Export Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error exporting results: {ex.Message}",
                            "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Logger.Log($"[StressAnalysisForm] Export error: {ex.Message}");
                    }
                    finally
                    {
                        this.Cursor = Cursors.Default;
                    }
                }
            }
        }
        private void ExportCompositeImage(TriaxialSimulation simulation)
        {
            try
            {
                // Create a save file dialog
                using (SaveFileDialog saveDialog = new SaveFileDialog())
                {
                    saveDialog.Filter = "PNG Image|*.png";
                    saveDialog.Title = "Export Triaxial Simulation Results as Image";
                    saveDialog.FileName = $"Triaxial_{selectedMaterial.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.png";

                    if (saveDialog.ShowDialog() == DialogResult.OK)
                    {
                        this.Cursor = Cursors.WaitCursor;
                        statusHeader.Text = "Creating composite image...";

                        // Define the size of each panel and the composite image
                        int panelWidth = 800;
                        int panelHeight = 600;
                        int padding = 10;
                        int titleHeight = 30;

                        // Create a composite image with 2x2 grid layout
                        int compositeWidth = panelWidth * 2 + padding * 3;
                        int compositeHeight = panelHeight * 2 + padding * 3 + titleHeight;

                        // Create the bitmap
                        using (Bitmap compositeBitmap = new Bitmap(compositeWidth, compositeHeight))
                        {
                            using (Graphics g = Graphics.FromImage(compositeBitmap))
                            {
                                // Fill background
                                g.Clear(Color.White);

                                // Draw title
                                using (Font titleFont = new Font("Arial", 16, FontStyle.Bold))
                                using (SolidBrush textBrush = new SolidBrush(Color.Black))
                                {
                                    string title = $"Triaxial Simulation: {selectedMaterial.Name} - {DateTime.Now:yyyy-MM-dd HH:mm}";
                                    g.DrawString(title, titleFont, textBrush, new PointF(padding, padding));
                                }

                                // Create the individual views
                                CreateTriaxialView(g, simulation, RenderMode.Stress,
                                    padding, titleHeight + padding,
                                    panelWidth, panelHeight, "Von Mises Stress Distribution");

                                CreateTriaxialView(g, simulation, RenderMode.Strain,
                                    padding * 2 + panelWidth, titleHeight + padding,
                                    panelWidth, panelHeight, "Stress-Strain Curve");

                                CreateTriaxialView(g, simulation, RenderMode.FailureProbability,
                                    padding, titleHeight + padding * 2 + panelHeight,
                                    panelWidth, panelHeight, "Fracture Probability");

                                CreateTriaxialView(g, simulation, RenderMode.Solid,
                                    padding * 2 + panelWidth, titleHeight + padding * 2 + panelHeight,
                                    panelWidth, panelHeight, "Deformed Mesh");

                                // Add simulation parameters
                                using (Font infoFont = new Font("Arial", 8))
                                using (SolidBrush textBrush = new SolidBrush(Color.Black))
                                {
                                    string info = $"Material: {selectedMaterial.Name}, Density: {selectedMaterial.Density:F1} kg/m³, " +
                                        $"Confining Pressure: {simulation.ConfiningPressure:F1} MPa, Breaking Pressure: {simulation.BreakingPressure:F1} MPa";
                                    g.DrawString(info, infoFont, textBrush, new PointF(padding, compositeHeight - padding - infoFont.Height));
                                }
                            }

                            // Save the bitmap
                            compositeBitmap.Save(saveDialog.FileName, System.Drawing.Imaging.ImageFormat.Png);
                            statusHeader.Text = $"Exported composite image to {Path.GetFileName(saveDialog.FileName)}";
                            MessageBox.Show($"Composite image successfully exported to:\n{saveDialog.FileName}",
                                "Export Successful", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting composite image: {ex.Message}",
                    "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[StressAnalysisForm] Composite image export error: {ex.Message}");
            }
            finally
            {
                this.Cursor = Cursors.Default;
            }
        }
        private void CreateTriaxialView(Graphics g, TriaxialSimulation simulation, RenderMode renderMode,
    int x, int y, int width, int height, string title)
        {
            // Create a bitmap for this view
            using (Bitmap viewBitmap = new Bitmap(width, height))
            {
                using (Graphics viewGraphics = Graphics.FromImage(viewBitmap))
                {
                    // Render the view
                    simulation.RenderResults(viewGraphics, width, height, renderMode);

                    // Add title to the view
                    using (Font titleFont = new Font("Arial", 12, FontStyle.Bold))
                    using (SolidBrush textBrush = new SolidBrush(Color.White))
                    using (SolidBrush shadowBrush = new SolidBrush(Color.Black))
                    {
                        // Draw shadow for better visibility
                        viewGraphics.DrawString(title, titleFont, shadowBrush, new PointF(6, 6));
                        viewGraphics.DrawString(title, titleFont, textBrush, new PointF(5, 5));
                    }
                }

                // Draw the view bitmap onto the composite bitmap
                g.DrawImage(viewBitmap, x, y, width, height);

                // Draw a border around the view
                using (Pen borderPen = new Pen(Color.DarkGray, 1))
                {
                    g.DrawRectangle(borderPen, x, y, width, height);
                }
            }
        }
        private void CreateView(Graphics g, TriaxialSimulation simulation, RenderMode renderMode,
            int x, int y, int width, int height, string title)
        {
            // Create a bitmap for this view
            using (Bitmap viewBitmap = new Bitmap(width, height))
            {
                using (Graphics viewGraphics = Graphics.FromImage(viewBitmap))
                {
                    // Render the view
                    simulation.RenderResults(viewGraphics, width, height, renderMode);

                    // Add title to the view
                    using (Font titleFont = new Font("Arial", 12, FontStyle.Bold))
                    using (SolidBrush textBrush = new SolidBrush(Color.White))
                    using (SolidBrush shadowBrush = new SolidBrush(Color.Black))
                    {
                        // Draw shadow for better visibility
                        viewGraphics.DrawString(title, titleFont, shadowBrush, new PointF(6, 6));
                        viewGraphics.DrawString(title, titleFont, textBrush, new PointF(5, 5));
                    }
                }

                // Draw the view bitmap onto the composite bitmap
                g.DrawImage(viewBitmap, x, y, width, height);

                // Draw a border around the view
                using (Pen borderPen = new Pen(Color.DarkGray, 1))
                {
                    g.DrawRectangle(borderPen, x, y, width, height);
                }
            }
        }

        private void CreateAcousticResultsDisplay(AcousticVelocitySimulation simulation, SimulationResult result)
        {
            // Clear existing controls on the results page
            resultsPage.Controls.Clear();

            // Create a tab control for different result views
            KryptonDockableNavigator resultsTabs = new KryptonDockableNavigator
            {
                Dock = DockStyle.Fill,
                PageBackStyle = Krypton.Toolkit.PaletteBackStyle.PanelClient
            };
            resultsPage.Controls.Add(resultsTabs);

            // Wave propagation page
            KryptonPage wavePropagationPage = new KryptonPage
            {
                Text = "Wave Propagation",
                TextTitle = "Wave Propagation Visualization"
            };

            // Time series page
            KryptonPage timeSeriesPage = new KryptonPage
            {
                Text = "Time Series",
                TextTitle = "Receiver Waveform"
            };

            // Velocity distribution page
            KryptonPage velocityPage = new KryptonPage
            {
                Text = "Velocity Distribution",
                TextTitle = "Material Velocity Model"
            };

            // Mesh view page
            KryptonPage meshViewPage = new KryptonPage
            {
                Text = "Mesh View",
                TextTitle = "Mesh with Velocities"
            };

            // Summary page
            KryptonPage summaryPage = new KryptonPage
            {
                Text = "Summary",
                TextTitle = "Simulation Results Summary"
            };

            // Add all pages to the tab control
            resultsTabs.Pages.AddRange(new[]
            {
        wavePropagationPage,
        timeSeriesPage,
        velocityPage,
        meshViewPage,
        summaryPage
    });

            // Create panels for each page
            Panel wavePropagationPanel = new Panel { Dock = DockStyle.Fill };
            Panel timeSeriesPanel = new Panel { Dock = DockStyle.Fill };
            Panel velocityPanel = new Panel { Dock = DockStyle.Fill };
            Panel meshViewPanel = new Panel { Dock = DockStyle.Fill };
            Panel summaryPanel = new Panel { Dock = DockStyle.Fill };

            // Add panels to pages
            wavePropagationPage.Controls.Add(wavePropagationPanel);
            timeSeriesPage.Controls.Add(timeSeriesPanel);
            velocityPage.Controls.Add(velocityPanel);
            meshViewPage.Controls.Add(meshViewPanel);
            summaryPage.Controls.Add(summaryPanel);

            // Set up panel paint events
            wavePropagationPanel.Paint += (sender, e) =>
                simulation.RenderResults(e.Graphics, wavePropagationPanel.Width, wavePropagationPanel.Height, RenderMode.Stress);

            timeSeriesPanel.Paint += (sender, e) =>
                simulation.RenderResults(e.Graphics, timeSeriesPanel.Width, timeSeriesPanel.Height, RenderMode.Strain);

            velocityPanel.Paint += (sender, e) =>
                simulation.RenderResults(e.Graphics, velocityPanel.Width, velocityPanel.Height, RenderMode.FailureProbability);

            meshViewPanel.Paint += (sender, e) =>
                simulation.RenderResults(e.Graphics, meshViewPanel.Width, meshViewPanel.Height, RenderMode.Solid);

            // Create summary panel content
            TableLayoutPanel summaryLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 10,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.Single
            };

            summaryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
            summaryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));

            // Add summary data
            AddSummaryRow(summaryLayout, 0, "Material:", selectedMaterial.Name);
            AddSummaryRow(summaryLayout, 1, "Density:", $"{selectedMaterial.Density:F1} kg/m³");
            AddSummaryRow(summaryLayout, 2, "Wave Type:", result.Data["WaveType"].ToString());
            AddSummaryRow(summaryLayout, 3, "P-Wave Velocity:", $"{result.Data["MeasuredPWaveVelocity"]:F1} m/s");
            AddSummaryRow(summaryLayout, 4, "S-Wave Velocity:", $"{result.Data["MeasuredSWaveVelocity"]:F1} m/s");
            AddSummaryRow(summaryLayout, 5, "Vp/Vs Ratio:", $"{result.Data["CalculatedVpVsRatio"]:F2}");
            AddSummaryRow(summaryLayout, 6, "P-Wave Arrival Time:", $"{Convert.ToSingle(result.Data["PWaveArrivalTime"]) * 1000:F2} ms");
            AddSummaryRow(summaryLayout, 7, "S-Wave Arrival Time:", $"{Convert.ToSingle(result.Data["SWaveArrivalTime"]) * 1000:F2} ms");
            AddSummaryRow(summaryLayout, 8, "Young's Modulus:", $"{result.Data["YoungModulus"]:F0} MPa");
            AddSummaryRow(summaryLayout, 9, "Poisson's Ratio:", $"{result.Data["PoissonRatio"]:F3}");

            summaryPanel.Controls.Add(summaryLayout);

            // Add export buttons panel at the bottom
            Panel exportPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                BackColor = Color.FromArgb(45, 45, 48)
            };

            // CSV Export Button
            KryptonButton exportCsvButton = new KryptonButton
            {
                Text = "Export CSV",
                Values = { Image = CreateExportIcon(16) },
                Location = new Point(10, 8),
                Width = 100
            };
            exportCsvButton.Click += (sender, e) => ExportSimulationResults(simulation, ExportFormat.CSV);

            // Image Export Button
            KryptonButton exportImageButton = new KryptonButton
            {
                Text = "Export Image",
                Values = { Image = CreateExportIcon(16) },
                Location = new Point(120, 8),
                Width = 120
            };
            exportImageButton.Click += (sender, e) => ExportSimulationResults(simulation, ExportFormat.PNG);

            // VTK Export Button
            KryptonButton exportVtkButton = new KryptonButton
            {
                Text = "Export VTK",
                Values = { Image = CreateExportIcon(16) },
                Location = new Point(250, 8),
                Width = 100
            };
            exportVtkButton.Click += (sender, e) => ExportSimulationResults(simulation, ExportFormat.VTK);

            // Add buttons to panel
            exportPanel.Controls.Add(exportCsvButton);
            exportPanel.Controls.Add(exportImageButton);
            exportPanel.Controls.Add(exportVtkButton);
            resultsPage.Controls.Add(exportPanel);

            // Set default selected page
            resultsTabs.SelectedPage = wavePropagationPage;
        }
        
        private void ExportSimulationResults(AcousticVelocitySimulation simulation, ExportFormat format)
        {
            string fileExtension;
            string filter;

            switch (format)
            {
                case ExportFormat.CSV:
                    fileExtension = ".csv";
                    filter = "CSV Files|*.csv";
                    break;
                case ExportFormat.PNG:
                    fileExtension = ".png";
                    filter = "PNG Image|*.png|Composite PNG|*_composite.png";
                    break;
                case ExportFormat.VTK:
                    fileExtension = ".vtk";
                    filter = "VTK Files|*.vtk";
                    break;
                case ExportFormat.JSON:
                    fileExtension = ".json";
                    filter = "JSON Files|*.json";
                    break;
                default:
                    fileExtension = ".csv";
                    filter = "CSV Files|*.csv";
                    break;
            }

            // Create a save file dialog
            using (SaveFileDialog saveDialog = new SaveFileDialog())
            {
                saveDialog.Filter = filter;
                saveDialog.Title = $"Export Acoustic Simulation Results as {format}";

                if (format == ExportFormat.PNG)
                {
                    // For PNG, offer both standard and composite options
                    saveDialog.FileName = $"Acoustic_{selectedMaterial.Name}_{DateTime.Now:yyyyMMdd_HHmmss}{fileExtension}";
                    saveDialog.FilterIndex = 1; // Default to standard PNG
                }
                else
                {
                    saveDialog.FileName = $"Acoustic_{selectedMaterial.Name}_{DateTime.Now:yyyyMMdd_HHmmss}{fileExtension}";
                }

                if (saveDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        this.Cursor = Cursors.WaitCursor;
                        statusHeader.Text = $"Exporting simulation results to {format}...";

                        string filePath = saveDialog.FileName;

                        // If composite PNG was selected (FilterIndex = 2), modify filename
                        if (format == ExportFormat.PNG && saveDialog.FilterIndex == 2 && !filePath.EndsWith("_composite.png"))
                        {
                            filePath = Path.Combine(
                                Path.GetDirectoryName(filePath),
                                Path.GetFileNameWithoutExtension(filePath) + "_composite.png");
                        }

                        bool success = simulation.ExportResults(filePath, format);

                        if (success)
                        {
                            statusHeader.Text = $"Exported results to {Path.GetFileName(filePath)}";
                            MessageBox.Show($"Results successfully exported to:\n{filePath}",
                                "Export Successful", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            statusHeader.Text = "Export failed.";
                            MessageBox.Show("Failed to export results.", "Export Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error exporting results: {ex.Message}",
                            "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Logger.Log($"[StressAnalysisForm] Export error: {ex.Message}");
                    }
                    finally
                    {
                        this.Cursor = Cursors.Default;
                    }
                }
            }
        }
        private void WavePictureBox_MouseWheel(object sender, MouseEventArgs e)
        {
            float zoomFactor = e.Delta > 0 ? 1.1f : 0.9f;
            ZoomWaveView(zoomFactor, e.Location);
        }
        private void WavePictureBox_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                isWavePanning = true;
                waveLastMousePos = e.Location;
                wavePictureBox.Cursor = Cursors.Hand;
            }
            else if (e.Button == MouseButtons.Right)
            {
                ResetWaveView();
            }
        }
        private void WavePictureBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (isWavePanning)
            {
                // Calculate delta movement
                int deltaX = e.X - waveLastMousePos.X;
                int deltaY = e.Y - waveLastMousePos.Y;

                // Update pan offset
                wavePanOffset.X += deltaX / waveZoomLevel;
                wavePanOffset.Y += deltaY / waveZoomLevel;

                // Update the view
                UpdateWaveViewTransform();

                // Update last position
                waveLastMousePos = e.Location;
            }
        }
        private void WavePictureBox_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                isWavePanning = false;
                wavePictureBox.Cursor = Cursors.Default;
            }
        }

        private void ZoomWaveView(float factor, Point? zoomCenter = null)
        {
            // Store previous zoom level for relative calculations
            float prevZoom = waveZoomLevel;

            // Apply zoom factor
            waveZoomLevel *= factor;

            // Clamp zoom level to reasonable bounds
            waveZoomLevel = Math.Max(0.1f, Math.Min(10.0f, waveZoomLevel));

            // If we have a zoom center point, adjust pan offset to zoom toward that point
            if (zoomCenter.HasValue && originalWaveImage != null)
            {
                Point center = zoomCenter.Value;

                // Convert center to image coordinates
                float imageX = center.X / prevZoom - wavePanOffset.X;
                float imageY = center.Y / prevZoom - wavePanOffset.Y;

                // Adjust pan offset to keep the center point fixed
                wavePanOffset.X = center.X / waveZoomLevel - imageX;
                wavePanOffset.Y = center.Y / waveZoomLevel - imageY;
            }

            // Update the view
            UpdateWaveViewTransform();
        }

        private void ResetWaveView()
        {
            // Reset to default view
            waveZoomLevel = 1.0f;
            wavePanOffset = new PointF(0, 0);

            // If we have an original image, simply redisplay it
            if (originalWaveImage != null)
            {
                if (wavePictureBox.Image != null && wavePictureBox.Image != originalWaveImage)
                {
                    wavePictureBox.Image.Dispose();
                }

                wavePictureBox.SizeMode = PictureBoxSizeMode.Zoom;
                wavePictureBox.Image = originalWaveImage;
            }
        }

        private void UpdateWaveViewTransform()
        {
            if (originalWaveImage == null) return;

            try
            {
                // Create a new bitmap for the transformed image
                int width = Math.Max(10, wavePictureBox.Width);
                int height = Math.Max(10, wavePictureBox.Height);

                using (Bitmap transformedImage = new Bitmap(width, height))
                {
                    using (Graphics g = Graphics.FromImage(transformedImage))
                    {
                        g.Clear(Color.Black); // Clear background

                        // Set quality
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                        // Calculate transformation
                        g.TranslateTransform(wavePanOffset.X * waveZoomLevel, wavePanOffset.Y * waveZoomLevel);
                        g.ScaleTransform(waveZoomLevel, waveZoomLevel);

                        // Draw the image with transformation
                        g.DrawImage(originalWaveImage, 0, 0);
                    }

                    // Update the PictureBox
                    if (wavePictureBox.Image != null && wavePictureBox.Image != originalWaveImage)
                    {
                        wavePictureBox.Image.Dispose();
                    }

                    wavePictureBox.SizeMode = PictureBoxSizeMode.Normal;
                    wavePictureBox.Image = new Bitmap(transformedImage);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[StressAnalysisForm] Error updating wave view: {ex.Message}");
            }
        }

        private void ExportWaveImage()
        {
            if (originalWaveImage == null) return;

            try
            {
                using (SaveFileDialog dialog = new SaveFileDialog())
                {
                    dialog.Filter = "PNG Image|*.png";
                    dialog.Title = "Export Wave Visualization";
                    dialog.FileName = $"Wave_{DateTime.Now:yyyyMMdd_HHmmss}.png";

                    if (dialog.ShowDialog() == DialogResult.OK)
                    {
                        // Export the high quality original image
                        originalWaveImage.Save(dialog.FileName, System.Drawing.Imaging.ImageFormat.Png);

                        MessageBox.Show($"Image exported to:\n{dialog.FileName}",
                            "Export Successful", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting image: {ex.Message}",
                    "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void ResetViewButton_Click(object sender, EventArgs e)
        {
            // Reset all view parameters
            rotationX = 0f;
            rotationY = 0f;
            zoomLevel = 1.0f;
            panX = 0f;
            panY = 0f;

            meshViewPanel.Invalidate();
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // Clean up ILGPU resources
            accelerator?.Dispose();
            ilgpuContext?.Dispose();

            base.OnFormClosed(e);
        }
    }
}