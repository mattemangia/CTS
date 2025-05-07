using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS.Modules.ArtificialIntelligence.GroundingDINO
{
    // Enum for mask creation methods with descriptions
    public enum MaskCreationMethod
    {
        [Description("Center Point")]
        CenterPoint,

        [Description("Corner Points")]
        CornerPoints,

        [Description("Box Outline")]
        BoxOutline,

        [Description("Weighted Grid")]
        WeightedGrid,

        [Description("Box Fill")]
        BoxFill
    }

    internal class GroundingDINODetector
    {

        // Loading form
        private ProgressBar loadingProgressBar;

        private Label loadingLabel;
        private Panel loadingPanel;
        private bool isLoading = false;

        // Main form components
        private Form dinoForm;

        private TableLayoutPanel mainLayout;
        private Panel viewPanel;
        private PictureBox imageViewer;
        private Panel controlPanel;
        private TextBox modelPathTextBox;
        private TextBox promptTextBox;
        private RadioButton rbCPU, rbGPU;
        private Button btnLoadModel, btnDetect, btnApply, btnClose;
        private Button btnSelectAll, btnClearSelection;
        private Button btnSendToSAM; // New button for SAM integration
        private ComboBox cboMaskCreationMethod; // New dropdown for mask creation method
        private TrackBar confidenceSlider;
        private Label confidenceLabel;
        private Label statusLabel;

        // Scrollbar controls
        private HScrollBar hScroll;

        private VScrollBar vScroll;

        // Zoom and pan state variables
        private float zoom = 1.0f;

        private Point pan = Point.Empty;

        // Image bounds tracking
        private Rectangle imageBounds = Rectangle.Empty;

        // References to parent application components
        private MainForm mainForm;

        private Material selectedMaterial;
        private AnnotationManager annotationManager;

        // ONNX model components
        private InferenceSession session;

        private string modelPath;
        private bool useGPU = false;

        // Tokenizer Configuration
        private Dictionary<string, int> vocabDict;

        private string clsToken = "[CLS]";
        private string sepToken = "[SEP]";
        private string padToken = "[PAD]";
        private string unkToken = "[UNK]";
        private int clsTokenId = 101;
        private int sepTokenId = 102;
        private int padTokenId = 0;
        private int unkTokenId = 100;
        private int maxTextLen = 256;  // From config.json

        // Views
        private enum ViewMode
        { XY, XZ, YZ }

        private ViewMode currentViewMode = ViewMode.XY;
        private Button btnXYView, btnXZView, btnYZView;

        // Detection results
        private class Detection
        {
            public Rectangle Box { get; set; }
            public float Score { get; set; }
            public string Label { get; set; }
            public bool Selected { get; set; } = false; // Added Selected property
        }

        private List<Detection> detections = new List<Detection>();
        private float confidenceThreshold = 0.3f;

        // Slice index
        private int currentSlice;

        public GroundingDINODetector(MainForm mainForm, Material selectedMaterial, AnnotationManager annotationManager = null)
        {
            Logger.Log("[GroundingDINO] Creating Grounding DINO detector interface");
            this.mainForm = mainForm;
            
            this.selectedMaterial = selectedMaterial;
            this.annotationManager = annotationManager ?? new AnnotationManager();

            // Get current slice position from MainForm
            currentSlice = mainForm.CurrentSlice;

            // Initialize form and UI
            InitializeForm();
            InitializeLoadingUI();

            // Set default model path
            string onnxDirectory = Path.Combine(Application.StartupPath, "ONNX");
            modelPath = Path.Combine(onnxDirectory, "g_dino.onnx");
            modelPathTextBox.Text = onnxDirectory;

            // Try to load model automatically if it exists
            try
            {
                Logger.Log("[GroundingDINO] Attempting to load ONNX model");
                LoadONNXModel();
            }
            catch (Exception ex)
            {
                Logger.Log($"[GroundingDINO] Error loading model: {ex.Message}");
                statusLabel.Text = $"Error loading model: {ex.Message}";
            }
        }

        private void InitializeLoadingUI()
        {
            // Create a panel to hold the progress bar and label
            loadingPanel = new Panel
            {
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(240, 240, 240),
                Size = new Size(300, 100),
                Padding = new Padding(10),
                Visible = false
            };

            // Position panel in center of form
            loadingPanel.Location = new Point(
                (dinoForm.ClientSize.Width - loadingPanel.Width) / 2,
                (dinoForm.ClientSize.Height - loadingPanel.Height) / 2
            );

            // Create loading label
            loadingLabel = new Label
            {
                Text = "Loading ONNX model...",
                AutoSize = true,
                Location = new Point(10, 15),
                Font = new Font("Segoe UI", 9.75f, FontStyle.Bold)
            };

            // Create progress bar (marquee style)
            loadingProgressBar = new ProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
                Size = new Size(280, 25),
                Location = new Point(10, 45)
            };

            // Add controls to panel
            loadingPanel.Controls.Add(loadingLabel);
            loadingPanel.Controls.Add(loadingProgressBar);

            // Add panel to form (above other controls)
            dinoForm.Controls.Add(loadingPanel);
            loadingPanel.BringToFront();

            // Handle form resize to keep the loading panel centered
            dinoForm.Resize += (s, e) =>
            {
                if (loadingPanel.Visible)
                {
                    loadingPanel.Location = new Point(
                        (dinoForm.ClientSize.Width - loadingPanel.Width) / 2,
                        (dinoForm.ClientSize.Height - loadingPanel.Height) / 2
                    );
                }
            };
        }

        private void InitializeForm()
        {
            Logger.Log("[GroundingDINODetector] Module initialization called");
            Logger.Log("[GroundingDINO] Initializing form");

            dinoForm = new Form
            {
                Text = "Grounding DINO - CT",
                Size = new Size(1200, 800),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.Sizable,
                MinimumSize = new Size(900, 600),
                Icon = Properties.Resources.favicon
            };
            dinoForm.Icon = mainForm.Icon;
            // Main layout with 2 columns
            mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 1,
                ColumnCount = 2,
                Padding = new Padding(5)
            };

            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));

            // Create viewer panel
            viewPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(5),
                BorderStyle = BorderStyle.FixedSingle
            };

            Label titleLabel = new Label
            {
                Text = "CT View",
                Dock = DockStyle.Top,
                BackColor = Color.Black,
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter,
                Height = 25
            };

            imageViewer = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Normal,
                BackColor = Color.Black
            };

            hScroll = new HScrollBar
            {
                Dock = DockStyle.Bottom,
                Height = 20
            };

            vScroll = new VScrollBar
            {
                Dock = DockStyle.Right,
                Width = 20
            };

            // Setup events for viewer
            SetupViewerEvents();

            viewPanel.Controls.Add(imageViewer);
            viewPanel.Controls.Add(hScroll);
            viewPanel.Controls.Add(vScroll);
            viewPanel.Controls.Add(titleLabel);

            // Create control panel with AutoScroll enabled
            controlPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10),
                BackColor = Color.WhiteSmoke,
                AutoScroll = true
            };

            // Create a TableLayoutPanel for organized control layout
            TableLayoutPanel controlsTable = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 13,  // Increased row count for new SAM controls
                Padding = new Padding(5),
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };

            controlsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            // --------- Model Loading Section ---------
            Panel modelPanel = new Panel
            {
                Width = 280,
                Height = 110,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, 10)
            };

            Label lblModelPath = new Label
            {
                Text = "Model Directory:",
                Location = new Point(0, 0),
                AutoSize = true
            };

            modelPathTextBox = new TextBox
            {
                Location = new Point(0, 20),
                Width = 200,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            Button btnBrowse = new Button
            {
                Text = "Browse...",
                Location = new Point(205, 19),
                Width = 75,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnBrowse.Click += (s, e) => BrowseForModelDirectory();

            Label lblDevice = new Label
            {
                Text = "Execution Device:",
                Location = new Point(0, 50),
                AutoSize = true
            };

            rbCPU = new RadioButton
            {
                Text = "CPU",
                Location = new Point(0, 70),
                Checked = true,
                AutoSize = true
            };

            rbGPU = new RadioButton
            {
                Text = "GPU",
                Location = new Point(70, 70),
                AutoSize = true
            };

            btnLoadModel = new Button
            {
                Text = "Load Model",
                Location = new Point(140, 68),
                Width = 100,
                Height = 25
            };
            btnLoadModel.Click += (s, e) => LoadONNXModel();

            modelPanel.Controls.AddRange(new Control[] {
                lblModelPath, modelPathTextBox, btnBrowse,
                lblDevice, rbCPU, rbGPU, btnLoadModel
            });

            // --------- View Selection Section ---------
            Panel viewSelectionPanel = new Panel
            {
                Width = 280,
                Height = 70,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, 10)
            };

            Label lblViewMode = new Label
            {
                Text = "Active View:",
                Location = new Point(0, 0),
                AutoSize = true
            };

            btnXYView = new Button
            {
                Text = "XY View",
                Location = new Point(0, 25),
                Width = 90,
                Height = 30,
                BackColor = Color.LightSkyBlue  // Indicates this is the active view
            };
            btnXYView.Click += (s, e) =>
            {
                currentViewMode = ViewMode.XY;
                UpdateViewButtons();
                UpdateViewer();
            };

            btnXZView = new Button
            {
                Text = "XZ View",
                Location = new Point(95, 25),
                Width = 90,
                Height = 30
            };
            btnXZView.Click += (s, e) =>
            {
                currentViewMode = ViewMode.XZ;
                UpdateViewButtons();
                UpdateViewer();
            };

            btnYZView = new Button
            {
                Text = "YZ View",
                Location = new Point(190, 25),
                Width = 90,
                Height = 30
            };
            btnYZView.Click += (s, e) =>
            {
                currentViewMode = ViewMode.YZ;
                UpdateViewButtons();
                UpdateViewer();
            };

            viewSelectionPanel.Controls.AddRange(new Control[] {
                lblViewMode, btnXYView, btnXZView, btnYZView
            });

            // --------- Prompt Section ---------
            Panel promptPanel = new Panel
            {
                Width = 280,
                Height = 100,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, 10)
            };

            Label lblPrompt = new Label
            {
                Text = "Detection Prompt:",
                Location = new Point(0, 0),
                AutoSize = true
            };

            promptTextBox = new TextBox
            {
                Location = new Point(0, 20),
                Width = 280,
                Height = 60,
                Multiline = true,
                Text = "bone"
            };

            promptPanel.Controls.AddRange(new Control[] {
                lblPrompt, promptTextBox
            });

            // --------- Confidence Threshold ---------
            Panel confidencePanel = new Panel
            {
                Width = 280,
                Height = 70,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, 10)
            };

            Label lblConfidence = new Label
            {
                Text = "Confidence Threshold:",
                Location = new Point(0, 0),
                AutoSize = true
            };

            confidenceLabel = new Label
            {
                Text = "0.3",
                Location = new Point(150, 0),
                AutoSize = true
            };

            confidenceSlider = new TrackBar
            {
                Location = new Point(0, 20),
                Width = 280,
                Minimum = 1,
                Maximum = 100,
                Value = 30,
                TickFrequency = 10
            };
            confidenceSlider.ValueChanged += (s, e) =>
            {
                confidenceThreshold = confidenceSlider.Value / 100.0f;
                confidenceLabel.Text = confidenceThreshold.ToString("F2");
                UpdateDetectionDisplay();
            };

            confidencePanel.Controls.AddRange(new Control[] {
                lblConfidence, confidenceLabel, confidenceSlider
            });

            // --------- Slice Controls ---------
            Panel slicePanel = new Panel
            {
                Width = 280,
                Height = 70,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, 10)
            };

            Label lblSlice = new Label
            {
                Text = $"Slice: {currentSlice} / {(mainForm.GetDepth() > 0 ? mainForm.GetDepth() - 1 : 0)}",
                Location = new Point(0, 0),
                AutoSize = true
            };

            TrackBar sliceSlider = new TrackBar
            {
                Minimum = 0,
                Maximum = mainForm.GetDepth() > 0 ? mainForm.GetDepth() - 1 : 0,
                Value = currentSlice,
                Location = new Point(0, 20),
                Width = 210,
                TickStyle = TickStyle.None
            };

            NumericUpDown numSlice = new NumericUpDown
            {
                Minimum = 0,
                Maximum = sliceSlider.Maximum,
                Value = currentSlice,
                Location = new Point(220, 20),
                Width = 60
            };

            // Add event handlers for slice controls
            sliceSlider.Scroll += (s, e) =>
            {
                currentSlice = sliceSlider.Value;
                numSlice.Value = currentSlice;
                lblSlice.Text = $"Slice: {currentSlice} / {sliceSlider.Maximum}";
                UpdateViewer();
                // Clear previous detections when changing slices
                detections.Clear();
            };

            numSlice.ValueChanged += (s, e) =>
            {
                if (numSlice.Value != currentSlice)
                {
                    currentSlice = (int)numSlice.Value;
                    sliceSlider.Value = currentSlice;
                    lblSlice.Text = $"Slice: {currentSlice} / {sliceSlider.Maximum}";
                    UpdateViewer();
                    // Clear previous detections when changing slices
                    detections.Clear();
                }
            };

            slicePanel.Controls.AddRange(new Control[] {
                lblSlice, sliceSlider, numSlice
            });

            // --------- Action Buttons ---------
            Panel buttonPanel = new Panel
            {
                Width = 280,
                Height = 200, // Increased height for new buttons
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, 10)
            };

            btnDetect = new Button
            {
                Text = "Detect Objects",
                Location = new Point(0, 0),
                Width = 135,
                Height = 30,
                Image = CreateButtonIcon("detect"),
                ImageAlign = ContentAlignment.MiddleLeft,
                TextAlign = ContentAlignment.MiddleRight,
                TextImageRelation = TextImageRelation.ImageBeforeText
            };
            btnDetect.Click += async (s, e) => await PerformDetection();

            btnApply = new Button
            {
                Text = "Apply Selection",
                Location = new Point(145, 0),
                Width = 135,
                Height = 30,
                Image = CreateButtonIcon("apply"),
                ImageAlign = ContentAlignment.MiddleLeft,
                TextAlign = ContentAlignment.MiddleRight,
                TextImageRelation = TextImageRelation.ImageBeforeText
            };
            btnApply.Click += (s, e) => ApplyDetectionResults();

            // Add Select All / Clear Selection buttons
            btnSelectAll = new Button
            {
                Text = "Select All",
                Location = new Point(0, 40),
                Width = 135,
                Height = 30,
                Image = CreateButtonIcon("selectAll"),
                ImageAlign = ContentAlignment.MiddleLeft,
                TextAlign = ContentAlignment.MiddleRight,
                TextImageRelation = TextImageRelation.ImageBeforeText
            };
            btnSelectAll.Click += (s, e) =>
            {
                if (detections != null && detections.Count > 0)
                {
                    foreach (var detection in detections.Where(d => d.Score >= confidenceThreshold))
                    {
                        detection.Selected = true;
                    }
                    imageViewer.Invalidate();
                    statusLabel.Text = "All valid detections selected";
                }
            };

            btnClearSelection = new Button
            {
                Text = "Clear Selection",
                Location = new Point(145, 40),
                Width = 135,
                Height = 30,
                Image = CreateButtonIcon("clearSelection"),
                ImageAlign = ContentAlignment.MiddleLeft,
                TextAlign = ContentAlignment.MiddleRight,
                TextImageRelation = TextImageRelation.ImageBeforeText
            };
            btnClearSelection.Click += (s, e) =>
            {
                if (detections != null && detections.Count > 0)
                {
                    foreach (var detection in detections)
                    {
                        detection.Selected = false;
                    }
                    imageViewer.Invalidate();
                    statusLabel.Text = "Selection cleared";
                }
            };

            // Add new controls for SAM integration
            Label lblMaskCreationMethod = new Label
            {
                Text = "Mask Creation Method:",
                Location = new Point(0, 80),
                AutoSize = true
            };

            cboMaskCreationMethod = new ComboBox
            {
                Location = new Point(145, 78),
                Width = 135,
                DropDownStyle = ComboBoxStyle.DropDownList
            };

            // Add mask creation methods
            cboMaskCreationMethod.Items.AddRange(new object[] {
                "Center Point",
                "Corner Points",
                "Box Outline",
                "Weighted Grid",
                "Box Fill"
            });
            cboMaskCreationMethod.SelectedIndex = 0;

            // Add Send to SAM button
            btnSendToSAM = new Button
            {
                Text = "Send to SAM",
                Location = new Point(0, 110),
                Width = 280,
                Height = 35,
                BackColor = Color.LightGreen,
                Image = CreateButtonIcon("sendToSAM"),
                ImageAlign = ContentAlignment.MiddleLeft,
                TextAlign = ContentAlignment.MiddleRight,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                Font = new Font(SystemFonts.DefaultFont.FontFamily, 9, FontStyle.Bold)
            };
            btnSendToSAM.Click += (s, e) => SendSelectedBoxesToSAM();

            btnClose = new Button
            {
                Text = "Close",
                Location = new Point(0, 155),
                Width = 100,
                Height = 25
            };
            btnClose.Click += (s, e) => dinoForm.Close();

            statusLabel = new Label
            {
                Text = "Ready",
                Location = new Point(110, 160),
                AutoSize = true
            };

            buttonPanel.Controls.AddRange(new Control[] {
                btnDetect, btnApply,
                btnSelectAll, btnClearSelection,
                lblMaskCreationMethod, cboMaskCreationMethod,
                btnSendToSAM,
                btnClose, statusLabel
            });

            // --------- Help Text ---------
            Panel helpPanel = new Panel
            {
                Width = 280,
                Height = 160, // Increased height for more instructions
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, 10)
            };

            Label lblHelp = new Label
            {
                Text = "Instructions:\n" +
                    "- Select view mode (XY, XZ, or YZ)\n" +
                    "- Enter a text prompt describing what to detect\n" +
                    "- Click 'Detect Objects' to run the detection\n" +
                    "- Click on boxes to select/deselect them\n" +
                    "- Click on borders or labels to select specific boxes\n" +
                    "- Hold Ctrl and click to cycle through overlapping boxes\n" +
                    "- Adjust confidence threshold as needed\n" +
                    "- Use mousewheel to zoom, right-drag to pan\n" +
                    "- Apply selection directly or send to SAM for refinement\n" +
                    "- The mask creation method determines how points are\n" +
                    "  placed when sending boxes to SAM",
                Location = new Point(0, 0),
                Size = new Size(280, 160),
                BorderStyle = BorderStyle.FixedSingle
            };

            helpPanel.Controls.Add(lblHelp);

            // --------- Selected Material Information ---------
            Panel materialPanel = new Panel
            {
                Width = 280,
                Height = 25,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, 10)
            };

            Label lblMaterial = new Label
            {
                Text = $"Selected Material: {selectedMaterial.Name}",
                Location = new Point(0, 0),
                AutoSize = true,
                ForeColor = selectedMaterial.Color
            };

            materialPanel.Controls.Add(lblMaterial);

            // Add all panels to the table layout
            controlsTable.Controls.Add(modelPanel, 0, 0);
            controlsTable.Controls.Add(viewSelectionPanel, 0, 1);
            controlsTable.Controls.Add(promptPanel, 0, 2);
            controlsTable.Controls.Add(confidencePanel, 0, 3);
            controlsTable.Controls.Add(slicePanel, 0, 4);
            controlsTable.Controls.Add(buttonPanel, 0, 5);
            controlsTable.Controls.Add(helpPanel, 0, 6);
            controlsTable.Controls.Add(materialPanel, 0, 7);

            // Add the table to the control panel
            controlPanel.Controls.Add(controlsTable);

            // Add all components to main layout
            mainLayout.Controls.Add(viewPanel, 0, 0);
            mainLayout.Controls.Add(controlPanel, 1, 0);

            dinoForm.Controls.Add(mainLayout);

            // Handle form events
            dinoForm.FormClosing += (s, e) =>
            {
                // Clean up resources
                session?.Dispose();
                imageViewer.Image?.Dispose();
                Logger.Log("[GroundingDINO] Form closing, resources cleaned up");
            };

            // Initially load the slice
            UpdateViewer();
        }

        // Method to create button icons
        private Image CreateButtonIcon(string iconType)
        {
            Bitmap iconBitmap = new Bitmap(24, 24);
            using (Graphics g = Graphics.FromImage(iconBitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                Pen pen = new Pen(Color.Black, 2);
                SolidBrush brush = new SolidBrush(Color.Black);

                switch (iconType)
                {
                    case "detect":
                        // Draw a magnifying glass
                        g.DrawEllipse(pen, 4, 4, 12, 12);
                        g.DrawLine(pen, 14, 14, 19, 19);
                        break;

                    case "apply":
                        // Draw a checkmark
                        g.DrawLine(pen, 5, 12, 10, 18);
                        g.DrawLine(pen, 10, 18, 19, 6);
                        break;

                    case "selectAll":
                        // Draw a selection of multiple boxes
                        g.DrawRectangle(pen, 4, 4, 8, 8);
                        g.DrawRectangle(pen, 8, 8, 8, 8);
                        g.DrawRectangle(pen, 12, 12, 8, 8);
                        break;

                    case "clearSelection":
                        // Draw an X
                        g.DrawLine(pen, 6, 6, 18, 18);
                        g.DrawLine(pen, 6, 18, 18, 6);
                        break;

                    case "sendToSAM":
                        // Box with arrow pointing to a mask-like shape
                        Color samColor = Color.FromArgb(0, 120, 215); // Blue
                        pen.Color = samColor;
                        brush.Color = samColor;

                        // Draw a box
                        g.DrawRectangle(pen, 3, 5, 8, 8);

                        // Draw an arrow pointing right
                        g.DrawLine(pen, 12, 9, 16, 9);
                        g.FillPolygon(brush, new Point[] {
                            new Point(15, 7),
                            new Point(17, 9),
                            new Point(15, 11)
                        });

                        // Draw "SAM" icon representation
                        pen.Width = 1.5f;
                        g.DrawEllipse(pen, 17, 4, 5, 10);
                        g.DrawCurve(pen, new Point[] {
                            new Point(18, 7),
                            new Point(19, 9),
                            new Point(20, 11)
                        });
                        break;
                }

                pen.Dispose();
                brush.Dispose();
            }

            return iconBitmap;
        }

        private void UpdateViewButtons()
        {
            btnXYView.BackColor = (currentViewMode == ViewMode.XY) ? Color.LightSkyBlue : SystemColors.Control;
            btnXZView.BackColor = (currentViewMode == ViewMode.XZ) ? Color.LightSkyBlue : SystemColors.Control;
            btnYZView.BackColor = (currentViewMode == ViewMode.YZ) ? Color.LightSkyBlue : SystemColors.Control;

            // Update title
            if (viewPanel.Controls.Count > 0 && viewPanel.Controls[0] is Label titleLabel)
            {
                titleLabel.Text = $"CT {currentViewMode} View";
            }
        }

        private void SetupViewerEvents()
        {
            // Scroll events
            hScroll.Scroll += (s, e) =>
            {
                pan.X = -hScroll.Value;
                imageViewer.Invalidate();
            };

            vScroll.Scroll += (s, e) =>
            {
                pan.Y = -vScroll.Value;
                imageViewer.Invalidate();
            };

            // Mouse wheel for zooming
            imageViewer.MouseWheel += (s, e) =>
            {
                float oldZoom = zoom;
                // Adjust zoom based on wheel direction
                if (e.Delta > 0)
                    zoom = Math.Min(10.0f, zoom * 1.1f);
                else
                    zoom = Math.Max(0.1f, zoom * 0.9f);

                // Adjust scrollbars based on new zoom
                UpdateScrollbars();

                // Redraw
                imageViewer.Invalidate();
                Logger.Log($"[GroundingDINO] Zoom changed to {zoom:F2}");
            };

            // Mouse events for panning
            Point lastPos = Point.Empty;
            bool isPanning = false;

            imageViewer.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    // Start panning with right mouse button
                    isPanning = true;
                    lastPos = e.Location;
                }
            };

            imageViewer.MouseMove += (s, e) =>
            {
                if (isPanning && e.Button == MouseButtons.Right)
                {
                    // Calculate the move delta
                    int dx = e.X - lastPos.X;
                    int dy = e.Y - lastPos.Y;

                    // Update the panel position
                    pan.X += dx;
                    pan.Y += dy;
                    UpdateScrollbars();

                    lastPos = e.Location;
                    imageViewer.Invalidate();
                }
            };

            imageViewer.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    isPanning = false;
                }
            };

            // Add mouse click handler for box selection
            imageViewer.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left && detections != null && detections.Count > 0)
                {
                    // Convert screen coordinates to image coordinates
                    Point imagePoint = new Point(
                        (int)((e.X - pan.X) / zoom),
                        (int)((e.Y - pan.Y) / zoom)
                    );

                    // First check if we're clicking on a border or label
                    Detection borderOrLabelClick = DetectBorderOrLabelClick(imagePoint, detections);

                    if (borderOrLabelClick != null)
                    {
                        // Toggle the specifically clicked box
                        borderOrLabelClick.Selected = !borderOrLabelClick.Selected;
                        statusLabel.Text = $"Box '{borderOrLabelClick.Label}' {(borderOrLabelClick.Selected ? "selected" : "deselected")}";
                        imageViewer.Invalidate();
                        return;
                    }

                    // If not clicking on border/label, check all boxes that contain this point
                    List<Detection> overlappingBoxes = detections
                        .Where(d => d.Score >= confidenceThreshold && d.Box.Contains(imagePoint))
                        .ToList();

                    if (overlappingBoxes.Count > 0)
                    {
                        // If Ctrl key is pressed, cycle through overlapping boxes instead of just picking the top one
                        bool isCtrlPressed = (Control.ModifierKeys & Keys.Control) == Keys.Control;

                        if (isCtrlPressed && overlappingBoxes.Count > 1)
                        {
                            // Find first selected box
                            int selectedIndex = overlappingBoxes.FindIndex(d => d.Selected);

                            // Deselect all boxes
                            foreach (var box in overlappingBoxes)
                            {
                                box.Selected = false;
                            }

                            // Select next box in cycle
                            int nextIndex = (selectedIndex + 1) % overlappingBoxes.Count;
                            overlappingBoxes[nextIndex].Selected = true;

                            statusLabel.Text = $"Selected box {nextIndex + 1} of {overlappingBoxes.Count} overlapping boxes";
                        }
                        else
                        {
                            // Default behavior: select topmost box (last in the list)
                            var topBox = overlappingBoxes.Last();
                            topBox.Selected = !topBox.Selected;
                            statusLabel.Text = $"Box '{topBox.Label}' {(topBox.Selected ? "selected" : "deselected")}";
                        }

                        imageViewer.Invalidate();
                    }
                }
            };

            // Paint event for custom rendering with detections
            imageViewer.Paint += (s, e) =>
            {
                // Clear background
                e.Graphics.Clear(Color.Black);

                if (imageViewer.Image != null)
                {
                    int imgWidth = imageViewer.Image.Width;
                    int imgHeight = imageViewer.Image.Height;

                    // Calculate the image bounds
                    imageBounds = new Rectangle(
                        pan.X,
                        pan.Y,
                        (int)(imgWidth * zoom),
                        (int)(imgHeight * zoom));

                    // Draw checkerboard pattern for the entire visible area
                    DrawCheckerboardBackground(e.Graphics, imageViewer.ClientRectangle);

                    // Draw the image with interpolation
                    e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    e.Graphics.DrawImage(imageViewer.Image, imageBounds);

                    // Draw a border around the image
                    using (Pen borderPen = new Pen(Color.DarkGray, 1))
                    {
                        e.Graphics.DrawRectangle(borderPen, imageBounds);
                    }

                    // Draw detection boxes
                    DrawDetections(e.Graphics);
                }
            };
        }

        // Helper method to get complementary color
        private Color GetComplementaryColor(Color color)
        {
            return Color.FromArgb(color.A, 255 - color.R, 255 - color.G, 255 - color.B);
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

        private void DrawDetections(Graphics g)
        {
            if (detections == null || detections.Count == 0)
                return;

            foreach (var detection in detections.Where(d => d.Score >= confidenceThreshold))
            {
                // Scale and position the box according to zoom and pan
                Rectangle scaledBox = new Rectangle(
                    (int)(detection.Box.X * zoom) + pan.X,
                    (int)(detection.Box.Y * zoom) + pan.Y,
                    (int)(detection.Box.Width * zoom),
                    (int)(detection.Box.Height * zoom));

                // Use different colors for selected vs. unselected boxes
                Color boxColor = detection.Selected ?
                    GetComplementaryColor(selectedMaterial.Color) :
                    selectedMaterial.Color;

                // Draw the bounding box
                using (Pen boxPen = new Pen(boxColor, 2))
                {
                    g.DrawRectangle(boxPen, scaledBox);
                }

                // Fill box with semi-transparent color if selected
                if (detection.Selected)
                {
                    using (SolidBrush fillBrush = new SolidBrush(Color.FromArgb(50, boxColor)))
                    {
                        g.FillRectangle(fillBrush, scaledBox);
                    }
                }

                // Draw the label with score
                string labelText = $"{detection.Label} ({detection.Score:F2})";
                if (detection.Selected)
                    labelText += " ✓"; // Add checkmark to indicate selection

                using (Font font = new Font("Arial", 8))
                using (SolidBrush brush = new SolidBrush(boxColor))
                using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(128, Color.Black)))
                {
                    SizeF textSize = g.MeasureString(labelText, font);
                    RectangleF textRect = new RectangleF(
                        scaledBox.X,
                        scaledBox.Y - textSize.Height,
                        textSize.Width,
                        textSize.Height);

                    // Draw background for text
                    g.FillRectangle(bgBrush, textRect);

                    // Draw text
                    g.DrawString(labelText, font, brush, textRect.Location);
                }
            }
        }

        private void UpdateScrollbars()
        {
            if (imageViewer.Image != null)
            {
                int imageWidth = (int)(imageViewer.Image.Width * zoom);
                int imageHeight = (int)(imageViewer.Image.Height * zoom);

                hScroll.Maximum = Math.Max(0, imageWidth - imageViewer.ClientSize.Width + hScroll.LargeChange);
                vScroll.Maximum = Math.Max(0, imageHeight - imageViewer.ClientSize.Height + vScroll.LargeChange);

                hScroll.Value = Math.Min(hScroll.Maximum, -pan.X);
                vScroll.Value = Math.Min(vScroll.Maximum, -pan.Y);
            }
        }

        private void UpdateViewer()
        {
            Bitmap viewBitmap = null;

            switch (currentViewMode)
            {
                case ViewMode.XY:
                    viewBitmap = CreateSliceBitmap(currentSlice);
                    break;

                case ViewMode.XZ:
                    viewBitmap = CreateXZSliceBitmap();
                    break;

                case ViewMode.YZ:
                    viewBitmap = CreateYZSliceBitmap();
                    break;
            }

            using (viewBitmap)
            {
                if (imageViewer.Image != null)
                    imageViewer.Image.Dispose();

                imageViewer.Image = new Bitmap(viewBitmap);
                UpdateScrollbars();
            }

            // Clear detections when switching views
            if (detections != null)
                detections.Clear();

            imageViewer.Invalidate();
        }

        private unsafe Bitmap CreateSliceBitmap(int sliceZ)
        {
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
            return bmp;
        }

        private void BrowseForModelDirectory()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select the directory containing Grounding DINO ONNX model";
                dialog.ShowNewFolderButton = false;

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    modelPathTextBox.Text = dialog.SelectedPath;

                    // Update path
                    modelPath = Path.Combine(dialog.SelectedPath, "groundingdino.onnx");

                    Logger.Log($"[GroundingDINO] Model directory set to: {dialog.SelectedPath}");
                }
            }
        }

        private async void LoadONNXModel()
        {
            if (isLoading) return; // Prevent multiple concurrent loads
            isLoading = true;

            // Show loading UI
            loadingPanel.Visible = true;
            btnLoadModel.Enabled = false;
            statusLabel.Text = "Loading model...";

            try
            {
                // Run the model loading in a background task
                await Task.Run(() =>
                {
                    try
                    {
                        // Dispose existing session if any
                        session?.Dispose();

                        // Verify file exists
                        if (!File.Exists(modelPath))
                        {
                            throw new FileNotFoundException($"Model not found at: {modelPath}");
                        }

                        // Load Vocabulary
                        LoadVocabulary();

                        // Create session options with enhanced performance settings
                        useGPU = rbGPU.Checked;
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
                                // ---------- CUDA first ------------------------------------------------
                                options.AppendExecutionProvider_CUDA();   // deviceId = 0
                                Logger.Log("[GroundingDINO] Using CUDA execution provider");
                            }
                            catch (Exception cudaEx)
                            {
                                Logger.Log($"[GroundingDINO] CUDA not available: {cudaEx.Message}");
                                // ---------- fallback: leave the session on CPU ------------------------
                                useGPU = false;
                            }
                        }

                        // Create session with optimized settings
                        session = new InferenceSession(modelPath, options);
                        InspectModelIO();
                        Logger.Log("[GroundingDINO] Model loaded successfully with optimized settings");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[GroundingDINO] Error loading model: {ex.Message}");
                        throw; // Re-throw to be caught by outer try-catch
                    }
                });

                // UI updates back on UI thread
                statusLabel.Text = "Model loaded successfully";
            }
            catch (Exception ex)
            {
                dinoForm.Invoke((MethodInvoker)delegate
                {
                    MessageBox.Show($"Error loading model: {ex.Message}");
                    statusLabel.Text = $"Error: {ex.Message}";
                });
            }
            finally
            {
                // Hide loading UI and re-enable button
                loadingPanel.Visible = false;
                btnLoadModel.Enabled = true;
                isLoading = false;
            }
        }

        private static DenseTensor<int> ToInt32(DenseTensor<long> src)
        {
            var dst = new DenseTensor<int>(src.Dimensions);
            var s = src.Buffer.Span;
            var d = dst.Buffer.Span;
            for (int i = 0; i < s.Length; i++) d[i] = (int)s[i];
            return dst;
        }

        private async Task PerformDetection()
        {
            if (session == null)
            {
                MessageBox.Show("Model not loaded. Please load the model first.");
                return;
            }

            if (string.IsNullOrWhiteSpace(promptTextBox.Text))
            {
                MessageBox.Show("Please enter a detection prompt.");
                return;
            }

            if (vocabDict == null || vocabDict.Count == 0)
            {
                MessageBox.Show("Vocabulary not loaded. Please check the model directory.");
                return;
            }

            statusLabel.Text = "Detecting...";
            btnDetect.Enabled = false;

            try
            {
                // 1) Preprocess image → DenseTensor<float>[1,3,800,800]
                DenseTensor<float> pixelValues = await Task.Run(() => PreprocessImage());

                // 2) Build pixel mask → DenseTensor<long>[1,800,800]
                var pixelMask = new DenseTensor<long>(new[] { 1, 800, 800 });
                for (int y = 0; y < 800; y++)
                    for (int x = 0; x < 800; x++)
                        pixelMask[0, y, x] = 1;

                // 3) Tokenize prompt → InputIds, TokenTypeIds, AttentionMask
                TokenizationResult tokenized = await Task.Run(() => TokenizeText(promptTextBox.Text.Trim()));

                // 4) Prepare ONNX inputs
                var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("pixel_values",   pixelValues),
            NamedOnnxValue.CreateFromTensor("input_ids",      tokenized.InputIds),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenized.TokenTypeIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", tokenized.AttentionMask),
            NamedOnnxValue.CreateFromTensor("pixel_mask",     pixelMask)
        };

                // 5) Specify only the real outputs
                var outputNames = new List<string> { "logits", "pred_boxes" };

                // 6) Run inference: inputs first, then outputNames
                using (var results = session.Run(inputs, outputNames))
                {
                    var logitsTensor = results.First(x => x.Name == "logits").AsTensor<float>();
                    var predBoxesTensor = results.First(x => x.Name == "pred_boxes").AsTensor<float>();

                    // 7) Convert to Detection list
                    detections = ProcessDetectionOutputs(logitsTensor, predBoxesTensor);
                }

                UpdateDetectionDisplay();
                int validDetections = detections.Count(d => d.Score >= confidenceThreshold);
                statusLabel.Text = $"Found {validDetections} detections. Click on boxes to select them.";

                imageViewer.Invalidate();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during detection: {ex.Message}");
                statusLabel.Text = "Error in detection";
                Logger.Log($"[GroundingDINO] Detection error: {ex.Message}");
            }
            finally
            {
                btnDetect.Enabled = true;
            }
        }

        private Detection DetectBorderOrLabelClick(Point imagePoint, List<Detection> detections)
        {
            const int borderThreshold = 3; // Pixels from border to consider a border click

            foreach (var detection in detections.Where(d => d.Score >= confidenceThreshold))
            {
                // Check if clicking on label area (above the box)
                Rectangle box = detection.Box;
                Rectangle labelArea = new Rectangle(
                    box.X, box.Y - 20, box.Width, 20);

                if (labelArea.Contains(imagePoint))
                {
                    return detection;
                }

                // Check if clicking on border (not deep inside box)
                bool isOnBorder = box.Contains(imagePoint) &&
                    (imagePoint.X <= box.X + borderThreshold ||
                     imagePoint.X >= box.X + box.Width - borderThreshold ||
                     imagePoint.Y <= box.Y + borderThreshold ||
                     imagePoint.Y >= box.Y + box.Height - borderThreshold);

                if (isOnBorder)
                {
                    return detection;
                }
            }

            return null;
        }

        /// <summary>
        /// Dumps the first <paramref name="count"/> elements of a DenseTensor<long>
        /// and logs min / max / distinct values – handy for spotting stray indices.
        /// </summary>
        private void LogTensor(string label, DenseTensor<long> tensor, int count = 20)
        {
            var span = tensor.Buffer.Span;
            int n = Math.Min(count, span.Length);

            // build a short preview string
            StringBuilder sb = new StringBuilder();
            sb.Append('[').Append(label).Append("] ");

            for (int i = 0; i < n; i++)
                sb.Append(span[i]).Append(' ');

            // stats
            long min = span[0], max = span[0];
            HashSet<long> distinct = new HashSet<long>();

            foreach (long v in span)
            {
                if (v < min) min = v;
                if (v > max) max = v;
                distinct.Add(v);
            }

            sb.Append($"| len={span.Length}  min={min}  max={max}  distinct={string.Join(",", distinct.Take(10))}");
            Logger.Log(sb.ToString());
        }

        private List<Detection> ProcessDetectionOutputs(Tensor<float> logits, Tensor<float> predBoxes)
        {
            var result = new List<Detection>();

            // The model outputs 900 potential detections (from num_queries in config.json)
            int numProposals = predBoxes.Dimensions[1]; // Should be 900
            int numClasses = logits.Dimensions[2];      // Should be 256

            // Apply sigmoid to logits to get probabilities
            float[,] probabilities = new float[numProposals, numClasses];
            for (int i = 0; i < numProposals; i++)
            {
                for (int j = 0; j < numClasses; j++)
                {
                    float logit = logits[0, i, j];
                    probabilities[i, j] = 1.0f / (1.0f + (float)Math.Exp(-logit)); // sigmoid
                }
            }

            // Process each detection proposal
            for (int i = 0; i < numProposals; i++)
            {
                // Find maximum class probability and its index
                float maxProb = float.MinValue;
                int maxClassIdx = 0;

                for (int j = 0; j < numClasses; j++)
                {
                    if (probabilities[i, j] > maxProb)
                    {
                        maxProb = probabilities[i, j];
                        maxClassIdx = j;
                    }
                }

                // Skip low confidence detections
                if (maxProb < confidenceThreshold)
                    continue;

                // Get normalized box coordinates in [cx, cy, w, h] format
                float cx = predBoxes[0, i, 0];
                float cy = predBoxes[0, i, 1];
                float w = predBoxes[0, i, 2];
                float h = predBoxes[0, i, 3];

                // Convert to [x1, y1, x2, y2] format
                float x1 = cx - w / 2;
                float y1 = cy - h / 2;
                float x2 = cx + w / 2;
                float y2 = cy + h / 2;

                // Clamp to [0, 1] range
                x1 = Math.Max(0, Math.Min(1, x1));
                y1 = Math.Max(0, Math.Min(1, y1));
                x2 = Math.Max(0, Math.Min(1, x2));
                y2 = Math.Max(0, Math.Min(1, y2));

                // Convert normalized coordinates to pixel coordinates
                int imgWidth = mainForm.GetWidth();
                int imgHeight = mainForm.GetHeight();

                int pixelX1 = (int)(x1 * imgWidth);
                int pixelY1 = (int)(y1 * imgHeight);
                int pixelX2 = (int)(x2 * imgWidth);
                int pixelY2 = (int)(y2 * imgHeight);

                // Create rectangle (ensure valid dimensions)
                pixelX1 = Math.Max(0, pixelX1);
                pixelY1 = Math.Max(0, pixelY1);
                pixelX2 = Math.Min(imgWidth - 1, pixelX2);
                pixelY2 = Math.Min(imgHeight - 1, pixelY2);

                int boxWidth = pixelX2 - pixelX1;
                int boxHeight = pixelY2 - pixelY1;

                if (boxWidth <= 0 || boxHeight <= 0)
                    continue;

                Rectangle box = new Rectangle(pixelX1, pixelY1, boxWidth, boxHeight);

                // In a real implementation, we would map from class index to name
                // Here we'll use the prompt + class index
                string label = $"{promptTextBox.Text} ({maxProb:F2})";

                // Add to results
                result.Add(new Detection
                {
                    Box = box,
                    Score = maxProb,
                    Label = label,
                    Selected = false // Default to not selected
                });
            }

            // Apply non-maximum suppression to filter out overlapping boxes
            var filteredResult = ApplyNonMaximumSuppression(result, 0.7f);

            return filteredResult.OrderByDescending(d => d.Score).ToList();
        }

        private List<Detection> ApplyNonMaximumSuppression(List<Detection> detections, float iouThreshold = 0.5f)
        {
            List<Detection> result = new List<Detection>();

            // Sort by confidence
            var orderedDetections = detections.OrderByDescending(d => d.Score).ToList();

            // Keep track of which detections have been selected
            bool[] isSelected = new bool[orderedDetections.Count];

            for (int i = 0; i < orderedDetections.Count; i++)
            {
                if (isSelected[i])
                    continue;

                result.Add(orderedDetections[i]);

                // Mark as selected
                isSelected[i] = true;

                // Calculate IoU with all remaining boxes and suppress if overlap is too large
                for (int j = i + 1; j < orderedDetections.Count; j++)
                {
                    if (isSelected[j])
                        continue;

                    float iou = CalculateIoU(orderedDetections[i].Box, orderedDetections[j].Box);

                    if (iou > iouThreshold)
                    {
                        isSelected[j] = true; // Suppress this detection
                    }
                }
            }

            return result;
        }

        private float CalculateIoU(Rectangle a, Rectangle b)
        {
            // Calculate intersection area
            int intersectLeft = Math.Max(a.Left, b.Left);
            int intersectTop = Math.Max(a.Top, b.Top);
            int intersectRight = Math.Min(a.Right, b.Right);
            int intersectBottom = Math.Min(a.Bottom, b.Bottom);

            if (intersectRight < intersectLeft || intersectBottom < intersectTop)
                return 0; // No intersection

            int intersectionArea = (intersectRight - intersectLeft) * (intersectBottom - intersectTop);

            // Calculate union area
            int areaA = a.Width * a.Height;
            int areaB = b.Width * b.Height;
            int unionArea = areaA + areaB - intersectionArea;

            return (float)intersectionArea / unionArea;
        }

        private DenseTensor<long> CreatePixelMask(int width, int height)
        {
            // Create a pixel mask where 1 indicates a valid pixel
            var pixelMask = new DenseTensor<long>(new[] { 1, 800, 800 });

            // Set all pixels as valid (1)
            for (int y = 0; y < 800; y++)
            {
                for (int x = 0; x < 800; x++)
                {
                    pixelMask[0, y, x] = 1;
                }
            }

            return pixelMask;
        }

        private unsafe DenseTensor<float> PreprocessImage()
        {
            // Get the current slice as a bitmap
            using (Bitmap sliceBitmap = CreateSliceBitmap(currentSlice))
            {
                // Create a tensor with shape [1, 3, 800, 800]
                DenseTensor<float> inputTensor = new DenseTensor<float>(new[] { 1, 3, 800, 800 });

                // Create a resized version of the slice
                using (Bitmap resized = new Bitmap(800, 800))
                {
                    using (Graphics g = Graphics.FromImage(resized))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(sliceBitmap, 0, 0, 800, 800);
                    }

                    // Lock the bitmap and access its pixel data
                    BitmapData bmpData = resized.LockBits(
                        new Rectangle(0, 0, resized.Width, resized.Height),
                        ImageLockMode.ReadOnly,
                        PixelFormat.Format24bppRgb);

                    int stride = bmpData.Stride;
                    int bytesPerPixel = 3; // RGB

                    byte* ptr = (byte*)bmpData.Scan0;

                    // Mean and std values from preprocessor_config.json
                    float[] mean = new float[] { 0.485f, 0.456f, 0.406f };
                    float[] std = new float[] { 0.229f, 0.224f, 0.225f };
                    float rescaleFactor = 0.00392156862745098f; // 1/255, from config

                    // Process pixels and normalize
                    for (int y = 0; y < 800; y++)
                    {
                        for (int x = 0; x < 800; x++)
                        {
                            int offset = y * stride + x * bytesPerPixel;

                            // BGR order (standard in Bitmap)
                            byte b = ptr[offset];
                            byte g = ptr[offset + 1];
                            byte r = ptr[offset + 2];

                            // Rescale and normalize according to configuration
                            float rNorm = (r * rescaleFactor - mean[0]) / std[0];
                            float gNorm = (g * rescaleFactor - mean[1]) / std[1];
                            float bNorm = (b * rescaleFactor - mean[2]) / std[2];

                            // RGB order for the model
                            inputTensor[0, 0, y, x] = rNorm;
                            inputTensor[0, 1, y, x] = gNorm;
                            inputTensor[0, 2, y, x] = bNorm;
                        }
                    }

                    resized.UnlockBits(bmpData);
                    return inputTensor;
                }
            }
        }

        private TokenizationResult TokenizeText(string text)
        {
            // Normalise prompt
            text = text.ToLower().Trim();

            // Allocate fixed-size tensors [1, maxTextLen] (256 for Grounding DINO)
            int sequenceLength = maxTextLen;
            var inputIds = new DenseTensor<long>(new[] { 1, sequenceLength });
            var tokenTypeIds = new DenseTensor<long>(new[] { 1, sequenceLength });
            var attentionMask = new DenseTensor<long>(new[] { 1, sequenceLength });

            // Fill with PAD tokens and zeros
            for (int i = 0; i < sequenceLength; i++)
            {
                inputIds[0, i] = padTokenId;  // [PAD]
                tokenTypeIds[0, i] = 0;           // single-sequence
                attentionMask[0, i] = 0;           // 0 → ignore
            }

            // Insert [CLS] at position 0
            inputIds[0, 0] = clsTokenId;
            attentionMask[0, 0] = 1;

            // Tokenise the prompt
            string[] words = text.Split(new[] { ' ', '\t', '\n', '\r' },
                                        StringSplitOptions.RemoveEmptyEntries);

            int position = 1;                                   // next free slot
            List<string> tokensLogged = new List<string>();     // C# 7.3-compatible
            tokensLogged.Add(clsToken);

            foreach (string word in words)
            {
                if (position >= sequenceLength - 1) break;      // leave room for [SEP]

                int tokenId;
                if (vocabDict.TryGetValue(word, out tokenId))
                {
                    inputIds[0, position] = tokenId;
                    attentionMask[0, position] = 1;
                    tokensLogged.Add(word);
                    position++;
                }
                else                                            // per-char fallback or [UNK]
                {
                    bool added = false;
                    foreach (char c in word)
                    {
                        if (position >= sequenceLength - 1) break;

                        string charStr = c.ToString();
                        if (vocabDict.TryGetValue(charStr, out tokenId))
                        {
                            inputIds[0, position] = tokenId;
                            attentionMask[0, position] = 1;
                            tokensLogged.Add(charStr);
                            position++;
                            added = true;
                        }
                    }

                    if (!added && position < sequenceLength - 1)
                    {
                        inputIds[0, position] = unkTokenId;
                        attentionMask[0, position] = 1;
                        tokensLogged.Add(unkToken);
                        position++;
                    }
                }
            }

            // Terminate with [SEP]
            if (position < sequenceLength)
            {
                inputIds[0, position] = sepTokenId;
                attentionMask[0, position] = 1;
                tokensLogged.Add(sepToken);
                position++;
            }

            Logger.Log("[GroundingDINO] Tokenized prompt to " +
                       tokensLogged.Count + " tokens: " +
                       string.Join(" ", tokensLogged));

            return new TokenizationResult
            {
                InputIds = inputIds,
                TokenTypeIds = tokenTypeIds,
                AttentionMask = attentionMask
            };
        }

        // ─── Helpers - GroundingDINODetector ──────────────

        private void LogIntTensorStats(string label, DenseTensor<long> tensor)
        {
            var span = tensor.Buffer.Span;
            long min = long.MaxValue, max = long.MinValue;

            // frequency count
            Dictionary<long, int> freq = new Dictionary<long, int>();
            for (int i = 0; i < span.Length; i++)
            {
                long v = span[i];
                if (v < min) min = v;
                if (v > max) max = v;
                int c;
                if (!freq.TryGetValue(v, out c)) c = 0;
                freq[v] = c + 1;
            }

            // build "value:count" string
            StringBuilder sb = new StringBuilder();
            bool first = true;
            foreach (var kv in freq.OrderBy(kv => kv.Key))
            {
                if (!first) sb.Append(", ");
                sb.Append(kv.Key).Append(':').Append(kv.Value);
                first = false;
            }

            Logger.Log("[" + label + "] len=" + span.Length +
                       "  min=" + min + "  max=" + max +
                       "  values=(" + sb.ToString() + ")");

            if (label == "token_type_ids" && (min < 0 || max > 1))
                Logger.Log("[GroundingDINO][WARN] token_type_ids contains out-of-range values!");
        }

        private void LogFloatTensorStats(string label, DenseTensor<float> tensor, int sample = 20)
        {
            var span = tensor.Buffer.Span;
            float min = span[0], max = span[0], sum = 0f;
            for (int i = 0; i < span.Length; i++)
            {
                float v = span[i];
                if (v < min) min = v;
                if (v > max) max = v;
                sum += v;
            }
            float mean = sum / span.Length;

            // first <sample> values
            StringBuilder sb = new StringBuilder();
            int shown = Math.Min(sample, span.Length);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(span[i].ToString("F4"));
            }

            Logger.Log("[" + label + "] len=" + span.Length +
                       "  min=" + min.ToString("F4") +
                       "  max=" + max.ToString("F4") +
                       "  mean=" + mean.ToString("F4") +
                       "  sample=" + sb.ToString());
        }

        private static DenseTensor<int> ToInt32Tensor(DenseTensor<long> src)
        {
            var dst = new DenseTensor<int>(src.Dimensions);
            var s = src.Buffer.Span;
            var d = dst.Buffer.Span;
            for (int i = 0; i < s.Length; i++) d[i] = (int)s[i];
            return dst;
        }

        private List<Detection> ProcessDetectionOutputs(Tensor<float> boxes, Tensor<float> scores, Tensor<string> labels)
        {
            var result = new List<Detection>();

            int numDetections = boxes.Dimensions[0];

            // Process each detection
            for (int i = 0; i < numDetections; i++)
            {
                float score = scores[i];

                // Skip low confidence detections
                if (score < confidenceThreshold)
                    continue;

                // Get the box coordinates (x1, y1, x2, y2 format)
                float x1 = boxes[i, 0];
                float y1 = boxes[i, 1];
                float x2 = boxes[i, 2];
                float y2 = boxes[i, 3];

                // Convert normalized coordinates to pixel coordinates
                int imgWidth = mainForm.GetWidth();
                int imgHeight = mainForm.GetHeight();

                int pixelX1 = (int)(x1 * imgWidth);
                int pixelY1 = (int)(y1 * imgHeight);
                int pixelX2 = (int)(x2 * imgWidth);
                int pixelY2 = (int)(y2 * imgHeight);

                // Create rectangle (ensure valid dimensions)
                pixelX1 = Math.Max(0, pixelX1);
                pixelY1 = Math.Max(0, pixelY1);
                pixelX2 = Math.Min(imgWidth - 1, pixelX2);
                pixelY2 = Math.Min(imgHeight - 1, pixelY2);

                int width = pixelX2 - pixelX1;
                int height = pixelY2 - pixelY1;

                if (width <= 0 || height <= 0)
                    continue;

                Rectangle box = new Rectangle(pixelX1, pixelY1, width, height);

                // Get label for this detection
                string label = labels[i];

                // Add to results
                result.Add(new Detection
                {
                    Box = box,
                    Score = score,
                    Label = label,
                    Selected = false // Default to not selected
                });
            }

            // Sort by score descending
            return result.OrderByDescending(d => d.Score).ToList();
        }

        private void UpdateDetectionDisplay()
        {
            // Redraw the image with current detections and confidence threshold
            imageViewer.Invalidate();
        }

        private void ApplyDetectionResults()
        {
            if (detections == null || detections.Count == 0)
            {
                MessageBox.Show("No detections to apply.");
                return;
            }

            // Check if any boxes are selected
            int selectedCount = detections.Count(d => d.Score >= confidenceThreshold && d.Selected);
            if (selectedCount == 0)
            {
                MessageBox.Show("Please select at least one detection box first.");
                statusLabel.Text = "No boxes selected. Click on boxes to select them.";
                return;
            }

            try
            {
                Logger.Log("[GroundingDINO] Applying selected detection results");
                statusLabel.Text = "Applying selected detections...";

                // Different handling based on view mode
                switch (currentViewMode)
                {
                    case ViewMode.XY:
                        ApplyXYDetections();
                        break;

                    case ViewMode.XZ:
                        ApplyXZDetections();
                        break;

                    case ViewMode.YZ:
                        ApplyYZDetections();
                        break;
                }

                // Update MainForm's view
                mainForm.RenderViews();
                mainForm.SaveLabelsChk();

                statusLabel.Text = $"Applied {selectedCount} selected detection results";
                Logger.Log("[GroundingDINO] Selected detection results applied");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error applying detection results: {ex.Message}");
                statusLabel.Text = $"Error: {ex.Message}";
                Logger.Log($"[GroundingDINO] Error applying detection results: {ex.Message}");
            }
        }

        private void ApplyXYDetections()
        {
            // Create a mask for the current slice
            byte[,] mask = new byte[mainForm.GetWidth(), mainForm.GetHeight()];

            // Fill in the mask areas corresponding to SELECTED detection boxes only
            foreach (var detection in detections.Where(d => d.Score >= confidenceThreshold && d.Selected))
            {
                Rectangle box = detection.Box;

                // Fill the rectangle in the mask
                for (int y = box.Y; y < box.Y + box.Height; y++)
                {
                    for (int x = box.X; x < box.X + box.Width; x++)
                    {
                        // Check bounds
                        if (x >= 0 && x < mask.GetLength(0) && y >= 0 && y < mask.GetLength(1))
                        {
                            mask[x, y] = selectedMaterial.ID;
                        }
                    }
                }
            }

            // Apply the mask to the current slice in the volume labels
            for (int y = 0; y < mainForm.GetHeight(); y++)
            {
                for (int x = 0; x < mainForm.GetWidth(); x++)
                {
                    if (mask[x, y] > 0)
                    {
                        mainForm.volumeLabels[x, y, currentSlice] = mask[x, y];
                    }
                }
            }

            // Update the mainForm's temporary selection
            if (mainForm.currentSelection == null ||
                mainForm.currentSelection.GetLength(0) != mainForm.GetWidth() ||
                mainForm.currentSelection.GetLength(1) != mainForm.GetHeight())
            {
                mainForm.currentSelection = new byte[mainForm.GetWidth(), mainForm.GetHeight()];
            }

            // Copy mask to current selection
            for (int y = 0; y < mainForm.GetHeight(); y++)
            {
                for (int x = 0; x < mainForm.GetWidth(); x++)
                {
                    if (mask[x, y] > 0)
                    {
                        mainForm.currentSelection[x, y] = mask[x, y];
                    }
                }
            }
        }

        private void ApplyXZDetections()
        {
            int width = mainForm.GetWidth();
            int depth = mainForm.GetDepth();
            int yRow = currentSlice; // The Y row we're viewing in XZ mode

            // Create a temporary mask for XZ plane
            byte[,] mask = new byte[width, depth];

            // Fill in mask from SELECTED detections only
            foreach (var detection in detections.Where(d => d.Score >= confidenceThreshold && d.Selected))
            {
                Rectangle box = detection.Box;

                // X remains X, but Y in the box corresponds to Z in the volume
                for (int z = box.Y; z < box.Y + box.Height; z++)
                {
                    for (int x = box.X; x < box.X + box.Width; x++)
                    {
                        // Check bounds
                        if (x >= 0 && x < width && z >= 0 && z < depth)
                        {
                            mask[x, z] = selectedMaterial.ID;
                        }
                    }
                }
            }

            // Apply mask to volume
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (mask[x, z] > 0)
                    {
                        mainForm.volumeLabels[x, yRow, z] = mask[x, z];
                    }
                }
            }

            // Update 2D selection for the current XY slice if it exists in our XZ detections
            if (mainForm.currentSelection == null ||
                mainForm.currentSelection.GetLength(0) != width ||
                mainForm.currentSelection.GetLength(1) != mainForm.GetHeight())
            {
                mainForm.currentSelection = new byte[width, mainForm.GetHeight()];
            }

            // Get the current XY slice number
            int xySlice = mainForm.CurrentSlice;

            // Copy detection from XZ plane to XY selection if it intersects
            if (xySlice >= 0 && xySlice < depth)
            {
                for (int x = 0; x < width; x++)
                {
                    if (mask[x, xySlice] > 0)
                    {
                        mainForm.currentSelection[x, yRow] = mask[x, xySlice];
                    }
                }
            }
        }

        private void ApplyYZDetections()
        {
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();
            int xCol = currentSlice; // The X column we're viewing in YZ mode

            // Create a temporary mask for YZ plane
            byte[,] mask = new byte[depth, height];

            // Fill in mask from SELECTED detections only
            foreach (var detection in detections.Where(d => d.Score >= confidenceThreshold && d.Selected))
            {
                Rectangle box = detection.Box;

                // X in the box corresponds to Z in the volume, Y remains Y
                for (int y = box.Y; y < box.Y + box.Height; y++)
                {
                    for (int z = box.X; z < box.X + box.Width; z++)
                    {
                        // Check bounds
                        if (z >= 0 && z < depth && y >= 0 && y < height)
                        {
                            mask[z, y] = selectedMaterial.ID;
                        }
                    }
                }
            }

            // Apply mask to volume
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    if (mask[z, y] > 0)
                    {
                        mainForm.volumeLabels[xCol, y, z] = mask[z, y];
                    }
                }
            }

            // Update 2D selection for the current XY slice if it exists in our YZ detections
            if (mainForm.currentSelection == null ||
                mainForm.currentSelection.GetLength(0) != mainForm.GetWidth() ||
                mainForm.currentSelection.GetLength(1) != height)
            {
                mainForm.currentSelection = new byte[mainForm.GetWidth(), height];
            }

            // Get the current XY slice number
            int xySlice = mainForm.CurrentSlice;

            // Copy detection from YZ plane to XY selection if it intersects
            if (xySlice >= 0 && xySlice < depth)
            {
                for (int y = 0; y < height; y++)
                {
                    if (mask[xySlice, y] > 0)
                    {
                        mainForm.currentSelection[xCol, y] = mask[xySlice, y];
                    }
                }
            }
        }

        // Methods to create XZ and YZ slice bitmaps
        private unsafe Bitmap CreateXZSliceBitmap()
        {
            int w = mainForm.GetWidth();
            int d = mainForm.GetDepth();
            int y = currentSlice; // Use the current slice as the Y coordinate for XZ view

            // Ensure Y is within valid range
            y = Math.Min(Math.Max(0, y), mainForm.GetHeight() - 1);

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
                    byte val = mainForm.volumeData[x, y, z];
                    int offset = z * stride + x * bytesPerPixel;

                    // RGB = same value for grayscale
                    ptr[offset] = val;     // Blue
                    ptr[offset + 1] = val; // Green
                    ptr[offset + 2] = val; // Red
                }
            }

            bmp.UnlockBits(bmpData);
            return bmp;
        }

        private unsafe Bitmap CreateYZSliceBitmap()
        {
            int h = mainForm.GetHeight();
            int d = mainForm.GetDepth();
            int x = currentSlice; // Use the current slice as the X coordinate for YZ view

            // Ensure X is within valid range
            x = Math.Min(Math.Max(0, x), mainForm.GetWidth() - 1);

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
                    byte val = mainForm.volumeData[x, y, z];
                    int offset = y * stride + z * bytesPerPixel;

                    // RGB = same value for grayscale
                    ptr[offset] = val;     // Blue
                    ptr[offset + 1] = val; // Green
                    ptr[offset + 2] = val; // Red
                }
            }

            bmp.UnlockBits(bmpData);
            return bmp;
        }

        private void LoadVocabulary()
        {
            vocabDict = new Dictionary<string, int>();

            string vocabPath = Path.Combine(Path.GetDirectoryName(modelPath), "vocab.txt");
            if (!File.Exists(vocabPath))
            {
                Logger.Log($"[GroundingDINO] Vocabulary file not found at: {vocabPath}");
                return;
            }

            try
            {
                string[] lines = File.ReadAllLines(vocabPath);
                for (int i = 0; i < lines.Length; i++)
                {
                    vocabDict[lines[i]] = i;
                }

                Logger.Log($"[GroundingDINO] Loaded vocabulary with {vocabDict.Count} tokens");

                // Get special token IDs if they exist in vocabulary
                if (vocabDict.ContainsKey(clsToken)) clsTokenId = vocabDict[clsToken];
                if (vocabDict.ContainsKey(sepToken)) sepTokenId = vocabDict[sepToken];
                if (vocabDict.ContainsKey(padToken)) padTokenId = vocabDict[padToken];
                if (vocabDict.ContainsKey(unkToken)) unkTokenId = vocabDict[unkToken];
            }
            catch (Exception ex)
            {
                Logger.Log($"[GroundingDINO] Error loading vocabulary: {ex.Message}");
            }
        }

        public void Show()
        {
            dinoForm.Show();
        }

        private void InspectModelIO()
        {
            if (session == null)
            {
                Logger.Log("[GroundingDINO] Cannot inspect model - no session loaded");
                return;
            }

            try
            {
                // Get model input metadata
                Logger.Log("[GroundingDINO] Model Input Requirements:");
                foreach (var input in session.InputMetadata)
                {
                    string shapes = string.Join("x", input.Value.Dimensions);
                    Logger.Log($"  Input: {input.Key}, Type: {input.Value.ElementType}, Shape: {shapes}");
                }

                // Get model output metadata
                Logger.Log("[GroundingDINO] Model Output Information:");
                foreach (var output in session.OutputMetadata)
                {
                    string shapes = output.Value.Dimensions != null ? string.Join("x", output.Value.Dimensions) : "dynamic";
                    Logger.Log($"  Output: {output.Key}, Type: {output.Value.ElementType}, Shape: {shapes}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GroundingDINO] Error inspecting model: {ex.Message}");
            }
        }

        private void SendSelectedBoxesToSAM()
        {
            if (detections == null || detections.Count == 0)
            {
                MessageBox.Show("No detections available. Please detect objects first.");
                return;
            }

            var selectedDetections = detections.Where(d => d.Score >= confidenceThreshold && d.Selected).ToList();

            if (selectedDetections.Count == 0)
            {
                MessageBox.Show("Please select at least one detection box first.");
                return;
            }

            try
            {
                // Get selected boxes
                Rectangle[] boxes = selectedDetections.Select(d => d.Box).ToArray();

                // Get selected mask creation method
                string method = cboMaskCreationMethod.SelectedItem.ToString();

                // Use our helper class
                SAMPointsHelper.SendBoxesToSAM(
                    mainForm,
                    selectedMaterial,
                    annotationManager ?? new AnnotationManager(),
                    boxes,
                    currentSlice,
                    method
                );

                Logger.Log("[GroundingDINO] Selected boxes sent to SAM with integrated helper");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error sending to SAM: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[GroundingDINO] Error sending to SAM: {ex.Message}");
            }
        }

        // Helper method to clear existing annotation points
        private void ClearAnnotationPoints()
        {
            if (annotationManager == null) return;

            var allPoints = annotationManager.GetAllPoints();
            foreach (var point in allPoints.ToList())
            {
                annotationManager.RemovePoint(point.ID);
            }
            Logger.Log("[GroundingDINO] Cleared existing annotation points");
        }

        // Helper method to add annotation points based on the selected creation method
        private void AddAnnotationPointsFromBox(Rectangle box)
        {
            if (annotationManager == null) return;

            // Make sure to create POSITIVE points, not negative ones
            string pointLabel = "Positive_" + selectedMaterial.Name;

            // Get the currently selected mask creation method
            string method = cboMaskCreationMethod.SelectedItem.ToString();

            switch (method)
            {
                case "Center Point":
                    // Add a single point at the center of the box
                    float centerX = box.X + box.Width / 2f;
                    float centerY = box.Y + box.Height / 2f;
                    var centerPoint = annotationManager.AddPoint(centerX, centerY, currentSlice, pointLabel);

                    // Make sure the point is registered as positive in SAM
                    if (mainForm != null && mainForm.AnnotationMgr != null)
                    {
                        // If we're using the main annotation manager, update it
                        AddPointToMainAnnotationManager(centerX, centerY, currentSlice, true);
                    }

                    Logger.Log($"[GroundingDINO] Added positive center point at ({centerX}, {centerY}, {currentSlice})");
                    break;

                case "Corner Points":
                    // Add points at each corner of the box
                    var topLeft = annotationManager.AddPoint(box.X, box.Y, currentSlice, pointLabel);
                    var topRight = annotationManager.AddPoint(box.X + box.Width, box.Y, currentSlice, pointLabel);
                    var bottomLeft = annotationManager.AddPoint(box.X, box.Y + box.Height, currentSlice, pointLabel);
                    var bottomRight = annotationManager.AddPoint(box.X + box.Width, box.Y + box.Height, currentSlice, pointLabel);

                    // Make sure all points are positive
                    if (mainForm != null && mainForm.AnnotationMgr != null)
                    {
                        AddPointToMainAnnotationManager(box.X, box.Y, currentSlice, true);
                        AddPointToMainAnnotationManager(box.X + box.Width, box.Y, currentSlice, true);
                        AddPointToMainAnnotationManager(box.X, box.Y + box.Height, currentSlice, true);
                        AddPointToMainAnnotationManager(box.X + box.Width, box.Y + box.Height, currentSlice, true);
                    }

                    Logger.Log($"[GroundingDINO] Added 4 positive corner points for box at ({box.X}, {box.Y})");
                    break;

                case "Box Outline":
                    // Add points along the perimeter of the box
                    int outlineStep = Math.Max(5, Math.Min(box.Width, box.Height) / 8);

                    // Top and bottom edges
                    for (int x = box.X; x <= box.X + box.Width; x += outlineStep)
                    {
                        annotationManager.AddPoint(x, box.Y, currentSlice, pointLabel); // Top edge
                        annotationManager.AddPoint(x, box.Y + box.Height, currentSlice, pointLabel); // Bottom edge

                        if (mainForm != null && mainForm.AnnotationMgr != null)
                        {
                            AddPointToMainAnnotationManager(x, box.Y, currentSlice, true);
                            AddPointToMainAnnotationManager(x, box.Y + box.Height, currentSlice, true);
                        }
                    }

                    // Left and right edges
                    for (int y = box.Y + outlineStep; y < box.Y + box.Height; y += outlineStep)
                    {
                        annotationManager.AddPoint(box.X, y, currentSlice, pointLabel); // Left edge
                        annotationManager.AddPoint(box.X + box.Width, y, currentSlice, pointLabel); // Right edge

                        if (mainForm != null && mainForm.AnnotationMgr != null)
                        {
                            AddPointToMainAnnotationManager(box.X, y, currentSlice, true);
                            AddPointToMainAnnotationManager(box.X + box.Width, y, currentSlice, true);
                        }
                    }

                    Logger.Log($"[GroundingDINO] Added outline points for box at ({box.X}, {box.Y})");
                    break;

                case "Weighted Grid":
                    // Create a grid of points with more weight in the center
                    int gridSize = 5; // 5x5 grid
                    for (int i = 0; i < gridSize; i++)
                    {
                        for (int j = 0; j < gridSize; j++)
                        {
                            // Calculate position in the grid
                            float x = box.X + (box.Width * i) / (float)(gridSize - 1);
                            float y = box.Y + (box.Height * j) / (float)(gridSize - 1);

                            // Determine weight: center has highest weight
                            int centerDistance = Math.Max(
                                Math.Abs(i - gridSize / 2),
                                Math.Abs(j - gridSize / 2)
                            );

                            int pointWeight = gridSize - centerDistance;

                            // Add the point multiple times based on weight
                            for (int w = 0; w < pointWeight; w++)
                            {
                                annotationManager.AddPoint(x, y, currentSlice, pointLabel);
                                if (mainForm != null && mainForm.AnnotationMgr != null)
                                {
                                    AddPointToMainAnnotationManager(x, y, currentSlice, true);
                                }
                            }
                        }
                    }

                    Logger.Log($"[GroundingDINO] Added weighted grid points for box at ({box.X}, {box.Y})");
                    break;

                case "Box Fill":
                    // Fill the entire box with points
                    int fillStep = Math.Max(4, Math.Min(box.Width, box.Height) / 10);

                    for (int x = box.X; x <= box.X + box.Width; x += fillStep)
                    {
                        for (int y = box.Y; y <= box.Y + box.Height; y += fillStep)
                        {
                            annotationManager.AddPoint(x, y, currentSlice, pointLabel);
                            if (mainForm != null && mainForm.AnnotationMgr != null)
                            {
                                AddPointToMainAnnotationManager(x, y, currentSlice, true);
                            }
                        }
                    }

                    Logger.Log($"[GroundingDINO] Added fill points for box at ({box.X}, {box.Y})");
                    break;
            }
        }

        // Helper method to ensure points are added to the main AnnotationManager and marked as positive
        private void AddPointToMainAnnotationManager(float x, float y, int z, bool isPositive)
        {
            try
            {
                if (mainForm != null && mainForm.AnnotationMgr != null)
                {
                    string labelType = isPositive ? "Positive" : "Negative";
                    string pointLabel = $"{labelType}_{selectedMaterial.Name}";

                    AnnotationPoint point = mainForm.AnnotationMgr.AddPoint(x, y, z, pointLabel);

                    // Set the point type in SAM's internal tracking
                    // This is a bit of a hack but necessary to ensure points are properly recognized as positive
                    var samClass = typeof(SegmentAnythingCT);
                    var pointTypesField = samClass.GetField("pointTypes",
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);

                    if (pointTypesField != null)
                    {
                        // Try to set the point type directly in the SAM instance
                        // This won't work until SAM is instantiated, but we'll handle that later
                        Logger.Log($"[GroundingDINO] Added point to main annotation manager: {point.ID} (Positive: {isPositive})");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GroundingDINO] Error adding point to main annotation manager: {ex.Message}");
            }
        }
    }

    internal class TokenizationResult
    {
        public DenseTensor<long> InputIds { get; set; }
        public DenseTensor<long> TokenTypeIds { get; set; }
        public DenseTensor<long> AttentionMask { get; set; }
    }
}