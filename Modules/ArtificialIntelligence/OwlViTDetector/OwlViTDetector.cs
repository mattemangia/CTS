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

        /// <summary>
        /// Determines if a term is likely a geological domain-specific term for CT analysis
        /// </summary>
        private bool IsCTDomainTerm(string term)
        {
            // Convert input to lowercase for case-insensitive comparison
            string lowerTerm = term.ToLower();

            // List of common geological terms for CT analysis
            string[] geologicalTerms = new string[] {
        // Rock types
        "limestone", "sandstone", "shale", "granite", "basalt", "quartzite", "dolomite",
        "mudstone", "siltstone", "schist", "gneiss", "marble", "slate", "andesite",
        
        // Minerals
        "quartz", "feldspar", "mica", "calcite", "dolomite", "pyrite", "clay",
        "gypsum", "halite", "sylvite", "apatite", "fluorite", "aragonite",
        
        // Fossil components
        "fossil", "shell", "skeleton", "coral", "bioclast", "bone", "tooth",
        "foraminifera", "stromatolite", "burrow", "trace", "ostracod", "brachiopod",
        
        // Geological features
        "pore", "grain", "crystal", "fracture", "vein", "fault", "clast",
        "cement", "matrix", "nodule", "concretion", "vugs", "stylolite", "lamination",
        
        // Porosity and permeability terms
        "porosity", "permeability", "void", "cavity", "channel", "porous", "permeable",
        "micropore", "macropore", "intergranular", "intragranular", "moldic",
        
        // Textures and structures
        "bedding", "laminae", "sorting", "graded", "massive", "brecciated", "foliated",
        "vesicular", "amygdaloidal", "oolitic", "pelletal", "bioturbated", "cross-bedded",
        
        // Common descriptors in geological CT
        "boundary", "contact", "interface", "dense", "porous", "crystalline", "amorphous",
        "heterogeneous", "homogeneous", "inclusions", "zonation", "alteration", "weathering"
    };

            // Use simple string contains after converting both strings to lowercase
            return geologicalTerms.Any(t => lowerTerm.Contains(t.ToLower()));
        }
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
        /// <summary>
        /// Process detection results for a specific query variant
        /// </summary>
        private void ProcessResultsForVariant(
            Tensor<float> logits,
            Tensor<float> predBoxes,
            string queryVariant,
            string baseQuery,
            float workingThreshold,
            List<DetectionResult> results)
        {
            try
            {
                // Log tensor shapes for debugging
                Logger.Log($"[OwlVitDetector] Processing variant results for '{queryVariant}'");
                Logger.Log($"[OwlVitDetector] - logits shape: {DimensionsToString(logits.Dimensions)}");
                Logger.Log($"[OwlVitDetector] - pred_boxes shape: {DimensionsToString(predBoxes.Dimensions)}");

                // Get dimensions from tensors
                int numBoxes = 0;
                int numQueries = 1; // Default if we can't determine from logits

                // Handle different output formats based on the model
                if (logits.Dimensions.Length == 3)
                {
                    // Format: [batch, boxes, queries]
                    numBoxes = logits.Dimensions[1];
                    numQueries = logits.Dimensions[2];
                    Logger.Log($"[OwlVitDetector] Multi-query format detected with {numQueries} queries");
                }
                else if (logits.Dimensions.Length == 2)
                {
                    // Format: [batch, boxes]
                    numBoxes = logits.Dimensions[1];
                    Logger.Log("[OwlVitDetector] Single-query format detected");
                }
                else
                {
                    // Unknown format, try to infer from pred_boxes
                    if (predBoxes.Dimensions.Length >= 2)
                    {
                        numBoxes = predBoxes.Dimensions[1];
                        Logger.Log($"[OwlVitDetector] Unknown logits format, inferring {numBoxes} boxes from pred_boxes");
                    }
                    else
                    {
                        throw new Exception($"Unsupported output tensor format: logits shape {DimensionsToString(logits.Dimensions)}");
                    }
                }

                // Track all detections above the working threshold
                List<(int index, float confidence, float[] bbox)> potentialDetections = new List<(int index, float confidence, float[] bbox)>();

                // Process each prediction box
                for (int i = 0; i < numBoxes; i++)
                {
                    float confidence = 0;

                    // Calculate confidence based on tensor format
                    if (logits.Dimensions.Length == 3)
                    {
                        // For multi-query, use max across all queries
                        for (int q = 0; q < numQueries; q++)
                        {
                            float logitValue = logits[0, i, q];
                            float score = 1.0f / (1.0f + (float)Math.Exp(-logitValue)); // Sigmoid
                            confidence = Math.Max(confidence, score);
                        }
                    }
                    else
                    {
                        // For single-query format
                        float logitValue = logits[0, i];
                        confidence = 1.0f / (1.0f + (float)Math.Exp(-logitValue)); // Sigmoid
                    }

                    // Save potential detections with confidence above working threshold
                    if (confidence >= workingThreshold)
                    {
                        float[] bbox = new float[4];
                        if (predBoxes.Dimensions.Length == 3 && predBoxes.Dimensions[2] >= 4)
                        {
                            bbox[0] = predBoxes[0, i, 0]; // centerX
                            bbox[1] = predBoxes[0, i, 1]; // centerY
                            bbox[2] = predBoxes[0, i, 2]; // width
                            bbox[3] = predBoxes[0, i, 3]; // height
                        }
                        potentialDetections.Add((i, confidence, bbox));
                    }
                }

                // Sort potential detections by confidence
                potentialDetections = potentialDetections.OrderByDescending(x => x.confidence).ToList();

                // Log top detections
                Logger.Log($"[OwlVitDetector] Found {potentialDetections.Count} detections above threshold {workingThreshold:F2} for '{queryVariant}'");
                int topN = Math.Min(5, potentialDetections.Count);
                for (int i = 0; i < topN; i++)
                {
                    var detection = potentialDetections[i];
                    Logger.Log($"[OwlVitDetector] Top {i + 1}: confidence={detection.confidence:F4}, center=({detection.bbox[0]:F3},{detection.bbox[1]:F3}), size=({detection.bbox[2]:F3},{detection.bbox[3]:F3})");
                }

                // Create detection results for all potential detections
                foreach (var detection in potentialDetections)
                {
                    int i = detection.index;
                    float confidence = detection.confidence;
                    float[] bbox = detection.bbox;

                    float centerX = bbox[0];
                    float centerY = bbox[1];
                    float width = bbox[2];
                    float height = bbox[3];

                    // Convert to top-left coordinates for easier visualization
                    float x = centerX - width / 2;
                    float y = centerY - height / 2;

                    // Ensure coordinates are valid (in range [0,1])
                    x = Math.Max(0, Math.Min(1, x));
                    y = Math.Max(0, Math.Min(1, y));
                    width = Math.Max(0, Math.Min(1 - x, width));
                    height = Math.Max(0, Math.Min(1 - y, height));

                    // Skip tiny boxes that might be noise
                    if (width < 0.01f || height < 0.01f)
                        continue;

                    // Create result object
                    DetectionResult result = new DetectionResult
                    {
                        Category = baseQuery, // Use the base query as the category
                        Confidence = confidence,
                        X = x,
                        Y = y,
                        Width = width,
                        Height = height,
                        Slice = currentSlice,
                        QueryVariant = queryVariant // Track which variant produced this result
                    };

                    results.Add(result);
                }

                Logger.Log($"[OwlVitDetector] Created {results.Count} detection results for '{queryVariant}'");
            }
            catch (Exception ex)
            {
                Logger.Log($"[OwlVitDetector] Error processing variant results: {ex.Message}");
                throw;
            }
        }
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
            string onnxDirectory = Path.Combine(Application.StartupPath, "ONNX/owlvit");
            modelPath = Path.Combine(onnxDirectory, "owlvit.onnx");

            // Use a much lower default threshold for CT images (medical domain)
            // CT features are often subtle and have lower confidence scores
            detectionThreshold = 0.15f;

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
        /// <summary>
        /// Process detection results from model outputs
        /// </summary>
        private void ProcessResults(Tensor<float> logits, Tensor<float> predBoxes, string category)
        {
            try
            {
                // Log tensor shapes for debugging
                Logger.Log($"[OwlVitDetector] Processing results for category '{category}'");
                Logger.Log($"[OwlVitDetector] - logits shape: {DimensionsToString(logits.Dimensions)}");
                Logger.Log($"[OwlVitDetector] - pred_boxes shape: {DimensionsToString(predBoxes.Dimensions)}");

                // Get dimensions from tensors
                int numBoxes = 0;
                int numQueries = 1; // Default if we can't determine from logits

                // Handle different output formats based on the model
                if (logits.Dimensions.Length == 3)
                {
                    // Format: [batch, boxes, queries]
                    numBoxes = logits.Dimensions[1];
                    numQueries = logits.Dimensions[2];
                    Logger.Log($"[OwlVitDetector] Multi-query format detected with {numQueries} queries");
                }
                else if (logits.Dimensions.Length == 2)
                {
                    // Format: [batch, boxes]
                    numBoxes = logits.Dimensions[1];
                    Logger.Log("[OwlVitDetector] Single-query format detected");
                }
                else
                {
                    // Unknown format, try to infer from pred_boxes
                    if (predBoxes.Dimensions.Length >= 2)
                    {
                        numBoxes = predBoxes.Dimensions[1];
                        Logger.Log($"[OwlVitDetector] Unknown logits format, inferring {numBoxes} boxes from pred_boxes");
                    }
                    else
                    {
                        throw new Exception($"Unsupported output tensor format: logits shape {DimensionsToString(logits.Dimensions)}");
                    }
                }

                // DEBUG: Track top confidence scores
                List<(int index, float confidence, float[] bbox)> topConfidences = new List<(int index, float confidence, float[] bbox)>();

                // Process each prediction box
                List<DetectionResult> results = new List<DetectionResult>();

                // Log min and max values in logits tensor for debugging
                float minLogit = float.MaxValue;
                float maxLogit = float.MinValue;

                if (logits.Dimensions.Length == 3)
                {
                    for (int i = 0; i < Math.Min(numBoxes, 100); i++)
                    {
                        for (int q = 0; q < numQueries; q++)
                        {
                            float logitValue = logits[0, i, q];
                            minLogit = Math.Min(minLogit, logitValue);
                            maxLogit = Math.Max(maxLogit, logitValue);
                        }
                    }
                }
                else if (logits.Dimensions.Length == 2)
                {
                    for (int i = 0; i < Math.Min(numBoxes, 100); i++)
                    {
                        float logitValue = logits[0, i];
                        minLogit = Math.Min(minLogit, logitValue);
                        maxLogit = Math.Max(maxLogit, logitValue);
                    }
                }

                Logger.Log($"[OwlVitDetector] Logit value range: min={minLogit:F4}, max={maxLogit:F4}");

                // Process each prediction box
                for (int i = 0; i < numBoxes; i++)
                {
                    float confidence = 0;

                    // Calculate confidence based on tensor format
                    if (logits.Dimensions.Length == 3)
                    {
                        // For multi-query, use max across all queries
                        for (int q = 0; q < numQueries; q++)
                        {
                            float logitValue = logits[0, i, q];
                            float score = 1.0f / (1.0f + (float)Math.Exp(-logitValue)); // Sigmoid
                            confidence = Math.Max(confidence, score);
                        }
                    }
                    else
                    {
                        // For single-query format
                        float logitValue = logits[0, i];
                        confidence = 1.0f / (1.0f + (float)Math.Exp(-logitValue)); // Sigmoid
                    }

                    // Save top confidence scores for debugging
                    if (confidence > 0.01f)
                    {
                        float[] bbox = new float[4];
                        if (predBoxes.Dimensions.Length == 3 && predBoxes.Dimensions[2] >= 4)
                        {
                            bbox[0] = predBoxes[0, i, 0]; // centerX
                            bbox[1] = predBoxes[0, i, 1]; // centerY
                            bbox[2] = predBoxes[0, i, 2]; // width
                            bbox[3] = predBoxes[0, i, 3]; // height
                        }
                        topConfidences.Add((i, confidence, bbox));
                    }

                    // Skip very low confidence detections
                    if (confidence < 0.05f)
                        continue;

                    // Get box coordinates
                    if (predBoxes.Dimensions.Length != 3 || predBoxes.Dimensions[2] < 4)
                    {
                        Logger.Log($"[OwlVitDetector] Warning: Unexpected pred_boxes shape: {DimensionsToString(predBoxes.Dimensions)}");
                        continue;
                    }

                    float centerX = predBoxes[0, i, 0];
                    float centerY = predBoxes[0, i, 1];
                    float width = predBoxes[0, i, 2];
                    float height = predBoxes[0, i, 3];

                    // Convert to top-left coordinates for easier visualization
                    float x = centerX - width / 2;
                    float y = centerY - height / 2;

                    // Ensure coordinates are valid (in range [0,1])
                    x = Math.Max(0, Math.Min(1, x));
                    y = Math.Max(0, Math.Min(1, y));
                    width = Math.Max(0, Math.Min(1 - x, width));
                    height = Math.Max(0, Math.Min(1 - y, height));

                    // Skip tiny boxes that might be noise
                    if (width < 0.01f || height < 0.01f)
                        continue;

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

                // Log top confidence scores for debugging
                topConfidences = topConfidences.OrderByDescending(x => x.confidence).Take(5).ToList();
                if (topConfidences.Count > 0)
                {
                    Logger.Log($"[OwlVitDetector] Top 5 confidence scores for '{category}':");
                    foreach (var (index, conf, bbox) in topConfidences)
                    {
                        Logger.Log($"[OwlVitDetector] - Box {index}: confidence={conf:F4}, center=({bbox[0]:F3},{bbox[1]:F3}), size=({bbox[2]:F3},{bbox[3]:F3})");
                    }
                }
                else
                {
                    Logger.Log($"[OwlVitDetector] No confidences above 0.01 found for '{category}'");
                }

                // Add results to the master list
                detectionResults.AddRange(results);

                Logger.Log($"[OwlVitDetector] Found {results.Count} detections for category '{category}'");
            }
            catch (Exception ex)
            {
                Logger.Log($"[OwlVitDetector] Error processing results: {ex.Message}");
                throw;
            }
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
        /// <summary>
        /// Creates an enhanced version of the slice bitmap with improved contrast for better detection
        /// </summary>
        /// <param name="sliceZ">Slice index to process</param>
        /// <returns>Enhanced bitmap</returns>
        private unsafe Bitmap CreateEnhancedSliceBitmap(int sliceZ)
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

            // First, analyze the slice to find min/max values for normalization
            byte minVal = 255;
            byte maxVal = 0;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte val = mainForm.volumeData[x, y, sliceZ];
                    minVal = Math.Min(minVal, val);
                    maxVal = Math.Max(maxVal, val);
                }
            }

            // If the image has very low contrast, use a wider normalization range
            if (maxVal - minVal < 50)
            {
                Logger.Log($"[OwlVitDetector] Low contrast image detected (range {minVal}-{maxVal}), enhancing contrast");
                minVal = (byte)Math.Max(0, minVal - 20);
                maxVal = (byte)Math.Min(255, maxVal + 20);
            }

            // Create RGB bitmap with enhanced contrast
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            BitmapData bmpData = bmp.LockBits(
                new Rectangle(0, 0, w, h),
                ImageLockMode.WriteOnly,
                PixelFormat.Format24bppRgb);

            int stride = bmpData.Stride;
            int bytesPerPixel = 3; // RGB

            byte* ptr = (byte*)bmpData.Scan0;

            float range = maxVal - minVal;
            if (range == 0) range = 1; // Avoid division by zero

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte originalVal = mainForm.volumeData[x, y, sliceZ];

                    // Normalize to 0-255 range with enhanced contrast
                    byte normalizedVal;

                    if (originalVal <= minVal)
                        normalizedVal = 0;
                    else if (originalVal >= maxVal)
                        normalizedVal = 255;
                    else
                        normalizedVal = (byte)(255 * (originalVal - minVal) / range);

                    // Apply additional contrast enhancement using a sigmoid curve
                    float enhancedVal = 255 * (1.0f / (1.0f + (float)Math.Exp(-6 * (normalizedVal / 255.0f - 0.5f))));
                    byte val = (byte)Math.Max(0, Math.Min(255, enhancedVal));

                    int offset = y * stride + x * bytesPerPixel;

                    // RGB = same value for grayscale with a slight tint to help model
                    ptr[offset] = (byte)(val * 0.95); // Blue - slightly less to help model
                    ptr[offset + 1] = val;            // Green
                    ptr[offset + 2] = val;            // Red
                }
            }

            bmp.UnlockBits(bmpData);

            // Add to cache
            Bitmap cacheCopy = new Bitmap(bmp);
            sliceCache.Add(sliceZ, cacheCopy);

            // For debugging
            try
            {
                string debugDir = Path.Combine(Application.StartupPath, "debug");
                if (!Directory.Exists(debugDir))
                    Directory.CreateDirectory(debugDir);

                string debugPath = Path.Combine(debugDir, $"enhanced_slice_{sliceZ}.png");
                bmp.Save(debugPath, ImageFormat.Png);
                Logger.Log($"[OwlVitDetector] Saved enhanced slice image to {debugPath}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[OwlVitDetector] Failed to save debug image: {ex.Message}");
            }

            return bmp;
        }
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

        /// <summary>
        /// Loads the OWL-ViT ONNX model
        /// </summary>
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

                // Enable memory pattern and arena
                options.EnableMemoryPattern = true;
                options.EnableCpuMemArena = true;

                // Set intra-op thread count for CPU (use half of available cores)
                int cpuThreads = Environment.ProcessorCount;
                options.IntraOpNumThreads = Math.Max(1, cpuThreads / 2);

                if (useGPU)
                {
                    try
                    {
                        // For DirectML (DirectX Machine Learning) GPU support
                        // This is the preferred EP for Windows
                        options.AppendExecutionProvider_DML(0);
                        Logger.Log("[OwlVitDetector] Using DirectML (GPU) execution provider");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[OwlVitDetector] DirectML not available, trying CUDA: {ex.Message}");

                        try
                        {
                            // Try CUDA as fallback
                            options.AppendExecutionProvider_CUDA();
                            Logger.Log("[OwlVitDetector] Using CUDA execution provider");
                        }
                        catch (Exception cudaEx)
                        {
                            Logger.Log($"[OwlVitDetector] CUDA not available, falling back to CPU: {cudaEx.Message}");
                            useGPU = false;

                            // Update checkbox
                            chkUseGPU.Checked = false;
                        }
                    }
                }

                if (!useGPU)
                {
                    Logger.Log("[OwlVitDetector] Using CPU execution provider");
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

                // Check for expected input and output names
                bool hasRequiredInputs = inputMetadata.ContainsKey("input_ids") &&
                                        inputMetadata.ContainsKey("pixel_values") &&
                                        inputMetadata.ContainsKey("attention_mask");

                bool hasRequiredOutputs = outputMetadata.ContainsKey("logits") &&
                                        outputMetadata.ContainsKey("pred_boxes");

                if (!hasRequiredInputs || !hasRequiredOutputs)
                {
                    string warningMsg = "Model does not have expected input/output names. Detection may fail.";
                    MessageBox.Show(warningMsg, "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    Logger.Log($"[OwlVitDetector] Warning: {warningMsg}");
                    statusLabel.Text = $"Warning: {warningMsg}";
                }

                // Update status
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
        /// <summary>
        /// Detects objects in the current slice using OWL-ViT model with open vocabulary approach
        /// </summary>
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

            // Set a much lower threshold during detection to catch more potential objects
            // We'll use a working threshold of 0.05 but keep the user's preferred threshold for display
            float originalThreshold = detectionThreshold;
            float workingThreshold = 0.05f;

            Logger.Log($"[OwlVitDetector] Using working threshold of {workingThreshold:F2} for detection (display threshold: {originalThreshold:F2})");

            try
            {
                // Get the text prompt
                string prompt = txtPrompt.Text.Trim();

                // Split into multiple queries if comma-separated
                string[] queries = prompt.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                         .Select(q => q.Trim())
                                         .ToArray();

                Logger.Log($"[OwlVitDetector] Running open vocabulary detection with {queries.Length} queries: {string.Join(", ", queries)}");

                // Create vocabulary-enhancing variants for each query
                Dictionary<string, string> queryVariants = new Dictionary<string, string>();
                foreach (string query in queries)
                {
                    // Process the base query
                    queryVariants[query] = query;

                    // For domain-specific geological terms, add some domain context
                    if (IsCTDomainTerm(query))
                    {
                        queryVariants[$"a CT scan of {query}"] = query;
                        queryVariants[$"geological {query}"] = query;
                        queryVariants[$"{query} in rock"] = query;
                    }
                    // For general terms, add standard CLIP-style prefixes
                    else
                    {
                        queryVariants[$"a photo of a {query}"] = query;
                        queryVariants[$"a {query}"] = query;
                    }
                }

                // Preprocess image once
                DenseTensor<float> imageInput = await Task.Run(() => PreprocessImage(currentSlice));

                // Track best detections for each base query
                Dictionary<string, List<DetectionResult>> bestDetectionsByQuery =
                    new Dictionary<string, List<DetectionResult>>();

                // Process each query variant
                foreach (var kvp in queryVariants)
                {
                    string queryVariant = kvp.Key;
                    string baseQuery = kvp.Value;

                    try
                    {
                        // Update status
                        statusLabel.Text = $"Detecting: {queryVariant}";
                        Logger.Log($"[OwlVitDetector] Processing query variant: '{queryVariant}' (base: '{baseQuery}')");

                        // Tokenize text
                        var tokenInputs = TokenizeText(queryVariant);

                        // Run inference
                        var results = await Task.Run(() =>
                            RunInference(imageInput, tokenInputs.inputIds, tokenInputs.attentionMask));

                        // Process results but don't add to main detection results yet
                        List<DetectionResult> variantResults = new List<DetectionResult>();
                        ProcessResultsForVariant(results.logits, results.predBoxes, queryVariant, baseQuery, workingThreshold, variantResults);

                        // If we don't already have results for this base query, initialize the list
                        if (!bestDetectionsByQuery.ContainsKey(baseQuery))
                        {
                            bestDetectionsByQuery[baseQuery] = new List<DetectionResult>();
                        }

                        // Add these results to the base query's list
                        bestDetectionsByQuery[baseQuery].AddRange(variantResults);

                        Logger.Log($"[OwlVitDetector] Found {variantResults.Count} potential detections for '{queryVariant}'");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[OwlVitDetector] Error processing query '{queryVariant}': {ex.Message}");
                        statusLabel.Text = $"Error with query '{queryVariant}': {ex.Message}";
                        await Task.Delay(300);
                    }
                }

                // Now process the best detections for each base query
                foreach (var kvp in bestDetectionsByQuery)
                {
                    string baseQuery = kvp.Key;
                    List<DetectionResult> queryResults = kvp.Value;

                    // If we have results, take only the best ones after NMS
                    if (queryResults.Count > 0)
                    {
                        // First sort by confidence
                        queryResults = queryResults.OrderByDescending(r => r.Confidence).ToList();

                        // Apply non-maximum suppression to remove duplicates
                        List<DetectionResult> filteredResults = new List<DetectionResult>();
                        foreach (var result in queryResults)
                        {
                            bool shouldKeep = true;
                            foreach (var kept in filteredResults)
                            {
                                if (CalculateIoU(result, kept) > 0.5f)
                                {
                                    shouldKeep = false;
                                    break;
                                }
                            }

                            if (shouldKeep)
                            {
                                // Set the category to the base query for consistency
                                result.Category = baseQuery;
                                filteredResults.Add(result);
                            }
                        }

                        // Add the filtered results to the main detection results
                        detectionResults.AddRange(filteredResults);

                        Logger.Log($"[OwlVitDetector] Added {filteredResults.Count} filtered results for '{baseQuery}'");
                    }
                }

                // Sort all results by confidence
                detectionResults = detectionResults.OrderByDescending(r => r.Confidence).ToList();

                // Restore the original threshold for display
                detectionThreshold = originalThreshold;

                // Update results list with user's original threshold
                UpdateResultsList();

                // Update display
                UpdateImageDisplay();

                int count = detectionResults.Count(r => r.Confidence >= detectionThreshold);
                statusLabel.Text = $"Detection complete. Found {count} objects above threshold {detectionThreshold:F2}.";
                Logger.Log($"[OwlVitDetector] Detection complete. Found {count} objects above threshold {detectionThreshold:F2} (total: {detectionResults.Count})");
            }
            catch (Exception ex)
            {
                // Restore original threshold in case of error
                detectionThreshold = originalThreshold;

                string errorMessage = $"Error during detection: {ex.Message}";
                if (ex.InnerException != null)
                {
                    errorMessage += $"\nInner exception: {ex.InnerException.Message}";
                }

                MessageBox.Show(errorMessage, "Detection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[OwlVitDetector] Detection error: {errorMessage}");
                statusLabel.Text = $"Error: {ex.Message}";
            }
            finally
            {
                btnDetect.Enabled = true;
            }
        }
        /// <summary>
        /// Filters detection results and removes duplicate boxes
        /// </summary>
        private List<DetectionResult> FilterAndDeduplicate(List<DetectionResult> results)
        {
            // First, sort by confidence
            results = results.OrderByDescending(r => r.Confidence).ToList();

            // List to keep track of which results to keep
            List<DetectionResult> filteredResults = new List<DetectionResult>();

            // Non-maximum suppression (NMS)
            foreach (var result in results)
            {
                bool shouldKeep = true;

                // Check if this box significantly overlaps with any higher-confidence box
                foreach (var kept in filteredResults)
                {
                    if (CalculateIoU(result, kept) > 0.5f)
                    {
                        shouldKeep = false;
                        break;
                    }
                }

                if (shouldKeep)
                {
                    filteredResults.Add(result);
                }
            }

            return filteredResults;
        }

        /// <summary>
        /// Calculates Intersection over Union (IoU) between two bounding boxes
        /// </summary>
        private float CalculateIoU(DetectionResult box1, DetectionResult box2)
        {
            // Calculate coordinates of the intersection
            float x1 = Math.Max(box1.X, box2.X);
            float y1 = Math.Max(box1.Y, box2.Y);
            float x2 = Math.Min(box1.X + box1.Width, box2.X + box2.Width);
            float y2 = Math.Min(box1.Y + box1.Height, box2.Y + box2.Height);

            // Check if there is an intersection
            if (x2 < x1 || y2 < y1)
                return 0;

            // Calculate area of intersection
            float intersectionArea = (x2 - x1) * (y2 - y1);

            // Calculate areas of both boxes
            float box1Area = box1.Width * box1.Height;
            float box2Area = box2.Width * box2.Height;

            // Calculate union area
            float unionArea = box1Area + box2Area - intersectionArea;

            // Return IoU
            return intersectionArea / unionArea;
        }

        /// <summary>
        /// Preprocesses a CT scan image for input to the OWL-ViT model with specific enhancements for medical images
        /// </summary>
        /// <param name="sliceZ">Slice index to process</param>
        /// <returns>Preprocessed image tensor</returns>
        private unsafe DenseTensor<float> PreprocessImage(int sliceZ)
        {
            try
            {
                Logger.Log($"[OwlVitDetector] Preprocessing CT scan slice {sliceZ} for OWL-ViT");

                // Get raw image data
                int w = mainForm.GetWidth();
                int h = mainForm.GetHeight();

                // Analyze the slice to find min/max values for normalization
                byte minVal = 255;
                byte maxVal = 0;

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        byte val = mainForm.volumeData[x, y, sliceZ];
                        minVal = Math.Min(minVal, val);
                        maxVal = Math.Max(maxVal, val);
                    }
                }

                Logger.Log($"[OwlVitDetector] Raw CT image range: min={minVal}, max={maxVal}");

                // Get model input size from session metadata if available
                int inputHeight = 768;
                int inputWidth = 768;

                // Try to get the expected input dimensions from the model
                var inputMeta = session.InputMetadata["pixel_values"];
                if (inputMeta != null && inputMeta.Dimensions.Length >= 3)
                {
                    // Check if dimensions are specified (not -1)
                    if (inputMeta.Dimensions.Length >= 4 &&
                        inputMeta.Dimensions[2] > 0 &&
                        inputMeta.Dimensions[3] > 0)
                    {
                        inputHeight = inputMeta.Dimensions[2];
                        inputWidth = inputMeta.Dimensions[3];
                        Logger.Log($"[OwlVitDetector] Using model-specified input dimensions: {inputWidth}×{inputHeight}");
                    }
                }

                // Create a tensor with shape [1, 3, height, width]
                DenseTensor<float> inputTensor = new DenseTensor<float>(new[] { 1, 3, inputHeight, inputWidth });

                // Create an RGB image that will be used for model input
                using (Bitmap enhancedImage = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                {
                    // Lock the bitmap and access its pixel data
                    BitmapData enhancedData = enhancedImage.LockBits(
                        new Rectangle(0, 0, w, h),
                        ImageLockMode.WriteOnly,
                        PixelFormat.Format24bppRgb);

                    int stride = enhancedData.Stride;
                    int bytesPerPixel = 3; // RGB

                    byte* ptr = (byte*)enhancedData.Scan0;

                    // Apply multiple visualizations to help the model see features
                    // For CT scans, we'll use different enhancement techniques based on the CT window

                    // 1. Auto-contrast enhancement
                    float range = maxVal - minVal;
                    if (range == 0) range = 1; // Avoid division by zero

                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            byte originalVal = mainForm.volumeData[x, y, sliceZ];

                            // Basic normalization to 0-255 range
                            float normalizedVal = (originalVal - minVal) / range;

                            // Apply window/level adjustment to make CT features more visible
                            // This is similar to what radiologists use to view different tissue types
                            byte val = (byte)(normalizedVal * 255);

                            // Apply color mapping to help model distinguish features
                            // Using a heat map style coloring (black -> red -> yellow -> white)
                            int offset = y * stride + x * bytesPerPixel;

                            // Create color mapping - this can help vision models detect features better
                            if (val < 64)
                            {
                                // Black to dark blue (0-63)
                                ptr[offset] = (byte)(val * 4); // Blue
                                ptr[offset + 1] = 0;           // Green
                                ptr[offset + 2] = 0;           // Red
                            }
                            else if (val < 128)
                            {
                                // Dark blue to cyan (64-127)
                                ptr[offset] = 255;                       // Blue
                                ptr[offset + 1] = (byte)((val - 64) * 4); // Green
                                ptr[offset + 2] = 0;                     // Red
                            }
                            else if (val < 192)
                            {
                                // Cyan to yellow (128-191)
                                ptr[offset] = (byte)(255 - (val - 128) * 4); // Blue
                                ptr[offset + 1] = 255;                      // Green
                                ptr[offset + 2] = (byte)((val - 128) * 4);  // Red
                            }
                            else
                            {
                                // Yellow to white (192-255)
                                ptr[offset] = (byte)((val - 192) * 4);   // Blue
                                ptr[offset + 1] = 255;                  // Green
                                ptr[offset + 2] = 255;                  // Red
                            }
                        }
                    }

                    enhancedImage.UnlockBits(enhancedData);

                    // For debugging, save the enhanced image
                    try
                    {
                        string debugDir = Path.Combine(Application.StartupPath, "debug");
                        if (!Directory.Exists(debugDir))
                            Directory.CreateDirectory(debugDir);

                        string debugPath = Path.Combine(debugDir, $"enhanced_ct_slice_{sliceZ}.png");
                        enhancedImage.Save(debugPath, ImageFormat.Png);
                        Logger.Log($"[OwlVitDetector] Saved color-enhanced CT image to {debugPath}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[OwlVitDetector] Failed to save debug image: {ex.Message}");
                    }

                    // Resize to model input dimensions
                    using (Bitmap resized = new Bitmap(inputWidth, inputHeight))
                    {
                        using (Graphics g = Graphics.FromImage(resized))
                        {
                            // Use high quality interpolation for resizing
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.DrawImage(enhancedImage, 0, 0, inputWidth, inputHeight);
                        }

                        // Lock the bitmap and access its pixel data
                        BitmapData bmpData = resized.LockBits(
                            new Rectangle(0, 0, resized.Width, resized.Height),
                            ImageLockMode.ReadOnly,
                            PixelFormat.Format24bppRgb);

                        stride = bmpData.Stride;
                        bytesPerPixel = 3; // RGB

                        ptr = (byte*)bmpData.Scan0;

                        // Process pixels with standard CLIP normalization
                        // Using the standard ImageNet/CLIP normalization values
                        float[] mean = new float[] { 0.48145466f, 0.4578275f, 0.40821073f };
                        float[] std = new float[] { 0.26862954f, 0.26130258f, 0.27577711f };

                        for (int y = 0; y < inputHeight; y++)
                        {
                            for (int x = 0; x < inputWidth; x++)
                            {
                                int offset = y * stride + x * bytesPerPixel;

                                // BGR order (standard in Bitmap)
                                byte b = ptr[offset];
                                byte g = ptr[offset + 1];
                                byte r = ptr[offset + 2];

                                // Normalize to range [0,1] and then apply mean/std
                                // CLIP/OWL-ViT expects RGB order
                                inputTensor[0, 0, y, x] = (r / 255.0f - mean[0]) / std[0];
                                inputTensor[0, 1, y, x] = (g / 255.0f - mean[1]) / std[1];
                                inputTensor[0, 2, y, x] = (b / 255.0f - mean[2]) / std[2];
                            }
                        }

                        resized.UnlockBits(bmpData);

                        Logger.Log($"[OwlVitDetector] Completed preprocessing of slice {sliceZ} to tensor shape: 1×3×{inputHeight}×{inputWidth}");
                        return inputTensor;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[OwlVitDetector] Error in CT image preprocessing: {ex.Message}");
                throw;
            }
        }
        #region Tokenization
        // Constants for the CLIP text encoder
        private const int MaxTokenLength = 16; // Standard CLIP context length
        private CLIPTokenizer clipTokenizer;

        /// <summary>
        /// Loads the tokenizer resources and initializes the tokenizer
        /// </summary>
        private void LoadTokenizerResources()
        {
            try
            {
                string modelDir = Path.GetDirectoryName(modelPath);

                if (string.IsNullOrEmpty(modelDir))
                    modelDir = Path.Combine(Application.StartupPath, "ONNX/owlvit");

                Logger.Log($"[OwlVitDetector] Looking for tokenizer resources in: {modelDir}");

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

                // Initialize the JSON serializer options
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };

                // Load vocab.json
                string vocabJson = File.ReadAllText(vocabPath);
                var vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(vocabJson, options);

                if (vocab == null || vocab.Count == 0)
                {
                    Logger.Log("[OwlVitDetector] Error: Failed to parse vocabulary");
                    return;
                }

                // Load tokenizer config as both strong type and dynamic dictionary
                TokenizerConfig config = null;

                if (File.Exists(tokenizerConfigPath))
                {
                    string configJson = File.ReadAllText(tokenizerConfigPath);

                    // Parse as strong type
                    config = JsonSerializer.Deserialize<TokenizerConfig>(configJson, options);

                    // Also parse as dictionary for flexibility
                    tokenizerConfig = JsonSerializer.Deserialize<Dictionary<string, object>>(configJson, options);

                    if (config != null)
                    {
                        Logger.Log($"[OwlVitDetector] Tokenizer config loaded: BOS={config.BosToken}, EOS={config.EosToken}, MaxLength={config.ModelMaxLength}");
                    }
                }

                // Create the tokenizer
                clipTokenizer = new CLIPTokenizer(vocab, config);
                Logger.Log($"[OwlVitDetector] CLIP tokenizer initialized with {vocab.Count} tokens and max length {clipTokenizer.GetModelMaxLength()}");
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
            try
            {
                // Force the model max length to 16 as specified in tokenizer_config.json
                int maxTokenLength = 16;
                Logger.Log($"[OwlVitDetector] Tokenizing text: '{text}' with max length {maxTokenLength}");

                // Initialize tokenizer if needed
                if (clipTokenizer == null)
                {
                    LoadTokenizerResources();

                    if (clipTokenizer == null)
                    {
                        Logger.Log("[OwlVitDetector] Failed to initialize tokenizer, using fallback");
                        return FallbackTokenization(text);
                    }
                }

                // Create output tensors with the correct dimensions (batch_size=1, seq_len=16)
                DenseTensor<long> inputIds = new DenseTensor<long>(new[] { 1, maxTokenLength });
                DenseTensor<long> attentionMask = new DenseTensor<long>(new[] { 1, maxTokenLength });

                // Encode the text with the CLIP tokenizer, forcing max length to 16
                var encoding = clipTokenizer.Encode(text, maxTokenLength);

                // Copy the results to the tensors
                for (int i = 0; i < encoding.InputIds.Count && i < maxTokenLength; i++)
                {
                    inputIds[0, i] = encoding.InputIds[i];
                    attentionMask[0, i] = encoding.AttentionMask[i];
                }

                // Double-check all dimensions match
                if (inputIds.Dimensions[1] != maxTokenLength || attentionMask.Dimensions[1] != maxTokenLength)
                {
                    Logger.Log($"[OwlVitDetector] WARNING: Tensor dimension mismatch! Expected 16, got input_ids={inputIds.Dimensions[1]}, attention_mask={attentionMask.Dimensions[1]}");
                    return FallbackTokenization(text);
                }

                // Log the encoded tokens for debugging
                Logger.Log($"[OwlVitDetector] Tokenized '{text}' to {encoding.InputIds.Count} tokens with max length {maxTokenLength}");
                if (encoding.InputIds.Count > 0)
                {
                    string tokenSample = string.Join(", ", encoding.InputIds.Take(Math.Min(5, encoding.InputIds.Count)));
                    Logger.Log($"[OwlVitDetector] Token sample: [{tokenSample}...]");
                }

                return (inputIds, attentionMask);
            }
            catch (Exception ex)
            {
                Logger.Log($"[OwlVitDetector] Tokenization error: {ex.Message}. Using fallback.");
                return FallbackTokenization(text);
            }
        }

        /// <summary>
        /// Fallback tokenization method when the main tokenizer fails
        /// </summary>
        private (DenseTensor<long> inputIds, DenseTensor<long> attentionMask) FallbackTokenization(string text)
        {
            Logger.Log("[OwlVitDetector] Using fallback tokenization");

            // Always use 16 for OWL-ViT compatibility
            int maxTokenLength = 16;

            // Create output tensors with correct dimensions
            DenseTensor<long> inputIds = new DenseTensor<long>(new[] { 1, maxTokenLength });
            DenseTensor<long> attentionMask = new DenseTensor<long>(new[] { 1, maxTokenLength });

            // First token is BOS (49406 for CLIP)
            inputIds[0, 0] = 49406;
            attentionMask[0, 0] = 1;

            // Initialize all other positions to 0
            for (int i = 1; i < maxTokenLength; i++)
            {
                inputIds[0, i] = 0;
                attentionMask[0, i] = 0;
            }

            // Add a simple encoding for the text by using a few common token IDs
            // This won't be semantically correct but will serve as a basic input
            if (maxTokenLength > 1)
            {
                inputIds[0, 1] = 320; // Common token for 'a'
                attentionMask[0, 1] = 1;
            }

            if (maxTokenLength > 2)
            {
                // Use text length to determine a token ID (just a heuristic)
                int tokenId = 1000 + (text.Length % 1000);
                inputIds[0, 2] = tokenId;
                attentionMask[0, 2] = 1;
            }

            // Set the EOS token near the end
            if (maxTokenLength > 3)
            {
                inputIds[0, 3] = 49407; // EOS token
                attentionMask[0, 3] = 1;
            }

            Logger.Log($"[OwlVitDetector] Fallback tokenization: Created token sequence with tokens=[49406, 320, ~1000, 49407] and max length 16");
            return (inputIds, attentionMask);
        }
        #endregion
        /// <summary>
        /// Helper function to convert tensor dimensions to string
        /// </summary>
        private string DimensionsToString(ReadOnlySpan<int> dimensions)
        {
            // Convert ReadOnlySpan<int> to array
            int[] dimensionsArray = dimensions.ToArray();
            return string.Join("×", dimensionsArray);
        }
        /// <summary>
        /// Runs inference on the OWL-ViT model with the provided image and text inputs
        /// </summary>
        private (Tensor<float> logits, Tensor<float> predBoxes, Tensor<float> textEmbeds, Tensor<float> imageEmbeds) RunInference(
            DenseTensor<float> imageInput,
            DenseTensor<long> inputIds,
            DenseTensor<long> attentionMask)
        {
            // Log input tensor shapes for debugging
            Logger.Log($"[OwlVitDetector] Running inference with tensor shapes:");
            Logger.Log($"[OwlVitDetector] - pixel_values: {DimensionsToString(imageInput.Dimensions)}");
            Logger.Log($"[OwlVitDetector] - input_ids: {DimensionsToString(inputIds.Dimensions)}");
            Logger.Log($"[OwlVitDetector] - attention_mask: {DimensionsToString(attentionMask.Dimensions)}");

            // Additional debug logging for input tokens
            Logger.Log($"[OwlVitDetector] Input token IDs (first few): {string.Join(", ", Enumerable.Range(0, Math.Min(5, inputIds.Dimensions[1])).Select(i => inputIds[0, i]))}");

            // Create input name mapping
            var inputs = new List<NamedOnnxValue>
    {
        NamedOnnxValue.CreateFromTensor("pixel_values", imageInput),
        NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
        NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask)
    };

            // Run inference
            Stopwatch stopwatch = Stopwatch.StartNew();

            Logger.Log("[OwlVitDetector] Starting model inference...");
            var outputs = session.Run(inputs);
            stopwatch.Stop();

            Logger.Log($"[OwlVitDetector] Inference completed in {stopwatch.ElapsedMilliseconds}ms");

            try
            {
                // Get outputs - use FirstOrDefault to handle potential missing outputs gracefully
                var logits = outputs.FirstOrDefault(x => x.Name == "logits")?.AsTensor<float>();
                var predBoxes = outputs.FirstOrDefault(x => x.Name == "pred_boxes")?.AsTensor<float>();
                var textEmbeds = outputs.FirstOrDefault(x => x.Name == "text_embeds")?.AsTensor<float>();
                var imageEmbeds = outputs.FirstOrDefault(x => x.Name == "image_embeds")?.AsTensor<float>();

                // Check for null outputs
                if (logits == null)
                    Logger.Log("[OwlVitDetector] WARNING: logits output is null!");
                if (predBoxes == null)
                    Logger.Log("[OwlVitDetector] WARNING: pred_boxes output is null!");
                if (textEmbeds == null)
                    Logger.Log("[OwlVitDetector] WARNING: text_embeds output is null!");
                if (imageEmbeds == null)
                    Logger.Log("[OwlVitDetector] WARNING: image_embeds output is null!");

                // Log output tensor shapes for debugging
                if (logits != null)
                    Logger.Log($"[OwlVitDetector] - logits: {DimensionsToString(logits.Dimensions)}");
                if (predBoxes != null)
                    Logger.Log($"[OwlVitDetector] - pred_boxes: {DimensionsToString(predBoxes.Dimensions)}");
                if (textEmbeds != null)
                    Logger.Log($"[OwlVitDetector] - text_embeds: {DimensionsToString(textEmbeds.Dimensions)}");
                if (imageEmbeds != null)
                    Logger.Log($"[OwlVitDetector] - image_embeds: {DimensionsToString(imageEmbeds.Dimensions)}");

                if (logits == null || predBoxes == null)
                {
                    throw new Exception("Required outputs (logits or pred_boxes) not found in model output");
                }

                return (logits, predBoxes, textEmbeds, imageEmbeds);
            }
            catch (Exception ex)
            {
                Logger.Log($"[OwlVitDetector] Error processing model outputs: {ex.Message}");
                throw;
            }
        }
        /// <summary>
        /// Updates the results list with current detection results
        /// </summary>
        private void UpdateResultsList()
        {
            if (resultsListBox == null)
                return;

            // Clear existing items
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

            // Log the results count
            Logger.Log($"[OwlVitDetector] Updated results list with {resultsListBox.Items.Count} items above threshold {detectionThreshold:F2}");
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

            public string QueryVariant { get; internal set; }
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