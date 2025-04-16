using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;

namespace CTSegmenter
{
    /// <summary>
    /// Implements object detection in CT slices using OWL-ViT model
    /// </summary>
    public class OwlVitDetector
    {
        #region Private Fields
        // UI Components
        private Form detectorForm;
        private TableLayoutPanel mainLayout;
        private Panel viewerPanel, controlPanel;
        private PictureBox imageViewer;
        private TextBox txtPrompt;
        private Button btnDetect, btnLoadModel, btnSave, btnClose;
        private ComboBox cboSlice;
        private CheckBox chkUseGPU;
        private Label statusLabel;
        private ListBox resultsListBox;
        private TrackBar thresholdSlider;
        private Label thresholdLabel;

        // Zoom and pan state variables
        private float zoom = 1.0f;
        private PointF pan = PointF.Empty;
        private HScrollBar hScroll;
        private VScrollBar vScroll;

        // Cached slices for faster rendering
        private LRUCache<int, Bitmap> sliceCache;
        private const int CACHE_SIZE = 30;

        // References to parent application components
        private MainForm mainForm;
        private AnnotationManager annotationManager;

        // ONNX model components
        private InferenceSession session;
        private string modelPath;
        private bool useGPU = true;

        // Current slice and detection state
        private int currentSlice = 0;
        private float detectionThreshold = 0.3f;
        private List<DetectionResult> detectionResults = new List<DetectionResult>();

        // Tokenization
        private Dictionary<string, int> vocab;
        private Dictionary<string, object> tokenizerConfig;
        #endregion

        #region Public Properties
        /// <summary>
        /// Gets the detection threshold value (0.0 to 1.0)
        /// </summary>
        public float DetectionThreshold
        {
            get => detectionThreshold;
            set
            {
                detectionThreshold = Math.Max(0.0f, Math.Min(1.0f, value));
                if (thresholdSlider != null)
                    thresholdSlider.Value = (int)(detectionThreshold * 100);
                if (thresholdLabel != null)
                    thresholdLabel.Text = $"Threshold: {detectionThreshold:F2}";
            }
        }

        /// <summary>
        /// Gets the list of current detection results
        /// </summary>
        public List<DetectionResult> DetectionResults => detectionResults;
        #endregion

        #region Constructor
        /// <summary>
        /// Constructor for OwlVitDetector
        /// </summary>
        /// <param name="mainForm">Reference to the main application form</param>
        /// <param name="annotationManager">The annotation manager for storing detection boxes</param>
        public OwlVitDetector(MainForm mainForm, AnnotationManager annotationManager)
        {
            Logger.Log("[OwlVitDetector] Creating OWL-ViT detector interface");
            this.mainForm = mainForm;
            this.annotationManager = annotationManager;

            // Initialize the cache for storing slice bitmaps
            sliceCache = new LRUCache<int, Bitmap>(CACHE_SIZE);

            // Set default model path
            string onnxDirectory = Path.Combine(Application.StartupPath, "ONNX");
            modelPath = Path.Combine(onnxDirectory, "owlvit.onnx");

            // Initialize UI
            InitializeForm();

            // Try to load tokenizer resources
            LoadTokenizerResources();

            // Try to load model automatically
            try
            {
                Logger.Log("[OwlVitDetector] Attempting to load OWL-ViT ONNX model");
                LoadONNXModel();
                statusLabel.Text = "Model loaded successfully";
            }
            catch (Exception ex)
            {
                Logger.Log($"[OwlVitDetector] Error loading model: {ex.Message}");
                statusLabel.Text = $"Error loading model: {ex.Message}";
            }

            // Get current slice from MainForm
            currentSlice = mainForm.CurrentSlice;
            UpdateImageDisplay();
        }
        #endregion

        #region UI Initialization
        private void InitializeForm()
        {
            Logger.Log("[OwlVitDetector] Initializing form");

            detectorForm = new Form
            {
                Text = "OWL-ViT Object Detector",
                Size = new Size(1200, 800),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.Sizable,
                Icon = mainForm.Icon
            };

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

            // Create image viewer panel
            viewerPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                BorderStyle = BorderStyle.FixedSingle
            };

            // Create image viewer with scrollbars
            imageViewer = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Normal,
                BackColor = Color.Black
            };

            hScroll = new HScrollBar
            {
                Dock = DockStyle.Bottom,
                Height = 20,
                SmallChange = 10,
                LargeChange = 50
            };

            vScroll = new VScrollBar
            {
                Dock = DockStyle.Right,
                Width = 20,
                SmallChange = 10,
                LargeChange = 50
            };

            // Add components to viewer panel
            viewerPanel.Controls.Add(imageViewer);
            viewerPanel.Controls.Add(hScroll);
            viewerPanel.Controls.Add(vScroll);

