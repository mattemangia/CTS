﻿using Microsoft.ML.OnnxRuntime;
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

namespace CTS
{
    public partial class SegmentAnythingCT
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
        private Button btnPositivePrompt, btnNegativePrompt;
        private CheckBox chkAutoUpdate;
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
        private DenseTensor<float> cachedHighResFeats0 = null;
        private DenseTensor<float> cachedHighResFeats1 = null;
        private int cachedFeatureSlice = -1;
        private int featureCacheRadius = 3; // How many slices to reuse encoder features for

        // Segmentation results
        private byte[,] segmentationMask;

        // Prompt mode
        private enum PromptMode
        { Positive, Negative }

        private PromptMode currentMode = PromptMode.Positive;

        // Slice change callback
        private Action<int> sliceChangeCallback;

        /// <summary>
        /// Constructor that allows using SegmentAnythingCT without showing the UI
        /// </summary>
        /// <param name="mainForm">Reference to the main application form</param>
        /// <param name="selectedMaterial">The material to use for segmentation</param>
        /// <param name="annotationManager">The annotation manager for storing points</param>
        /// <param name="showUI">Whether to show the user interface (default: true)</param>
        public SegmentAnythingCT(MainForm mainForm, Material selectedMaterial, AnnotationManager annotationManager, bool showUI = true)
        {
            Logger.Log("[SegmentAnythingCT] Creating SAM interface");
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
            encoderPath = Path.Combine(onnxDirectory, "sam2.1_large.encoder.onnx");
            decoderPath = Path.Combine(onnxDirectory, "sam2.1_large.decoder.onnx");

            if (showUI)
            {
                modelPathTextBox.Text = onnxDirectory;
            }

            // Try to load models automatically
            try
            {
                Logger.Log("[SegmentAnythingCT] Attempting to load ONNX models");
                LoadONNXModels();

                if (showUI)
                {
                    statusLabel.Text = "Models loaded successfully";
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SegmentAnythingCT] Error loading models: {ex.Message}");

                if (showUI)
                {
                    statusLabel.Text = $"Error loading models: {ex.Message}";
                }
            }
        }

        public SegmentAnythingCT(MainForm mainForm, Material selectedMaterial, AnnotationManager annotationManager)
     : this(mainForm, selectedMaterial, annotationManager, true)
        {
        }

