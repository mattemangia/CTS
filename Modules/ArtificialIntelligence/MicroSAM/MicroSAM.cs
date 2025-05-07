using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS.Modules.ArtificialIntelligence.MicroSAM
{
    internal class MicroSAM
    {
        // Main form components
        private Form samForm;

        private TableLayoutPanel mainLayout;
        private Panel xyPanel, xzPanel, yzPanel;
        private PictureBox xyViewer, xzViewer, yzViewer;
        private Panel controlPanel;
        private TextBox modelPathTextBox;
        private RadioButton rbCPU, rbGPU;
        private Button btnLoadModel, btnApply, btnClose;
        private Button btnPositivePrompt, btnNegativePrompt, btnZeroShotPrompt;
        private CheckBox chkAutoUpdate, chkShowBoundingBoxes;
        private Label statusLabel;

        private LRUCache<int, Bitmap> xySliceCache;
        private LRUCache<int, Bitmap> xzSliceCache;
        private LRUCache<int, Bitmap> yzSliceCache;
        private HashSet<int> cachedXYKeys = new HashSet<int>();
        private HashSet<int> cachedXZKeys = new HashSet<int>();
        private HashSet<int> cachedYZKeys = new HashSet<int>();
        private const int CACHE_SIZE = 30; // Number of slices to cache

        private enum ActiveView
        { XY, XZ, YZ }

        private ActiveView currentActiveView = ActiveView.XY;

        private Button btnXYView, btnXZView, btnYZView;

        // Slice control components
        private Label lblSliceXY;

        private TrackBar sliderXY;
        private NumericUpDown numXY;
        private CheckBox chkSyncWithMainView;

        // Orthoslice controls
        private Label lblSliceXZ;

        private TrackBar sliderXZ;
        private NumericUpDown numXZ;

        private Label lblSliceYZ;
        private TrackBar sliderYZ;
        private NumericUpDown numYZ;

        // Scrollbar controls
        private HScrollBar xyHScroll, xzHScroll, yzHScroll;

        private VScrollBar xyVScroll, xzVScroll, yzVScroll;

        // Zoom and pan state variables
        private float xyZoom = 1.0f, xzZoom = 1.0f, yzZoom = 1.0f;

        private Point xyPan = Point.Empty, xzPan = Point.Empty, yzPan = Point.Empty;

        // Image bounds tracking
        private Rectangle xyImageBounds = Rectangle.Empty;

        private Rectangle xzImageBounds = Rectangle.Empty;
        private Rectangle yzImageBounds = Rectangle.Empty;

        // References to parent application components
        private MainForm mainForm;

        private Material selectedMaterial;
        private AnnotationManager annotationManager;
        private Dictionary<int, bool> pointTypes = new Dictionary<int, bool>(); // Maps point ID to isPositive

        // ONNX model components
        private InferenceSession encoderSession;

        private InferenceSession decoderSession;
        private string encoderPath;
        private string decoderPath;
        private bool useGPU = false;

        // Slices information
        private int xySlice, xzRow, yzCol;

        private DenseTensor<float> cachedImageEmbed = null;
        private int cachedFeatureSlice = -1;
        private int featureCacheRadius = 3; // How many slices to reuse encoder features for

        // Segmentation results
        private byte[,] segmentationMask;

        private List<Rectangle> boundingBoxes = new List<Rectangle>();
        private bool showBoundingBoxes = false;

        // Prompt mode
        private enum PromptMode
        { Positive, Negative, ZeroShot }

        private PromptMode currentMode = PromptMode.Positive;

        // candidate masks produced by zero‑shot
        private List<byte[,]> zeroShotMasks = null;

        private List<Rectangle> zeroShotBoxes = null;
        private List<int> zeroShotActive = null;

        // Slice change callback
        private Action<int> sliceChangeCallback;

        /// <summary>
        /// Constructor that allows using MicroSAM without showing the UI
        /// </summary>
        /// <param name="mainForm">Reference to the main application form</param>
        /// <param name="selectedMaterial">The material to use for segmentation</param>
        /// <param name="annotationManager">The annotation manager for storing points</param>
        /// <param name="showUI">Whether to show the user interface (default: true)</param>
        public MicroSAM(MainForm mainForm, Material selectedMaterial, AnnotationManager annotationManager, bool showUI = true)
        {
            Logger.Log("[MicroSAM] Creating MicroSAM interface");

            this.mainForm = mainForm;
            this.selectedMaterial = selectedMaterial;
            this.annotationManager = annotationManager;

            // Get current slice positions from MainForm
            xySlice = mainForm.CurrentSlice;
            xzRow = mainForm.XzSliceY;
            yzCol = mainForm.YzSliceX;

            // Initialize caches
            xySliceCache = new LRUCache<int, Bitmap>(CACHE_SIZE);
            xzSliceCache = new LRUCache<int, Bitmap>(CACHE_SIZE);
            yzSliceCache = new LRUCache<int, Bitmap>(CACHE_SIZE);

            if (showUI)
            {
                // Initialize form and UI
                InitializeForm();

                // Register for slice changes from MainForm
                sliceChangeCallback = UpdateSliceFromMainForm;
                mainForm.RegisterSliceChangeCallback(sliceChangeCallback);
            }

            // Set default model paths
            string onnxDirectory = Path.Combine(Application.StartupPath, "ONNX");
            encoderPath = Path.Combine(onnxDirectory, "micro-sam-encoder.onnx");
            decoderPath = Path.Combine(onnxDirectory, "micro-sam-decoder.onnx");

            if (showUI)
            {
                modelPathTextBox.Text = onnxDirectory;
            }

            // Try to load models automatically
            try
            {
                Logger.Log("[MicroSAM] Attempting to load ONNX models");
                LoadONNXModels();

                if (showUI)
                {
                    statusLabel.Text = "Models loaded successfully";
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[MicroSAM] Error loading models: {ex.Message}");

                if (showUI)
                {
                    statusLabel.Text = $"Error loading models: {ex.Message}";
                }
            }
        }

        public MicroSAM(MainForm mainForm, Material selectedMaterial, AnnotationManager annotationManager)
            : this(mainForm, selectedMaterial, annotationManager, true)
        {
        }

        private void InitializeForm()
        {
            Logger.Log("[MicroSAM] Initializing form");
            samForm = new Form
            {
                Text = "MicroSAM - CT",
                Size = new Size(1100, 850),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.Sizable,
                Icon=Properties.Resources.favicon
            };

            // Main layout with 2x2 grid
            mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 2,
                Padding = new Padding(5)
            };

            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            // Create viewers with scrollbars
            xyPanel = CreateViewerPanel("XY Slice");
            xzPanel = CreateViewerPanel("XZ Slice");
            yzPanel = CreateViewerPanel("YZ Slice");

            // Create control panel
            controlPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10),
                BackColor = Color.WhiteSmoke,
                AutoScroll = true
            };

            // --------- Model Loading Section ---------
            Label lblModelPath = new Label
            {
                Text = "Model Directory:",
                Location = new Point(10, 10),
                AutoSize = true
            };

            modelPathTextBox = new TextBox
            {
                Location = new Point(10, 30),
                Width = 280,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            Button btnBrowse = new Button
            {
                Text = "Browse...",
                Location = new Point(300, 29),
                Width = 80,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnBrowse.Click += (s, e) => BrowseForModelDirectory();

            // --------- Device Selection Section ---------
            Label lblDevice = new Label
            {
                Text = "Execution Device:",
                Location = new Point(10, 60),
                AutoSize = true
            };

            rbCPU = new RadioButton
            {
                Text = "CPU",
                Location = new Point(10, 80),
                Checked = true,
                AutoSize = true
            };

            rbGPU = new RadioButton
            {
                Text = "GPU",
                Location = new Point(80, 80),
                AutoSize = true
            };

            btnLoadModel = new Button
            {
                Text = "Load Models",
                Location = new Point(10, 110),
                Width = 100,
                Height = 30
            };
            btnLoadModel.Click += (s, e) => LoadONNXModels();

            // --------- Prompt Mode Section ---------
            Label lblPromptMode = new Label
            {
                Text = "Prompt Mode:",
                Location = new Point(10, 150),
                AutoSize = true
            };

            btnPositivePrompt = new Button
            {
                Text = "Positive Prompt (+)",
                Location = new Point(10, 170),
                Width = 110,
                Height = 30,
                BackColor = Color.LightGreen
            };
            btnPositivePrompt.Click += (s, e) =>
            {
                currentMode = PromptMode.Positive;
                btnPositivePrompt.BackColor = Color.LightGreen;
                btnNegativePrompt.BackColor = SystemColors.Control;
                btnZeroShotPrompt.BackColor = SystemColors.Control;
                Logger.Log("[MicroSAM] Switched to positive prompt mode");
            };

            btnNegativePrompt = new Button
            {
                Text = "Negative Prompt (-)",
                Location = new Point(130, 170),
                Width = 110,
                Height = 30
            };
            btnNegativePrompt.Click += (s, e) =>
            {
                currentMode = PromptMode.Negative;
                btnPositivePrompt.BackColor = SystemColors.Control;
                btnNegativePrompt.BackColor = Color.LightPink;
                btnZeroShotPrompt.BackColor = SystemColors.Control;
                Logger.Log("[MicroSAM] Switched to negative prompt mode");
            };

            // Add new Zero-Shot prompt button
            btnZeroShotPrompt = new Button
            {
                Text = "Zero-Shot Prompt",
                Location = new Point(250, 170),
                Width = 110,
                Height = 30
            };
            btnZeroShotPrompt.Click += (s, e) =>
            {
                currentMode = PromptMode.ZeroShot;
                btnPositivePrompt.BackColor = SystemColors.Control;
                btnNegativePrompt.BackColor = SystemColors.Control;
                btnZeroShotPrompt.BackColor = Color.LightBlue;
                Logger.Log("[MicroSAM] Switched to zero-shot prompt mode");

                // Auto-run zero-shot segmentation when selected
                Task.Run(() => PerformZeroShotSegmentation());
            };

            // --------- Auto-Update and Bounding Box Section ---------
            chkAutoUpdate = new CheckBox
            {
                Text = "Auto-update when annotations change",
                Location = new Point(10, 210),
                AutoSize = true,
                Checked = true
            };

            // Add new checkbox for showing bounding boxes
            chkShowBoundingBoxes = new CheckBox
            {
                Text = "Show bounding boxes (instead of masks)",
                Location = new Point(10, 230),
                AutoSize = true,
                Checked = false
            };
            chkShowBoundingBoxes.CheckedChanged += (s, e) =>
            {
                showBoundingBoxes = chkShowBoundingBoxes.Checked;
                UpdateViewers();
                Logger.Log($"[MicroSAM] Bounding box mode {(showBoundingBoxes ? "enabled" : "disabled")}");
            };

            // --------- Active View for Segmentation Section ---------
            Label lblActiveView = new Label
            {
                Text = "Active View for Segmentation:",
                Location = new Point(10, 260),
                AutoSize = true
            };

            btnXYView = new Button
            {
                Text = "XY View",
                Location = new Point(10, 280),
                Width = 80,
                Height = 30,
                BackColor = Color.LightSkyBlue
            };
            btnXYView.Click += (s, e) =>
            {
                currentActiveView = ActiveView.XY;
                UpdateActiveViewButtons();
                Logger.Log("[MicroSAM] Switched to XY view for segmentation");

                // Check if there are points in this view and auto-update if enabled
                if (chkAutoUpdate.Checked)
                {
                    var viewPoints = GetRelevantPointsForCurrentView();
                    if (viewPoints.Count > 0 || currentMode == PromptMode.ZeroShot)
                    {
                        if (currentMode == PromptMode.ZeroShot)
                            Task.Run(() => PerformZeroShotSegmentation());
                        else
                            Task.Run(() => PerformSegmentation());
                    }
                }
            };

            btnXZView = new Button
            {
                Text = "XZ View",
                Location = new Point(100, 280),
                Width = 80,
                Height = 30
            };
            btnXZView.Click += (s, e) =>
            {
                currentActiveView = ActiveView.XZ;
                UpdateActiveViewButtons();
                Logger.Log("[MicroSAM] Switched to XZ view for segmentation");

                // Check if there are points in this view and auto-update if enabled
                if (chkAutoUpdate.Checked)
                {
                    var viewPoints = GetRelevantPointsForCurrentView();
                    if (viewPoints.Count > 0 || currentMode == PromptMode.ZeroShot)
                    {
                        if (currentMode == PromptMode.ZeroShot)
                            Task.Run(() => PerformZeroShotSegmentation());
                        else
                            Task.Run(() => PerformSegmentation());
                    }
                }
            };

            btnYZView = new Button
            {
                Text = "YZ View",
                Location = new Point(190, 280),
                Width = 80,
                Height = 30
            };
            btnYZView.Click += (s, e) =>
            {
                currentActiveView = ActiveView.YZ;
                UpdateActiveViewButtons();
                Logger.Log("[MicroSAM] Switched to YZ view for segmentation");

                // Check if there are points in this view and auto-update if enabled
                if (chkAutoUpdate.Checked)
                {
                    var viewPoints = GetRelevantPointsForCurrentView();
                    if (viewPoints.Count > 0 || currentMode == PromptMode.ZeroShot)
                    {
                        if (currentMode == PromptMode.ZeroShot)
                            Task.Run(() => PerformZeroShotSegmentation());
                        else
                            Task.Run(() => PerformSegmentation());
                    }
                }
            };

            // --------- Action Buttons ---------
            btnApply = new Button
            {
                Text = "Apply Mask",
                Location = new Point(10, 320),
                Width = 100,
                Height = 30
            };
            btnApply.Click += (s, e) => ApplySegmentationMask();

            Button btnApplyToVolume = new Button
            {
                Text = "Apply to Volume",
                Location = new Point(230, 320),
                Width = 120,
                Height = 30
            };
            btnApplyToVolume.Click += (s, e) => ApplyToVolume();
            controlPanel.Controls.Add(btnApplyToVolume);

            btnClose = new Button
            {
                Text = "Close",
                Location = new Point(120, 320),
                Width = 100,
                Height = 30
            };
            btnClose.Click += (s, e) => samForm.Close();

            statusLabel = new Label
            {
                Text = "Ready",
                Location = new Point(10, 360),
                AutoSize = true
            };

            // --------- XY Slice Controls ---------
            lblSliceXY = new Label
            {
                Text = $"XY Slice: {xySlice} / {(mainForm.GetDepth() > 0 ? mainForm.GetDepth() - 1 : 0)}",
                Location = new Point(10, 390),
                AutoSize = true
            };

            sliderXY = new TrackBar
            {
                Minimum = 0,
                Maximum = mainForm.GetDepth() > 0 ? mainForm.GetDepth() - 1 : 0,
                Value = xySlice,
                Location = new Point(10, 410),
                Width = 220,
                TickStyle = TickStyle.None
            };

            numXY = new NumericUpDown
            {
                Minimum = 0,
                Maximum = sliderXY.Maximum,
                Value = xySlice,
                Location = new Point(240, 410),
                Width = 60
            };

            // Add event handlers for XY slice controls
            sliderXY.Scroll += (s, e) =>
            {
                xySlice = sliderXY.Value;
                UpdateSliceControls();
                UpdateViewers();
            };

            numXY.ValueChanged += (s, e) =>
            {
                if (numXY.Value != xySlice)
                {
                    xySlice = (int)numXY.Value;
                    UpdateSliceControls();
                    UpdateViewers();
                }
            };

            // --------- XZ Slice Controls ---------
            lblSliceXZ = new Label
            {
                Text = $"XZ Row: {xzRow} / {(mainForm.GetHeight() > 0 ? mainForm.GetHeight() - 1 : 0)}",
                Location = new Point(10, 440),
                AutoSize = true
            };

            sliderXZ = new TrackBar
            {
                Minimum = 0,
                Maximum = mainForm.GetHeight() > 0 ? mainForm.GetHeight() - 1 : 0,
                Value = xzRow,
                Location = new Point(10, 460),
                Width = 220,
                TickStyle = TickStyle.None
            };

            numXZ = new NumericUpDown
            {
                Minimum = 0,
                Maximum = sliderXZ.Maximum,
                Value = xzRow,
                Location = new Point(240, 460),
                Width = 60
            };

            // Add event handlers for XZ slice controls
            sliderXZ.Scroll += (s, e) =>
            {
                xzRow = sliderXZ.Value;
                UpdateSliceControls();
                UpdateViewers();
            };

            numXZ.ValueChanged += (s, e) =>
            {
                if (numXZ.Value != xzRow)
                {
                    xzRow = (int)numXZ.Value;
                    UpdateSliceControls();
                    UpdateViewers();
                }
            };

            // --------- YZ Slice Controls ---------
            lblSliceYZ = new Label
            {
                Text = $"YZ Column: {yzCol} / {(mainForm.GetWidth() > 0 ? mainForm.GetWidth() - 1 : 0)}",
                Location = new Point(10, 490),
                AutoSize = true
            };

            sliderYZ = new TrackBar
            {
                Minimum = 0,
                Maximum = mainForm.GetWidth() > 0 ? mainForm.GetWidth() - 1 : 0,
                Value = yzCol,
                Location = new Point(10, 510),
                Width = 220,
                TickStyle = TickStyle.None
            };

            numYZ = new NumericUpDown
            {
                Minimum = 0,
                Maximum = sliderYZ.Maximum,
                Value = yzCol,
                Location = new Point(240, 510),
                Width = 60
            };

            // Add event handlers for YZ slice controls
            sliderYZ.Scroll += (s, e) =>
            {
                yzCol = sliderYZ.Value;
                UpdateSliceControls();
                UpdateViewers();
            };

            numYZ.ValueChanged += (s, e) =>
            {
                if (numYZ.Value != yzCol)
                {
                    yzCol = (int)numYZ.Value;
                    UpdateSliceControls();
                    UpdateViewers();
                }
            };

            // --------- Sync with Main View Checkbox ---------
            chkSyncWithMainView = new CheckBox
            {
                Text = "Sync with main view",
                Location = new Point(10, 540),
                Checked = true,
                AutoSize = true
            };

            chkSyncWithMainView.CheckedChanged += (s, e) =>
            {
                // If synchronization is turned on, update the main view
                if (chkSyncWithMainView.Checked)
                {
                    mainForm.CurrentSlice = xySlice;
                    mainForm.XzSliceY = xzRow;
                    mainForm.YzSliceX = yzCol;
                }
            };

            // --------- Help Text ---------
            Label lblHelp = new Label
            {
                Text = "Instructions:\n" +
                      "- Click on the image to add points\n" +
                      "- Use '+' for foreground, '-' for background points\n" +
                      "- Zero-Shot mode segments without points\n" +
                      "- Toggle bounding box mode to see region outlines\n" +
                      "- Use mousewheel to zoom, drag to pan\n" +
                      "- Apply mask to save the segmentation",
                Location = new Point(10, 570),
                Size = new Size(300, 140),
                BorderStyle = BorderStyle.FixedSingle
            };

            // --------- Selected Material Information ---------
            Label lblMaterial = new Label
            {
                Text = $"Selected Material: {selectedMaterial.Name}",
                Location = new Point(10, 720),
                AutoSize = true,
                ForeColor = selectedMaterial.Color
            };

            // Add all controls to the panel
            controlPanel.Controls.AddRange(new Control[] {
                // Model Loading
                lblModelPath, modelPathTextBox, btnBrowse,

                // Device Selection
                lblDevice, rbCPU, rbGPU, btnLoadModel,

                // Prompt Mode
                lblPromptMode, btnPositivePrompt, btnNegativePrompt, btnZeroShotPrompt,

                // Auto-Update and Bounding Box
                chkAutoUpdate, chkShowBoundingBoxes,

                // Active View Selection
                lblActiveView, btnXYView, btnXZView, btnYZView,

                // Action Buttons
                btnApply, btnClose, statusLabel,

                // Slice Controls
                lblSliceXY, sliderXY, numXY,
                lblSliceXZ, sliderXZ, numXZ,
                lblSliceYZ, sliderYZ, numYZ,

                // Sync Checkbox
                chkSyncWithMainView,

                // Help and Material Info
                lblHelp, lblMaterial
            });

            // Add all components to main layout
            mainLayout.Controls.Add(xyPanel, 0, 0);
            mainLayout.Controls.Add(yzPanel, 1, 0);
            mainLayout.Controls.Add(xzPanel, 0, 1);
            mainLayout.Controls.Add(controlPanel, 1, 1);

            samForm.Controls.Add(mainLayout);

            // Handle form events
            samForm.FormClosing += (s, e) =>
            {
                // Clean up resources
                encoderSession?.Dispose();
                decoderSession?.Dispose();

                // Clear all bitmap caches
                ClearCaches();

                // Clean up the viewer images
                xyViewer.Image?.Dispose();
                xzViewer.Image?.Dispose();
                yzViewer.Image?.Dispose();

                // Unregister callback
                mainForm.UnregisterSliceChangeCallback(sliceChangeCallback);

                Logger.Log("[MicroSAM] Form closing, resources cleaned up");
            };

            // Initially load the slices
            UpdateViewers();
        }

        private void UpdateActiveViewButtons()
        {
            btnXYView.BackColor = currentActiveView == ActiveView.XY ? Color.LightSkyBlue : SystemColors.Control;
            btnXZView.BackColor = currentActiveView == ActiveView.XZ ? Color.LightSkyBlue : SystemColors.Control;
            btnYZView.BackColor = currentActiveView == ActiveView.YZ ? Color.LightSkyBlue : SystemColors.Control;
        }

        private Panel CreateViewerPanel(string title)
        {
            Panel container = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(5),
                BorderStyle = BorderStyle.FixedSingle
            };

            Label titleLabel = new Label
            {
                Text = title,
                Dock = DockStyle.Top,
                BackColor = Color.Black,
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter,
                Height = 25
            };

            PictureBox viewer = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Normal,
                BackColor = Color.Black
            };

            HScrollBar hScroll = new HScrollBar
            {
                Dock = DockStyle.Bottom,
                Height = 20
            };

            VScrollBar vScroll = new VScrollBar
            {
                Dock = DockStyle.Right,
                Width = 20
            };

            // Store the created controls in class variables
            if (title == "XY Slice")
            {
                xyViewer = viewer;
                xyHScroll = hScroll;
                xyVScroll = vScroll;

                // Setup events for XY viewer
                SetupXYViewerEvents();
            }
            else if (title == "XZ Slice")
            {
                xzViewer = viewer;
                xzHScroll = hScroll;
                xzVScroll = vScroll;

                // Setup events for XZ viewer
                SetupXZViewerEvents();
            }
            else if (title == "YZ Slice")
            {
                yzViewer = viewer;
                yzHScroll = hScroll;
                yzVScroll = vScroll;

                // Setup events for YZ viewer
                SetupYZViewerEvents();
            }

            container.Controls.Add(viewer);
            container.Controls.Add(hScroll);
            container.Controls.Add(vScroll);
            container.Controls.Add(titleLabel);

            return container;
        }

        private void SetupXYViewerEvents()
        {
            // XY viewer scroll events
            xyHScroll.Scroll += (s, e) =>
            {
                xyPan.X = -xyHScroll.Value;
                xyViewer.Invalidate();
            };

            xyVScroll.Scroll += (s, e) =>
            {
                xyPan.Y = -xyVScroll.Value;
                xyViewer.Invalidate();
            };

            // XY viewer mouse wheel for zooming
            xyViewer.MouseWheel += (s, e) =>
            {
                float oldZoom = xyZoom;
                // Adjust zoom based on wheel direction
                if (e.Delta > 0)
                    xyZoom = Math.Min(10.0f, xyZoom * 1.1f);
                else
                    xyZoom = Math.Max(0.1f, xyZoom * 0.9f);

                // Adjust scrollbars based on new zoom
                UpdateXYScrollbars();

                // Redraw
                xyViewer.Invalidate();
                Logger.Log($"[MicroSAM] XY zoom changed to {xyZoom:F2}");
            };

            // XY viewer mouse events for panning and point placement
            Point lastPos = Point.Empty;
            bool isPanning = false;

            xyViewer.MouseDown += (s, e) =>
            {
                if (currentMode == PromptMode.ZeroShot && zeroShotBoxes != null)
                {
                    int hit = PickZeroShotBox(e.X, e.Y, currentActiveView);
                    if (hit >= 0)
                    {
                        if (zeroShotActive.Contains(hit))
                            zeroShotActive.Remove(hit);   // toggle OFF
                        else
                            zeroShotActive.Add(hit);      // toggle ON

                        // Union of all active masks (or null if none)
                        segmentationMask = BuildMergedMask(zeroShotActive);

                        // Outline only the boxes that are still OFF
                        boundingBoxes = zeroShotBoxes
                                        .Where((_, idx) => !zeroShotActive.Contains(idx))
                                        .ToList();

                        UpdateViewers();

                        statusLabel.Text = zeroShotActive.Count == 0
                            ? "No mask selected – click boxes to add"
                            : $"{zeroShotActive.Count} mask(s) selected – click boxes to add/remove";
                        return; // suppress point‑adding logic
                    }
                }
                if (e.Button == MouseButtons.Left)
                {
                    // If in zero-shot mode, don't add points, but perform segmentation
                    if (currentMode == PromptMode.ZeroShot)
                    {
                        Task.Run(() => PerformZeroShotSegmentation());
                        return;
                    }

                    // Convert mouse coordinates to image coordinates
                    float pointX = (e.X - xyPan.X) / xyZoom;
                    float pointY = (e.Y - xyPan.Y) / xyZoom;

                    // Check if within image bounds
                    if (pointX >= 0 && pointX < mainForm.GetWidth() &&
                        pointY >= 0 && pointY < mainForm.GetHeight())
                    {
                        bool pointDeleted = false;
                        var points = annotationManager.GetPointsForSlice(xySlice);
                        foreach (var point in points.ToList())
                        {
                            float dx = point.X - pointX;
                            float dy = point.Y - pointY;
                            float distSq = dx * dx + dy * dy;

                            // If within 10 pixels of a point, delete it
                            if (distSq < 100)
                            { // 10^2
                                annotationManager.RemovePoint(point.ID);
                                // Also remove from our types dictionary
                                if (pointTypes.ContainsKey(point.ID))
                                    pointTypes.Remove(point.ID);
                                pointDeleted = true;
                                Logger.Log($"[MicroSAM] Deleted point at ({point.X}, {point.Y}, {point.Z})");

                                // Check if we should auto-update after deleting a point
                                if (chkAutoUpdate.Checked)
                                {
                                    var remainingPoints = GetRelevantPointsForCurrentView();
                                    if (remainingPoints.Count > 0)
                                    {
                                        Task.Run(() => PerformSegmentation());
                                    }
                                    else
                                    {
                                        // Clear the segmentation mask if no points remain
                                        segmentationMask = null;
                                        boundingBoxes.Clear();
                                        UpdateViewers();
                                    }
                                }
                                break;
                            }
                        }

                        // If we didn't delete a point, add a new one
                        if (!pointDeleted)
                        {
                            // Store point type in a standardized way
                            bool isPositive = (currentMode == PromptMode.Positive);

                            // Use consistent naming - either "Positive" or "Negative" as the actual type
                            string pointType = isPositive ? "Positive" : "Negative";
                            string label = pointType + "_" + selectedMaterial.Name;

                            // Add the point
                            AnnotationPoint newPoint = annotationManager.AddPoint(pointX, pointY, xySlice, label);

                            // Track whether this is a positive or negative point in our dictionary
                            pointTypes[newPoint.ID] = isPositive;

                            Logger.Log($"[MicroSAM] Added {pointType.ToLower()} point at ({pointX}, {pointY})");

                            // Auto-update segmentation if enabled
                            if (chkAutoUpdate.Checked)
                            {
                                Task.Run(() => PerformSegmentation());
                            }
                        }

                        // Update view with new or deleted point
                        UpdateViewers();
                    }
                    else
                    {
                        Logger.Log("[MicroSAM] Attempted to place point outside image bounds");
                    }
                }
                else if (e.Button == MouseButtons.Right)
                {
                    // Start panning with right mouse button
                    isPanning = true;
                    lastPos = e.Location;
                }
            };

            xyViewer.MouseMove += (s, e) =>
            {
                if (isPanning && e.Button == MouseButtons.Right)
                {
                    // Calculate the move delta
                    int dx = e.X - lastPos.X;
                    int dy = e.Y - lastPos.Y;

                    // Update the panel position
                    xyPan.X += dx;
                    xyPan.Y += dy;
                    UpdateXYScrollbars();

                    lastPos = e.Location;
                    xyViewer.Invalidate();
                }
            };

            xyViewer.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    isPanning = false;
                }
            };

            // Paint event for custom rendering with checkerboard background
            xyViewer.Paint += (s, e) =>
            {
                // Clear background
                e.Graphics.Clear(Color.Black);

                if (xyViewer.Image != null)
                {
                    int imgWidth = xyViewer.Image.Width;
                    int imgHeight = xyViewer.Image.Height;

                    // Calculate the image bounds
                    xyImageBounds = new Rectangle(
                        xyPan.X,
                        xyPan.Y,
                        (int)(imgWidth * xyZoom),
                        (int)(imgHeight * xyZoom));

                    // Draw checkerboard pattern for the entire visible area
                    DrawCheckerboardBackground(e.Graphics, xyViewer.ClientRectangle);

                    // Draw the image with interpolation
                    e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    e.Graphics.DrawImage(xyViewer.Image, xyImageBounds);

                    // Draw a border around the image
                    using (Pen borderPen = new Pen(Color.DarkGray, 1))
                    {
                        e.Graphics.DrawRectangle(borderPen, xyImageBounds);
                    }

                    // Draw annotations
                    DrawAnnotationsOnXY(e.Graphics);

                    // Draw bounding boxes if enabled
                    if (showBoundingBoxes && currentActiveView == ActiveView.XY && boundingBoxes.Count > 0)
                    {
                        DrawBoundingBoxes(e.Graphics, ActiveView.XY);
                    }
                }
            };
        }

        private void SetupXZViewerEvents()
        {
            // XZ viewer scroll events
            xzHScroll.Scroll += (s, e) =>
            {
                xzPan.X = -xzHScroll.Value;
                xzViewer.Invalidate();
            };

            xzVScroll.Scroll += (s, e) =>
            {
                xzPan.Y = -xzVScroll.Value;
                xzViewer.Invalidate();
            };

            // XZ viewer mouse wheel for zooming
            xzViewer.MouseWheel += (s, e) =>
            {
                float oldZoom = xzZoom;
                // Adjust zoom based on wheel direction
                if (e.Delta > 0)
                    xzZoom = Math.Min(10.0f, xzZoom * 1.1f);
                else
                    xzZoom = Math.Max(0.1f, xzZoom * 0.9f);

                // Adjust scrollbars based on new zoom
                UpdateXZScrollbars();

                // Redraw
                xzViewer.Invalidate();
                Logger.Log($"[MicroSAM] XZ zoom changed to {xzZoom:F2}");
            };

            // XZ viewer mouse events for panning and point placement
            Point lastPos = Point.Empty;
            bool isPanning = false;

            xzViewer.MouseDown += (s, e) =>
            {
                if (currentMode == PromptMode.ZeroShot && zeroShotBoxes != null)
                {
                    int hit = PickZeroShotBox(e.X, e.Y, currentActiveView);
                    if (hit >= 0)
                    {
                        if (zeroShotActive.Contains(hit))
                            zeroShotActive.Remove(hit);   // toggle OFF
                        else
                            zeroShotActive.Add(hit);      // toggle ON

                        // Union of all active masks (or null if none)
                        segmentationMask = BuildMergedMask(zeroShotActive);

                        // Outline only the boxes that are still OFF
                        boundingBoxes = zeroShotBoxes
                                        .Where((_, idx) => !zeroShotActive.Contains(idx))
                                        .ToList();

                        UpdateViewers();

                        statusLabel.Text = zeroShotActive.Count == 0
                            ? "No mask selected – click boxes to add"
                            : $"{zeroShotActive.Count} mask(s) selected – click boxes to add/remove";
                        return; // suppress point‑adding logic
                    }
                }
                if (e.Button == MouseButtons.Left)
                {
                    // If in zero-shot mode, don't add points, but perform segmentation
                    if (currentMode == PromptMode.ZeroShot)
                    {
                        Task.Run(() => PerformZeroShotSegmentation());
                        return;
                    }

                    // Convert mouse coordinates to image coordinates
                    float pointX = (e.X - xzPan.X) / xzZoom;
                    float pointZ = (e.Y - xzPan.Y) / xzZoom;

                    // Check if within image bounds
                    if (pointX >= 0 && pointX < mainForm.GetWidth() &&
                        pointZ >= 0 && pointZ < mainForm.GetDepth())
                    {
                        bool pointDeleted = false;
                        // Check if clicked on an existing point
                        var points = annotationManager.GetAllPoints();
                        foreach (var point in points.ToList())
                        {
                            // For XZ view, we need points with Y coordinate equal to xzRow
                            if (Math.Abs(point.Y - xzRow) <= 1) // Allow small tolerance
                            {
                                float dx = point.X - pointX;
                                float dz = point.Z - pointZ;
                                float distSq = dx * dx + dz * dz;

                                // If within 10 pixels of a point, delete it
                                if (distSq < 100)
                                { // 10^2
                                    annotationManager.RemovePoint(point.ID);
                                    // Also remove from our types dictionary
                                    if (pointTypes.ContainsKey(point.ID))
                                        pointTypes.Remove(point.ID);
                                    pointDeleted = true;
                                    Logger.Log($"[MicroSAM] Deleted point at ({point.X}, {point.Y}, {point.Z})");

                                    // Check if we should auto-update after deleting a point
                                    if (chkAutoUpdate.Checked && currentActiveView == ActiveView.XZ)
                                    {
                                        var remainingPoints = GetRelevantPointsForCurrentView();
                                        if (remainingPoints.Count > 0)
                                        {
                                            Task.Run(() => PerformSegmentation());
                                        }
                                        else
                                        {
                                            // Clear the segmentation mask if no points remain
                                            segmentationMask = null;
                                            boundingBoxes.Clear();
                                            UpdateViewers();
                                        }
                                    }
                                    break;
                                }
                            }
                        }

                        // If we didn't delete a point, add a new one
                        if (!pointDeleted)
                        {
                            // Store point type in a standardized way
                            bool isPositive = (currentMode == PromptMode.Positive);
                            string pointType = isPositive ? "Positive" : "Negative";
                            string label = pointType + "_" + selectedMaterial.Name;

                            // Add point at the current xzRow (Y coordinate)
                            AnnotationPoint newPoint = annotationManager.AddPoint(pointX, xzRow, (int)pointZ, label);

                            // Track point type
                            pointTypes[newPoint.ID] = isPositive;

                            Logger.Log($"[MicroSAM] Added {pointType.ToLower()} point at ({pointX}, {xzRow}, {pointZ})");

                            // Auto-update segmentation if enabled and we're in this view
                            if (chkAutoUpdate.Checked && currentActiveView == ActiveView.XZ)
                            {
                                Task.Run(() => PerformSegmentation());
                            }
                        }

                        // Update views with new or deleted point
                        UpdateViewers();
                    }
                    else
                    {
                        Logger.Log("[MicroSAM] Attempted to place point outside image bounds");
                    }
                }
                else if (e.Button == MouseButtons.Right)
                {
                    // Start panning with right mouse button
                    isPanning = true;
                    lastPos = e.Location;
                }
            };

            xzViewer.MouseMove += (s, e) =>
            {
                if (isPanning && e.Button == MouseButtons.Right)
                {
                    // Calculate the move delta
                    int dx = e.X - lastPos.X;
                    int dy = e.Y - lastPos.Y;

                    // Update the panel position
                    xzPan.X += dx;
                    xzPan.Y += dy;
                    UpdateXZScrollbars();

                    lastPos = e.Location;
                    xzViewer.Invalidate();
                }
            };

            xzViewer.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    isPanning = false;
                }
            };

            // Paint event for custom rendering with checkerboard background
            xzViewer.Paint += (s, e) =>
            {
                // Clear background
                e.Graphics.Clear(Color.Black);

                if (xzViewer.Image != null)
                {
                    int imgWidth = xzViewer.Image.Width;
                    int imgHeight = xzViewer.Image.Height;

                    // Calculate the image bounds
                    xzImageBounds = new Rectangle(
                        xzPan.X,
                        xzPan.Y,
                        (int)(imgWidth * xzZoom),
                        (int)(imgHeight * xzZoom));

                    // Draw checkerboard pattern for the entire visible area
                    DrawCheckerboardBackground(e.Graphics, xzViewer.ClientRectangle);

                    // Draw the image with interpolation
                    e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    e.Graphics.DrawImage(xzViewer.Image, xzImageBounds);

                    // Draw a border around the image
                    using (Pen borderPen = new Pen(Color.DarkGray, 1))
                    {
                        e.Graphics.DrawRectangle(borderPen, xzImageBounds);
                    }

                    // Draw annotations
                    DrawAnnotationsOnXZ(e.Graphics);

                    // Draw bounding boxes if enabled
                    if (showBoundingBoxes && currentActiveView == ActiveView.XZ && boundingBoxes.Count > 0)
                    {
                        DrawBoundingBoxes(e.Graphics, ActiveView.XZ);
                    }
                }
            };
        }

        private void SetupYZViewerEvents()
        {
            // YZ viewer scroll events
            yzHScroll.Scroll += (s, e) =>
            {
                yzPan.X = -yzHScroll.Value;
                yzViewer.Invalidate();
            };

            yzVScroll.Scroll += (s, e) =>
            {
                yzPan.Y = -yzVScroll.Value;
                yzViewer.Invalidate();
            };

            // YZ viewer mouse wheel for zooming
            yzViewer.MouseWheel += (s, e) =>
            {
                float oldZoom = yzZoom;
                // Adjust zoom based on wheel direction
                if (e.Delta > 0)
                    yzZoom = Math.Min(10.0f, yzZoom * 1.1f);
                else
                    yzZoom = Math.Max(0.1f, yzZoom * 0.9f);

                // Adjust scrollbars based on new zoom
                UpdateYZScrollbars();

                // Redraw
                yzViewer.Invalidate();
                Logger.Log($"[MicroSAM] YZ zoom changed to {yzZoom:F2}");
            };

            // YZ viewer mouse events for panning and point placement
            Point lastPos = Point.Empty;
            bool isPanning = false;

            yzViewer.MouseDown += (s, e) =>
            {
                if (currentMode == PromptMode.ZeroShot && zeroShotBoxes != null)
                {
                    int hit = PickZeroShotBox(e.X, e.Y, currentActiveView);
                    if (hit >= 0)
                    {
                        if (zeroShotActive.Contains(hit))
                            zeroShotActive.Remove(hit);   // toggle OFF
                        else
                            zeroShotActive.Add(hit);      // toggle ON

                        // Union of all active masks (or null if none)
                        segmentationMask = BuildMergedMask(zeroShotActive);

                        // Outline only the boxes that are still OFF
                        boundingBoxes = zeroShotBoxes
                                        .Where((_, idx) => !zeroShotActive.Contains(idx))
                                        .ToList();

                        UpdateViewers();

                        statusLabel.Text = zeroShotActive.Count == 0
                            ? "No mask selected – click boxes to add"
                            : $"{zeroShotActive.Count} mask(s) selected – click boxes to add/remove";
                        return; // suppress point‑adding logic
                    }
                }
                if (e.Button == MouseButtons.Left)
                {
                    // If in zero-shot mode, don't add points, but perform segmentation
                    if (currentMode == PromptMode.ZeroShot)
                    {
                        Task.Run(() => PerformZeroShotSegmentation());
                        return;
                    }

                    // Convert mouse coordinates to image coordinates
                    float pointZ = (e.X - yzPan.X) / yzZoom;
                    float pointY = (e.Y - yzPan.Y) / yzZoom;

                    // Check if within image bounds
                    if (pointZ >= 0 && pointZ < mainForm.GetDepth() &&
                        pointY >= 0 && pointY < mainForm.GetHeight())
                    {
                        bool pointDeleted = false;
                        // Check if clicked on an existing point
                        var points = annotationManager.GetAllPoints();
                        foreach (var point in points.ToList())
                        {
                            // For YZ view, we need points with X coordinate equal to yzCol
                            if (Math.Abs(point.X - yzCol) <= 1) // Allow small tolerance
                            {
                                float dz = point.Z - pointZ;
                                float dy = point.Y - pointY;
                                float distSq = dz * dz + dy * dy;

                                // If within 10 pixels of a point, delete it
                                if (distSq < 100)
                                { // 10^2
                                    annotationManager.RemovePoint(point.ID);
                                    // Also remove from our types dictionary
                                    if (pointTypes.ContainsKey(point.ID))
                                        pointTypes.Remove(point.ID);
                                    pointDeleted = true;
                                    Logger.Log($"[MicroSAM] Deleted point at ({point.X}, {point.Y}, {point.Z})");

                                    // Check if we should auto-update after deleting a point
                                    if (chkAutoUpdate.Checked && currentActiveView == ActiveView.YZ)
                                    {
                                        var remainingPoints = GetRelevantPointsForCurrentView();
                                        if (remainingPoints.Count > 0)
                                        {
                                            Task.Run(() => PerformSegmentation());
                                        }
                                        else
                                        {
                                            // Clear the segmentation mask if no points remain
                                            segmentationMask = null;
                                            boundingBoxes.Clear();
                                            UpdateViewers();
                                        }
                                    }
                                    break;
                                }
                            }
                        }

                        // If we didn't delete a point, add a new one
                        if (!pointDeleted)
                        {
                            // Store point type in a standardized way
                            bool isPositive = (currentMode == PromptMode.Positive);
                            string pointType = isPositive ? "Positive" : "Negative";
                            string label = pointType + "_" + selectedMaterial.Name;

                            // Add point at the current yzCol (X coordinate)
                            AnnotationPoint newPoint = annotationManager.AddPoint(yzCol, pointY, (int)pointZ, label);

                            // Track point type
                            pointTypes[newPoint.ID] = isPositive;

                            Logger.Log($"[MicroSAM] Added {pointType.ToLower()} point at ({yzCol}, {pointY}, {pointZ})");

                            // Auto-update segmentation if enabled and we're in this view
                            if (chkAutoUpdate.Checked && currentActiveView == ActiveView.YZ)
                            {
                                Task.Run(() => PerformSegmentation());
                            }
                        }

                        // Update views with new or deleted point
                        UpdateViewers();
                    }
                    else
                    {
                        Logger.Log("[MicroSAM] Attempted to place point outside image bounds");
                    }
                }
                else if (e.Button == MouseButtons.Right)
                {
                    // Start panning with right mouse button
                    isPanning = true;
                    lastPos = e.Location;
                }
            };

            yzViewer.MouseMove += (s, e) =>
            {
                if (isPanning && e.Button == MouseButtons.Right)
                {
                    // Calculate the move delta
                    int dx = e.X - lastPos.X;
                    int dy = e.Y - lastPos.Y;

                    // Update the panel position
                    yzPan.X += dx;
                    yzPan.Y += dy;
                    UpdateYZScrollbars();

                    lastPos = e.Location;
                    yzViewer.Invalidate();
                }
            };

            yzViewer.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    isPanning = false;
                }
            };

            // Paint event for custom rendering with checkerboard background
            yzViewer.Paint += (s, e) =>
            {
                // Clear background
                e.Graphics.Clear(Color.Black);

                if (yzViewer.Image != null)
                {
                    int imgWidth = yzViewer.Image.Width;
                    int imgHeight = yzViewer.Image.Height;

                    // Calculate the image bounds
                    yzImageBounds = new Rectangle(
                        yzPan.X,
                        yzPan.Y,
                        (int)(imgWidth * yzZoom),
                        (int)(imgHeight * yzZoom));

                    // Draw checkerboard pattern for the entire visible area
                    DrawCheckerboardBackground(e.Graphics, yzViewer.ClientRectangle);

                    // Draw the image with interpolation
                    e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    e.Graphics.DrawImage(yzViewer.Image, yzImageBounds);

                    // Draw a border around the image
                    using (Pen borderPen = new Pen(Color.DarkGray, 1))
                    {
                        e.Graphics.DrawRectangle(borderPen, yzImageBounds);
                    }

                    // Draw annotations
                    DrawAnnotationsOnYZ(e.Graphics);

                    // Draw bounding boxes if enabled
                    if (showBoundingBoxes && currentActiveView == ActiveView.YZ && boundingBoxes.Count > 0)
                    {
                        DrawBoundingBoxes(e.Graphics, ActiveView.YZ);
                    }
                }
            };
        }

        private void DrawBoundingBoxes(Graphics g, ActiveView view)
        {
            using (Pen boxPen = new Pen(Color.Yellow, 2))
            {
                float zoom;
                Point pan;

                switch (view)
                {
                    case ActiveView.XY:
                        zoom = xyZoom;
                        pan = xyPan;
                        break;

                    case ActiveView.XZ:
                        zoom = xzZoom;
                        pan = xzPan;
                        break;

                    case ActiveView.YZ:
                        zoom = yzZoom;
                        pan = yzPan;
                        break;

                    default:
                        return;
                }

                foreach (var box in boundingBoxes)
                {
                    // Apply zoom and pan to the bounding box
                    Rectangle scaledBox = new Rectangle(
                        (int)(box.X * zoom) + pan.X,
                        (int)(box.Y * zoom) + pan.Y,
                        (int)(box.Width * zoom),
                        (int)(box.Height * zoom));

                    // Draw the bounding box
                    g.DrawRectangle(boxPen, scaledBox);

                    // Draw a label with material name
                    string label = selectedMaterial.Name;
                    g.DrawString(label, new Font("Arial", 8), Brushes.Yellow,
                        scaledBox.X, scaledBox.Y - 15);
                }
            }
        }

        private void DrawAnnotationsOnXZ(Graphics g)
        {
            // Get all points
            var allPoints = annotationManager.GetAllPoints();

            // Filter points that are on or near the current XZ plane (Y = xzRow)
            foreach (var point in allPoints)
            {
                // Only draw points that are on or very close to this plane
                if (Math.Abs(point.Y - xzRow) <= 1) // Allow small tolerance
                {
                    // Get position with zoom and pan applied (X, Z coordinates)
                    float x = point.X * xzZoom + xzPan.X;
                    float z = point.Z * xzZoom + xzPan.Y; // Z is Y in the XZ view

                    // Use the pointTypes dictionary to determine if positive or negative
                    bool isPositive = !pointTypes.ContainsKey(point.ID) || pointTypes[point.ID];

                    // Use different colors based on point type
                    Color pointColor = isPositive ? Color.Green : Color.Red;

                    // Draw larger point circle (10 pixel radius)
                    int radius = 10;
                    g.FillEllipse(new SolidBrush(Color.FromArgb(128, pointColor)),
                        x - radius, z - radius, radius * 2, radius * 2);
                    g.DrawEllipse(new Pen(pointColor, 2),
                        x - radius, z - radius, radius * 2, radius * 2);

                    // Draw ID number with +/- indicator to make it clearer
                    string typeIndicator = isPositive ? "+" : "-";
                    g.DrawString($"{point.ID} {typeIndicator}", new Font("Arial", 8), Brushes.White, x + radius, z + radius);
                }
            }
        }

        private void DrawAnnotationsOnYZ(Graphics g)
        {
            // Get all points
            var allPoints = annotationManager.GetAllPoints();

            // Filter points that are on or near the current YZ plane (X = yzCol)
            foreach (var point in allPoints)
            {
                // Only draw points that are on or very close to this plane
                if (Math.Abs(point.X - yzCol) <= 1) // Allow small tolerance
                {
                    // Get position with zoom and pan applied (Z, Y coordinates)
                    float z = point.Z * yzZoom + yzPan.X; // Z is X in the YZ view
                    float y = point.Y * yzZoom + yzPan.Y;

                    // Use the pointTypes dictionary to determine if positive or negative
                    bool isPositive = !pointTypes.ContainsKey(point.ID) || pointTypes[point.ID];

                    // Use different colors based on point type
                    Color pointColor = isPositive ? Color.Green : Color.Red;

                    // Draw larger point circle (10 pixel radius)
                    int radius = 10;
                    g.FillEllipse(new SolidBrush(Color.FromArgb(128, pointColor)),
                        z - radius, y - radius, radius * 2, radius * 2);
                    g.DrawEllipse(new Pen(pointColor, 2),
                        z - radius, y - radius, radius * 2, radius * 2);

                    // Draw ID number with +/- indicator to make it clearer
                    string typeIndicator = isPositive ? "+" : "-";
                    g.DrawString($"{point.ID} {typeIndicator}", new Font("Arial", 8), Brushes.White, z + radius, y + radius);
                }
            }
        }

        private void DrawCheckerboardBackground(Graphics g, Rectangle bounds)
        {
            int cellSize = 10; // Size of checkerboard cells

            using (Brush darkBrush = new SolidBrush(Color.FromArgb(30, 30, 30)))
            using (Brush lightBrush = new SolidBrush(Color.FromArgb(50, 50, 50)))
            {
                for (int x = 0; x < bounds.Width; x += cellSize)
                {
                    for (int y = 0; y < bounds.Height; y += cellSize)
                    {
                        // Alternate colors
                        Brush brush = ((x / cellSize + y / cellSize) % 2 == 0) ? darkBrush : lightBrush;
                        g.FillRectangle(brush, x, y, cellSize, cellSize);
                    }
                }
            }
        }

        private void UpdateXYScrollbars()
        {
            if (xyViewer.Image != null)
            {
                int imageWidth = (int)(xyViewer.Image.Width * xyZoom);
                int imageHeight = (int)(xyViewer.Image.Height * xyZoom);

                xyHScroll.Maximum = Math.Max(0, imageWidth - xyViewer.ClientSize.Width + xyHScroll.LargeChange);
                xyVScroll.Maximum = Math.Max(0, imageHeight - xyViewer.ClientSize.Height + xyVScroll.LargeChange);

                xyHScroll.Value = Math.Min(xyHScroll.Maximum, -xyPan.X);
                xyVScroll.Value = Math.Min(xyVScroll.Maximum, -xyPan.Y);
            }
        }

        private void UpdateXZScrollbars()
        {
            if (xzViewer.Image != null)
            {
                int imageWidth = (int)(xzViewer.Image.Width * xzZoom);
                int imageHeight = (int)(xzViewer.Image.Height * xzZoom);

                xzHScroll.Maximum = Math.Max(0, imageWidth - xzViewer.ClientSize.Width + xzHScroll.LargeChange);
                xzVScroll.Maximum = Math.Max(0, imageHeight - xzViewer.ClientSize.Height + xzVScroll.LargeChange);

                xzHScroll.Value = Math.Min(xzHScroll.Maximum, -xzPan.X);
                xzVScroll.Value = Math.Min(xzVScroll.Maximum, -xzPan.Y);
            }
        }

        private void UpdateYZScrollbars()
        {
            if (yzViewer.Image != null)
            {
                int imageWidth = (int)(yzViewer.Image.Width * yzZoom);
                int imageHeight = (int)(yzViewer.Image.Height * yzZoom);

                yzHScroll.Maximum = Math.Max(0, imageWidth - yzViewer.ClientSize.Width + yzHScroll.LargeChange);
                yzVScroll.Maximum = Math.Max(0, imageHeight - yzViewer.ClientSize.Height + yzVScroll.LargeChange);

                yzHScroll.Value = Math.Min(yzHScroll.Maximum, -yzPan.X);
                yzVScroll.Value = Math.Min(yzVScroll.Maximum, -yzPan.Y);
            }
        }

        private void DrawAnnotationsOnXY(Graphics g)
        {
            // Draw annotations
            var points = annotationManager.GetPointsForSlice(xySlice);

            foreach (var point in points)
            {
                // Get position with zoom and pan applied
                float x = point.X * xyZoom + xyPan.X;
                float y = point.Y * xyZoom + xyPan.Y;

                // Use the pointTypes dictionary to determine if positive or negative
                bool isPositive = !pointTypes.ContainsKey(point.ID) || pointTypes[point.ID];

                // Use different colors based on point type
                Color pointColor = isPositive ? Color.Green : Color.Red;

                // Draw larger point circle (10 pixel radius)
                int radius = 10;
                g.FillEllipse(new SolidBrush(Color.FromArgb(128, pointColor)),
                    x - radius, y - radius, radius * 2, radius * 2);
                g.DrawEllipse(new Pen(pointColor, 2),
                    x - radius, y - radius, radius * 2, radius * 2);

                // Draw ID number with +/- indicator to make it clearer
                string typeIndicator = isPositive ? "+" : "-";
                g.DrawString($"{point.ID} {typeIndicator}", new Font("Arial", 8), Brushes.White, x + radius, y + radius);
            }
        }

        private void BrowseForModelDirectory()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select the directory containing MicroSAM ONNX models";
                dialog.ShowNewFolderButton = false;

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    modelPathTextBox.Text = dialog.SelectedPath;

                    // Update paths
                    encoderPath = Path.Combine(dialog.SelectedPath, "micro-sam-encoder.onnx");
                    decoderPath = Path.Combine(dialog.SelectedPath, "micro-sam-decoder.onnx");

                    Logger.Log($"[MicroSAM] Model directory set to: {dialog.SelectedPath}");
                }
            }
        }

        private void LoadONNXModels()
        {
            try
            {
                // Dispose existing sessions if any
                encoderSession?.Dispose();
                decoderSession?.Dispose();

                // Reset feature cache when loading new models
                cachedImageEmbed = null;
                cachedFeatureSlice = -1;

                // Verify files exist
                if (!File.Exists(encoderPath))
                {
                    string errorMsg = $"Encoder model not found at: {encoderPath}";
                    if (samForm != null)
                    {
                        MessageBox.Show(errorMsg);
                    }
                    Logger.Log($"[MicroSAM] {errorMsg}");
                    return;
                }

                if (!File.Exists(decoderPath))
                {
                    string errorMsg = $"Decoder model not found at: {decoderPath}";
                    if (samForm != null)
                    {
                        MessageBox.Show(errorMsg);
                    }
                    Logger.Log($"[MicroSAM] {errorMsg}");
                    return;
                }

                // Create session options with enhanced performance settings
                useGPU = rbGPU != null ? rbGPU.Checked : false;
                SessionOptions options = new SessionOptions();

                // Set graph optimization level to maximum
                options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

                // Enable parallel execution
                options.EnableMemoryPattern = true;
                options.EnableCpuMemArena = true;

                // Set intra-op and inter-op thread count for CPU
                int cpuThreads = Environment.ProcessorCount;
                options.IntraOpNumThreads = Math.Max(1, cpuThreads / 2);

                if (useGPU)
                {
                    try
                    {
                        // Prefer DirectML on Windows
                        options.AppendExecutionProvider_DML();   // deviceId = 0 by default
                        Logger.Log("[MicroSAM] Using DirectML execution provider");
                    }
                    catch (Exception dmlEx)
                    {
                        Logger.Log($"[MicroSAM] DirectML not available: {dmlEx.Message}");

                        try
                        {
                            options.AppendExecutionProvider_CUDA();   // likewise, device 0
                            Logger.Log("[MicroSAM] Using CUDA execution provider");
                        }
                        catch (Exception cudaEx)
                        {
                            Logger.Log($"[MicroSAM] CUDA not available, falling back to CPU: {cudaEx.Message}");
                            // nothing more to do – session will run on the default CPU EP
                        }
                    }
                }
                // Create sessions with optimized settings
                encoderSession = new InferenceSession(encoderPath, options);
                decoderSession = new InferenceSession(decoderPath, options);

                Logger.Log("[MicroSAM] Models loaded successfully with optimized settings");

                if (statusLabel != null)
                {
                    statusLabel.Text = "Models loaded successfully";
                }
            }
            catch (Exception ex)
            {
                if (samForm != null)
                {
                    MessageBox.Show($"Error loading models: {ex.Message}");
                }

                if (statusLabel != null)
                {
                    statusLabel.Text = $"Error: {ex.Message}";
                }

                Logger.Log($"[MicroSAM] Error loading models: {ex.Message}");
                throw; // Re-throw to allow caller to handle
            }
        }

        private void UpdateSliceControls()
        {
            // Update XY slice controls
            if (lblSliceXY != null)
                lblSliceXY.Text = $"XY Slice: {xySlice} / {sliderXY.Maximum}";

            if (sliderXY != null && sliderXY.Value != xySlice)
                sliderXY.Value = xySlice;

            if (numXY != null && numXY.Value != xySlice)
                numXY.Value = xySlice;

            // Update XZ slice (row) controls
            if (lblSliceXZ != null)
                lblSliceXZ.Text = $"XZ Row: {xzRow} / {sliderXZ.Maximum}";

            if (sliderXZ != null && sliderXZ.Value != xzRow)
                sliderXZ.Value = xzRow;

            if (numXZ != null && numXZ.Value != xzRow)
                numXZ.Value = xzRow;

            // Update YZ slice (column) controls
            if (lblSliceYZ != null)
                lblSliceYZ.Text = $"YZ Column: {yzCol} / {sliderYZ.Maximum}";

            if (sliderYZ != null && sliderYZ.Value != yzCol)
                sliderYZ.Value = yzCol;

            if (numYZ != null && numYZ.Value != yzCol)
                numYZ.Value = yzCol;

            // If sync with main view is enabled, update the main form
            if (chkSyncWithMainView != null && chkSyncWithMainView.Checked)
            {
                mainForm.CurrentSlice = xySlice;
                mainForm.XzSliceY = xzRow;
                mainForm.YzSliceX = yzCol;
            }
        }

        public void UpdateSliceFromMainForm(int newSlice)
        {
            if (newSlice != xySlice && newSlice >= 0 && newSlice < mainForm.GetDepth())
            {
                xySlice = newSlice;
                UpdateSliceControls();
                UpdateViewers();
            }
        }

        public void UpdateXZFromMainForm(int newRow)
        {
            if (newRow != xzRow && newRow >= 0 && newRow < mainForm.GetHeight())
            {
                xzRow = newRow;
                UpdateSliceControls();
                UpdateViewers();
            }
        }

        public void UpdateYZFromMainForm(int newCol)
        {
            if (newCol != yzCol && newCol >= 0 && newCol < mainForm.GetWidth())
            {
                yzCol = newCol;
                UpdateSliceControls();
                UpdateViewers();
            }
        }

        private void UpdateViewers()
        {
            // Update XY viewer
            using (Bitmap xySliceBitmap = CreateSliceBitmap(xySlice))
            {
                if (xyViewer.Image != null)
                    xyViewer.Image.Dispose();

                // Apply segmentation overlay if available
                if (segmentationMask != null && !showBoundingBoxes)
                {
                    ApplySegmentationOverlay(xySliceBitmap, segmentationMask, ActiveView.XY);
                }

                xyViewer.Image = new Bitmap(xySliceBitmap);
                UpdateXYScrollbars();
            }

            // Update XZ viewer
            using (Bitmap xzSliceBitmap = CreateXZSliceBitmap(xzRow))
            {
                if (xzViewer.Image != null)
                    xzViewer.Image.Dispose();

                // Apply segmentation overlay if available
                if (segmentationMask != null && !showBoundingBoxes)
                {
                    ApplySegmentationOverlay(xzSliceBitmap, segmentationMask, ActiveView.XZ);
                }

                xzViewer.Image = new Bitmap(xzSliceBitmap);
                UpdateXZScrollbars();
            }

            // Update YZ viewer
            using (Bitmap yzSliceBitmap = CreateYZSliceBitmap(yzCol))
            {
                if (yzViewer.Image != null)
                    yzViewer.Image.Dispose();

                // Apply segmentation overlay if available
                if (segmentationMask != null && !showBoundingBoxes)
                {
                    ApplySegmentationOverlay(yzSliceBitmap, segmentationMask, ActiveView.YZ);
                }

                yzViewer.Image = new Bitmap(yzSliceBitmap);
                UpdateYZScrollbars();
            }

            // Repaint all viewers
            xyViewer.Invalidate();
            xzViewer.Invalidate();
            yzViewer.Invalidate();
        }

        private unsafe Bitmap CreateSliceBitmap(int sliceZ)
        {
            // Try to get from cache first
            Bitmap cachedBitmap = xySliceCache.Get(sliceZ);
            if (cachedBitmap != null)
            {
                // Return a copy of the cached bitmap
                // We need a copy so the original cached version stays clean
                return new Bitmap(cachedBitmap);
            }

            // Create a new bitmap if not in cache
            int w = mainForm.GetWidth();
            int h = mainForm.GetHeight();

            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            BitmapData bmpData = bmp.LockBits(
                new Rectangle(0, 0, w, h),
                ImageLockMode.WriteOnly,
                PixelFormat.Format24bppRgb);

            int stride = bmpData.Stride;
            int bytesPerPixel = 3; // RGB

            byte* ptr = (byte*)bmpData.Scan0;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte val = mainForm.volumeData[x, y, sliceZ];
                    int offset = y * stride + x * bytesPerPixel;

                    // RGB = same value for grayscale
                    ptr[offset] = val;     // Blue
                    ptr[offset + 1] = val; // Green
                    ptr[offset + 2] = val; // Red
                }
            }

            bmp.UnlockBits(bmpData);

            // Add to cache (store a copy so the original stays clean)
            Bitmap cacheCopy = new Bitmap(bmp);
            xySliceCache.Add(sliceZ, cacheCopy);
            cachedXYKeys.Add(sliceZ); // Track key for cleanup

            return bmp;
        }

        private unsafe Bitmap CreateXZSliceBitmap(int sliceY)
        {
            // Try to get from cache first
            Bitmap cachedBitmap = xzSliceCache.Get(sliceY);
            if (cachedBitmap != null)
            {
                // Return a copy of the cached bitmap
                return new Bitmap(cachedBitmap);
            }

            // Create a new bitmap if not in cache
            int w = mainForm.GetWidth();
            int d = mainForm.GetDepth();

            Bitmap bmp = new Bitmap(w, d, PixelFormat.Format24bppRgb);
            BitmapData bmpData = bmp.LockBits(
                new Rectangle(0, 0, w, d),
                ImageLockMode.WriteOnly,
                PixelFormat.Format24bppRgb);

            int stride = bmpData.Stride;
            int bytesPerPixel = 3; // RGB

            byte* ptr = (byte*)bmpData.Scan0;

            for (int z = 0; z < d; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte val = mainForm.volumeData[x, sliceY, z];
                    int offset = z * stride + x * bytesPerPixel;

                    // RGB = same value for grayscale
                    ptr[offset] = val;     // Blue
                    ptr[offset + 1] = val; // Green
                    ptr[offset + 2] = val; // Red
                }
            }

            // Mark the current XY slice position
            int xyLine = xySlice;
            if (xyLine >= 0 && xyLine < d)
            {
                for (int x = 0; x < w; x++)
                {
                    int offset = xyLine * stride + x * bytesPerPixel;

                    // Bright yellow line
                    ptr[offset] = 0;       // Blue
                    ptr[offset + 1] = 255; // Green
                    ptr[offset + 2] = 255; // Red
                }
            }

            bmp.UnlockBits(bmpData);

            // Add to cache
            Bitmap cacheCopy = new Bitmap(bmp);
            xzSliceCache.Add(sliceY, cacheCopy);
            cachedXZKeys.Add(sliceY);

            return bmp;
        }

        private unsafe Bitmap CreateYZSliceBitmap(int sliceX)
        {
            // Try to get from cache first
            Bitmap cachedBitmap = yzSliceCache.Get(sliceX);
            if (cachedBitmap != null)
            {
                // Return a copy of the cached bitmap
                return new Bitmap(cachedBitmap);
            }

            // Create a new bitmap if not in cache
            int h = mainForm.GetHeight();
            int d = mainForm.GetDepth();

            Bitmap bmp = new Bitmap(d, h, PixelFormat.Format24bppRgb);
            BitmapData bmpData = bmp.LockBits(
                new Rectangle(0, 0, d, h),
                ImageLockMode.WriteOnly,
                PixelFormat.Format24bppRgb);

            int stride = bmpData.Stride;
            int bytesPerPixel = 3; // RGB

            byte* ptr = (byte*)bmpData.Scan0;

            for (int y = 0; y < h; y++)
            {
                for (int z = 0; z < d; z++)
                {
                    byte val = mainForm.volumeData[sliceX, y, z];
                    int offset = y * stride + z * bytesPerPixel;

                    // RGB = same value for grayscale
                    ptr[offset] = val;     // Blue
                    ptr[offset + 1] = val; // Green
                    ptr[offset + 2] = val; // Red
                }
            }

            // Mark the current XY slice position
            int xyLine = xySlice;
            if (xyLine >= 0 && xyLine < d)
            {
                for (int y = 0; y < h; y++)
                {
                    int offset = y * stride + xyLine * bytesPerPixel;

                    // Bright yellow line
                    ptr[offset] = 0;       // Blue
                    ptr[offset + 1] = 255; // Green
                    ptr[offset + 2] = 255; // Red
                }
            }

            bmp.UnlockBits(bmpData);

            // Add to cache
            Bitmap cacheCopy = new Bitmap(bmp);
            yzSliceCache.Add(sliceX, cacheCopy);
            cachedYZKeys.Add(sliceX);

            return bmp;
        }

        /// <summary>Returns the union of the masks whose indices are in
        /// <paramref name="active"/>.  If none are active, returns null.</summary>
        private byte[,] BuildMergedMask(IEnumerable<int> active)
        {
            if (zeroShotMasks == null) return null;

            int[] ids = active.ToArray();
            if (ids.Length == 0) return null;

            // assume all masks share the same size as the first one
            int w = zeroShotMasks[0].GetLength(0);
            int h = zeroShotMasks[0].GetLength(1);
            var merged = new byte[w, h];

            foreach (int id in ids)
            {
                byte[,] m = zeroShotMasks[id];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (m[x, y] > 0) merged[x, y] = selectedMaterial.ID;
            }
            return merged;
        }

        private async Task PerformSegmentation()
        {
            zeroShotBoxes = null;
            zeroShotMasks = null;
            zeroShotActive = null;
            if (encoderSession == null || decoderSession == null)
            {
                MessageBox.Show("Models not loaded. Please load models first.");
                return;
            }

            /* ---------- gather annotation points --------------------------------- */
            List<AnnotationPoint> relevantPoints = GetRelevantPointsForCurrentView();

            if (currentMode == PromptMode.ZeroShot)
                return;          // zero‑shot needs no auto‑refresh

            if (relevantPoints.Count == 0)
                return;

            // UI feedback
            Action setBusy = () => statusLabel.Text = "Segmenting...";
            if (samForm.InvokeRequired) samForm.Invoke(setBusy); else setBusy();

            Logger.Log($"[MicroSAM] Starting segmentation on {currentActiveView}");

            try
            {
                /* ---------- encoder ----------------------------------------------- */
                Tensor<float> imageInput = await Task.Run(() => PreprocessImage());

                DenseTensor<float> imageEmbed;

                using (var encOut = await Task.Run(() =>
                    encoderSession.Run(new[] { NamedOnnxValue.CreateFromTensor("image", imageInput) })))
                {
                    var encTensor = encOut.First(o => o.Name == "image_embeddings").AsTensor<float>();

                    cachedImageEmbed = new DenseTensor<float>(encTensor.Dimensions);
                    CopyTensorData(encTensor, cachedImageEmbed);
                    cachedFeatureSlice = currentActiveView == ActiveView.XY ? xySlice
                                         : currentActiveView == ActiveView.XZ ? xzRow : yzCol;

                    imageEmbed = cachedImageEmbed;
                }

                /* ---------- build point tensors ----------------------------------- */
                int numPoints = relevantPoints.Count == 0 ? 1 : relevantPoints.Count;
                var pointCoords = new DenseTensor<float>(new[] { 1, numPoints, 2 });
                var pointLabels = new DenseTensor<float>(new[] { 1, numPoints });

                float scaleX, scaleY;
                switch (currentActiveView)
                {
                    case ActiveView.XY:
                        scaleX = 1024f / mainForm.GetWidth();
                        scaleY = 1024f / mainForm.GetHeight();
                        break;

                    case ActiveView.XZ:
                        scaleX = 1024f / mainForm.GetWidth();
                        scaleY = 1024f / mainForm.GetDepth();
                        break;

                    default: /* YZ */
                        scaleX = 1024f / mainForm.GetDepth();
                        scaleY = 1024f / mainForm.GetHeight();
                        break;
                }

                if (relevantPoints.Count == 0)   // zero‑shot fallback
                {
                    pointCoords[0, 0, 0] = 512;
                    pointCoords[0, 0, 1] = 512;
                    pointLabels[0, 0] = 0f;
                }
                else
                {
                    for (int i = 0; i < relevantPoints.Count; i++)
                    {
                        AnnotationPoint p = relevantPoints[i];
                        float px, py;

                        switch (currentActiveView)
                        {
                            case ActiveView.XY: px = p.X; py = p.Y; break;
                            case ActiveView.XZ: px = p.X; py = p.Z; break;
                            default: px = p.Z; py = p.Y; break;
                        }

                        pointCoords[0, i, 0] = px * scaleX;
                        pointCoords[0, i, 1] = py * scaleY;
                        pointLabels[0, i] = (pointTypes.TryGetValue(p.ID, out bool pos) && pos) ? 1f : 0f;
                    }
                }

                /* ---------- empty mask input for SAM ------------------------------ */
                var maskInput = new DenseTensor<float>(new[] { 1, 1, 256, 256 });
                var hasMaskInput = new DenseTensor<float>(new[] { 1 });
                hasMaskInput[0] = 0f;

                /* ---------- original image size ----------------------------------- */
                int origW, origH;
                if (currentActiveView == ActiveView.XY)
                {
                    origW = mainForm.GetWidth(); origH = mainForm.GetHeight();
                }
                else if (currentActiveView == ActiveView.XZ)
                {
                    origW = mainForm.GetWidth(); origH = mainForm.GetDepth();
                }
                else
                {
                    origW = mainForm.GetDepth(); origH = mainForm.GetHeight();
                }

                var origImSize = new DenseTensor<float>(new[] { 2 });
                origImSize[0] = origH; origImSize[1] = origW;

                /* ---------- decoder ----------------------------------------------- */
                byte[,] tempMask = null;
                float bestIoU = 0f;

                using (var decOut = await Task.Run(() => decoderSession.Run(new[]
                {
            NamedOnnxValue.CreateFromTensor("image_embeddings", imageEmbed),
            NamedOnnxValue.CreateFromTensor("point_coords",       pointCoords),
            NamedOnnxValue.CreateFromTensor("point_labels",       pointLabels),
            NamedOnnxValue.CreateFromTensor("mask_input",         maskInput),
            NamedOnnxValue.CreateFromTensor("has_mask_input",     hasMaskInput),
            NamedOnnxValue.CreateFromTensor("orig_im_size",       origImSize)
        })))
                {
                    var masks = decOut.First(o => o.Name == "masks").AsTensor<float>();
                    var iouPredictions = decOut.First(o => o.Name == "iou_predictions").AsTensor<float>();

                    int bestIdx = GetBestMaskIndex(iouPredictions);
                    bestIoU = iouPredictions[bestIdx, 0];

                    tempMask = ConvertSamMaskToByteMask(masks, bestIdx, currentActiveView);

                    DetectBoundingBoxes(tempMask, currentActiveView);
                }

                /* ---------- update UI --------------------------------------------- */
                Action finish = () =>
                {
                    segmentationMask = tempMask;
                    UpdateViewers();
                    statusLabel.Text = $"Segmentation complete (IoU {bestIoU:F3})";
                };
                if (samForm.InvokeRequired) samForm.Invoke(finish); else finish();

                Logger.Log("[MicroSAM] Segmentation complete");
            }
            catch (Exception ex)
            {
                if (samForm.InvokeRequired)
                    samForm.Invoke(new Action(() =>
                        MessageBox.Show($"Error during segmentation: {ex.Message}")));
                else
                    MessageBox.Show($"Error during segmentation: {ex.Message}");

                statusLabel.Text = "Error";
                Logger.Log($"[MicroSAM] Segmentation error: {ex.Message}");
            }
        }

        // Zero-shot prompt mode - performs segmentation without any points
        private async Task PerformZeroShotSegmentation()
        {
            if (encoderSession == null || decoderSession == null)
            {
                MessageBox.Show("Models not loaded. Please load models first.");
                return;
            }

            // Ensure UI is updated from the UI thread
            Action updateStatus = () => statusLabel.Text = "Performing zero-shot segmentation...";
            if (samForm.InvokeRequired)
                samForm.Invoke(updateStatus);
            else
                updateStatus();

            Logger.Log($"[MicroSAM] Starting zero-shot segmentation on {currentActiveView} view");

            try
            {
                // Create a tensor with the image data
                Tensor<float> imageInput = await Task.Run(() => PreprocessImage());

                // Create input and run the encoder on a background thread
                var encoderInputs = new List<NamedOnnxValue> {
                    NamedOnnxValue.CreateFromTensor("image", imageInput)
                };

                var encoderOutputs = await Task.Run(() => encoderSession.Run(encoderInputs));

                try
                {
                    // Extract encoder outputs
                    var imageEmbed = encoderOutputs.First(x => x.Name == "image_embeddings").AsTensor<float>();

                    // Cache the image embeddings
                    cachedImageEmbed = new DenseTensor<float>(imageEmbed.Dimensions);
                    CopyTensorData(imageEmbed, cachedImageEmbed);
                    cachedFeatureSlice = currentActiveView == ActiveView.XY ? xySlice :
                                         currentActiveView == ActiveView.XZ ? xzRow : yzCol;

                    // For zero-shot, we create a single point in the center
                    // This point won't be used, but the API needs the tensors
                    DenseTensor<float> pointCoords = new DenseTensor<float>(new[] { 1, 1, 2 });
                    DenseTensor<float> pointLabels = new DenseTensor<float>(new[] { 1, 1 });

                    // Use center point (just as a placeholder)
                    pointCoords[0, 0, 0] = 512; // center x (1024/2)
                    pointCoords[0, 0, 1] = 512; // center y (1024/2)
                    pointLabels[0, 0] = -1; // zero-shot mode

                    // Empty mask input
                    DenseTensor<float> maskInput = new DenseTensor<float>(new[] { 1, 1, 256, 256 });
                    DenseTensor<float> hasMaskInput = new DenseTensor<float>(new[] { 1 });
                    hasMaskInput[0] = 0;  // No mask input for zero-shot

                    // Original image size depends on active view
                    int origWidth, origHeight;
                    switch (currentActiveView)
                    {
                        case ActiveView.XY:
                            origWidth = mainForm.GetWidth();
                            origHeight = mainForm.GetHeight();
                            break;

                        case ActiveView.XZ:
                            origWidth = mainForm.GetWidth();
                            origHeight = mainForm.GetDepth();
                            break;

                        case ActiveView.YZ:
                            origWidth = mainForm.GetDepth();
                            origHeight = mainForm.GetHeight();
                            break;

                        default:
                            origWidth = mainForm.GetWidth();
                            origHeight = mainForm.GetHeight();
                            break;
                    }

                    // For MicroSAM, we need to pass orig_im_size as a float tensor
                    DenseTensor<float> origImSize = new DenseTensor<float>(new[] { 2 });
                    origImSize[0] = origHeight;
                    origImSize[1] = origWidth;

                    // Run decoder on background thread
                    var decoderInputs = new List<NamedOnnxValue> {
                        NamedOnnxValue.CreateFromTensor("image_embeddings", imageEmbed),
                        NamedOnnxValue.CreateFromTensor("point_coords", pointCoords),
                        NamedOnnxValue.CreateFromTensor("point_labels", pointLabels),
                        NamedOnnxValue.CreateFromTensor("mask_input", maskInput),
                        NamedOnnxValue.CreateFromTensor("has_mask_input", hasMaskInput),
                        NamedOnnxValue.CreateFromTensor("orig_im_size", origImSize)
                    };

                    var decoderOutputs = await Task.Run(() => decoderSession.Run(decoderInputs));

                    try
                    {
                        /* ---------- collect best masks ----------------------------------- */
                        var masks = decoderOutputs.First(x => x.Name == "masks").AsTensor<float>();
                        var iouPredictions = decoderOutputs.First(x => x.Name == "iou_predictions").AsTensor<float>();
                        int nMasks = iouPredictions.Dimensions[0];
                        var lowRes = decoderOutputs.First(x => x.Name == "low_res_masks").AsTensor<float>();

                        /* take up to 5 masks with the highest IoU --------------------------- */
                        var ranked = Enumerable.Range(0, nMasks)
                                       .Select(i => (score: iouPredictions[i, 0], idx: i))
                                       .OrderByDescending(t => t.score)
                                       .Take(5)
                                       .ToArray();

                        /* keep those ≥ 0.40 … */
                        int[] best = ranked.Where(t => t.score >= 0.40f)
                                           .Select(t => t.idx)
                                           .ToArray();

                        /* … but if none pass, fall back to the single best one               */
                        if (best.Length == 0)
                            best = new[] { ranked[0].idx };        // always at least one mask
                        zeroShotMasks = new List<byte[,]>();
                        zeroShotBoxes = new List<Rectangle>();
                        zeroShotActive = new List<int>();          // will start empty

                        foreach (int i in best)
                        {
                            var m = ConvertSamMaskToByteMask(masks, i, currentActiveView);
                            zeroShotMasks.Add(m);

                            DetectBoundingBoxes(m, currentActiveView);

                            /*  keep all boxes, not only the last one                     */
                            if (boundingBoxes.Count > 0)
                                zeroShotBoxes.AddRange(boundingBoxes);
                        }
                        if (zeroShotBoxes.Count == 0)
                        {
                            Logger.Log("[MicroSAM] Hi‑res mask empty – using low‑res logits");

                            int nLow = lowRes.Dimensions[0];          // batch size (same as nMasks)
                            int h = lowRes.Dimensions[2];
                            int w = lowRes.Dimensions[3];

                            // pick the best *k* low‑res masks (same indices we already ranked)
                            foreach (int i in best)
                            {
                                // build byte[,] directly from the  low‑res 256×256 logits
                                byte[,] small = new byte[w, h];
                                for (int y = 0; y < h; y++)
                                    for (int x = 0; x < w; x++)
                                        if (lowRes[i, 0, y, x] > 0)       // threshold at 0
                                            small[x, y] = selectedMaterial.ID;

                                // upscale to the view resolution
                                var up = ResizeByteMask(small, currentActiveView);
                                zeroShotMasks.Add(up);

                                DetectBoundingBoxes(up, currentActiveView);
                                zeroShotBoxes.AddRange(boundingBoxes);    // keep every box
                            }
                        }

                        // Show only boxes for now – user must click one to choose
                        segmentationMask = null;
                        boundingBoxes = zeroShotBoxes;

                        samForm.Invoke(new Action(() =>
                        {
                            UpdateViewers();                       // refresh the three viewers
                            statusLabel.Text = zeroShotBoxes.Count == 0
                                ? "Zero‑shot: no confident masks"
    : "Zero‑shot: click boxes to add them to the selection";
                        }));

                        Logger.Log($"[MicroSAM] Zero‑shot produced {zeroShotBoxes.Count} boxes");
                    }
                    finally
                    {
                        decoderOutputs.Dispose();
                    }
                }
                finally
                {
                    encoderOutputs.Dispose();
                }
            }
            catch (Exception ex)
            {
                samForm.Invoke(new Action(() =>
                {
                    MessageBox.Show($"Error during zero-shot segmentation: {ex.Message}");
                    statusLabel.Text = $"Error: {ex.Message}";
                }));

                Logger.Log($"[MicroSAM] Zero-shot segmentation error: {ex.Message}");
            }
        }

        /// <summary>Upscales a small 256×256 byte mask to the current view size.</summary>
        private byte[,] ResizeByteMask(byte[,] src, ActiveView view)
        {
            int srcW = src.GetLength(0), srcH = src.GetLength(1);
            int dstW, dstH;

            switch (view)
            {
                case ActiveView.XY: dstW = mainForm.GetWidth(); dstH = mainForm.GetHeight(); break;
                case ActiveView.XZ: dstW = mainForm.GetWidth(); dstH = mainForm.GetDepth(); break;
                default: dstW = mainForm.GetDepth(); dstH = mainForm.GetHeight(); break;
            }

            var dst = new byte[dstW, dstH];
            double sx = (double)srcW / dstW;
            double sy = (double)srcH / dstH;

            for (int y = 0; y < dstH; y++)
            {
                int syi = Math.Min((int)(y * sy), srcH - 1);
                for (int x = 0; x < dstW; x++)
                {
                    int sxi = Math.Min((int)(x * sx), srcW - 1);
                    if (src[sxi, syi] > 0) dst[x, y] = selectedMaterial.ID;
                }
            }
            return dst;
        }

        /// <summary>Returns the index of the zero‑shot box that contains (x,y)
        /// in *viewer‑space*, or –1 if none.</summary>
        private int PickZeroShotBox(int vx, int vy, ActiveView view)
        {
            if (zeroShotBoxes == null) return -1;

            float zoom = view == ActiveView.XY ? xyZoom : view == ActiveView.XZ ? xzZoom : yzZoom;
            Point pan = view == ActiveView.XY ? xyPan : view == ActiveView.XZ ? xzPan : yzPan;

            for (int i = 0; i < zeroShotBoxes.Count; i++)
            {
                Rectangle box = zeroShotBoxes[i];
                Rectangle scr = new Rectangle(
                    (int)(box.X * zoom) + pan.X,
                    (int)(box.Y * zoom) + pan.Y,
                    (int)(box.Width * zoom),
                    (int)(box.Height * zoom));

                if (scr.Contains(vx, vy)) return i;
            }
            return -1;
        }

        // Detect bounding boxes from a segmentation mask
        private void DetectBoundingBoxes(byte[,] mask, ActiveView view)
        {
            boundingBoxes.Clear();

            if (mask == null)
                return;

            int width = mask.GetLength(0);
            int height = mask.GetLength(1);

            // Detect connected components in the mask
            bool[,] visited = new bool[width, height];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (mask[x, y] > 0 && !visited[x, y])
                    {
                        // Start a new connected component
                        int minX = x, minY = y, maxX = x, maxY = y;
                        Queue<(int, int)> queue = new Queue<(int, int)>();
                        queue.Enqueue((x, y));
                        visited[x, y] = true;

                        // BFS to find connected component
                        while (queue.Count > 0)
                        {
                            var (cx, cy) = queue.Dequeue();

                            // Update bounding box
                            minX = Math.Min(minX, cx);
                            minY = Math.Min(minY, cy);
                            maxX = Math.Max(maxX, cx);
                            maxY = Math.Max(maxY, cy);

                            // Check 4 neighbors
                            int[] dx = { 0, 0, 1, -1 };
                            int[] dy = { 1, -1, 0, 0 };

                            for (int i = 0; i < 4; i++)
                            {
                                int nx = cx + dx[i];
                                int ny = cy + dy[i];

                                if (nx >= 0 && nx < width && ny >= 0 && ny < height &&
                                    mask[nx, ny] > 0 && !visited[nx, ny])
                                {
                                    queue.Enqueue((nx, ny));
                                    visited[nx, ny] = true;
                                }
                            }
                        }

                        // Add the bounding box if it's large enough
                        int boxWidth = maxX - minX + 1;
                        int boxHeight = maxY - minY + 1;

                        if (boxWidth > 5 && boxHeight > 5) // Minimum size filter
                        {
                            boundingBoxes.Add(new Rectangle(minX, minY, boxWidth, boxHeight));
                            Logger.Log($"[MicroSAM] Detected bounding box: ({minX}, {minY}, {boxWidth}, {boxHeight})");
                        }
                    }
                }
            }

            Logger.Log($"[MicroSAM] Detected {boundingBoxes.Count} bounding boxes");
        }

        private unsafe Tensor<float> PreprocessImage()
        {
            // Create the image scaled to 1024x1024 for the encoder based on active view
            Bitmap viewBitmap;

            switch (currentActiveView)
            {
                case ActiveView.XY:
                    viewBitmap = CreateSliceBitmap(xySlice);
                    break;

                case ActiveView.XZ:
                    viewBitmap = CreateXZSliceBitmap(xzRow);
                    break;

                case ActiveView.YZ:
                    viewBitmap = CreateYZSliceBitmap(yzCol);
                    break;

                default:
                    viewBitmap = CreateSliceBitmap(xySlice);
                    break;
            }

            using (viewBitmap)
            {
                // Create a tensor with shape [1, 3, 1024, 1024]
                DenseTensor<float> inputTensor = new DenseTensor<float>(new[] { 1, 3, 1024, 1024 });

                // Create a resized version of the slice
                using (Bitmap resized = new Bitmap(1024, 1024))
                {
                    using (Graphics g = Graphics.FromImage(resized))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(viewBitmap, 0, 0, 1024, 1024);
                    }

                    // Lock the bitmap and access its pixel data
                    BitmapData bmpData = resized.LockBits(
                        new Rectangle(0, 0, resized.Width, resized.Height),
                        ImageLockMode.ReadOnly,
                        PixelFormat.Format24bppRgb);

                    int stride = bmpData.Stride;
                    int bytesPerPixel = 3; // RGB

                    byte* ptr = (byte*)bmpData.Scan0;

                    // Process pixels and normalize to range [0.0, 1.0]
                    for (int y = 0; y < 1024; y++)
                    {
                        for (int x = 0; x < 1024; x++)
                        {
                            int offset = y * stride + x * bytesPerPixel;

                            // BGR order (standard in Bitmap)
                            byte b = ptr[offset];
                            byte g = ptr[offset + 1];
                            byte r = ptr[offset + 2];

                            // Normalize to range [0.0, 1.0] and convert to RGB order for the model
                            inputTensor[0, 0, y, x] = r / 255.0f;
                            inputTensor[0, 1, y, x] = g / 255.0f;
                            inputTensor[0, 2, y, x] = b / 255.0f;
                        }
                    }

                    resized.UnlockBits(bmpData);
                    return inputTensor;
                }
            }
        }

        private void CopyTensorData<T>(Tensor<T> source, DenseTensor<T> destination)
        {
            // Get dimensions
            int[] dimensions = source.Dimensions.ToArray();

            // For simplicity, handle up to 4D tensors (which should cover our use case)
            if (dimensions.Length == 1)
            {
                for (int i = 0; i < dimensions[0]; i++)
                {
                    destination[i] = source[i];
                }
            }
            else if (dimensions.Length == 2)
            {
                for (int i = 0; i < dimensions[0]; i++)
                {
                    for (int j = 0; j < dimensions[1]; j++)
                    {
                        destination[i, j] = source[i, j];
                    }
                }
            }
            else if (dimensions.Length == 3)
            {
                for (int i = 0; i < dimensions[0]; i++)
                {
                    for (int j = 0; j < dimensions[1]; j++)
                    {
                        for (int k = 0; k < dimensions[2]; k++)
                        {
                            destination[i, j, k] = source[i, j, k];
                        }
                    }
                }
            }
            else if (dimensions.Length == 4)
            {
                for (int i = 0; i < dimensions[0]; i++)
                {
                    for (int j = 0; j < dimensions[1]; j++)
                    {
                        for (int k = 0; k < dimensions[2]; k++)
                        {
                            for (int l = 0; l < dimensions[3]; l++)
                            {
                                destination[i, j, k, l] = source[i, j, k, l];
                            }
                        }
                    }
                }
            }
        }

        private List<AnnotationPoint> GetRelevantPointsForCurrentView()
        {
            var allPoints = annotationManager.GetAllPoints();
            var relevantPoints = new List<AnnotationPoint>();

            switch (currentActiveView)
            {
                case ActiveView.XY:
                    // Points on the current XY slice
                    relevantPoints.AddRange(allPoints.Where(p => p.Z == xySlice));
                    break;

                case ActiveView.XZ:
                    // Points on the current XZ row
                    relevantPoints.AddRange(allPoints.Where(p => Math.Abs(p.Y - xzRow) <= 1));
                    break;

                case ActiveView.YZ:
                    // Points on the current YZ column
                    relevantPoints.AddRange(allPoints.Where(p => Math.Abs(p.X - yzCol) <= 1));
                    break;
            }

            return relevantPoints;
        }

        private unsafe void ApplySegmentationOverlay(Bitmap bitmap, byte[,] mask, ActiveView viewType)
        {
            if (mask == null || showBoundingBoxes || currentActiveView != viewType)
                return;

            int w = bitmap.Width;
            int h = bitmap.Height;

            BitmapData bmpData = bitmap.LockBits(
                new Rectangle(0, 0, w, h),
                ImageLockMode.ReadWrite,
                PixelFormat.Format24bppRgb);

            int stride = bmpData.Stride;
            int bytesPerPixel = 3;

            byte* ptr = (byte*)bmpData.Scan0;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    // Check if this pixel is in the mask and is set
                    bool isInMask = false;

                    if (viewType == ActiveView.XY &&
                        x < mask.GetLength(0) && y < mask.GetLength(1) && mask[x, y] > 0)
                        isInMask = true;
                    else if (viewType == ActiveView.XZ &&
                        x < mask.GetLength(0) && y < mask.GetLength(1) && mask[x, y] > 0)
                        isInMask = true;
                    else if (viewType == ActiveView.YZ &&
                        x < mask.GetLength(0) && y < mask.GetLength(1) && mask[x, y] > 0)
                        isInMask = true;

                    if (isInMask)
                    {
                        int offset = y * stride + x * bytesPerPixel;

                        // Current color values
                        byte b = ptr[offset];
                        byte g = ptr[offset + 1];
                        byte r = ptr[offset + 2];

                        // Mix with material color (50% blend)
                        Color matColor = selectedMaterial.Color;
                        ptr[offset] = (byte)((b + matColor.B) / 2);
                        ptr[offset + 1] = (byte)((g + matColor.G) / 2);
                        ptr[offset + 2] = (byte)((r + matColor.R) / 2);
                    }
                }
            }

            bitmap.UnlockBits(bmpData);
        }

        private void ApplySegmentationMask()
        {
            if (segmentationMask == null)
            {
                MessageBox.Show("No segmentation result available.");
                return;
            }

            if (mainForm == null)
            {
                Logger.Log("[MicroSAM] Error: MainForm reference is null");
                MessageBox.Show("Error: Cannot access the main application.");
                return;
            }

            try
            {
                Logger.Log($"[MicroSAM] Applying segmentation mask from {currentActiveView} view to volume labels");
                statusLabel.Text = "Applying mask...";

                // Get dimensions with null checks
                int width = mainForm.GetWidth();
                int height = mainForm.GetHeight();
                int depth = mainForm.GetDepth();

                if (width <= 0 || height <= 0 || depth <= 0)
                {
                    MessageBox.Show("Invalid volume dimensions.");
                    return;
                }

                switch (currentActiveView)
                {
                    case ActiveView.XY:
                        // Apply the mask to the current XY slice
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                if (segmentationMask[x, y] > 0)
                                {
                                    mainForm.volumeLabels[x, y, xySlice] = segmentationMask[x, y];
                                }
                            }
                        }

                        // Update the mainForm's temporary selection for XY slice
                        if (mainForm.currentSelection == null ||
                            mainForm.currentSelection.GetLength(0) != width ||
                            mainForm.currentSelection.GetLength(1) != height)
                        {
                            mainForm.currentSelection = new byte[width, height];
                        }

                        // Copy mask to current selection
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                if (segmentationMask[x, y] > 0)
                                {
                                    mainForm.currentSelection[x, y] = segmentationMask[x, y];
                                }
                            }
                        }
                        break;

                    case ActiveView.XZ:
                        // Apply the mask to the current XZ plane (fixed Y/row)
                        for (int z = 0; z < depth; z++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                if (segmentationMask[x, z] > 0)
                                {
                                    mainForm.volumeLabels[x, xzRow, z] = segmentationMask[x, z];
                                }
                            }
                        }

                        // Create a temporary selection for the current XY slice
                        if (mainForm.currentSelection == null ||
                            mainForm.currentSelection.GetLength(0) != width ||
                            mainForm.currentSelection.GetLength(1) != height)
                        {
                            mainForm.currentSelection = new byte[width, height];
                        }

                        // Clear the current selection
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                mainForm.currentSelection[x, y] = 0;
                            }
                        }

                        // Add any points where the XZ mask intersects with the current XY slice
                        if (xySlice < depth)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                if (segmentationMask[x, xySlice] > 0)
                                {
                                    mainForm.currentSelection[x, xzRow] = segmentationMask[x, xySlice];
                                }
                            }
                        }
                        break;

                    case ActiveView.YZ:
                        // Apply the mask to the current YZ plane (fixed X/column)
                        for (int z = 0; z < depth; z++)
                        {
                            for (int y = 0; y < height; y++)
                            {
                                if (segmentationMask[z, y] > 0)
                                {
                                    mainForm.volumeLabels[yzCol, y, z] = segmentationMask[z, y];
                                }
                            }
                        }

                        // Create a temporary selection for the current XY slice
                        if (mainForm.currentSelection == null ||
                            mainForm.currentSelection.GetLength(0) != width ||
                            mainForm.currentSelection.GetLength(1) != height)
                        {
                            mainForm.currentSelection = new byte[width, height];
                        }

                        // Clear the current selection
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                mainForm.currentSelection[x, y] = 0;
                            }
                        }

                        // Add any points where the YZ mask intersects with the current XY slice
                        if (xySlice < depth)
                        {
                            for (int y = 0; y < height; y++)
                            {
                                if (segmentationMask[xySlice, y] > 0)
                                {
                                    mainForm.currentSelection[yzCol, y] = segmentationMask[xySlice, y];
                                }
                            }
                        }
                        break;
                }

                // Update MainForm's view
                mainForm.RenderViews();
                mainForm.SaveLabelsChk();

                statusLabel.Text = "Mask applied successfully";
                Logger.Log("[MicroSAM] Mask applied successfully");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error applying mask: {ex.Message}");
                statusLabel.Text = $"Error: {ex.Message}";
                Logger.Log($"[MicroSAM] Error applying mask: {ex.Message}");
            }
        }

        private async void ApplyToVolume()
        {
            if (segmentationMask == null)
            {
                MessageBox.Show("No segmentation result available. Please segment the current slice first.");
                return;
            }

            if (encoderSession == null || decoderSession == null)
            {
                MessageBox.Show("Models not loaded. Please load models first.");
                return;
            }

            // Improved configuration dialog with better layout
            using (var dialog = new Form()
            {
                Width = 500,
                Height = 380,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen,
                Text = "Volume Segmentation Settings",
                MaximizeBox = false,
                MinimizeBox = false
            })
            {
                // Title
                Label lblTitle = new Label()
                {
                    Text = "Configure Volume Segmentation",
                    Font = new Font(SystemFonts.DefaultFont.FontFamily, 12, FontStyle.Bold),
                    AutoSize = true,
                    Left = 20,
                    Top = 15
                };

                // Slice range section
                GroupBox grpRange = new GroupBox()
                {
                    Text = "Slice Range",
                    Left = 20,
                    Top = 40,
                    Width = 450,
                    Height = 70
                };

                Label lblRange = new Label()
                {
                    Left = 15,
                    Top = 25,
                    Text = "Number of slices to segment in each direction:",
                    Width = 250,
                    AutoSize = true
                };

                NumericUpDown numRange = new NumericUpDown()
                {
                    Left = 270,
                    Top = 23,
                    Width = 60,
                    Minimum = 1,
                    Maximum = 1000,
                    Value = 10
                };

                // Add feature cache control for performance tuning
                CheckBox chkUseFeatureCache = new CheckBox()
                {
                    Left = 350,
                    Top = 24,
                    Text = "Use feature caching (faster)",
                    Checked = true,
                    Width = 200,
                    AutoSize = true
                };

                grpRange.Controls.AddRange(new Control[] { lblRange, numRange, chkUseFeatureCache });

                // Operation selection section
                GroupBox grpOperation = new GroupBox()
                {
                    Text = "Operation",
                    Left = 20,
                    Top = 120,
                    Width = 450,
                    Height = 110
                };

                RadioButton rbCreateMaterial = new RadioButton()
                {
                    Left = 15,
                    Top = 25,
                    Text = "Create material from segmentation",
                    Width = 300,
                    Checked = true,
                    AutoSize = true
                };

                RadioButton rbExportCropped = new RadioButton()
                {
                    Left = 15,
                    Top = 50,
                    Text = "Export cropped dataset of segmented region",
                    Width = 300,
                    AutoSize = true
                };

                // Sub-option for material creation
                CheckBox chkNewMaterial = new CheckBox()
                {
                    Left = 35,
                    Top = 75,
                    Text = "Create new material (unchecked = use current material)",
                    Checked = true,
                    Width = 350,
                    Visible = true,
                    AutoSize = true
                };

                grpOperation.Controls.AddRange(new Control[] { rbCreateMaterial, rbExportCropped, chkNewMaterial });

                // Processing strategy section
                GroupBox grpProcessing = new GroupBox()
                {
                    Text = "Processing Strategy",
                    Left = 20,
                    Top = 240,
                    Width = 450,
                    Height = 70
                };

                Label lblProcessStrategy = new Label()
                {
                    Left = 15,
                    Top = 25,
                    Text = "Select method:",
                    Width = 100,
                    AutoSize = true
                };

                ComboBox cboStrategy = new ComboBox()
                {
                    Left = 120,
                    Top = 23,
                    Width = 300,
                    DropDownStyle = ComboBoxStyle.DropDownList
                };
                cboStrategy.Items.AddRange(new string[] {
                    "Forward, then Backward",
                    "Forward and Backward simultaneously",
                    "Forward only",
                    "Zero-shot on each slice (no prompts)" // New zero-shot option
                });
                cboStrategy.SelectedIndex = 0;  // Default to forward then backward

                grpProcessing.Controls.AddRange(new Control[] { lblProcessStrategy, cboStrategy });

                // Add event handler to control sub-option visibility
                rbCreateMaterial.CheckedChanged += (s, e) =>
                {
                    chkNewMaterial.Enabled = rbCreateMaterial.Checked;
                };

                Button btnOk = new Button() { Text = "Start", Left = 300, Top = 320, Width = 75, DialogResult = DialogResult.OK };
                Button btnCancel = new Button() { Text = "Cancel", Left = 390, Top = 320, Width = 75, DialogResult = DialogResult.Cancel };

                // Add all controls to the dialog
                dialog.Controls.AddRange(new Control[] {
                    lblTitle,
                    grpRange,
                    grpOperation,
                    grpProcessing,
                    btnOk, btnCancel
                });

                dialog.AcceptButton = btnOk;
                dialog.CancelButton = btnCancel;

                if (dialog.ShowDialog() != DialogResult.OK)
                    return;

                int sliceRange = (int)numRange.Value;
                bool useFeatureCache = chkUseFeatureCache.Checked;
                bool exportCropped = rbExportCropped.Checked;
                bool createNewMaterial = !exportCropped && chkNewMaterial.Checked && chkNewMaterial.Enabled;
                int processingStrategy = cboStrategy.SelectedIndex;
                bool useZeroShot = processingStrategy == 3; // Check if zero-shot strategy was selected

                // Set feature caching radius based on user selection
                featureCacheRadius = useFeatureCache ? 3 : 0;

                if (exportCropped)
                {
                    // Export cropped dataset
                    string outputFolder = null;
                    using (FolderBrowserDialog folderDialog = new FolderBrowserDialog())
                    {
                        folderDialog.Description = "Select folder to save cropped dataset";
                        folderDialog.ShowNewFolderButton = true;

                        if (folderDialog.ShowDialog() == DialogResult.OK)
                        {
                            outputFolder = folderDialog.SelectedPath;
                        }
                        else
                        {
                            return;
                        }
                    }

                    ProgressForm progressForm = new ProgressForm("Exporting cropped dataset...");
                    progressForm.Show();

                    try
                    {
                        await ExportCroppedDataset(sliceRange, outputFolder, progressForm, useZeroShot);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error exporting dataset: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Logger.Log($"[MicroSAM] Export error: {ex.Message}");
                    }
                    finally
                    {
                        progressForm.Close();
                        progressForm.Dispose();
                    }
                }
                else
                {
                    // Create progress form
                    ProgressForm progressForm = new ProgressForm("Segmenting volume slices...");
                    progressForm.Show();

                    try
                    {
                        // Get or create material
                        byte materialID;
                        if (createNewMaterial)
                        {
                            // Create a new material for the segmented volume
                            Material volumeMaterial = new Material(
                                "MicroSAM_Volume",
                                selectedMaterial.Color,
                                0, 255,
                                mainForm.GetNextMaterialID());

                            mainForm.Materials.Add(volumeMaterial);
                            materialID = volumeMaterial.ID;

                            // Refresh material list in ControlForm
                            foreach (Form form in Application.OpenForms)
                            {
                                if (form.GetType().Name == "ControlForm")
                                {
                                    form.Invoke(new Action(() =>
                                    {
                                        var refreshMethod = form.GetType().GetMethod("RefreshMaterialList");
                                        if (refreshMethod != null)
                                            refreshMethod.Invoke(form, null);
                                    }));
                                    break;
                                }
                            }

                            mainForm.SaveLabelsChk();
                        }
                        else
                        {
                            materialID = selectedMaterial.ID;
                        }

                        // Apply current slice segmentation to the volume
                        ApplyMaskToVolume(segmentationMask, xySlice, materialID);

                        // Calculate slice range
                        int startSlice = Math.Max(0, xySlice - sliceRange);
                        int endSlice = Math.Min(mainForm.GetDepth() - 1, xySlice + sliceRange);

                        // Get relevant points for propagation if not using zero-shot
                        var relevantPoints = useZeroShot ? new List<AnnotationPoint>() : GetRelevantPointsForCurrentView();

                        if (useZeroShot)
                        {
                            // Use zero-shot segmentation for each slice
                            await SegmentVolumeZeroShot(startSlice, endSlice, xySlice, materialID, progressForm);
                        }
                        else
                        {
                            switch (processingStrategy)
                            {
                                case 0:  // Forward, then Backward (sequential)
                                    await SegmentVolumeSequential(startSlice, endSlice, xySlice, materialID,
                                                                 relevantPoints, progressForm);
                                    break;

                                case 1:  // Forward and Backward simultaneously (parallel)
                                    await SegmentVolumeParallel(startSlice, endSlice, xySlice, materialID,
                                                              relevantPoints, progressForm);
                                    break;

                                case 2:  // Forward only
                                    await SegmentVolumeForwardOnly(xySlice, endSlice, materialID,
                                                                 relevantPoints, progressForm);
                                    break;
                            }
                        }

                        // Update MainForm's views
                        mainForm.RenderViews();
                        await mainForm.RenderOrthoViewsAsync();
                        mainForm.SaveLabelsChk();

                        MessageBox.Show("Volume segmentation complete!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error during volume segmentation: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Logger.Log($"[MicroSAM] Volume segmentation error: {ex.Message}");
                    }
                    finally
                    {
                        progressForm.Close();
                        progressForm.Dispose();
                    }
                }
            }
        }

        // Apply mask to a specific slice in the volume
        private void ApplyMaskToVolume(byte[,] mask, int sliceZ, byte materialID)
        {
            if (mask == null) return;

            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (x < mask.GetLength(0) && y < mask.GetLength(1) && mask[x, y] > 0)
                    {
                        mainForm.volumeLabels[x, y, sliceZ] = materialID;
                    }
                }
            }
        }

        // Zero-shot segmentation strategy for volume processing
        private async Task SegmentVolumeZeroShot(int startSlice, int endSlice, int currentSlice,
                                          byte materialID, ProgressForm progressForm)
        {
            int totalSlices = endSlice - startSlice + 1;
            int processedSlices = 0;

            // Save current mode
            PromptMode originalMode = currentMode;
            currentMode = PromptMode.ZeroShot;

            // Process slices sequentially, using zero-shot on each
            progressForm.UpdateProgress(processedSlices, totalSlices, "Processing with zero-shot segmentation...");

            for (int slice = startSlice; slice <= endSlice; slice++)
            {
                // Skip current slice as it's already processed
                if (slice == currentSlice)
                {
                    processedSlices++;
                    continue;
                }

                // Update progress
                progressForm.SafeUpdateProgress(processedSlices, totalSlices,
                    $"Zero-shot segmenting slice {slice}...");

                // Temporarily set the active slice for segmentation
                int originalXYSlice = xySlice;
                xySlice = slice;

                // Segment this slice using zero-shot
                cachedImageEmbed = null; // Clear cache to ensure fresh segmentation
                byte[,] sliceMask = await SegmentSliceZeroShot(slice);

                // Restore original slice
                xySlice = originalXYSlice;

                // If segmentation failed or mask is empty, continue to next slice
                if (sliceMask == null)
                {
                    Logger.Log($"[MicroSAM] Zero-shot segmentation failed for slice {slice}");
                    processedSlices++;
                    continue;
                }

                // Apply mask to volume
                ApplyMaskToVolume(sliceMask, slice, materialID);
                processedSlices++;
            }

            // Restore original mode
            currentMode = originalMode;
        }

        // Sequential segmentation strategy
        private async Task SegmentVolumeSequential(int startSlice, int endSlice, int currentSlice,
                                           byte materialID, List<AnnotationPoint> relevantPoints,
                                           ProgressForm progressForm)
        {
            int totalSlices = (endSlice - currentSlice) + (currentSlice - startSlice);
            int processedSlices = 0;

            // Current mask starts with segmentation from current slice
            byte[,] currentMask = segmentationMask;

            // Propagate forward
            progressForm.UpdateProgress(processedSlices, totalSlices, "Propagating forward...");
            for (int slice = currentSlice + 1; slice <= endSlice; slice++)
            {
                // Update progress
                processedSlices++;
                progressForm.SafeUpdateProgress(processedSlices, totalSlices,
                    $"Processing slice {slice} (forward)...");

                // Segment this slice using previous mask as guidance
                currentMask = await SegmentSliceWithMask(slice, currentMask, relevantPoints);

                // If segmentation failed or mask is empty, stop
                if (currentMask == null)
                {
                    Logger.Log($"[MicroSAM] Forward propagation stopped at slice {slice}");
                    break;
                }

                // Apply mask to volume
                ApplyMaskToVolume(currentMask, slice, materialID);
            }

            // Reset to current slice for backward propagation
            currentMask = segmentationMask;

            // Propagate backward
            progressForm.UpdateProgress(processedSlices, totalSlices, "Propagating backward...");
            for (int slice = currentSlice - 1; slice >= startSlice; slice--)
            {
                // Update progress
                processedSlices++;
                progressForm.SafeUpdateProgress(processedSlices, totalSlices,
                    $"Processing slice {slice} (backward)...");

                // Segment this slice using previous mask as guidance
                currentMask = await SegmentSliceWithMask(slice, currentMask, relevantPoints);

                // If segmentation failed or mask is empty, stop
                if (currentMask == null)
                {
                    Logger.Log($"[MicroSAM] Backward propagation stopped at slice {slice}");
                    break;
                }

                // Apply mask to volume
                ApplyMaskToVolume(currentMask, slice, materialID);
            }
        }

        // Parallel segmentation strategy
        private async Task SegmentVolumeParallel(int startSlice, int endSlice, int currentSlice,
                                        byte materialID, List<AnnotationPoint> relevantPoints,
                                        ProgressForm progressForm)
        {
            int totalSlices = (endSlice - currentSlice) + (currentSlice - startSlice);
            int processedSlices = 0;

            byte[,] forwardMask = segmentationMask;
            byte[,] backwardMask = segmentationMask;

            // Create progress reporting
            var progress = new Progress<(int processed, string message)>(update =>
            {
                processedSlices = update.processed;
                progressForm.SafeUpdateProgress(processedSlices, totalSlices, update.message);
            });

            // Process forward and backward in parallel
            await Task.WhenAll(
                SegmentForward(currentSlice + 1, endSlice, forwardMask, materialID, relevantPoints, progress),
                SegmentBackward(currentSlice - 1, startSlice, backwardMask, materialID, relevantPoints, progress)
            );
        }

        // Helper for parallel forward segmentation
        private async Task SegmentForward(int startSlice, int endSlice, byte[,] initialMask,
                                byte materialID, List<AnnotationPoint> relevantPoints,
                                IProgress<(int, string)> progress)
        {
            byte[,] currentMask = initialMask;
            int processed = 0;

            for (int slice = startSlice; slice <= endSlice; slice++)
            {
                processed++;
                progress.Report((processed, $"Processing slice {slice} (forward)..."));

                // Segment this slice
                currentMask = await SegmentSliceWithMask(slice, currentMask, relevantPoints);

                // If segmentation failed, stop
                if (currentMask == null)
                {
                    Logger.Log($"[MicroSAM] Forward propagation stopped at slice {slice}");
                    break;
                }

                // Apply mask to volume
                ApplyMaskToVolume(currentMask, slice, materialID);
            }
        }

        // Helper for parallel backward segmentation
        private async Task SegmentBackward(int startSlice, int endSlice, byte[,] initialMask,
                                 byte materialID, List<AnnotationPoint> relevantPoints,
                                 IProgress<(int, string)> progress)
        {
            byte[,] currentMask = initialMask;
            int processed = 0;

            for (int slice = startSlice; slice >= endSlice; slice--)
            {
                processed++;
                progress.Report((processed, $"Processing slice {slice} (backward)..."));

                // Segment this slice
                currentMask = await SegmentSliceWithMask(slice, currentMask, relevantPoints);

                // If segmentation failed, stop
                if (currentMask == null)
                {
                    Logger.Log($"[MicroSAM] Backward propagation stopped at slice {slice}");
                    break;
                }

                // Apply mask to volume
                ApplyMaskToVolume(currentMask, slice, materialID);
            }
        }

        // Process forward direction only
        private async Task SegmentVolumeForwardOnly(int startSlice, int endSlice, byte materialID,
                                           List<AnnotationPoint> relevantPoints, ProgressForm progressForm)
        {
            int totalSlices = endSlice - startSlice;
            int processedSlices = 0;

            // Current mask starts with segmentation from current slice
            byte[,] currentMask = segmentationMask;

            // Propagate forward
            progressForm.UpdateProgress(processedSlices, totalSlices, "Propagating forward only...");
            for (int slice = startSlice + 1; slice <= endSlice; slice++)
            {
                // Update progress
                processedSlices++;
                progressForm.SafeUpdateProgress(processedSlices, totalSlices,
                    $"Processing slice {slice} (forward)...");

                // Segment this slice using previous mask as guidance
                currentMask = await SegmentSliceWithMask(slice, currentMask, relevantPoints);

                // If segmentation failed or mask is empty, stop
                if (currentMask == null)
                {
                    Logger.Log($"[MicroSAM] Forward propagation stopped at slice {slice}");
                    break;
                }

                // Apply mask to volume
                ApplyMaskToVolume(currentMask, slice, materialID);
            }
        }

        // Zero-shot segmentation for a single slice
        // Zero‑shot segmentation for a single XY slice
        private async Task<byte[,]> SegmentSliceZeroShot(int sliceZ)
        {
            try
            {
                /* ---------- run encoder ------------------------------------------- */
                Tensor<float> imageInput = await Task.Run(() => PreprocessSliceImage(sliceZ));

                byte[,] finalMask = null;   // will be assigned in the using‑block

                using (var encOut = await Task.Run(() =>
                    encoderSession.Run(new[] { NamedOnnxValue.CreateFromTensor("image", imageInput) })))
                {
                    var imageEmbed = encOut.First(o => o.Name == "image_embeddings").AsTensor<float>();

                    /* ---------- dummy prompt tensors ------------------------------ */
                    var pointCoords = new DenseTensor<float>(new[] { 1, 1, 2 });
                    var pointLabels = new DenseTensor<float>(new[] { 1, 1 });
                    pointCoords[0, 0, 0] = 512;   // centre point (ignored)
                    pointCoords[0, 0, 1] = 512;
                    pointLabels[0, 0] = 0;

                    var maskInput = new DenseTensor<float>(new[] { 1, 1, 256, 256 });
                    var hasMaskInput = new DenseTensor<float>(new[] { 1 });   // zero‑shot
                    hasMaskInput[0] = 0;

                    var origImSize = new DenseTensor<float>(new[] { 2 });
                    origImSize[0] = mainForm.GetHeight();
                    origImSize[1] = mainForm.GetWidth();

                    /* ---------- run decoder -------------------------------------- */
                    using (var decOut = await Task.Run(() => decoderSession.Run(new[]
                    {
                NamedOnnxValue.CreateFromTensor("image_embeddings", imageEmbed),
                NamedOnnxValue.CreateFromTensor("point_coords",       pointCoords),
                NamedOnnxValue.CreateFromTensor("point_labels",       pointLabels),
                NamedOnnxValue.CreateFromTensor("mask_input",         maskInput),
                NamedOnnxValue.CreateFromTensor("has_mask_input",     hasMaskInput),
                NamedOnnxValue.CreateFromTensor("orig_im_size",       origImSize)
            })))
                    {
                        var masks = decOut.First(o => o.Name == "masks").AsTensor<float>();
                        var iouPredictions = decOut.First(o => o.Name == "iou_predictions").AsTensor<float>();

                        int bestIdx = GetBestMaskIndex(iouPredictions);
                        float bestIoU = iouPredictions[bestIdx, 0];
                        Logger.Log($"[MicroSAM] Zero‑shot slice {sliceZ} IoU {bestIoU:F3}");

                        finalMask = ConvertSamMaskToByteMask(masks, bestIdx, currentActiveView);
                    }
                }

                return finalMask;
            }
            catch (Exception ex)
            {
                Logger.Log($"[MicroSAM] Zero‑shot error on slice {sliceZ}: {ex.Message}");
                return null;
            }
        }

        // Guided segmentation for a single slice
        // Guided propagation (previous mask + annotation points)
        private async Task<byte[,]> SegmentSliceWithMask(
            int sliceZ, byte[,] prevMask, List<AnnotationPoint> relevantPoints)
        {
            try
            {
                /* ---------- encoder with caching --------------------------------- */
                bool useCache = Math.Abs(sliceZ - cachedFeatureSlice) <= featureCacheRadius &&
                                cachedImageEmbed != null;

                DenseTensor<float> imageEmbed;

                if (useCache)
                {
                    imageEmbed = cachedImageEmbed;
                    Logger.Log($"[MicroSAM] Using cached features for slice {sliceZ}");
                }
                else
                {
                    Tensor<float> imageInput = await Task.Run(() => PreprocessSliceImage(sliceZ));

                    using (var encOut = await Task.Run(() =>
                        encoderSession.Run(new[] { NamedOnnxValue.CreateFromTensor("image", imageInput) })))
                    {
                        var encTensor = encOut.First(o => o.Name == "image_embeddings").AsTensor<float>();

                        cachedImageEmbed = new DenseTensor<float>(encTensor.Dimensions);
                        CopyTensorData(encTensor, cachedImageEmbed);
                        cachedFeatureSlice = sliceZ;
                        imageEmbed = cachedImageEmbed;
                    }
                }

                /* ---------- build mask_input ------------------------------------- */
                var maskInput = new DenseTensor<float>(new[] { 1, 1, 256, 256 });
                float sx, sy;
                switch (currentActiveView)
                {
                    case ActiveView.XY:
                        sx = 256f / mainForm.GetWidth();
                        sy = 256f / mainForm.GetHeight();
                        break;

                    case ActiveView.XZ:
                        sx = 256f / mainForm.GetWidth();
                        sy = 256f / mainForm.GetDepth();
                        break;

                    default:       /* YZ */
                        sx = 256f / mainForm.GetDepth();
                        sy = 256f / mainForm.GetHeight();
                        break;
                }

                for (int y = 0; y < prevMask.GetLength(1); y++)
                    for (int x = 0; x < prevMask.GetLength(0); x++)
                    {
                        if (prevMask[x, y] > 0)
                        {
                            int tx = Math.Min((int)(x * sx), 255);
                            int ty = Math.Min((int)(y * sy), 255);
                            maskInput[0, 0, ty, tx] = 1f;
                        }
                    }

                var hasMaskInput = new DenseTensor<float>(new[] { 1 });
                hasMaskInput[0] = 1f;

                /* ---------- annotation points ------------------------------------ */
                int nPts = Math.Max(1, relevantPoints.Count);
                var pCoords = new DenseTensor<float>(new[] { 1, nPts, 2 });
                var pLabels = new DenseTensor<float>(new[] { 1, nPts });

                float scaleX, scaleY;
                switch (currentActiveView)
                {
                    case ActiveView.XY:
                        scaleX = 1024f / mainForm.GetWidth();
                        scaleY = 1024f / mainForm.GetHeight();
                        break;

                    case ActiveView.XZ:
                        scaleX = 1024f / mainForm.GetWidth();
                        scaleY = 1024f / mainForm.GetDepth();
                        break;

                    default:       /* YZ */
                        scaleX = 1024f / mainForm.GetDepth();
                        scaleY = 1024f / mainForm.GetHeight();
                        break;
                }

                for (int i = 0; i < relevantPoints.Count; i++)
                {
                    AnnotationPoint pt = relevantPoints[i];
                    pCoords[0, i, 0] = pt.X * scaleX;
                    pCoords[0, i, 1] = pt.Y * scaleY;
                    pLabels[0, i] = (pointTypes.TryGetValue(pt.ID, out bool pos) && pos) ? 1f : 0f;
                }

                if (relevantPoints.Count == 0)
                {
                    pCoords[0, 0, 0] = 512; pCoords[0, 0, 1] = 512;
                    pLabels[0, 0] = 0f;
                }

                /* ---------- decoder ---------------------------------------------- */
                var origImSize = new DenseTensor<float>(new[] { 2 });
                switch (currentActiveView)
                {
                    case ActiveView.XY:
                        origImSize[0] = mainForm.GetHeight();
                        origImSize[1] = mainForm.GetWidth();
                        break;

                    case ActiveView.XZ:
                        origImSize[0] = mainForm.GetDepth();
                        origImSize[1] = mainForm.GetWidth();
                        break;

                    default:       /* YZ */
                        origImSize[0] = mainForm.GetHeight();
                        origImSize[1] = mainForm.GetDepth();
                        break;
                }

                byte[,] finalMask = null;

                using (var decOut = await Task.Run(() => decoderSession.Run(new[]
                {
            NamedOnnxValue.CreateFromTensor("image_embeddings", imageEmbed),
            NamedOnnxValue.CreateFromTensor("point_coords",       pCoords),
            NamedOnnxValue.CreateFromTensor("point_labels",       pLabels),
            NamedOnnxValue.CreateFromTensor("mask_input",         maskInput),
            NamedOnnxValue.CreateFromTensor("has_mask_input",     hasMaskInput),
            NamedOnnxValue.CreateFromTensor("orig_im_size",       origImSize)
        })))
                {
                    var masks = decOut.First(o => o.Name == "masks").AsTensor<float>();
                    var iouPredictions = decOut.First(o => o.Name == "iou_predictions").AsTensor<float>();

                    int bestIdx = GetBestMaskIndex(iouPredictions);
                    float bestIoU = iouPredictions[bestIdx, 0];
                    Logger.Log($"[MicroSAM] Guided slice {sliceZ} IoU {bestIoU:F3}");

                    if (bestIoU < 0.30f)
                        return null;  // stop propagation

                    finalMask = ConvertSamMaskToByteMask(masks, bestIdx, currentActiveView);
                }

                return finalMask;
            }
            catch (Exception ex)
            {
                Logger.Log($"[MicroSAM] Propagation error on slice {sliceZ}: {ex.Message}");
                return null;
            }
        }

        // Export cropped dataset based on segmentation
        private async Task ExportCroppedDataset(int sliceRange, string outputFolder, ProgressForm progressForm, bool useZeroShot)
        {
            // First we need to segment the volume slices
            var relevantPoints = GetRelevantPointsForCurrentView();

            // Current slice's mask
            byte[,] currentMask = segmentationMask;

            // Calculate slice range
            int startSlice = Math.Max(0, xySlice - sliceRange);
            int endSlice = Math.Min(mainForm.GetDepth() - 1, xySlice + sliceRange);
            int totalSlices = endSlice - startSlice + 1;

            // Dictionary to store masks for each slice
            Dictionary<int, byte[,]> allSliceMasks = new Dictionary<int, byte[,]>();

            // Add current slice mask
            allSliceMasks[xySlice] = segmentationMask;

            int processedSlices = 1; // Start at 1 because we already have the current slice

            // Progress reporting
            progressForm.UpdateProgress(processedSlices, totalSlices * 2, "Segmenting volume...");

            // Save current prompt mode
            PromptMode originalMode = currentMode;

            if (useZeroShot)
            {
                // Switch to zero-shot mode for segmentation
                currentMode = PromptMode.ZeroShot;

                // Process all slices with zero-shot segmentation
                for (int slice = startSlice; slice <= endSlice; slice++)
                {
                    // Skip current slice as we already have it
                    if (slice == xySlice)
                        continue;

                    // Update progress
                    processedSlices++;
                    progressForm.SafeUpdateProgress(processedSlices, totalSlices * 2,
                        $"Zero-shot segmenting slice {slice}...");

                    // Segment with zero-shot
                    byte[,] sliceMask = await SegmentSliceZeroShot(slice);

                    if (sliceMask != null)
                    {
                        allSliceMasks[slice] = sliceMask;
                    }
                }
            }
            else
            {
                // First pass - forward propagation
                for (int slice = xySlice + 1; slice <= endSlice; slice++)
                {
                    // Update progress
                    processedSlices++;
                    progressForm.SafeUpdateProgress(processedSlices, totalSlices * 2,
                        $"Segmenting slice {slice} (forward)...");

                    // Segment this slice using previous mask as guidance
                    currentMask = await SegmentSliceWithMask(slice, currentMask, relevantPoints);

                    // If segmentation failed or mask is empty, stop
                    if (currentMask == null)
                    {
                        Logger.Log($"[MicroSAM] Forward propagation stopped at slice {slice}");
                        break;
                    }

                    // Store the mask
                    allSliceMasks[slice] = currentMask;
                }

                // Reset to current slice for backward propagation
                currentMask = segmentationMask;

                // Second pass - backward propagation
                for (int slice = xySlice - 1; slice >= startSlice; slice--)
                {
                    // Update progress
                    processedSlices++;
                    progressForm.SafeUpdateProgress(processedSlices, totalSlices * 2,
                        $"Segmenting slice {slice} (backward)...");

                    // Segment this slice using previous mask as guidance
                    currentMask = await SegmentSliceWithMask(slice, currentMask, relevantPoints);

                    // If segmentation failed or mask is empty, stop
                    if (currentMask == null)
                    {
                        Logger.Log($"[MicroSAM] Backward propagation stopped at slice {slice}");
                        break;
                    }

                    // Store the mask
                    allSliceMasks[slice] = currentMask;
                }
            }

            // Restore original prompt mode
            currentMode = originalMode;

            // Now we have masks for all slices in range, determine the bounds of the segmented region
            int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
            int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;

            // Calculate the bounds and check if this is a "dark" or "bright" segmentation
            bool isDarkSegmentation = await Task.Run(() =>
            {
                long totalPixelValue = 0;
                long totalPixelCount = 0;

                foreach (var slicePair in allSliceMasks)
                {
                    int z = slicePair.Key;
                    byte[,] mask = slicePair.Value;

                    // Update Z bounds
                    minZ = Math.Min(minZ, z);
                    maxZ = Math.Max(maxZ, z);

                    // Calculate bounds for this slice
                    for (int y = 0; y < mask.GetLength(1); y++)
                    {
                        for (int x = 0; x < mask.GetLength(0); x++)
                        {
                            if (mask[x, y] > 0)
                            {
                                // Update XY bounds
                                minX = Math.Min(minX, x);
                                minY = Math.Min(minY, y);
                                maxX = Math.Max(maxX, x);
                                maxY = Math.Max(maxY, y);

                                // Add pixel value to total for average calculation
                                byte pixelValue = mainForm.volumeData[x, y, z];
                                totalPixelValue += pixelValue;
                                totalPixelCount++;
                            }
                        }
                    }
                }

                // Calculate average pixel intensity in the segmented region
                double avgIntensity = totalPixelCount > 0 ? (double)totalPixelValue / totalPixelCount : 128;

                // If average intensity is less than 128, it's a dark segmentation
                return avgIntensity < 128;
            });

            // Ensure we found some bounds
            if (minX == int.MaxValue || minY == int.MaxValue || minZ == int.MaxValue ||
                maxX == int.MinValue || maxY == int.MinValue || maxZ == int.MinValue)
            {
                throw new Exception("Could not determine bounds of segmented region!");
            }

            // Calculate dimensions of cropped volume
            int croppedWidth = maxX - minX + 1;
            int croppedHeight = maxY - minY + 1;
            int croppedDepth = maxZ - minZ + 1;

            // Create output directory if it doesn't exist
            if (!Directory.Exists(outputFolder))
            {
                Directory.CreateDirectory(outputFolder);
            }

            // Create metadata file
            using (StreamWriter writer = new StreamWriter(Path.Combine(outputFolder, "metadata.txt")))
            {
                writer.WriteLine($"Original Size: {mainForm.GetWidth()} x {mainForm.GetHeight()} x {mainForm.GetDepth()}");
                writer.WriteLine($"Cropped Size: {croppedWidth} x {croppedHeight} x {croppedDepth}");
                writer.WriteLine($"Cropped Bounds: X[{minX}-{maxX}], Y[{minY}-{maxY}], Z[{minZ}-{maxZ}]");
                writer.WriteLine($"Pixel Size: {mainForm.GetPixelSize()} m");
                writer.WriteLine($"Segmentation Type: {(isDarkSegmentation ? "Dark" : "Bright")} area");
                writer.WriteLine($"Segmentation Method: {(useZeroShot ? "Zero-Shot" : "Prompt-Based")}");
                writer.WriteLine($"Created: {DateTime.Now}");
            }

            // Save the cropped slices
            progressForm.UpdateProgress(0, croppedDepth, "Saving cropped slices...");

            // Use parallel processing for saving the images
            await Task.Run(() =>
            {
                object lockObj = new object();
                int saved = 0;

                Parallel.For(minZ, maxZ + 1, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, z =>
                {
                    try
                    {
                        // Check if we have a mask for this slice
                        if (!allSliceMasks.TryGetValue(z, out byte[,] sliceMask))
                        {
                            // If not, we need to create one (this should be rare)
                            Logger.Log($"[MicroSAM] Warning: No mask for slice {z}, creating one");
                            sliceMask = new byte[mainForm.GetWidth(), mainForm.GetHeight()];
                        }

                        // Create a new bitmap for the cropped slice
                        using (Bitmap croppedBmp = new Bitmap(croppedWidth, croppedHeight, PixelFormat.Format8bppIndexed))
                        {
                            // Set up grayscale palette
                            ColorPalette palette = croppedBmp.Palette;
                            for (int i = 0; i < 256; i++)
                            {
                                palette.Entries[i] = Color.FromArgb(i, i, i);
                            }
                            croppedBmp.Palette = palette;

                            // Lock the bitmap for writing
                            BitmapData bmpData = croppedBmp.LockBits(
                                new Rectangle(0, 0, croppedWidth, croppedHeight),
                                ImageLockMode.WriteOnly,
                                PixelFormat.Format8bppIndexed);

                            // Copy the pixel data
                            unsafe
                            {
                                byte* ptr = (byte*)bmpData.Scan0;
                                int stride = bmpData.Stride;

                                for (int y = 0; y < croppedHeight; y++)
                                {
                                    int origY = y + minY;
                                    for (int x = 0; x < croppedWidth; x++)
                                    {
                                        int origX = x + minX;

                                        if (origX < sliceMask.GetLength(0) && origY < sliceMask.GetLength(1) && sliceMask[origX, origY] > 0)
                                        {
                                            // Get the original voxel value
                                            byte pixelValue = mainForm.volumeData[origX, origY, z];

                                            // Invert if necessary for dark segmentations
                                            if (isDarkSegmentation)
                                            {
                                                pixelValue = (byte)(255 - pixelValue);
                                            }

                                            // Set the pixel in the output bitmap (for 8bpp, one byte per pixel)
                                            ptr[y * stride + x] = pixelValue;
                                        }
                                        else
                                        {
                                            // For pixels outside the mask, use black (or white for inverted)
                                            ptr[y * stride + x] = isDarkSegmentation ? (byte)255 : (byte)0;
                                        }
                                    }
                                }
                            }

                            // Unlock the bitmap
                            croppedBmp.UnlockBits(bmpData);

                            // Save the bitmap
                            string fileName = Path.Combine(outputFolder, $"slice_{z - minZ:D5}.bmp");
                            croppedBmp.Save(fileName, ImageFormat.Bmp);

                            // Update progress (thread-safe)
                            lock (lockObj)
                            {
                                saved++;
                                int currentProgress = saved * 100 / croppedDepth;
                                progressForm.SafeUpdateProgress(saved, croppedDepth, $"Saved {saved} of {croppedDepth} slices");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[MicroSAM] Error saving slice {z}: {ex.Message}");
                    }
                });
            });

            // Show success message
            MessageBox.Show($"Successfully exported {croppedDepth} slices to {outputFolder}.", "Export Complete",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private unsafe Tensor<float> PreprocessSliceImage(int sliceZ)
        {
            // Create a bitmap for the specified slice
            using (Bitmap sliceBitmap = CreateSliceBitmap(sliceZ))
            {
                // Create a tensor with shape [1, 3, 1024, 1024]
                DenseTensor<float> inputTensor = new DenseTensor<float>(new[] { 1, 3, 1024, 1024 });

                // Create a resized version of the slice
                using (Bitmap resized = new Bitmap(1024, 1024))
                {
                    using (Graphics g = Graphics.FromImage(resized))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(sliceBitmap, 0, 0, 1024, 1024);
                    }

                    // Lock the bitmap and access its pixel data
                    BitmapData bmpData = resized.LockBits(
                        new Rectangle(0, 0, resized.Width, resized.Height),
                        ImageLockMode.ReadOnly,
                        PixelFormat.Format24bppRgb);

                    int stride = bmpData.Stride;
                    int bytesPerPixel = 3; // RGB

                    byte* ptr = (byte*)bmpData.Scan0;

                    // Process pixels and normalize to range [0.0, 1.0]
                    for (int y = 0; y < 1024; y++)
                    {
                        for (int x = 0; x < 1024; x++)
                        {
                            int offset = y * stride + x * bytesPerPixel;

                            // BGR order (standard in Bitmap)
                            byte b = ptr[offset];
                            byte g = ptr[offset + 1];
                            byte r = ptr[offset + 2];

                            // Normalize to range [0.0, 1.0] and convert to RGB order for the model
                            inputTensor[0, 0, y, x] = r / 255.0f;
                            inputTensor[0, 1, y, x] = g / 255.0f;
                            inputTensor[0, 2, y, x] = b / 255.0f;
                        }
                    }

                    resized.UnlockBits(bmpData);
                    return inputTensor;
                }
            }
        }

        /// <summary>Returns the index of the mask with the highest IoU prediction.</summary>
        private static int GetBestMaskIndex(Tensor<float> iouPredictions)
        {
            int best = 0;
            float bestVal = iouPredictions[0, 0];

            for (int i = 1; i < iouPredictions.Dimensions[0]; i++)
            {
                if (iouPredictions[i, 0] > bestVal)
                {
                    bestVal = iouPredictions[i, 0];
                    best = i;
                }
            }
            return best;
        }

        /// <summary>
        /// Converts the SAM decoder tensor into a byte[,] mask that is the same
        /// size as the currently active view (XY, XZ or YZ).
        /// Works with any SAM layout:
        ///   • [N,1,H,W]  (MicroSAM default)
        ///   • [N,H,W]
        ///   • [N,1,L] or [N,L,1] – will be stretched over the long side
        /// </summary>
        private byte[,] ConvertSamMaskToByteMask(Tensor<float> masks, int maskIdx, ActiveView view)
        {
            /* ---------- source tensor size ---------- */
            bool channelFirst = masks.Rank == 4;                 // [N,1,H,W]
            int srcH, srcW;

            if (channelFirst)
            {
                srcH = masks.Dimensions[2];
                srcW = masks.Dimensions[3];
            }
            else if (masks.Rank == 3)                            // [N,H,W]  or  degenerate
            {
                srcH = masks.Dimensions[1];
                srcW = masks.Dimensions[2];
            }
            else
                throw new InvalidOperationException($"Unexpected mask tensor rank {masks.Rank}");

            /* vector output → square image ---------------------------------------- */
            if (srcH == 1 || srcW == 1)
            {
                if (srcH == 1 && srcW == 1)
                    throw new InvalidOperationException("SAM returned a 1 × 1 mask.");
                if (srcH == 1) srcH = srcW;
                if (srcW == 1) srcW = srcH;
            }

            /* ---------- destination size ---------- */
            int dstW, dstH;
            switch (view)
            {
                case ActiveView.XY:
                    dstW = mainForm.GetWidth();
                    dstH = mainForm.GetHeight();
                    break;

                case ActiveView.XZ:
                    dstW = mainForm.GetWidth();
                    dstH = mainForm.GetDepth();
                    break;

                case ActiveView.YZ:
                    dstW = mainForm.GetDepth();
                    dstH = mainForm.GetHeight();
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(view));
            }

            byte[,] dst = new byte[dstW, dstH];

            /* ---------- scale copy ---------- */
            double scaleX = (double)srcW / dstW;
            double scaleY = (double)srcH / dstH;

            for (int y = 0; y < dstH; y++)
            {
                int sy = Math.Min((int)(y * scaleY), srcH - 1);

                for (int x = 0; x < dstW; x++)
                {
                    int sx = Math.Min((int)(x * scaleX), srcW - 1);

                    float v = channelFirst
                              ? masks[maskIdx, 0, sy, sx]          // [N,1,H,W]
                              : masks[maskIdx, sy, sx];            // [N,H,W]

                    if (v > 0f)
                        dst[x, y] = selectedMaterial.ID;
                }
            }

            return dst;
        }

        private void ClearCaches()
        {
            // Clear XY cache
            foreach (int key in cachedXYKeys)
            {
                var bitmap = xySliceCache.Get(key);
                bitmap?.Dispose();
            }
            cachedXYKeys.Clear();

            // Clear XZ cache
            foreach (int key in cachedXZKeys)
            {
                var bitmap = xzSliceCache.Get(key);
                bitmap?.Dispose();
            }
            cachedXZKeys.Clear();

            // Clear YZ cache
            foreach (int key in cachedYZKeys)
            {
                var bitmap = yzSliceCache.Get(key);
                bitmap?.Dispose();
            }
            cachedYZKeys.Clear();

            Logger.Log("[MicroSAM] All slice caches cleared");
        }

        public void Show()
        {
            samForm.Show();
        }
    }
}