            // Create control panel
            controlPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.WhiteSmoke,
                Padding = new Padding(10),
                AutoScroll = true
            };

            // ---- Control Panel Components ----

            // Model section
            GroupBox grpModel = new GroupBox
            {
                Text = "Model Settings",
                Dock = DockStyle.Top,
                Height = 100,
                Padding = new Padding(10)
            };

            Label lblModelPath = new Label
            {
                Text = "Model Path:",
                AutoSize = true,
                Location = new Point(10, 25)
            };

            TextBox txtModelPath = new TextBox
            {
                Text = modelPath,
                Width = 250,
                Location = new Point(10, 45),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };

            Button btnBrowse = new Button
            {
                Text = "Browse...",
                Location = new Point(270, 44),
                Width = 80,
                Anchor = AnchorStyles.Right | AnchorStyles.Top
            };
            btnBrowse.Click += (s, e) => BrowseForModel();

            chkUseGPU = new CheckBox
            {
                Text = "Use GPU (if available)",
                Checked = useGPU,
                Location = new Point(10, 75),
                AutoSize = true
            };

            btnLoadModel = new Button
            {
                Text = "Load Model",
                Location = new Point(270, 70),
                Width = 80,
                Anchor = AnchorStyles.Right | AnchorStyles.Top
            };
            btnLoadModel.Click += (s, e) => LoadONNXModel();

            grpModel.Controls.AddRange(new Control[] {
                lblModelPath, txtModelPath, btnBrowse, chkUseGPU, btnLoadModel
            });

            // Slice selection section
            GroupBox grpSlice = new GroupBox
            {
                Text = "Slice Selection",
                Dock = DockStyle.Top,
                Height = 70,
                Padding = new Padding(10),
                Margin = new Padding(0, 10, 0, 0)
            };

            Label lblSlice = new Label
            {
                Text = "Select Slice:",
                AutoSize = true,
                Location = new Point(10, 25)
            };

            cboSlice = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(100, 22),
                Width = 80
            };

            // Populate slice dropdown
            if (mainForm.GetDepth() > 0)
            {
                for (int i = 0; i < mainForm.GetDepth(); i++)
                {
                    cboSlice.Items.Add(i.ToString());
                }
                cboSlice.SelectedIndex = currentSlice;
            }

            cboSlice.SelectedIndexChanged += (s, e) => {
                currentSlice = cboSlice.SelectedIndex;
                UpdateImageDisplay();
            };

            Button btnPrev = new Button
            {
                Text = "◀",
                Location = new Point(190, 22),
                Width = 40
            };
            btnPrev.Click += (s, e) => {
                if (currentSlice > 0)
                {
                    currentSlice--;
                    cboSlice.SelectedIndex = currentSlice;
                }
            };

            Button btnNext = new Button
            {
                Text = "▶",
                Location = new Point(240, 22),
                Width = 40
            };
            btnNext.Click += (s, e) => {
                if (currentSlice < mainForm.GetDepth() - 1)
                {
                    currentSlice++;
                    cboSlice.SelectedIndex = currentSlice;
                }
            };

            Button btnSync = new Button
            {
                Text = "Sync",
                Location = new Point(290, 22),
                Width = 60
            };

            // Create ToolTip component for the form
            ToolTip toolTip = new ToolTip();
            toolTip.SetToolTip(btnSync, "Sync with main view");
            btnSync.Click += (s, e) => {
                currentSlice = mainForm.CurrentSlice;
                cboSlice.SelectedIndex = currentSlice;
                UpdateImageDisplay();
            };

            grpSlice.Controls.AddRange(new Control[] {
                lblSlice, cboSlice, btnPrev, btnNext, btnSync
            });

            // Detection section
            GroupBox grpDetection = new GroupBox
            {
                Text = "Object Detection",
                Dock = DockStyle.Top,
                Height = 180,
                Padding = new Padding(10),
                Margin = new Padding(0, 10, 0, 0)
            };

            Label lblPrompt = new Label
            {
                Text = "Text Prompt:",
                AutoSize = true,
                Location = new Point(10, 25)
            };

            txtPrompt = new TextBox
            {
                Text = "pore, grain, fracture, coral, bioclast",
                Width = 340,
                Location = new Point(10, 45),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };

            thresholdLabel = new Label
            {
                Text = $"Threshold: {detectionThreshold:F2}",
                AutoSize = true,
                Location = new Point(10, 75)
            };

            thresholdSlider = new TrackBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = (int)(detectionThreshold * 100),
                TickFrequency = 10,
                Location = new Point(10, 95),
                Width = 340,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };
            thresholdSlider.ValueChanged += (s, e) => {
                detectionThreshold = thresholdSlider.Value / 100.0f;
                thresholdLabel.Text = $"Threshold: {detectionThreshold:F2}";
                // Redraw with new threshold if we have results
                if (detectionResults.Count > 0)
                {
                    UpdateImageDisplay();
                }
            };

            btnDetect = new Button
            {
                Text = "Detect Objects",
                Location = new Point(10, 135),
                Width = 120,
                Height = 30
            };
            btnDetect.Click += async (s, e) => await DetectObjects();

            grpDetection.Controls.AddRange(new Control[] {
                lblPrompt, txtPrompt, thresholdLabel, thresholdSlider, btnDetect
            });

            // Results section
            GroupBox grpResults = new GroupBox
            {
                Text = "Detection Results",
                Dock = DockStyle.Top,
                Height = 200,
                Padding = new Padding(10),
                Margin = new Padding(0, 10, 0, 0)
            };

            resultsListBox = new ListBox
            {
                Dock = DockStyle.Fill,
                SelectionMode = SelectionMode.MultiExtended,
                DisplayMember = "DisplayText"
            };

            resultsListBox.SelectedIndexChanged += (s, e) => {
                // Highlight selected detections
                UpdateImageDisplay();
            };

            grpResults.Controls.Add(resultsListBox);

            // Actions section
            GroupBox grpActions = new GroupBox
            {
                Text = "Actions",
                Dock = DockStyle.Top,
                Height = 100,
                Padding = new Padding(10),
                Margin = new Padding(0, 10, 0, 0)
            };

            btnSave = new Button
            {
                Text = "Save Annotations",
                Location = new Point(10, 25),
                Width = 160,
                Height = 30
            };
            btnSave.Click += (s, e) => SaveDetectionsAsAnnotations();

            Button btnClear = new Button
            {
                Text = "Clear Results",
                Location = new Point(180, 25),
                Width = 160,
                Height = 30,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnClear.Click += (s, e) => {
                detectionResults.Clear();
                resultsListBox.Items.Clear();
                UpdateImageDisplay();
            };

            btnClose = new Button
            {
                Text = "Close",
                Location = new Point(10, 65),
                Width = 330,
                Height = 25,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };
            btnClose.Click += (s, e) => detectorForm.Close();

            grpActions.Controls.AddRange(new Control[] {
                btnSave, btnClear, btnClose
            });

            // Status section
            statusLabel = new Label
            {
                Text = "Ready",
                Dock = DockStyle.Bottom,
                Height = 20,
                BorderStyle = BorderStyle.Fixed3D,
                TextAlign = ContentAlignment.MiddleLeft
            };

            // Add all sections to control panel
            controlPanel.Controls.Add(grpActions);
            controlPanel.Controls.Add(grpResults);
            controlPanel.Controls.Add(grpDetection);
            controlPanel.Controls.Add(grpSlice);
            controlPanel.Controls.Add(grpModel);

            // Spacer panel to push everything up in the scrollable panel
            Panel spacer = new Panel
            {
                Height = 20,
                Dock = DockStyle.Top
            };
            controlPanel.Controls.Add(spacer);

            // Add main components to layout
            mainLayout.Controls.Add(viewerPanel, 0, 0);
            mainLayout.Controls.Add(controlPanel, 1, 0);

            // Add layout and status label to form
            detectorForm.Controls.Add(mainLayout);
            detectorForm.Controls.Add(statusLabel);

            // Set up viewer events
            SetupViewerEvents();

            // Handle form closing
            detectorForm.FormClosing += (s, e) => {
                // Clean up resources
                session?.Dispose();
                ClearCache();
            };

            Logger.Log("[OwlVitDetector] Form initialized");
        }

        private void SetupViewerEvents()
        {
            // Scroll events
            hScroll.Scroll += (s, e) => {
                pan.X = -hScroll.Value;
                imageViewer.Invalidate();
            };

            vScroll.Scroll += (s, e) => {
                pan.Y = -vScroll.Value;
                imageViewer.Invalidate();
            };

            // Mouse wheel for zooming
            imageViewer.MouseWheel += (s, e) => {
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
                Logger.Log($"[OwlVitDetector] Zoom changed to {zoom:F2}");
            };

            // Mouse events for panning
            Point lastPos = Point.Empty;
            bool isPanning = false;

            imageViewer.MouseDown += (s, e) => {
                if (e.Button == MouseButtons.Right)
                {
                    // Start panning with right mouse button
                    isPanning = true;
                    lastPos = e.Location;
                }
            };

            imageViewer.MouseMove += (s, e) => {
                if (isPanning && e.Button == MouseButtons.Right)
                {
                    // Calculate the move delta
                    int dx = e.X - lastPos.X;
                    int dy = e.Y - lastPos.Y;

                    // Update the pan position
                    pan.X += dx;
                    pan.Y += dy;
                    UpdateScrollbars();

                    lastPos = e.Location;
                    imageViewer.Invalidate();
                }
            };

            imageViewer.MouseUp += (s, e) => {
                if (e.Button == MouseButtons.Right)
                {
                    isPanning = false;
                }
            };

            // Paint event for custom rendering
            imageViewer.Paint += (s, e) => {
                // Clear background
                e.Graphics.Clear(Color.Black);

                if (imageViewer.Image != null)
                {
                    int imgWidth = imageViewer.Image.Width;
                    int imgHeight = imageViewer.Image.Height;

                    // Calculate the image bounds
                    Rectangle imageBounds = new Rectangle(
                        (int)pan.X,
                        (int)pan.Y,
                        (int)(imgWidth * zoom),
                        (int)(imgHeight * zoom));

                    // Draw checkerboard pattern for transparency
                    DrawCheckerboardBackground(e.Graphics, imageViewer.ClientRectangle);

                    // Draw the image with interpolation
                    e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    e.Graphics.DrawImage(imageViewer.Image, imageBounds);

                    // Draw detection boxes
                    DrawDetectionBoxes(e.Graphics, imageBounds);
                }
            };
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

        private void DrawDetectionBoxes(Graphics g, Rectangle imageBounds)
        {
            if (detectionResults == null || detectionResults.Count == 0)
                return;

            // Calculate scale factors
            float scaleX = imageBounds.Width / (float)mainForm.GetWidth();
            float scaleY = imageBounds.Height / (float)mainForm.GetHeight();

            // Get selected results indices
            var selectedIndices = resultsListBox.SelectedIndices.Cast<int>().ToList();

            // Draw all detection boxes
            for (int i = 0; i < detectionResults.Count; i++)
            {
                var result = detectionResults[i];

                // Skip results below threshold
                if (result.Confidence < detectionThreshold)
                    continue;

                // Convert normalized coordinates to pixel coordinates
                int x = (int)(result.X * mainForm.GetWidth() * scaleX + imageBounds.X);
                int y = (int)(result.Y * mainForm.GetHeight() * scaleY + imageBounds.Y);
                int width = (int)(result.Width * mainForm.GetWidth() * scaleX);
                int height = (int)(result.Height * mainForm.GetHeight() * scaleY);

                // Set colors based on selection state and category
                Color boxColor = GetColorForCategory(result.Category);
                int penWidth = selectedIndices.Contains(i) ? 3 : 2;

                // Draw the bounding box
                using (Pen pen = new Pen(boxColor, penWidth))
                {
                    g.DrawRectangle(pen, x, y, width, height);
                }

                // Draw label with confidence
                string label = $"{result.Category} ({result.Confidence:P1})";

                // Create shadow effect for better visibility
                using (Font font = new Font("Arial", 9, FontStyle.Bold))
                using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(180, Color.Black)))
                using (SolidBrush textBrush = new SolidBrush(boxColor))
                {
                    // Measure text to create background
                    SizeF textSize = g.MeasureString(label, font);

                    // Draw text background
                    g.FillRectangle(shadowBrush, x, y - textSize.Height, textSize.Width, textSize.Height);

                    // Draw text
                    g.DrawString(label, font, textBrush, x, y - textSize.Height);
                }
            }
        }

        private Color GetColorForCategory(string category)
        {
            // Generate a consistent color based on the category name
            int hash = category.GetHashCode();

            // Use the hash to generate a color, but avoid too dark or too light colors
            int r = ((hash & 0xFF0000) >> 16) % 200 + 50;
            int g = ((hash & 0x00FF00) >> 8) % 200 + 50;
            int b = (hash & 0x0000FF) % 200 + 50;

            return Color.FromArgb(255, r, g, b);
        }

        private void UpdateScrollbars()
        {
            if (imageViewer.Image != null)
            {
                int imageWidth = (int)(imageViewer.Image.Width * zoom);
                int imageHeight = (int)(imageViewer.Image.Height * zoom);

                hScroll.Maximum = Math.Max(0, imageWidth - imageViewer.ClientSize.Width + hScroll.LargeChange);
                vScroll.Maximum = Math.Max(0, imageHeight - imageViewer.ClientSize.Height + vScroll.LargeChange);

                hScroll.Value = Math.Min(hScroll.Maximum, -pan.X < 0 ? 0 : (int)-pan.X);
                vScroll.Value = Math.Min(vScroll.Maximum, -pan.Y < 0 ? 0 : (int)-pan.Y);
            }
        }
        #endregion

        #region Image Processing and Display
        private unsafe Bitmap CreateSliceBitmap(int sliceZ)
        {
            // Try to get from cache first
            Bitmap cachedBitmap = sliceCache.Get(sliceZ);
            if (cachedBitmap != null)
            {
                // Return a copy of the cached bitmap
                return new Bitmap(cachedBitmap);
            }

            // Create a new bitmap
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

            // Add to cache
            Bitmap cacheCopy = new Bitmap(bmp);
            sliceCache.Add(sliceZ, cacheCopy);

            return bmp;
        }

        private void UpdateImageDisplay()
        {
            if (imageViewer == null)
                return;

            try
            {
                using (Bitmap sliceBitmap = CreateSliceBitmap(currentSlice))
                {
                    if (imageViewer.Image != null)
                        imageViewer.Image.Dispose();

                    imageViewer.Image = new Bitmap(sliceBitmap);
                    UpdateScrollbars();
                    imageViewer.Invalidate();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[OwlVitDetector] Error updating display: {ex.Message}");
                statusLabel.Text = $"Error updating display: {ex.Message}";
            }
        }

        private void ClearCache()
        {
            // Dispose all bitmaps in the cache
            foreach (var key in sliceCache.GetKeys())
            {
                var bitmap = sliceCache.Get(key);
                bitmap?.Dispose();
            }

            sliceCache.Clear();
            Logger.Log("[OwlVitDetector] Slice cache cleared");
        }
        #endregion

        #region Model Loading and Inference
        private void BrowseForModel()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Filter = "ONNX Models (*.onnx)|*.onnx|All Files (*.*)|*.*";
                dialog.Title = "Select OWL-ViT ONNX Model";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    modelPath = dialog.FileName;

                    // Update textbox if it exists
                    foreach (Control control in controlPanel.Controls)
                    {
                        if (control is GroupBox grp && grp.Text == "Model Settings")
                        {
                            foreach (Control c in grp.Controls)
                            {
                                if (c is TextBox txt)
                                {
                                    txt.Text = modelPath;
                                    break;
                                }
                            }
                            break;
                        }
                    }

                    Logger.Log($"[OwlVitDetector] Model path set to: {modelPath}");
                }
            }
        }

        private void LoadONNXModel()
        {
            try
            {
                // Dispose existing session if any
                session?.Dispose();

                // Verify model file exists
                if (!File.Exists(modelPath))
                {
                    string errorMsg = $"Model not found at: {modelPath}";
                    MessageBox.Show(errorMsg, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Logger.Log($"[OwlVitDetector] {errorMsg}");
                    return;
                }

                // Get GPU preference
                useGPU = chkUseGPU.Checked;

                // Create session options with enhanced performance settings
                SessionOptions options = new SessionOptions();

                // Set graph optimization level to maximum
                options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

                // Enable parallel execution
                options.EnableMemoryPattern = true;
                options.EnableCpuMemArena = true;

                // Set intra-op thread count for CPU 
                int cpuThreads = Environment.ProcessorCount;
                options.IntraOpNumThreads = Math.Max(1, cpuThreads / 2);

                if (useGPU)
                {
                    try
                    {
                        // Configure for GPU with optimized settings
                        options.AppendExecutionProvider_CUDA();
                        Logger.Log("[OwlVitDetector] Using GPU execution provider with optimized settings");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[OwlVitDetector] GPU not available, falling back to CPU: {ex.Message}");
                        useGPU = false;

                        // Update checkbox if needed
                        chkUseGPU.Checked = false;
                    }
                }
                else
                {
                    Logger.Log("[OwlVitDetector] Using optimized CPU execution provider");
                }

                // Create session with optimized settings
                session = new InferenceSession(modelPath, options);

                // Log model information
                var inputMetadata = session.InputMetadata;
                var outputMetadata = session.OutputMetadata;

                Logger.Log($"[OwlVitDetector] Model loaded successfully with {inputMetadata.Count} inputs and {outputMetadata.Count} outputs");
                foreach (var input in inputMetadata)
                {
                    Logger.Log($"[OwlVitDetector] Input: {input.Key} - {string.Join(",", input.Value.Dimensions)}");
                }
                foreach (var output in outputMetadata)
                {
                    Logger.Log($"[OwlVitDetector] Output: {output.Key} - {string.Join(",", output.Value.Dimensions)}");
                }

                statusLabel.Text = $"Model loaded successfully. Using {(useGPU ? "GPU" : "CPU")}.";
            }
            catch (Exception ex)
            {
                string errorMsg = $"Error loading model: {ex.Message}";
                MessageBox.Show(errorMsg, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[OwlVitDetector] {errorMsg}");
                statusLabel.Text = errorMsg;
            }
        }

        

        private async Task DetectObjects()
        {
            if (session == null)
            {
                MessageBox.Show("Please load the model first.", "Model Not Loaded", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(txtPrompt.Text))
            {
                MessageBox.Show("Please enter a text prompt.", "No Prompt", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Start detection process
            statusLabel.Text = "Detecting objects...";
            btnDetect.Enabled = false;
            detectionResults.Clear();
            resultsListBox.Items.Clear();

            try
            {
                // Get the text prompt
                string prompt = txtPrompt.Text.Trim();

                // Split into multiple queries if comma-separated
                string[] queries = prompt.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                         .Select(q => q.Trim())
                                         .ToArray();

                Logger.Log($"[OwlVitDetector] Running detection with {queries.Length} text queries: {string.Join(", ", queries)}");

                // Preprocess image
                DenseTensor<float> imageInput = await Task.Run(() => PreprocessImage(currentSlice));

                // Process each query separately to track which category produced which result
                foreach (string query in queries)
                {
                    // Tokenize text
                    var tokenInputs = TokenizeText(query);

                    // Run inference
                    var results = await Task.Run(() => RunInference(imageInput, tokenInputs.inputIds, tokenInputs.attentionMask));

                    // Process results
                    ProcessResults(results.logits, results.predBoxes, query);

                    // Update progress
                    statusLabel.Text = $"Processed query: {query}";
                }

                // Sort results by confidence
                detectionResults = detectionResults.OrderByDescending(r => r.Confidence).ToList();

                // Update results list
                UpdateResultsList();

                // Update display
                UpdateImageDisplay();

                int count = detectionResults.Count(r => r.Confidence >= detectionThreshold);
                statusLabel.Text = $"Detection complete. Found {count} objects above threshold.";
                Logger.Log($"[OwlVitDetector] Detection complete. Found {count} objects above threshold.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during detection: {ex.Message}", "Detection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[OwlVitDetector] Detection error: {ex.Message}");
                statusLabel.Text = $"Error: {ex.Message}";
            }
            finally
            {
                btnDetect.Enabled = true;
            }
        }

        private unsafe DenseTensor<float> PreprocessImage(int sliceZ)
        {
            using (Bitmap sliceBitmap = CreateSliceBitmap(sliceZ))
            {
                // Create a tensor with shape [1, 3, 768, 768] - OWL-ViT typically uses 768x768
                DenseTensor<float> inputTensor = new DenseTensor<float>(new[] { 1, 3, 768, 768 });

                // Create a resized version of the slice
                using (Bitmap resized = new Bitmap(768, 768))
                {
                    using (Graphics g = Graphics.FromImage(resized))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(sliceBitmap, 0, 0, 768, 768);
                    }

                    // Lock the bitmap and access its pixel data
                    BitmapData bmpData = resized.LockBits(
                        new Rectangle(0, 0, resized.Width, resized.Height),
                        ImageLockMode.ReadOnly,
                        PixelFormat.Format24bppRgb);

                    int stride = bmpData.Stride;
                    int bytesPerPixel = 3; // RGB

                    byte* ptr = (byte*)bmpData.Scan0;

                    // Process pixels and normalize using ImageNet mean and std
                    float[] mean = new float[] { 0.485f, 0.456f, 0.406f };
                    float[] std = new float[] { 0.229f, 0.224f, 0.225f };

                    for (int y = 0; y < 768; y++)
                    {
                        for (int x = 0; x < 768; x++)
                        {
                            int offset = y * stride + x * bytesPerPixel;

                            // BGR order (standard in Bitmap)
                            byte b = ptr[offset];
                            byte g = ptr[offset + 1];
                            byte r = ptr[offset + 2];

                            // Normalize to range [0,1] and then apply mean/std
                            inputTensor[0, 0, y, x] = (r / 255.0f - mean[0]) / std[0];
                            inputTensor[0, 1, y, x] = (g / 255.0f - mean[1]) / std[1];
                            inputTensor[0, 2, y, x] = (b / 255.0f - mean[2]) / std[2];
                        }
                    }

                    resized.UnlockBits(bmpData);
                    return inputTensor;
                }
            }
        }

        #region Tokenization
        // Constants for the CLIP text encoder
        private const int MaxTokenLength = 77; // Standard CLIP context length
        private CLIPTokenizer clipTokenizer;

        /// <summary>
        /// Loads the tokenizer resources and initializes the tokenizer
        /// </summary>
        private void LoadTokenizerResources()
        {
            try
            {
                string modelDir = Path.GetDirectoryName(modelPath);

                // Load vocabulary
                string vocabPath = Path.Combine(modelDir, "vocab.json");
                if (!File.Exists(vocabPath))
                {
                    Logger.Log("[OwlVitDetector] Error: vocab.json not found");
                    return;
                }

                // Load tokenizer config
                string tokenizerConfigPath = Path.Combine(modelDir, "tokenizer_config.json");
                if (!File.Exists(tokenizerConfigPath))
                {
                    Logger.Log("[OwlVitDetector] Warning: tokenizer_config.json not found, using default settings");
                }

                // Initialize the CLIP tokenizer
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                };

                // Load vocab.json
                string vocabJson = File.ReadAllText(vocabPath);
                var vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(vocabJson, options);

                // Load tokenizer config if available
                TokenizerConfig config = null;
                if (File.Exists(tokenizerConfigPath))
                {
                    string configJson = File.ReadAllText(tokenizerConfigPath);
                    config = JsonSerializer.Deserialize<TokenizerConfig>(configJson, options);
                }

                // Create the tokenizer
                clipTokenizer = new CLIPTokenizer(vocab, config);
                Logger.Log($"[OwlVitDetector] CLIP tokenizer initialized with {vocab.Count} tokens");
            }
            catch (Exception ex)
            {
                Logger.Log($"[OwlVitDetector] Error loading tokenizer resources: {ex.Message}");
            }
        }

        /// <summary>
        /// Tokenizes input text for the OWL-ViT model
        /// </summary>
        /// <param name="text">The text prompt to tokenize</param>
        /// <returns>Tuple of input IDs and attention mask tensors</returns>
        private (DenseTensor<long> inputIds, DenseTensor<long> attentionMask) TokenizeText(string text)
        {
            // Create output tensors
            DenseTensor<long> inputIds = new DenseTensor<long>(new[] { 1, MaxTokenLength });
            DenseTensor<long> attentionMask = new DenseTensor<long>(new[] { 1, MaxTokenLength });

            try
            {
                // Initialize tokenizer if needed
                if (clipTokenizer == null)
                {
                    LoadTokenizerResources();
                }

                if (clipTokenizer != null)
                {
                    // Encode the text with the CLIP tokenizer
                    var encoding = clipTokenizer.Encode(text, MaxTokenLength);

                    // Copy the results to the tensors
                    for (int i = 0; i < encoding.InputIds.Count && i < MaxTokenLength; i++)
                    {
                        inputIds[0, i] = encoding.InputIds[i];
                        attentionMask[0, i] = encoding.AttentionMask[i];
                    }

                    Logger.Log($"[OwlVitDetector] Tokenized text: '{text}' to {encoding.InputIds.Count} tokens");
                    return (inputIds, attentionMask);
                }
                else
                {
                    // Fallback to simplified tokenization
                    return FallbackTokenization(text);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[OwlVitDetector] Tokenization error: {ex.Message}");
                return FallbackTokenization(text);
            }
        }

        /// <summary>
        /// Fallback tokenization method when the main tokenizer fails
        /// </summary>
        private (DenseTensor<long> inputIds, DenseTensor<long> attentionMask) FallbackTokenization(string text)
        {
            Logger.Log("[OwlVitDetector] Using fallback tokenization");

            // Create output tensors
            DenseTensor<long> inputIds = new DenseTensor<long>(new[] { 1, MaxTokenLength });
            DenseTensor<long> attentionMask = new DenseTensor<long>(new[] { 1, MaxTokenLength });

            // First token is BOS
            inputIds[0, 0] = 49406;  // Beginning of sentence token for CLIP
            attentionMask[0, 0] = 1;

            if (vocab != null && vocab.Count > 0)
            {
                // Normalize the text (lowercase, trim)
                text = text.ToLower().Trim();

                // Simple whitespace tokenization for fallback
                string[] words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int position = 1;

                foreach (string word in words)
                {
                    if (position >= MaxTokenLength - 1)
                        break;

                    // Try to find the word in vocab
                    if (vocab.TryGetValue(word, out int tokenId))
                    {
                        inputIds[0, position] = tokenId;
                        attentionMask[0, position] = 1;
                        position++;
                    }
                    else
                    {
                        // If word not found, try character by character
                        foreach (char c in word)
                        {
                            if (position >= MaxTokenLength - 1)
                                break;

                            string charStr = c.ToString();
                            if (vocab.TryGetValue(charStr, out tokenId))
                            {
                                inputIds[0, position] = tokenId;
                                attentionMask[0, position] = 1;
                                position++;
                            }
                        }
                    }
                }

                // End with EOS token
                if (position < MaxTokenLength)
                {
                    inputIds[0, position] = 49407;  // End of sentence token for CLIP
                    attentionMask[0, position] = 1;
                    position++;
                }
            }
            else
            {
                // Very simplified approach if vocab is not available

                // Last token that fits is EOS
                inputIds[0, MaxTokenLength - 1] = 49407;
                attentionMask[0, MaxTokenLength - 1] = 1;

                // Set all positions to 1 in attention mask
                for (int i = 0; i < MaxTokenLength; i++)
                {
                    attentionMask[0, i] = 1;
                }
            }

            return (inputIds, attentionMask);
        }
        #endregion

        private (Tensor<float> logits, Tensor<float> predBoxes) RunInference(
            DenseTensor<float> imageInput,
            DenseTensor<long> inputIds,
            DenseTensor<long> attentionMask)
        {
            // Create input name mapping
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("pixel_values", imageInput),
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask)
            };

            // Run inference
            Stopwatch stopwatch = Stopwatch.StartNew();
            var outputs = session.Run(inputs);
            stopwatch.Stop();

            Logger.Log($"[OwlVitDetector] Inference completed in {stopwatch.ElapsedMilliseconds}ms");

            try
            {
                // Get outputs
                var logits = outputs.FirstOrDefault(x => x.Name == "logits")?.AsTensor<float>();
                var predBoxes = outputs.FirstOrDefault(x => x.Name == "pred_boxes")?.AsTensor<float>();

                if (logits == null || predBoxes == null)
                {
                    throw new Exception("Required outputs not found in model output");
                }

                return (logits, predBoxes);
            }
            finally
            {
                // Dispose outputs to free memory
                foreach (var output in outputs)
                {
                    output.Dispose();
                }
            }
        }

        private void ProcessResults(Tensor<float> logits, Tensor<float> predBoxes, string category)
        {
            // Get dimensions
            int numBoxes = predBoxes.Dimensions[1];

            // Calculate logistic sigmoid to get confidence scores
            List<DetectionResult> results = new List<DetectionResult>();

            for (int i = 0; i < numBoxes; i++)
            {
                float confidence = 1.0f / (1.0f + (float)Math.Exp(-logits[0, 0, i]));

                // Skip very low confidence detections
                if (confidence < 0.05f)
                    continue;

                // Get box coordinates (center_x, center_y, width, height)
                float centerX = predBoxes[0, i, 0];
                float centerY = predBoxes[0, i, 1];
                float width = predBoxes[0, i, 2];
                float height = predBoxes[0, i, 3];

                // Convert from center coordinates to top-left coordinates
                float x = centerX - width / 2;
                float y = centerY - height / 2;

                // Create result object
                DetectionResult result = new DetectionResult
                {
                    Category = category,
                    Confidence = confidence,
                    X = x,
                    Y = y,
                    Width = width,
                    Height = height,
                    Slice = currentSlice
                };

                results.Add(result);
            }

            // Add results to the master list
            detectionResults.AddRange(results);

            Logger.Log($"[OwlVitDetector] Found {results.Count} detections for category '{category}'");
        }

        private void UpdateResultsList()
        {
            resultsListBox.Items.Clear();

            // Add all results that meet threshold
            foreach (var result in detectionResults)
            {
                if (result.Confidence >= detectionThreshold)
                {
                    resultsListBox.Items.Add(result);
                }
            }

            // Update count in form title
            detectorForm.Text = $"OWL-ViT Object Detector - {resultsListBox.Items.Count} detections";
        }
        #endregion

        #region Annotations
        private void SaveDetectionsAsAnnotations()
        {
            if (detectionResults.Count == 0 || resultsListBox.Items.Count == 0)
            {
                MessageBox.Show("No detection results to save.", "No Results", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                int count = 0;

                // Create a dictionary to track materials by category
                Dictionary<string, Material> materialsByCategory = new Dictionary<string, Material>();

                // Get current slice
                int slice = currentSlice;

                // Process each result that meets threshold
                foreach (var result in detectionResults)
                {
                    if (result.Confidence < detectionThreshold)
                        continue;

                    // Get or create material for this category
                    if (!materialsByCategory.TryGetValue(result.Category, out Material material))
                    {
                        // Try to find existing material with this name
                        material = mainForm.Materials.FirstOrDefault(m => m.Name.Contains(result.Category));

                        if (material == null)
                        {
                            // Create new material
                            Color color = GetColorForCategory(result.Category);
                            material = new Material(
                                $"OWLViT_{result.Category}",
                                color,
                                0, 255,
                                mainForm.GetNextMaterialID());

                            mainForm.Materials.Add(material);
                        }

                        materialsByCategory[result.Category] = material;
                    }

                    // Convert normalized coordinates to pixel coordinates
                    float x1 = result.X * mainForm.GetWidth();
                    float y1 = result.Y * mainForm.GetHeight();
                    float x2 = (result.X + result.Width) * mainForm.GetWidth();
                    float y2 = (result.Y + result.Height) * mainForm.GetHeight();

                    // Ensure coordinates are within bounds
                    x1 = Math.Max(0, Math.Min(x1, mainForm.GetWidth() - 1));
                    y1 = Math.Max(0, Math.Min(y1, mainForm.GetHeight() - 1));
                    x2 = Math.Max(0, Math.Min(x2, mainForm.GetWidth() - 1));
                    y2 = Math.Max(0, Math.Min(y2, mainForm.GetHeight() - 1));

                    // Add annotation box
                    string label = $"{result.Category}_{result.Confidence:P0}";
                    annotationManager.AddBox(x1, y1, x2, y2, slice, label);
                    count++;
                }

                // Save materials to disk
                mainForm.SaveLabelsChk();

                // Update views
                mainForm.RenderViews();

                MessageBox.Show($"Successfully saved {count} annotations.", "Save Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Logger.Log($"[OwlVitDetector] Saved {count} annotations to the annotation manager");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving annotations: {ex.Message}", "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[OwlVitDetector] Error saving annotations: {ex.Message}");
            }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Shows the detector form
        /// </summary>
        public void Show()
        {
            detectorForm.Show();
        }

        /// <summary>
        /// Run detection with the specified prompt on the current slice
        /// </summary>
        /// <param name="prompt">Text prompt for detection</param>
        /// <returns>List of detection results</returns>
        public async Task<List<DetectionResult>> DetectObjectsAsync(string prompt)
        {
            if (session == null)
            {
                throw new InvalidOperationException("Model not loaded");
            }

            try
            {
                // Set the prompt
                txtPrompt.Text = prompt;

                // Clear previous results
                detectionResults.Clear();

                // Split into multiple queries if comma-separated
                string[] queries = prompt.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                         .Select(q => q.Trim())
                                         .ToArray();

                Logger.Log($"[OwlVitDetector] Running detection with {queries.Length} text queries: {string.Join(", ", queries)}");

                // Preprocess image
                DenseTensor<float> imageInput = await Task.Run(() => PreprocessImage(currentSlice));

                // Process each query separately
                foreach (string query in queries)
                {
                    // Tokenize text
                    var tokenInputs = TokenizeText(query);

                    // Run inference
                    var results = await Task.Run(() => RunInference(imageInput, tokenInputs.inputIds, tokenInputs.attentionMask));

                    // Process results
                    ProcessResults(results.logits, results.predBoxes, query);
                }

                // Sort results by confidence
                detectionResults = detectionResults.OrderByDescending(r => r.Confidence).ToList();

                return detectionResults;
            }
            catch (Exception ex)
            {
                Logger.Log($"[OwlVitDetector] Detection error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Sets the current slice and updates the display
        /// </summary>
        /// <param name="slice">Slice index</param>
        public void SetSlice(int slice)
        {
            if (slice >= 0 && slice < mainForm.GetDepth())
            {
                currentSlice = slice;

                // Update UI if needed
                if (cboSlice != null && cboSlice.Items.Count > slice)
                {
                    cboSlice.SelectedIndex = slice;
                }
                else
                {
                    UpdateImageDisplay();
                }
            }
        }
        #endregion

        #region Helper Classes
        /// <summary>
        /// Represents a detection result
        /// </summary>
        public class DetectionResult
        {
            /// <summary>
            /// The detected category/class name
            /// </summary>
            public string Category { get; set; }

            /// <summary>
            /// Confidence score (0.0 to 1.0)
            /// </summary>
            public float Confidence { get; set; }

            /// <summary>
            /// Normalized X coordinate (0.0 to 1.0)
            /// </summary>
            public float X { get; set; }

            /// <summary>
            /// Normalized Y coordinate (0.0 to 1.0)
            /// </summary>
            public float Y { get; set; }

            /// <summary>
            /// Normalized width (0.0 to 1.0)
            /// </summary>
            public float Width { get; set; }

            /// <summary>
            /// Normalized height (0.0 to 1.0)
            /// </summary>
            public float Height { get; set; }

            /// <summary>
            /// Slice index where the detection was found
            /// </summary>
            public int Slice { get; set; }

            /// <summary>
            /// Text representation for display in UI
            /// </summary>
            public string DisplayText => $"{Category} ({Confidence:P1})";
        }

        /// <summary>
        /// Simple LRU (Least Recently Used) cache implementation
        /// </summary>
        private class LRUCache<TKey, TValue>
        {
            private readonly int capacity;
            private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> cacheMap;
            private readonly LinkedList<KeyValuePair<TKey, TValue>> lruList;

            public LRUCache(int capacity)
            {
                this.capacity = capacity;
                this.cacheMap = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>(capacity);
                this.lruList = new LinkedList<KeyValuePair<TKey, TValue>>();
            }

            public TValue Get(TKey key)
            {
                if (!cacheMap.TryGetValue(key, out LinkedListNode<KeyValuePair<TKey, TValue>> node))
                    return default;

                // Move accessed node to front of LRU list
                lruList.Remove(node);
                lruList.AddFirst(node);
                return node.Value.Value;
            }

            public void Add(TKey key, TValue value)
            {
                if (cacheMap.TryGetValue(key, out LinkedListNode<KeyValuePair<TKey, TValue>> existingNode))
                {
                    // Update existing item
                    lruList.Remove(existingNode);
                    lruList.AddFirst(new KeyValuePair<TKey, TValue>(key, value));
                    cacheMap[key] = lruList.First;
                    return;
                }

                // If at capacity, remove least recently used item
                if (cacheMap.Count >= capacity)
                {
                    cacheMap.Remove(lruList.Last.Value.Key);
                    lruList.RemoveLast();
                }

                // Add new item
                lruList.AddFirst(new KeyValuePair<TKey, TValue>(key, value));
                cacheMap[key] = lruList.First;
            }

            public void Clear()
            {
                cacheMap.Clear();
                lruList.Clear();
            }

            public List<TKey> GetKeys()
            {
                return cacheMap.Keys.ToList();
            }
        }
        #endregion
    }
}