        private void InitializeForm()
        {
            Logger.Log("[SegmentAnythingCT] Initializing form");
            samForm = new Form
            {
                Text = "Segment Anything - CT",
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
                Width = 140,
                Height = 30,
                BackColor = Color.LightGreen
            };
            btnPositivePrompt.Click += (s, e) =>
            {
                currentMode = PromptMode.Positive;
                btnPositivePrompt.BackColor = Color.LightGreen;
                btnNegativePrompt.BackColor = SystemColors.Control;
                Logger.Log("[SegmentAnythingCT] Switched to positive prompt mode");
            };

            btnNegativePrompt = new Button
            {
                Text = "Negative Prompt (-)",
                Location = new Point(160, 170),
                Width = 140,
                Height = 30
            };
            btnNegativePrompt.Click += (s, e) =>
            {
                currentMode = PromptMode.Negative;
                btnPositivePrompt.BackColor = SystemColors.Control;
                btnNegativePrompt.BackColor = Color.LightPink;
                Logger.Log("[SegmentAnythingCT] Switched to negative prompt mode");
            };

            // --------- Auto-Update Section ---------
            chkAutoUpdate = new CheckBox
            {
                Text = "Auto-update when annotations change",
                Location = new Point(10, 210),
                AutoSize = true,
                Checked = true
            };

            // --------- Active View for Segmentation Section ---------
            Label lblActiveView = new Label
            {
                Text = "Active View for Segmentation:",
                Location = new Point(10, 240),
                AutoSize = true
            };

            btnXYView = new Button
            {
                Text = "XY View",
                Location = new Point(10, 260),
                Width = 80,
                Height = 30,
                BackColor = Color.LightSkyBlue
            };
            btnXYView.Click += (s, e) =>
            {
                currentActiveView = ActiveView.XY;
                UpdateActiveViewButtons();
                Logger.Log("[SegmentAnythingCT] Switched to XY view for segmentation");

                // Check if there are points in this view and auto-update if enabled
                if (chkAutoUpdate.Checked)
                {
                    var viewPoints = GetRelevantPointsForCurrentView();
                    if (viewPoints.Count > 0)
                    {
                        Task.Run(() => PerformSegmentation());
                    }
                }
            };

            btnXZView = new Button
            {
                Text = "XZ View",
                Location = new Point(100, 260),
                Width = 80,
                Height = 30
            };
            btnXZView.Click += (s, e) =>
            {
                currentActiveView = ActiveView.XZ;
                UpdateActiveViewButtons();
                Logger.Log("[SegmentAnythingCT] Switched to XZ view for segmentation");

                // Check if there are points in this view and auto-update if enabled
                if (chkAutoUpdate.Checked)
                {
                    var viewPoints = GetRelevantPointsForCurrentView();
                    if (viewPoints.Count > 0)
                    {
                        Task.Run(() => PerformSegmentation());
                    }
                }
            };

            btnYZView = new Button
            {
                Text = "YZ View",
                Location = new Point(190, 260),
                Width = 80,
                Height = 30
            };
            btnYZView.Click += (s, e) =>
            {
                currentActiveView = ActiveView.YZ;
                UpdateActiveViewButtons();
                Logger.Log("[SegmentAnythingCT] Switched to YZ view for segmentation");

                // Check if there are points in this view and auto-update if enabled
                if (chkAutoUpdate.Checked)
                {
                    var viewPoints = GetRelevantPointsForCurrentView();
                    if (viewPoints.Count > 0)
                    {
                        Task.Run(() => PerformSegmentation());
                    }
                }
            };

            // --------- Action Buttons ---------
            btnApply = new Button
            {
                Text = "Apply Mask",
                Location = new Point(10, 300),
                Width = 100,
                Height = 30
            };
            btnApply.Click += (s, e) => ApplySegmentationMask();
            Button btnApplyToVolume = new Button
            {
                Text = "Apply to Volume",
                Location = new Point(230, 300),
                Width = 120,
                Height = 30
            };
            btnApplyToVolume.Click += (s, e) => ApplyToVolume();
            controlPanel.Controls.Add(btnApplyToVolume);
            btnClose = new Button
            {
                Text = "Close",
                Location = new Point(120, 300),
                Width = 100,
                Height = 30
            };
            btnClose.Click += (s, e) => samForm.Close();

            statusLabel = new Label
            {
                Text = "Ready",
                Location = new Point(10, 340),
                AutoSize = true
            };

            // --------- XY Slice Controls ---------
            lblSliceXY = new Label
            {
                Text = $"XY Slice: {xySlice} / {(mainForm.GetDepth() > 0 ? mainForm.GetDepth() - 1 : 0)}",
                Location = new Point(10, 370),
                AutoSize = true
            };

            sliderXY = new TrackBar
            {
                Minimum = 0,
                Maximum = mainForm.GetDepth() > 0 ? mainForm.GetDepth() - 1 : 0,
                Value = xySlice,
                Location = new Point(10, 390),
                Width = 220,
                TickStyle = TickStyle.None
            };

            numXY = new NumericUpDown
            {
                Minimum = 0,
                Maximum = sliderXY.Maximum,
                Value = xySlice,
                Location = new Point(240, 390),
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
                Location = new Point(10, 420),
                AutoSize = true
            };

            sliderXZ = new TrackBar
            {
                Minimum = 0,
                Maximum = mainForm.GetHeight() > 0 ? mainForm.GetHeight() - 1 : 0,
                Value = xzRow,
                Location = new Point(10, 440),
                Width = 220,
                TickStyle = TickStyle.None
            };

            numXZ = new NumericUpDown
            {
                Minimum = 0,
                Maximum = sliderXZ.Maximum,
                Value = xzRow,
                Location = new Point(240, 440),
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
                Location = new Point(10, 470),
                AutoSize = true
            };

            sliderYZ = new TrackBar
            {
                Minimum = 0,
                Maximum = mainForm.GetWidth() > 0 ? mainForm.GetWidth() - 1 : 0,
                Value = yzCol,
                Location = new Point(10, 490),
                Width = 220,
                TickStyle = TickStyle.None
            };

            numYZ = new NumericUpDown
            {
                Minimum = 0,
                Maximum = sliderYZ.Maximum,
                Value = yzCol,
                Location = new Point(240, 490),
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
                Location = new Point(10, 520),
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
                      "- Click on existing points to remove them\n" +
                      "- Use mousewheel to zoom, drag to pan\n" +
                      "- Select which view to segment (XY/XZ/YZ)\n" +
                      "- Click 'Apply Mask' to save the segmentation",
                Location = new Point(10, 550),
                Size = new Size(300, 130),
                BorderStyle = BorderStyle.FixedSingle
            };

            // --------- Selected Material Information ---------
            Label lblMaterial = new Label
            {
                Text = $"Selected Material: {selectedMaterial.Name}",
                Location = new Point(10, 690),
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
        lblPromptMode, btnPositivePrompt, btnNegativePrompt,

        // Auto-Update
        chkAutoUpdate,

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

                Logger.Log("[SegmentAnythingCT] Form closing, resources cleaned up");
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
                Logger.Log($"[SegmentAnythingCT] XY zoom changed to {xyZoom:F2}");
            };

            // XY viewer mouse events for panning and point placement
            Point lastPos = Point.Empty;
            bool isPanning = false;

            // Replace the mousDown handler in SetupXYViewerEvents method:
            xyViewer.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
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
                                Logger.Log($"[SegmentAnythingCT] Deleted point at ({point.X}, {point.Y}, {point.Z})");

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
                                        UpdateViewers();
                                    }
                                }
                                break;
                            }
                        }

                        // If we didn't delete a point, add a new one
                        if (!pointDeleted)
                        {
                            // FIX: Store point type in a standardized way
                            bool isPositive = (currentMode == PromptMode.Positive);

                            // Use consistent naming - either "Positive" or "Negative" as the actual type
                            string pointType = isPositive ? "Positive" : "Negative";
                            string label = pointType + "_" + selectedMaterial.Name;

                            // Add the point
                            AnnotationPoint newPoint = annotationManager.AddPoint(pointX, pointY, xySlice, label);

                            // Track whether this is a positive or negative point in our dictionary
                            pointTypes[newPoint.ID] = isPositive;

                            Logger.Log($"[SegmentAnythingCT] Added {pointType.ToLower()} point at ({pointX}, {pointY})");

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
                        Logger.Log("[SegmentAnythingCT] Attempted to place point outside image bounds");
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
                Logger.Log($"[SegmentAnythingCT] XZ zoom changed to {xzZoom:F2}");
            };

            // XZ viewer mouse events for panning and point placement
            Point lastPos = Point.Empty;
            bool isPanning = false;

            xzViewer.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
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
                                    Logger.Log($"[SegmentAnythingCT] Deleted point at ({point.X}, {point.Y}, {point.Z})");

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

                            Logger.Log($"[SegmentAnythingCT] Added {pointType.ToLower()} point at ({pointX}, {xzRow}, {pointZ})");

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
                        Logger.Log("[SegmentAnythingCT] Attempted to place point outside image bounds");
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
                Logger.Log($"[SegmentAnythingCT] YZ zoom changed to {yzZoom:F2}");
            };

            // YZ viewer mouse events for panning and point placement
            Point lastPos = Point.Empty;
            bool isPanning = false;

            yzViewer.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
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
                                    Logger.Log($"[SegmentAnythingCT] Deleted point at ({point.X}, {point.Y}, {point.Z})");

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

                            Logger.Log($"[SegmentAnythingCT] Added {pointType.ToLower()} point at ({yzCol}, {pointY}, {pointZ})");

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
                        Logger.Log("[SegmentAnythingCT] Attempted to place point outside image bounds");
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
                }
            };
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
                    bool isPositive = pointTypes.ContainsKey(point.ID) && pointTypes[point.ID];

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
                    bool isPositive = pointTypes.ContainsKey(point.ID) && pointTypes[point.ID];

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
                bool isPositive = pointTypes.ContainsKey(point.ID) && pointTypes[point.ID];

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
                dialog.Description = "Select the directory containing SAM ONNX models";
                dialog.ShowNewFolderButton = false;

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    modelPathTextBox.Text = dialog.SelectedPath;

                    // Update paths
                    encoderPath = Path.Combine(dialog.SelectedPath, "sam2.1_large.encoder.onnx");
                    decoderPath = Path.Combine(dialog.SelectedPath, "sam2.1_large.decoder.onnx");

                    Logger.Log($"[SegmentAnythingCT] Model directory set to: {dialog.SelectedPath}");
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
                cachedHighResFeats0 = null;
                cachedHighResFeats1 = null;
                cachedFeatureSlice = -1;

                // Verify files exist
                if (!File.Exists(encoderPath))
                {
                    string errorMsg = $"Encoder model not found at: {encoderPath}";
                    if (samForm != null)
                    {
                        MessageBox.Show(errorMsg);
                    }
                    Logger.Log($"[SegmentAnythingCT] {errorMsg}");
                    return;
                }

                if (!File.Exists(decoderPath))
                {
                    string errorMsg = $"Decoder model not found at: {decoderPath}";
                    if (samForm != null)
                    {
                        MessageBox.Show(errorMsg);
                    }
                    Logger.Log($"[SegmentAnythingCT] {errorMsg}");
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
                    // Configure for GPU with optimized settings
                    options.AppendExecutionProvider_CUDA();
                    Logger.Log("[SegmentAnythingCT] Using GPU execution provider with optimized settings");
                }
                else
                {
                    Logger.Log("[SegmentAnythingCT] Using optimized CPU execution provider");
                }

                // Create sessions with optimized settings
                encoderSession = new InferenceSession(encoderPath, options);
                decoderSession = new InferenceSession(decoderPath, options);

                Logger.Log("[SegmentAnythingCT] Models loaded successfully with optimized settings");

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

                Logger.Log($"[SegmentAnythingCT] Error loading models: {ex.Message}");
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
                ApplySegmentationOverlay(xySliceBitmap, segmentationMask, ActiveView.XY);

                xyViewer.Image = new Bitmap(xySliceBitmap);
                UpdateXYScrollbars();
            }

            // Update XZ viewer
            using (Bitmap xzSliceBitmap = CreateXZSliceBitmap(xzRow))
            {
                if (xzViewer.Image != null)
                    xzViewer.Image.Dispose();

                // Apply segmentation overlay if available
                ApplySegmentationOverlay(xzSliceBitmap, segmentationMask, ActiveView.XZ);

                // Draw the yellow line AFTER the overlay
                DrawXYPositionLine(xzSliceBitmap, xySlice, true);

                xzViewer.Image = new Bitmap(xzSliceBitmap);
                UpdateXZScrollbars();
            }

            // Update YZ viewer
            using (Bitmap yzSliceBitmap = CreateYZSliceBitmap(yzCol))
            {
                if (yzViewer.Image != null)
                    yzViewer.Image.Dispose();

                // Apply segmentation overlay if available
                ApplySegmentationOverlay(yzSliceBitmap, segmentationMask, ActiveView.YZ);

                // Draw the yellow line AFTER the overlay
                DrawXYPositionLine(yzSliceBitmap, xySlice, false);

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

            // We no longer draw the yellow line here
            // It will be drawn after segmentation mask is applied

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

            // We no longer draw the yellow line here
            // It will be drawn after segmentation mask is applied

            bmp.UnlockBits(bmpData);

            // Add to cache
            Bitmap cacheCopy = new Bitmap(bmp);
            yzSliceCache.Add(sliceX, cacheCopy);
            cachedYZKeys.Add(sliceX);

            return bmp;
        }
        private unsafe void DrawXYPositionLine(Bitmap bmp, int xyLine, bool isXZView)
        {
            int width = bmp.Width;
            int height = bmp.Height;

            // Only proceed if the line is within the image bounds
            if (xyLine < 0 || (isXZView && xyLine >= height) || (!isXZView && xyLine >= width))
                return;

            BitmapData bmpData = bmp.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadWrite,
                PixelFormat.Format24bppRgb);

            int stride = bmpData.Stride;
            int bytesPerPixel = 3;

            byte* ptr = (byte*)bmpData.Scan0;

            // Draw the bright yellow line
            if (isXZView)
            {
                // For XZ view, the XY slice is a horizontal line
                for (int x = 0; x < width; x++)
                {
                    int offset = xyLine * stride + x * bytesPerPixel;

                    // Make sure we're within bounds
                    if (offset >= 0 && offset + 2 < stride * height)
                    {
                        // Bright yellow
                        ptr[offset] = 0;       // Blue
                        ptr[offset + 1] = 255; // Green
                        ptr[offset + 2] = 255; // Red
                    }
                }
            }
            else
            {
                // For YZ view, the XY slice is a vertical line
                for (int y = 0; y < height; y++)
                {
                    int offset = y * stride + xyLine * bytesPerPixel;

                    // Make sure we're within bounds
                    if (offset >= 0 && offset + 2 < stride * height)
                    {
                        // Bright yellow
                        ptr[offset] = 0;       // Blue
                        ptr[offset + 1] = 255; // Green
                        ptr[offset + 2] = 255; // Red
                    }
                }
            }

            bmp.UnlockBits(bmpData);
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

        private async Task PerformSegmentation()
        {
            if (encoderSession == null || decoderSession == null)
            {
                MessageBox.Show("Models not loaded. Please load models first.");
                return;
            }

            // Get relevant points based on active view
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

            if (relevantPoints.Count == 0)
            {
                string viewName;
                switch (currentActiveView)
                {
                    case ActiveView.XY: viewName = "XY"; break;
                    case ActiveView.XZ: viewName = "XZ"; break;
                    case ActiveView.YZ: viewName = "YZ"; break;
                    default: viewName = "current"; break;
                }

                MessageBox.Show($"Please add at least one annotation point on the {viewName} view.");
                return;
            }

            // Ensure UI is updated from the UI thread
            Action updateStatus = () => statusLabel.Text = "Segmenting...";
            if (samForm.InvokeRequired)
                samForm.Invoke(updateStatus);
            else
                updateStatus();

            Logger.Log($"[SegmentAnythingCT] Starting segmentation on {currentActiveView} view");

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
                    var imageEmbed = encoderOutputs.First(x => x.Name == "image_embed").AsTensor<float>();
                    var highResFeats0 = encoderOutputs.First(x => x.Name == "high_res_feats_0").AsTensor<float>();
                    var highResFeats1 = encoderOutputs.First(x => x.Name == "high_res_feats_1").AsTensor<float>();

                    // Prepare point tensors
                    int numPoints = relevantPoints.Count;

                    // Scale factors depend on the active view
                    float scaleX, scaleY;
                    switch (currentActiveView)
                    {
                        case ActiveView.XY:
                            scaleX = 1024.0f / mainForm.GetWidth();
                            scaleY = 1024.0f / mainForm.GetHeight();
                            break;

                        case ActiveView.XZ:
                            scaleX = 1024.0f / mainForm.GetWidth();
                            scaleY = 1024.0f / mainForm.GetDepth();
                            break;

                        case ActiveView.YZ:
                            scaleX = 1024.0f / mainForm.GetDepth();
                            scaleY = 1024.0f / mainForm.GetHeight();
                            break;

                        default:
                            scaleX = scaleY = 1.0f;
                            break;
                    }

                    DenseTensor<float> pointCoords = new DenseTensor<float>(new[] { 1, numPoints, 2 });
                    DenseTensor<float> pointLabels = new DenseTensor<float>(new[] { 1, numPoints });

                    for (int i = 0; i < numPoints; i++)
                    {
                        var point = relevantPoints[i];
                        float x, y;

                        // Transform point coordinates based on active view
                        switch (currentActiveView)
                        {
                            case ActiveView.XY:
                                x = point.X;
                                y = point.Y;
                                break;

                            case ActiveView.XZ:
                                x = point.X;
                                y = point.Z;
                                break;

                            case ActiveView.YZ:
                                x = point.Z;
                                y = point.Y;
                                break;

                            default:
                                x = point.X;
                                y = point.Y;
                                break;
                        }

                        // Scale to image size
                        x = x * scaleX;
                        y = y * scaleY;

                        pointCoords[0, i, 0] = x;
                        pointCoords[0, i, 1] = y;

                        // Use pointTypes dictionary to determine if point is positive
                        bool isPositive = pointTypes.ContainsKey(point.ID) && pointTypes[point.ID];
                        pointLabels[0, i] = isPositive ? 1.0f : 0.0f;
                    }

                    // Rest of tensor preparation
                    DenseTensor<float> maskInput = new DenseTensor<float>(new[] { 1, 1, 256, 256 });
                    DenseTensor<float> hasMaskInput = new DenseTensor<float>(new[] { 1 });
                    hasMaskInput[0] = 0;

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

                    DenseTensor<int> origImSize = new DenseTensor<int>(new[] { 2 });
                    origImSize[0] = origHeight;
                    origImSize[1] = origWidth;

                    // Run decoder on background thread
                    var decoderInputs = new List<NamedOnnxValue> {
                NamedOnnxValue.CreateFromTensor("image_embed", imageEmbed),
                NamedOnnxValue.CreateFromTensor("high_res_feats_0", highResFeats0),
                NamedOnnxValue.CreateFromTensor("high_res_feats_1", highResFeats1),
                NamedOnnxValue.CreateFromTensor("point_coords", pointCoords),
                NamedOnnxValue.CreateFromTensor("point_labels", pointLabels),
                NamedOnnxValue.CreateFromTensor("mask_input", maskInput),
                NamedOnnxValue.CreateFromTensor("has_mask_input", hasMaskInput),
                NamedOnnxValue.CreateFromTensor("orig_im_size", origImSize)
            };

                    var decoderOutputs = await Task.Run(() => decoderSession.Run(decoderInputs));

                    try
                    {
                        // Process decoder outputs
                        byte[,] tempMask = null;
                        float bestIoU = 0;

                        await Task.Run(() =>
                        {
                            var masks = decoderOutputs.First(x => x.Name == "masks").AsTensor<float>();
                            var iouPredictions = decoderOutputs.First(x => x.Name == "iou_predictions").AsTensor<float>();

                            // Save masks for debugging
                            SaveAllMasks(masks, iouPredictions);

                            int bestMaskIdx = 0;
                            bestIoU = iouPredictions[0, 0];

                            for (int i = 1; i < iouPredictions.Dimensions[1]; i++)
                            {
                                if (iouPredictions[0, i] > bestIoU)
                                {
                                    bestIoU = iouPredictions[0, i];
                                    bestMaskIdx = i;
                                }
                            }

                            Logger.Log($"[SegmentAnythingCT] Best mask IoU: {bestIoU}");

                            // Create mask with dimensions appropriate for the active view
                            switch (currentActiveView)
                            {
                                case ActiveView.XY:
                                    tempMask = new byte[mainForm.GetWidth(), mainForm.GetHeight()];
                                    for (int y = 0; y < mainForm.GetHeight(); y++)
                                    {
                                        for (int x = 0; x < mainForm.GetWidth(); x++)
                                        {
                                            tempMask[x, y] = masks[0, bestMaskIdx, y, x] > 0.0f ? selectedMaterial.ID : (byte)0;
                                        }
                                    }
                                    break;

                                case ActiveView.XZ:
                                    tempMask = new byte[mainForm.GetWidth(), mainForm.GetDepth()];
                                    for (int z = 0; z < mainForm.GetDepth(); z++)
                                    {
                                        for (int x = 0; x < mainForm.GetWidth(); x++)
                                        {
                                            tempMask[x, z] = masks[0, bestMaskIdx, z, x] > 0.0f ? selectedMaterial.ID : (byte)0;
                                        }
                                    }
                                    break;

                                case ActiveView.YZ:
                                    tempMask = new byte[mainForm.GetDepth(), mainForm.GetHeight()];
                                    for (int y = 0; y < mainForm.GetHeight(); y++)
                                    {
                                        for (int z = 0; z < mainForm.GetDepth(); z++)
                                        {
                                            tempMask[z, y] = masks[0, bestMaskIdx, y, z] > 0.0f ? selectedMaterial.ID : (byte)0;
                                        }
                                    }
                                    break;
                            }
                        });

                        // Update UI on the UI thread
                        samForm.Invoke(new Action(() =>
                        {
                            segmentationMask = tempMask;
                            UpdateViewers();
                            statusLabel.Text = $"Segmentation complete (IoU: {bestIoU:F3})";
                        }));

                        Logger.Log("[SegmentAnythingCT] Segmentation complete");
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
                    MessageBox.Show($"Error during segmentation: {ex.Message}");
                    statusLabel.Text = $"Error: {ex.Message}";
                }));

                Logger.Log($"[SegmentAnythingCT] Segmentation error: {ex.Message}");
            }
        }

        private unsafe void SaveAllMasks(Tensor<float> masks, Tensor<float> iouPredictions)
        {
            try
            {
                // Create directory for saving masks if it doesn't exist
                string masksDir = Path.Combine(Application.StartupPath, "SAM_Masks");
                if (!Directory.Exists(masksDir))
                {
                    Directory.CreateDirectory(masksDir);
                }

                // Generate timestamp for unique filenames
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

                // Get dimensions
                int width = mainForm.GetWidth();
                int height = mainForm.GetHeight();
                int numMasks = masks.Dimensions[1]; // Number of masks in the batch

                Logger.Log($"[SegmentAnythingCT] Saving {numMasks} masks to {masksDir}");

                // Save each mask
                for (int maskIdx = 0; maskIdx < numMasks; maskIdx++)
                {
                    // Create a new bitmap for this mask
                    using (Bitmap maskBitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                    {
                        // Lock the bitmap for direct memory access
                        BitmapData bmpData = maskBitmap.LockBits(
                            new Rectangle(0, 0, width, height),
                            ImageLockMode.WriteOnly,
                            PixelFormat.Format24bppRgb);

                        int stride = bmpData.Stride;
                        int bytesPerPixel = 3; // RGB

                        byte* ptr = (byte*)bmpData.Scan0;

                        // Fill the bitmap
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                // Get mask value and convert to byte (0 or 255)
                                byte pixelValue = masks[0, maskIdx, y, x] > 0 ? (byte)255 : (byte)0;

                                // Calculate position in bitmap memory
                                int pos = y * stride + x * bytesPerPixel;

                                // Set RGB values (same for grayscale)
                                ptr[pos] = pixelValue;     // Blue
                                ptr[pos + 1] = pixelValue; // Green
                                ptr[pos + 2] = pixelValue; // Red
                            }
                        }

                        // Unlock the bitmap
                        maskBitmap.UnlockBits(bmpData);

                        // Get the IoU for this mask
                        float iou = iouPredictions[0, maskIdx];

                        // Save the bitmap to a file
                        string filename = Path.Combine(masksDir, $"mask_{timestamp}_{maskIdx}_IoU_{iou:F3}.jpg");
                        maskBitmap.Save(filename, ImageFormat.Jpeg);

                        Logger.Log($"[SegmentAnythingCT] Saved mask {maskIdx} with IoU {iou:F3}");
                    }
                }

                // Also save a colored composite image showing all masks
                using (Bitmap compositeMask = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                {
                    // Create graphics for drawing
                    using (Graphics g = Graphics.FromImage(compositeMask))
                    {
                        // Fill with black background
                        g.Clear(Color.Black);

                        // Array of distinct colors for different masks
                        Color[] colors = new Color[] {
                    Color.Red, Color.Green, Color.Blue, Color.Yellow,
                    Color.Cyan, Color.Magenta, Color.Orange, Color.Purple
                };

                        // Draw each mask with semi-transparency
                        for (int maskIdx = 0; maskIdx < numMasks; maskIdx++)
                        {
                            // Get a color for this mask
                            Color maskColor = colors[maskIdx % colors.Length];
                            using (SolidBrush brush = new SolidBrush(Color.FromArgb(128, maskColor)))
                            {
                                // Draw the mask onto the composite image
                                for (int y = 0; y < height; y++)
                                {
                                    for (int x = 0; x < width; x++)
                                    {
                                        if (masks[0, maskIdx, y, x] > 0)
                                        {
                                            g.FillRectangle(brush, x, y, 1, 1);
                                        }
                                    }
                                }
                            }

                            // Add IoU score text to the image
                            float iou = iouPredictions[0, maskIdx];
                            g.DrawString($"Mask {maskIdx}: IoU = {iou:F3}", new Font("Arial", 10),
                                new SolidBrush(maskColor), 10, 20 + maskIdx * 20);
                        }
                    }

                    // Save the composite image
                    string compositeFilename = Path.Combine(masksDir, $"composite_masks_{timestamp}.jpg");
                    compositeMask.Save(compositeFilename, ImageFormat.Jpeg);
                    Logger.Log($"[SegmentAnythingCT] Saved composite mask image");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SegmentAnythingCT] Error saving masks: {ex.Message}");
            }
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
                Logger.Log("[SegmentAnythingCT] Error: MainForm reference is null");
                MessageBox.Show("Error: Cannot access the main application.");
                return;
            }

            try
            {
                Logger.Log($"[SegmentAnythingCT] Applying segmentation mask from {currentActiveView} view to volume labels");
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
                Logger.Log("[SegmentAnythingCT] Mask applied successfully");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error applying mask: {ex.Message}");
                statusLabel.Text = $"Error: {ex.Message}";
                Logger.Log($"[SegmentAnythingCT] Error applying mask: {ex.Message}");
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
            if (mask == null || currentActiveView != viewType)
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
            "Forward only"
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
                        await ExportCroppedDataset(sliceRange, outputFolder, progressForm);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error exporting dataset: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Logger.Log($"[SegmentAnythingCT] Export error: {ex.Message}");
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
                                "SAM_Volume",
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

                        // Get relevant points for propagation
                        var relevantPoints = GetRelevantPointsForCurrentView();

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

                        // Update MainForm's views
                        mainForm.RenderViews();
                        await mainForm.RenderOrthoViewsAsync();
                        mainForm.SaveLabelsChk();

                        MessageBox.Show("Volume segmentation complete!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error during volume segmentation: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Logger.Log($"[SegmentAnythingCT] Volume segmentation error: {ex.Message}");
                    }
                    finally
                    {
                        progressForm.Close();
                        progressForm.Dispose();
                    }
                }
            }
        }

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
                    Logger.Log($"[SegmentAnythingCT] Forward propagation stopped at slice {slice}");
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
                    Logger.Log($"[SegmentAnythingCT] Backward propagation stopped at slice {slice}");
                    break;
                }

                // Apply mask to volume
                ApplyMaskToVolume(currentMask, slice, materialID);
            }
        }

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
                    Logger.Log($"[SegmentAnythingCT] Forward propagation stopped at slice {slice}");
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
                    Logger.Log($"[SegmentAnythingCT] Backward propagation stopped at slice {slice}");
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
                    Logger.Log($"[SegmentAnythingCT] Forward propagation stopped at slice {slice}");
                    break;
                }

                // Apply mask to volume
                ApplyMaskToVolume(currentMask, slice, materialID);
            }
        }

        /// <summary>
        /// Exports a cropped dataset containing only the segmented region
        /// </summary>
        private async Task ExportCroppedDataset(int sliceRange, string outputFolder, ProgressForm progressForm)
        {
            // First we need to segment the volume slices forward and backward
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
                    Logger.Log($"[SegmentAnythingCT] Forward propagation stopped at slice {slice}");
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
                    Logger.Log($"[SegmentAnythingCT] Backward propagation stopped at slice {slice}");
                    break;
                }

                // Store the mask
                allSliceMasks[slice] = currentMask;
            }

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
                            Logger.Log($"[SegmentAnythingCT] Warning: No mask for slice {z}, creating one");
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
                        Logger.Log($"[SegmentAnythingCT] Error saving slice {z}: {ex.Message}");
                    }
                });
            });

            // Show success message
            MessageBox.Show($"Successfully exported {croppedDepth} slices to {outputFolder}.", "Export Complete",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
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

        // Segment a slice using the previous mask as guidance
        private async Task<byte[,]> SegmentSliceWithMask(int sliceZ, byte[,] previousMask, List<AnnotationPoint> relevantPoints)
        {
            try
            {
                // Check if we can reuse cached features from a nearby slice
                bool useCache = Math.Abs(sliceZ - cachedFeatureSlice) <= featureCacheRadius &&
                                cachedImageEmbed != null &&
                                cachedHighResFeats0 != null &&
                                cachedHighResFeats1 != null;

                DenseTensor<float> imageEmbed;
                DenseTensor<float> highResFeats0;
                DenseTensor<float> highResFeats1;

                if (!useCache)
                {
                    // Need to run the encoder for this slice
                    Tensor<float> imageInput = await Task.Run(() => PreprocessSliceImage(sliceZ));

                    var encoderInputs = new List<NamedOnnxValue> {
                NamedOnnxValue.CreateFromTensor("image", imageInput)
            };

                    var encoderOutputs = await Task.Run(() => encoderSession.Run(encoderInputs));

                    try
                    {
                        // Extract encoder outputs
                        var imageEmbedTensor = encoderOutputs.First(x => x.Name == "image_embed").AsTensor<float>();
                        var highResFeats0Tensor = encoderOutputs.First(x => x.Name == "high_res_feats_0").AsTensor<float>();
                        var highResFeats1Tensor = encoderOutputs.First(x => x.Name == "high_res_feats_1").AsTensor<float>();

                        // Create new tensors to store cached values
                        cachedImageEmbed = new DenseTensor<float>(imageEmbedTensor.Dimensions);
                        cachedHighResFeats0 = new DenseTensor<float>(highResFeats0Tensor.Dimensions);
                        cachedHighResFeats1 = new DenseTensor<float>(highResFeats1Tensor.Dimensions);

                        // Copy tensor data - manual copy since we don't have Buffer access
                        CopyTensorData(imageEmbedTensor, cachedImageEmbed);
                        CopyTensorData(highResFeats0Tensor, cachedHighResFeats0);
                        CopyTensorData(highResFeats1Tensor, cachedHighResFeats1);

                        cachedFeatureSlice = sliceZ;

                        // Use the cached tensors for this run
                        imageEmbed = cachedImageEmbed;
                        highResFeats0 = cachedHighResFeats0;
                        highResFeats1 = cachedHighResFeats1;
                    }
                    finally
                    {
                        // Make sure to dispose of the encoder outputs
                        foreach (var output in encoderOutputs)
                        {
                            output.Dispose();
                        }
                    }
                }
                else
                {
                    // Use cached features
                    imageEmbed = cachedImageEmbed;
                    highResFeats0 = cachedHighResFeats0;
                    highResFeats1 = cachedHighResFeats1;
                    Logger.Log($"[SegmentAnythingCT] Using cached features for slice {sliceZ} from slice {cachedFeatureSlice}");
                }

                // Prepare mask input from previous result (256x256)
                DenseTensor<float> maskInput = new DenseTensor<float>(new[] { 1, 1, 256, 256 });

                // Resize previous mask to 256x256
                int width = mainForm.GetWidth();
                int height = mainForm.GetHeight();
                float scaleX = 256.0f / width;
                float scaleY = 256.0f / height;

                // Convert previous mask to proper size for mask_input
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (x < previousMask.GetLength(0) && y < previousMask.GetLength(1) && previousMask[x, y] > 0)
                        {
                            int targetX = (int)(x * scaleX);
                            int targetY = (int)(y * scaleY);

                            if (targetX < 256 && targetY < 256)
                            {
                                maskInput[0, 0, targetY, targetX] = 1.0f;
                            }
                        }
                    }
                }

                // Set has_mask_input to 1 to indicate we're using a mask
                DenseTensor<float> hasMaskInput = new DenseTensor<float>(new[] { 1 });
                hasMaskInput[0] = 1.0f;

                // Prepare point data
                int numPoints = relevantPoints.Count;
                float inputScaleX = 1024.0f / width;
                float inputScaleY = 1024.0f / height;

                DenseTensor<float> pointCoords = new DenseTensor<float>(new[] { 1, numPoints, 2 });
                DenseTensor<float> pointLabels = new DenseTensor<float>(new[] { 1, numPoints });

                for (int i = 0; i < numPoints; i++)
                {
                    var point = relevantPoints[i];
                    pointCoords[0, i, 0] = point.X * inputScaleX;
                    pointCoords[0, i, 1] = point.Y * inputScaleY;

                    bool isPositive = pointTypes.ContainsKey(point.ID) && pointTypes[point.ID];
                    pointLabels[0, i] = isPositive ? 1.0f : 0.0f;
                }

                // Image size tensor
                DenseTensor<int> origImSize = new DenseTensor<int>(new[] { 2 });
                origImSize[0] = height;
                origImSize[1] = width;

                // Run decoder with mask input
                var decoderInputs = new List<NamedOnnxValue> {
            NamedOnnxValue.CreateFromTensor("image_embed", imageEmbed),
            NamedOnnxValue.CreateFromTensor("high_res_feats_0", highResFeats0),
            NamedOnnxValue.CreateFromTensor("high_res_feats_1", highResFeats1),
            NamedOnnxValue.CreateFromTensor("point_coords", pointCoords),
            NamedOnnxValue.CreateFromTensor("point_labels", pointLabels),
            NamedOnnxValue.CreateFromTensor("mask_input", maskInput),
            NamedOnnxValue.CreateFromTensor("has_mask_input", hasMaskInput),
            NamedOnnxValue.CreateFromTensor("orig_im_size", origImSize)
        };

                var decoderOutputs = await Task.Run(() => decoderSession.Run(decoderInputs));

                try
                {
                    byte[,] resultMask = null;

                    await Task.Run(() =>
                    {
                        var masks = decoderOutputs.First(x => x.Name == "masks").AsTensor<float>();
                        var iouPredictions = decoderOutputs.First(x => x.Name == "iou_predictions").AsTensor<float>();

                        // Find best mask
                        int bestMaskIdx = 0;
                        float bestIoU = iouPredictions[0, 0];

                        for (int i = 1; i < iouPredictions.Dimensions[1]; i++)
                        {
                            if (iouPredictions[0, i] > bestIoU)
                            {
                                bestIoU = iouPredictions[0, i];
                                bestMaskIdx = i;
                            }
                        }

                        // For boundary slices, reduce the IoU threshold
                        // This helps ensure processing of the extreme slices
                        float minIoU = 0.5f;

                        // If we're at the beginning or end of the volume, use lower threshold
                        int maxSlice = mainForm.GetDepth() - 1;
                        if (sliceZ <= 3 || sliceZ >= maxSlice - 3)
                        {
                            minIoU = 0.3f;  // Lower threshold for boundary slices
                            Logger.Log($"[SegmentAnythingCT] Using reduced IoU threshold ({minIoU}) for boundary slice {sliceZ}");
                        }

                        // If IoU is too low, stop propagation (with adjusted threshold for boundary cases)
                        if (bestIoU < minIoU)
                        {
                            Logger.Log($"[SegmentAnythingCT] Stopping at slice {sliceZ} due to low IoU ({bestIoU:F3})");
                            return;
                        }

                        Logger.Log($"[SegmentAnythingCT] Slice {sliceZ} mask IoU: {bestIoU:F3}");

                        // Convert mask to byte array
                        resultMask = new byte[width, height];
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                if (masks[0, bestMaskIdx, y, x] > 0.0f)
                                {
                                    resultMask[x, y] = selectedMaterial.ID;
                                }
                            }
                        }
                    });

                    return resultMask;
                }
                finally
                {
                    // Dispose decoder outputs
                    foreach (var output in decoderOutputs)
                    {
                        output.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SegmentAnythingCT] Error segmenting slice {sliceZ}: {ex.Message}");
                return null;
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

        // Preprocess a slice image for the SAM model
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

            Logger.Log("[SegmentAnythingCT] All slice caches cleared");
        }

        public void Show()
        {
            samForm.Show();
        }
    }
}