﻿using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS
{
    internal class TextureClassifier
    {
        private Context gpuContext;
        private Accelerator accelerator;
        private bool useGPU = true;
        private bool gpuInitialized = false;
        private CheckBox chkUseGPU;

        // Main form components
        private Form textureForm;

        private TableLayoutPanel mainLayout;
        private Panel xyPanel, xzPanel, yzPanel;
        private PictureBox xyViewer, xzViewer, yzViewer;
        private Panel controlPanel;
        private Button btnTrain, btnApply, btnClose;
        private Label statusLabel;

        private LRUCache<int, Bitmap> xySliceCache;
        private LRUCache<int, Bitmap> xzSliceCache;
        private LRUCache<int, Bitmap> yzSliceCache;

        private const int CACHE_SIZE = 10; // Number of slices to cache

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

        //Quantization levels
        private bool useQuantization = true;

        private int quantizationLevels = 32;
        private CheckBox chkQuantization;
        private NumericUpDown numQuantizationLevels;

        // Rectangle selection
        private bool isSelectingRectangle = false;

        private Point startPoint;
        private Point endPoint;
        private Rectangle selectionRectangle = Rectangle.Empty;

        // Texture classification
        private byte[,] classificationMask;

        private double[] referenceFeatures;
        private double threshold = 0.85;
        private NumericUpDown numThreshold;
        private TrackBar trkThreshold;
        private Label lblThreshold;

        // References to parent application components
        private MainForm mainForm;

        private Material selectedMaterial;

        // Slices information
        private int xySlice, xzRow, yzCol;

        // Classification parameters
        private GroupBox grpParameters;

        private RadioButton rbGlcm, rbLbp, rbHistogram;
        private Label lblPatchSize;
        private NumericUpDown numPatchSize;
        private int patchSize = 7;

        // Propagation options
        private GroupBox grpPropagation;

        private RadioButton rbCurrentSlice, rbWholeVolume, rbRange;
        private Label lblRange;
        private NumericUpDown numRange;
        private int propagationRange = 10;

        // Slice change callback
        private Action<int> sliceChangeCallback;

        private void InitializeGPU()
        {
            try
            {
                // Create ILGPU context
                gpuContext = Context.Create(builder => builder.Default().EnableAlgorithms());

                // Try to get the best accelerator
                Accelerator bestAccelerator = null;
                bool foundNvidia = false;

                // First, specifically look for NVIDIA GPU
                foreach (var device in gpuContext.Devices)
                {
                    string deviceName = device.Name.ToLower();
                    if (device.AcceleratorType != AcceleratorType.CPU &&
                        (deviceName.Contains("nvidia") || deviceName.Contains("geforce") ||
                         deviceName.Contains("quadro") || deviceName.Contains("rtx") ||
                         deviceName.Contains("gtx")))
                    {
                        Logger.Log($"[TextureClassifier] Found NVIDIA GPU: {device.Name}");
                        bestAccelerator = device.CreateAccelerator(gpuContext);
                        foundNvidia = true;
                        break;
                    }
                }

                // If no NVIDIA device found, try any other GPU
                if (!foundNvidia)
                {
                    foreach (var device in gpuContext.Devices)
                    {
                        if (device.AcceleratorType != AcceleratorType.CPU)
                        {
                            Logger.Log($"[TextureClassifier] Using non-NVIDIA GPU accelerator: {device.Name}");
                            bestAccelerator = device.CreateAccelerator(gpuContext);
                            break;
                        }
                    }
                }

                // If no GPU is available, fall back to CPU
                if (bestAccelerator == null)
                {
                    bestAccelerator = gpuContext.GetCPUDevice(0).CreateAccelerator(gpuContext);
                    Logger.Log("[TextureClassifier] Falling back to CPU accelerator");
                }

                accelerator = bestAccelerator;
                gpuInitialized = true;

                // Update UI
                if (chkUseGPU != null)
                {
                    chkUseGPU.Text = $"Use GPU acceleration ({accelerator.Name})";
                    chkUseGPU.Enabled = true;
                    chkUseGPU.Checked = accelerator.AcceleratorType != AcceleratorType.CPU;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TextureClassifier] GPU initialization failed: {ex.Message}");

                // Reset flag and update UI
                gpuInitialized = false;
                useGPU = false;

                if (chkUseGPU != null)
                {
                    chkUseGPU.Text = "GPU acceleration unavailable";
                    chkUseGPU.Enabled = false;
                    chkUseGPU.Checked = false;
                }
            }
        }

        private void DisposeGPU()
        {
            if (accelerator != null)
            {
                accelerator.Dispose();
                accelerator = null;
            }

            if (gpuContext != null)
            {
                gpuContext.Dispose();
                gpuContext = null;
            }

            gpuInitialized = false;
        }

        public TextureClassifier(MainForm mainForm, Material selectedMaterial)
        {
            Logger.Log("[TextureClassifier] Creating texture classification interface");
            this.mainForm = mainForm;
            this.selectedMaterial = selectedMaterial;

            // Get current slice positions from MainForm
            xySlice = mainForm.CurrentSlice;
            xzRow = mainForm.XzSliceY;
            yzCol = mainForm.YzSliceX;

            // Initialize caches
            xySliceCache = new LRUCache<int, Bitmap>(CACHE_SIZE);
            xzSliceCache = new LRUCache<int, Bitmap>(CACHE_SIZE);
            yzSliceCache = new LRUCache<int, Bitmap>(CACHE_SIZE);

            // Initialize form and UI
            InitializeForm();

            // Register for slice changes from MainForm
            sliceChangeCallback = UpdateSliceFromMainForm;
            mainForm.RegisterSliceChangeCallback(sliceChangeCallback);
        }

        private void InitializeForm()
        {
            Logger.Log("[TextureClassifier] Initializing form");
            textureForm = new Form
            {
                Text = "Texture Classifier",
                Size = new Size(1100, 850),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.Sizable,
                Icon = Properties.Resources.favicon
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

            // Title label
            Label lblTitle = new Label
            {
                Text = "Texture Classification",
                Font = new Font(SystemFonts.DefaultFont.FontFamily, 12, FontStyle.Bold),
                Location = new Point(10, 10),
                AutoSize = true
            };
            controlPanel.Controls.Add(lblTitle);

            // Active View for Segmentation Section
            Label lblActiveView = new Label
            {
                Text = "Active View:",
                Location = new Point(10, 40),
                AutoSize = true
            };
            controlPanel.Controls.Add(lblActiveView);

            btnXYView = new Button
            {
                Text = "XY View",
                Location = new Point(10, 60),
                Width = 80,
                Height = 30,
                BackColor = Color.LightSkyBlue
            };
            btnXYView.Click += (s, e) =>
            {
                currentActiveView = ActiveView.XY;
                UpdateActiveViewButtons();
                Logger.Log("[TextureClassifier] Switched to XY view");
            };
            controlPanel.Controls.Add(btnXYView);

            btnXZView = new Button
            {
                Text = "XZ View",
                Location = new Point(100, 60),
                Width = 80,
                Height = 30
            };
            btnXZView.Click += (s, e) =>
            {
                currentActiveView = ActiveView.XZ;
                UpdateActiveViewButtons();
                Logger.Log("[TextureClassifier] Switched to XZ view");
            };
            controlPanel.Controls.Add(btnXZView);

            btnYZView = new Button
            {
                Text = "YZ View",
                Location = new Point(190, 60),
                Width = 80,
                Height = 30
            };
            btnYZView.Click += (s, e) =>
            {
                currentActiveView = ActiveView.YZ;
                UpdateActiveViewButtons();
                Logger.Log("[TextureClassifier] Switched to YZ view");
            };
            controlPanel.Controls.Add(btnYZView);

            // Classification parameters
            grpParameters = new GroupBox
            {
                Text = "Texture Features",
                Location = new Point(10, 100),
                Width = 350,
                Height = 120
            };
            controlPanel.Controls.Add(grpParameters);

            rbGlcm = new RadioButton
            {
                Text = "GLCM (Gray Level Co-occurrence Matrix)",
                Location = new Point(10, 20),
                Width = 300,
                Checked = true,
                AutoSize = true
            };
            grpParameters.Controls.Add(rbGlcm);

            rbLbp = new RadioButton
            {
                Text = "LBP (Local Binary Patterns)",
                Location = new Point(10, 45),
                Width = 300,
                AutoSize = true
            };
            grpParameters.Controls.Add(rbLbp);

            rbHistogram = new RadioButton
            {
                Text = "Histogram",
                Location = new Point(10, 70),
                Width = 300,
                AutoSize = true
            };
            grpParameters.Controls.Add(rbHistogram);

            lblPatchSize = new Label
            {
                Text = "Patch Size:",
                Location = new Point(10, 230),
                AutoSize = true
            };
            controlPanel.Controls.Add(lblPatchSize);

            chkQuantization = new CheckBox
            {
                Text = "Use Quantization (faster)",
                Location = new Point(10, 95),
                Width = 200,
                Checked = true,
                AutoSize = true
            };
            grpParameters.Controls.Add(chkQuantization);

            Label lblQuantizationLevels = new Label
            {
                Text = "Levels:",
                Location = new Point(230, 95),
                AutoSize = true
            };
            grpParameters.Controls.Add(lblQuantizationLevels);

            numQuantizationLevels = new NumericUpDown
            {
                Location = new Point(280, 93),
                Width = 60,
                Minimum = 8,
                Maximum = 256,
                Value = 32,
                Increment = 8
            };
            grpParameters.Controls.Add(numQuantizationLevels);

            numPatchSize = new NumericUpDown
            {
                Location = new Point(90, 228),
                Width = 60,
                Minimum = 3,
                Maximum = 21,
                Value = patchSize,
                Increment = 2 // Only odd values
            };
            numPatchSize.ValueChanged += (s, e) =>
            {
                // Ensure patch size is odd
                if (numPatchSize.Value % 2 == 0)
                    numPatchSize.Value += 1;
                patchSize = (int)numPatchSize.Value;
            };
            controlPanel.Controls.Add(numPatchSize);

            // Propagation options
            grpPropagation = new GroupBox
            {
                Text = "Propagation Options",
                Location = new Point(10, 260),
                Width = 350,
                Height = 120
            };
            controlPanel.Controls.Add(grpPropagation);

            rbCurrentSlice = new RadioButton
            {
                Text = "Current Slice Only",
                Location = new Point(10, 20),
                Width = 300,
                AutoSize = true
            };
            grpPropagation.Controls.Add(rbCurrentSlice);

            rbWholeVolume = new RadioButton
            {
                Text = "Whole Volume",
                Location = new Point(10, 45),
                Width = 300,
                AutoSize = true
            };
            grpPropagation.Controls.Add(rbWholeVolume);

            rbRange = new RadioButton
            {
                Text = "Range:",
                Location = new Point(10, 70),
                Width = 60,
                Checked = true,
                AutoSize = true
            };
            grpPropagation.Controls.Add(rbRange);

            numRange = new NumericUpDown
            {
                Location = new Point(70, 68),
                Width = 60,
                Minimum = 1,
                Maximum = 100,
                Value = propagationRange
            };
            numRange.ValueChanged += (s, e) =>
            {
                propagationRange = (int)numRange.Value;
            };
            grpPropagation.Controls.Add(numRange);

            // Threshold
            lblThreshold = new Label
            {
                Text = $"Similarity Threshold: {threshold:F2}",
                Location = new Point(10, 390),
                AutoSize = true
            };
            controlPanel.Controls.Add(lblThreshold);

            trkThreshold = new TrackBar
            {
                Location = new Point(10, 410),
                Width = 250,
                Height = 45,
                Minimum = 50,
                Maximum = 100,
                Value = (int)(threshold * 100),
                TickFrequency = 5
            };
            trkThreshold.Scroll += (s, e) =>
            {
                threshold = trkThreshold.Value / 100.0;
                lblThreshold.Text = $"Similarity Threshold: {threshold:F2}";
                numThreshold.Value = (decimal)threshold;
            };
            controlPanel.Controls.Add(trkThreshold);

            numThreshold = new NumericUpDown
            {
                Location = new Point(270, 410),
                Width = 60,
                Minimum = 0.50m,
                Maximum = 1.00m,
                Value = (decimal)threshold,
                DecimalPlaces = 2,
                Increment = 0.05m
            };
            numThreshold.ValueChanged += (s, e) =>
            {
                threshold = (double)numThreshold.Value;
                lblThreshold.Text = $"Similarity Threshold: {threshold:F2}";
                trkThreshold.Value = (int)(threshold * 100);
            };
            controlPanel.Controls.Add(numThreshold);

            chkUseGPU = new CheckBox
            {
                Text = "Use GPU acceleration (initializing...)",
                Location = new Point(190, 228),
                Width = 200,
                AutoSize = true,
                Enabled = false,
                Checked = true
            };
            chkUseGPU.CheckedChanged += (s, e) =>
            {
                useGPU = chkUseGPU.Checked;
                Logger.Log($"[TextureClassifier] GPU acceleration set to: {useGPU}");
            };
            controlPanel.Controls.Add(chkUseGPU);

            Task.Run(() => InitializeGPU());

            // Action buttons
            btnTrain = new Button
            {
                Text = "Extract Features",
                Location = new Point(10, 465),
                Width = 140,
                Height = 30
            };
            btnTrain.Click += (s, e) => TrainClassifier();
            controlPanel.Controls.Add(btnTrain);

            btnApply = new Button
            {
                Text = "Apply Classification",
                Location = new Point(160, 465),
                Width = 140,
                Height = 30,
                Enabled = false
            };
            btnApply.Click += (s, e) => ApplyClassification();
            controlPanel.Controls.Add(btnApply);

            btnClose = new Button
            {
                Text = "Close",
                Location = new Point(10, 505),
                Width = 100,
                Height = 30
            };
            btnClose.Click += (s, e) => textureForm.Close();
            controlPanel.Controls.Add(btnClose);

            statusLabel = new Label
            {
                Text = "Draw a rectangle to select texture region",
                Location = new Point(10, 545),
                AutoSize = true
            };
            controlPanel.Controls.Add(statusLabel);

            // --------- XY Slice Controls ---------
            lblSliceXY = new Label
            {
                Text = $"XY Slice: {xySlice} / {(mainForm.GetDepth() > 0 ? mainForm.GetDepth() - 1 : 0)}",
                Location = new Point(10, 570),
                AutoSize = true
            };
            controlPanel.Controls.Add(lblSliceXY);

            sliderXY = new TrackBar
            {
                Minimum = 0,
                Maximum = mainForm.GetDepth() > 0 ? mainForm.GetDepth() - 1 : 0,
                Value = xySlice,
                Location = new Point(10, 590),
                Width = 220,
                TickStyle = TickStyle.None
            };
            sliderXY.Scroll += (s, e) =>
            {
                xySlice = sliderXY.Value;
                UpdateSliceControls();
                UpdateViewers();
            };
            controlPanel.Controls.Add(sliderXY);

            numXY = new NumericUpDown
            {
                Minimum = 0,
                Maximum = sliderXY.Maximum,
                Value = xySlice,
                Location = new Point(240, 590),
                Width = 60
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
            controlPanel.Controls.Add(numXY);

            // --------- XZ Slice Controls ---------
            lblSliceXZ = new Label
            {
                Text = $"XZ Row: {xzRow} / {(mainForm.GetHeight() > 0 ? mainForm.GetHeight() - 1 : 0)}",
                Location = new Point(10, 620),
                AutoSize = true
            };
            controlPanel.Controls.Add(lblSliceXZ);

            sliderXZ = new TrackBar
            {
                Minimum = 0,
                Maximum = mainForm.GetHeight() > 0 ? mainForm.GetHeight() - 1 : 0,
                Value = xzRow,
                Location = new Point(10, 640),
                Width = 220,
                TickStyle = TickStyle.None
            };
            sliderXZ.Scroll += (s, e) =>
            {
                xzRow = sliderXZ.Value;
                UpdateSliceControls();
                UpdateViewers();
            };
            controlPanel.Controls.Add(sliderXZ);

            numXZ = new NumericUpDown
            {
                Minimum = 0,
                Maximum = sliderXZ.Maximum,
                Value = xzRow,
                Location = new Point(240, 640),
                Width = 60
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
            controlPanel.Controls.Add(numXZ);

            // --------- YZ Slice Controls ---------
            lblSliceYZ = new Label
            {
                Text = $"YZ Column: {yzCol} / {(mainForm.GetWidth() > 0 ? mainForm.GetWidth() - 1 : 0)}",
                Location = new Point(10, 670),
                AutoSize = true
            };
            controlPanel.Controls.Add(lblSliceYZ);

            sliderYZ = new TrackBar
            {
                Minimum = 0,
                Maximum = mainForm.GetWidth() > 0 ? mainForm.GetWidth() - 1 : 0,
                Value = yzCol,
                Location = new Point(10, 690),
                Width = 220,
                TickStyle = TickStyle.None
            };
            sliderYZ.Scroll += (s, e) =>
            {
                yzCol = sliderYZ.Value;
                UpdateSliceControls();
                UpdateViewers();
            };
            controlPanel.Controls.Add(sliderYZ);

            numYZ = new NumericUpDown
            {
                Minimum = 0,
                Maximum = sliderYZ.Maximum,
                Value = yzCol,
                Location = new Point(240, 690),
                Width = 60
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
            controlPanel.Controls.Add(numYZ);

            // Sync with main view checkbox
            chkSyncWithMainView = new CheckBox
            {
                Text = "Sync with main view",
                Location = new Point(10, 720),
                Checked = true,
                AutoSize = true
            };
            chkSyncWithMainView.CheckedChanged += (s, e) =>
            {
                if (chkSyncWithMainView.Checked)
                {
                    mainForm.CurrentSlice = xySlice;
                    mainForm.XzSliceY = xzRow;
                    mainForm.YzSliceX = yzCol;
                }
            };
            controlPanel.Controls.Add(chkSyncWithMainView);

            // Add help text
            Label lblHelp = new Label
            {
                Text = "Instructions:\n" +
                      "1. Draw a rectangle on the image to select a texture region.\n" +
                      "2. Click 'Extract Features' to analyze the texture.\n" +
                      "3. Adjust the similarity threshold.\n" +
                      "4. Select propagation options.\n" +
                      "5. Click 'Apply Classification' to segment the volume.",
                Location = new Point(10, 750),
                Size = new Size(320, 100),
                BorderStyle = BorderStyle.FixedSingle
            };
            controlPanel.Controls.Add(lblHelp);

            // Add all components to main layout
            mainLayout.Controls.Add(xyPanel, 0, 0);
            mainLayout.Controls.Add(yzPanel, 1, 0);
            mainLayout.Controls.Add(xzPanel, 0, 1);
            mainLayout.Controls.Add(controlPanel, 1, 1);

            textureForm.Controls.Add(mainLayout);

            // Handle form events
            textureForm.FormClosing += (s, e) =>
            {
                // Unregister callback
                mainForm.UnregisterSliceChangeCallback(sliceChangeCallback);

                // Clean up resources
                xyViewer.Image?.Dispose();
                xzViewer.Image?.Dispose();
                yzViewer.Image?.Dispose();
                DisposeGPU();

                Logger.Log("[TextureClassifier] Form closing, resources cleaned up");
            };

            chkQuantization.CheckedChanged += (s, e) =>
            {
                useQuantization = chkQuantization.Checked;
                numQuantizationLevels.Enabled = useQuantization;
            };

            numQuantizationLevels.ValueChanged += (s, e) =>
            {
                quantizationLevels = (int)numQuantizationLevels.Value;
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

        private double[] ExtractGLCMFeaturesGPU(byte[,] region, int width, int height)
        {
            // Safe UI update method
            void UpdateProgress(string message)
            {
                if (textureForm.InvokeRequired)
                {
                    textureForm.BeginInvoke(new Action(() =>
                    {
                        statusLabel.Text = message;
                        Application.DoEvents();
                    }));
                }
                else
                {
                    statusLabel.Text = message;
                    Application.DoEvents();
                }
            }

            UpdateProgress("Getting region data...");

            // Determine if sampling should be used for large regions
            int pixelCount = width * height;
            int sampleStep = 1;

            // For very large regions, use sampling
            if (pixelCount > 40000)
            {
                sampleStep = (int)Math.Sqrt(pixelCount / 40000.0);
                sampleStep = Math.Max(2, sampleStep); // Ensure minimum step of 2

                UpdateProgress($"Large region detected ({pixelCount} pixels). Using sampling 1:{sampleStep}");
            }

            // Get selection region
            int x = Math.Min(selectionRectangle.X, width - 1);
            int y = Math.Min(selectionRectangle.Y, height - 1);
            int selWidth = Math.Min(selectionRectangle.Width, width - x);
            int selHeight = Math.Min(selectionRectangle.Height, height - y);

            // Calculate new dimensions after sampling
            int sampledWidth = selWidth / sampleStep;
            int sampledHeight = selHeight / sampleStep;

            // Determine matrix size based on quantization settings
            int matrixSize = useQuantization ? quantizationLevels : 256;

            UpdateProgress($"Preparing pixel data" +
                         (useQuantization ? $" with quantization ({quantizationLevels} levels)" : ""));

            // Extract the pixel values from the region with sampling and optionally quantize
            byte[] sampledData = new byte[sampledWidth * sampledHeight];
            int idx = 0;
            for (int j = 0; j < selHeight; j += sampleStep)
            {
                if (j / sampleStep >= sampledHeight) continue;

                for (int i = 0; i < selWidth; i += sampleStep)
                {
                    if (i / sampleStep >= sampledWidth) continue;

                    int pixelX = x + i;
                    int pixelY = y + j;

                    if (pixelX < width && pixelY < height)
                    {
                        byte value = region[pixelX, pixelY];

                        // Apply quantization if enabled
                        if (useQuantization)
                        {
                            value = (byte)(value * quantizationLevels / 256);
                        }

                        sampledData[idx++] = value;
                    }
                }
            }

            UpdateProgress("Allocating GPU memory...");

            // Create zero-filled arrays on CPU side
            int[] glcm0 = new int[matrixSize * matrixSize];
            int[] glcm45 = new int[matrixSize * matrixSize];
            int[] glcm90 = new int[matrixSize * matrixSize];
            int[] glcm135 = new int[matrixSize * matrixSize];

            // Prepare GPU memory
            var regionBuffer = accelerator.Allocate1D(sampledData);
            var glcm0Buffer = accelerator.Allocate1D(glcm0);
            var glcm45Buffer = accelerator.Allocate1D(glcm45);
            var glcm90Buffer = accelerator.Allocate1D(glcm90);
            var glcm135Buffer = accelerator.Allocate1D(glcm135);

            try
            {
                UpdateProgress("Preparing GPU kernel...");

                // Setup kernel with matrix size parameter
                Action<Index1D,
      ArrayView1D<byte, Stride1D.Dense>,
      ArrayView1D<int, Stride1D.Dense>,
      ArrayView1D<int, Stride1D.Dense>,
      ArrayView1D<int, Stride1D.Dense>,
      ArrayView1D<int, Stride1D.Dense>,
      int, int, int> glcmKernelAction = CalculateQuantizedGLCMKernel;
                var kernel = accelerator.LoadAutoGroupedStreamKernel(glcmKernelAction);


                UpdateProgress("Executing GPU kernel...");

                // Launch kernel
                kernel(new Index1D((sampledWidth - 1) * (sampledHeight - 1)), regionBuffer.View,
                      glcm0Buffer.View, glcm45Buffer.View, glcm90Buffer.View, glcm135Buffer.View,
                      sampledWidth, sampledHeight, matrixSize);

                // Synchronize to ensure completion
                accelerator.Synchronize();

                UpdateProgress("Retrieving results from GPU...");

                // Copy results back to host
                int[] data0 = glcm0Buffer.GetAsArray1D();
                int[] data45 = glcm45Buffer.GetAsArray1D();
                int[] data90 = glcm90Buffer.GetAsArray1D();
                int[] data135 = glcm135Buffer.GetAsArray1D();

                UpdateProgress("Processing GLCM matrices...");

                // Convert to 2D arrays with appropriate size
                int[,] glcm0_2d = new int[matrixSize, matrixSize];
                int[,] glcm45_2d = new int[matrixSize, matrixSize];
                int[,] glcm90_2d = new int[matrixSize, matrixSize];
                int[,] glcm135_2d = new int[matrixSize, matrixSize];

                for (int i = 0; i < matrixSize; i++)
                {
                    for (int j = 0; j < matrixSize; j++)
                    {
                        glcm0_2d[i, j] = data0[i * matrixSize + j];
                        glcm45_2d[i, j] = data45[i * matrixSize + j];
                        glcm90_2d[i, j] = data90[i * matrixSize + j];
                        glcm135_2d[i, j] = data135[i * matrixSize + j];
                    }
                }

                UpdateProgress("Calculating texture features...");

                // Extract features from each GLCM matrix
                double[] features0 = ExtractGLCMFeatures(glcm0_2d);
                double[] features45 = ExtractGLCMFeatures(glcm45_2d);
                double[] features90 = ExtractGLCMFeatures(glcm90_2d);
                double[] features135 = ExtractGLCMFeatures(glcm135_2d);

                UpdateProgress("Finalizing feature extraction...");

                // Average the features
                double[] combinedFeatures = new double[features0.Length];
                for (int i = 0; i < features0.Length; i++)
                {
                    combinedFeatures[i] = (features0[i] + features45[i] + features90[i] + features135[i]) / 4.0;
                }

                UpdateProgress("Feature extraction complete");
                return combinedFeatures;
            }
            finally
            {
                // Free GPU memory
                regionBuffer.Dispose();
                glcm0Buffer.Dispose();
                glcm45Buffer.Dispose();
                glcm90Buffer.Dispose();
                glcm135Buffer.Dispose();
            }
        }

        // Updated GPU kernel for GLCM calculation with quantization support
        private static void CalculateQuantizedGLCMKernel(
            Index1D index,
            ArrayView1D<byte, Stride1D.Dense> region,
            ArrayView1D<int, Stride1D.Dense> glcm0,
            ArrayView1D<int, Stride1D.Dense> glcm45,
            ArrayView1D<int, Stride1D.Dense> glcm90,
            ArrayView1D<int, Stride1D.Dense> glcm135,
            int width,
            int height,
            int matrixSize)
        {
            // Get position in the 2D region
            int pixelIndex = index.X;
            int x = pixelIndex % (width - 1);
            int y = pixelIndex / (width - 1);

            if (y < height - 1 && x < width - 1)
            {
                // Get current pixel value
                byte value = region[y * width + x];

                // 0° (horizontal)
                byte neighbor0 = region[y * width + (x + 1)];
                int idx0 = value * matrixSize + neighbor0; // Use matrix size for stride
                if (idx0 < glcm0.Length)
                    ILGPU.Atomic.Add(ref glcm0[idx0], 1);

                // 45° (diagonal)
                if (y > 0)
                {
                    byte neighbor45 = region[(y - 1) * width + (x + 1)];
                    int idx45 = value * matrixSize + neighbor45;
                    if (idx45 < glcm45.Length)
                        ILGPU.Atomic.Add(ref glcm45[idx45], 1);
                }

                // 90° (vertical)
                byte neighbor90 = region[(y + 1) * width + x];
                int idx90 = value * matrixSize + neighbor90;
                if (idx90 < glcm90.Length)
                    ILGPU.Atomic.Add(ref glcm90[idx90], 1);

                // 135° (diagonal)
                if (x > 0)
                {
                    byte neighbor135 = region[(y + 1) * width + (x - 1)];
                    int idx135 = value * matrixSize + neighbor135;
                    if (idx135 < glcm135.Length)
                        ILGPU.Atomic.Add(ref glcm135[idx135], 1);
                }
            }
        }

        // GPU kernel for GLCM calculation
        private static void CalculateGLCMKernel(
            Index1D index,
            ArrayView1D<byte, Stride1D.Dense> region,
            ArrayView1D<int, Stride1D.Dense> glcm0,
            ArrayView1D<int, Stride1D.Dense> glcm45,
            ArrayView1D<int, Stride1D.Dense> glcm90,
            ArrayView1D<int, Stride1D.Dense> glcm135,
            int width,
            int height)
        {
            // Get position in the 2D region
            int pixelIndex = index.X;
            int x = pixelIndex % (width - 1);
            int y = pixelIndex / (width - 1);

            if (y < height - 1 && x < width - 1)
            {
                // Get current pixel value
                byte value = region[y * width + x];

                // 0° (horizontal)
                byte neighbor0 = region[y * width + (x + 1)];
                int idx0 = value * 256 + neighbor0; // Calculate 1D index from 2D coordinates
                ILGPU.Atomic.Add(ref glcm0[idx0], 1);

                // 45° (diagonal)
                if (y > 0)
                {
                    byte neighbor45 = region[(y - 1) * width + (x + 1)];
                    int idx45 = value * 256 + neighbor45;
                    ILGPU.Atomic.Add(ref glcm45[idx45], 1);
                }

                // 90° (vertical)
                byte neighbor90 = region[(y + 1) * width + x];
                int idx90 = value * 256 + neighbor90;
                ILGPU.Atomic.Add(ref glcm90[idx90], 1);

                // 135° (diagonal)
                if (x > 0)
                {
                    byte neighbor135 = region[(y + 1) * width + (x - 1)];
                    int idx135 = value * 256 + neighbor135;
                    ILGPU.Atomic.Add(ref glcm135[idx135], 1);
                }
            }
        }

        private double[] ExtractLBPFeaturesGPU(byte[,] region, int width, int height)
        {
            // Get the region of interest
            int x = Math.Min(selectionRectangle.X, width - 1);
            int y = Math.Min(selectionRectangle.Y, height - 1);
            int selWidth = Math.Min(selectionRectangle.Width, width - x);
            int selHeight = Math.Min(selectionRectangle.Height, height - y);

            if (selWidth < 3 || selHeight < 3)
                throw new ArgumentException("Selected region is too small for LBP analysis");

            // Extract the pixel values to a 1D array
            byte[] regionData = new byte[selWidth * selHeight];
            int idx = 0;
            for (int j = 0; j < selHeight; j++)
            {
                for (int i = 0; i < selWidth; i++)
                {
                    int srcX = x + i;
                    int srcY = y + j;
                    if (srcX < width && srcY < height)
                        regionData[idx++] = region[srcX, srcY];
                }
            }

            // Prepare GPU memory
            var regionBuffer = accelerator.Allocate1D(regionData);
            var histogramBuffer = accelerator.Allocate1D<int>(256);

            try
            {
                // Setup kernel
                Action<Index1D,
      ArrayView1D<byte, Stride1D.Dense>,
      ArrayView1D<int, Stride1D.Dense>,
      int, int> lbpKernelAction = CalculateLBPKernel;
                var kernel = accelerator.LoadAutoGroupedStreamKernel(lbpKernelAction);

                // Launch kernel
                kernel(new Index1D((selWidth - 2) * (selHeight - 2)), regionBuffer.View,
                       histogramBuffer.View, selWidth, selHeight);

                // Synchronize
                accelerator.Synchronize();

                // Copy results back
                int[] histogram = histogramBuffer.GetAsArray1D();

                // Normalize the histogram
                double[] normalizedHistogram = new double[256];
                int totalPixels = (selWidth - 2) * (selHeight - 2);

                if (totalPixels > 0)
                {
                    for (int i = 0; i < 256; i++)
                    {
                        normalizedHistogram[i] = histogram[i] / (double)totalPixels;
                    }
                }

                return normalizedHistogram;
            }
            finally
            {
                // Free GPU memory
                regionBuffer.Dispose();
                histogramBuffer.Dispose();
            }
        }

        // GPU kernel for LBP calculation
        private static void CalculateLBPKernel(
            Index1D index,
            ArrayView1D<byte, Stride1D.Dense> region,
            ArrayView1D<int, Stride1D.Dense> histogram,
            int width,
            int height)
        {
            // Get position in the 2D region (excluding borders)
            int pixelIndex = index.X;
            int x = (pixelIndex % (width - 2)) + 1;
            int y = (pixelIndex / (width - 2)) + 1;

            if (y < height - 1 && x < width - 1)
            {
                // Get center pixel value
                byte center = region[y * width + x];
                byte lbpValue = 0;

                // Top-left
                if (region[(y - 1) * width + (x - 1)] >= center) lbpValue |= 0x01;
                // Top
                if (region[(y - 1) * width + x] >= center) lbpValue |= 0x02;
                // Top-right
                if (region[(y - 1) * width + (x + 1)] >= center) lbpValue |= 0x04;
                // Right
                if (region[y * width + (x + 1)] >= center) lbpValue |= 0x08;
                // Bottom-right
                if (region[(y + 1) * width + (x + 1)] >= center) lbpValue |= 0x10;
                // Bottom
                if (region[(y + 1) * width + x] >= center) lbpValue |= 0x20;
                // Bottom-left
                if (region[(y + 1) * width + (x - 1)] >= center) lbpValue |= 0x40;
                // Left
                if (region[y * width + (x - 1)] >= center) lbpValue |= 0x80;

                // Update histogram
                ILGPU.Atomic.Add(ref histogram[lbpValue], 1);
            }
        }

        private double[] ExtractHistogramFeaturesGPU(byte[,] region, int width, int height)
        {
            // Get the region of interest
            int x = Math.Min(selectionRectangle.X, width - 1);
            int y = Math.Min(selectionRectangle.Y, height - 1);
            int selWidth = Math.Min(selectionRectangle.Width, width - x);
            int selHeight = Math.Min(selectionRectangle.Height, height - y);

            if (selWidth <= 0 || selHeight <= 0)
                throw new ArgumentException("Selected region is empty");

            // Extract the pixel values to a 1D array
            byte[] regionData = new byte[selWidth * selHeight];
            int idx = 0;
            for (int j = 0; j < selHeight; j++)
            {
                for (int i = 0; i < selWidth; i++)
                {
                    int srcX = x + i;
                    int srcY = y + j;
                    if (srcX < width && srcY < height)
                        regionData[idx++] = region[srcX, srcY];
                }
            }

            // Prepare GPU memory
            var regionBuffer = accelerator.Allocate1D(regionData);
            var histogramBuffer = accelerator.Allocate1D<int>(256);

            try
            {
                // Setup kernel
                Action<Index1D,
      ArrayView1D<byte, Stride1D.Dense>,
      ArrayView1D<int, Stride1D.Dense>> histogramKernelAction = CalculateHistogramKernel;
                var kernel = accelerator.LoadAutoGroupedStreamKernel(histogramKernelAction);

                // Launch kernel
                kernel(new Index1D(selWidth * selHeight), regionBuffer.View, histogramBuffer.View);

                // Synchronize
                accelerator.Synchronize();

                // Copy results back
                int[] histogram = histogramBuffer.GetAsArray1D();

                // Calculate statistical features
                double[] normalizedHistogram = new double[256];
                int totalPixels = selWidth * selHeight;

                if (totalPixels > 0)
                {
                    for (int i = 0; i < 256; i++)
                    {
                        normalizedHistogram[i] = histogram[i] / (double)totalPixels;
                    }
                }

                // Calculate features from the histogram
                double mean = 0.0;
                double stdDev = 0.0;
                double skewness = 0.0;
                double kurtosis = 0.0;
                double energy = 0.0;
                double entropy = 0.0;

                // Calculate mean
                for (int i = 0; i < 256; i++)
                {
                    mean += i * normalizedHistogram[i];
                }

                // Calculate other statistics
                for (int i = 0; i < 256; i++)
                {
                    stdDev += Math.Pow(i - mean, 2) * normalizedHistogram[i];
                    energy += normalizedHistogram[i] * normalizedHistogram[i];

                    if (normalizedHistogram[i] > 0)
                        entropy -= normalizedHistogram[i] * Math.Log(normalizedHistogram[i], 2);
                }
                stdDev = Math.Sqrt(stdDev);

                // Calculate higher moments
                if (stdDev > 0)
                {
                    for (int i = 0; i < 256; i++)
                    {
                        skewness += Math.Pow((i - mean) / stdDev, 3) * normalizedHistogram[i];
                        kurtosis += Math.Pow((i - mean) / stdDev, 4) * normalizedHistogram[i];
                    }
                }
                kurtosis -= 3.0; // Excess kurtosis

                return new double[] { mean, stdDev, skewness, kurtosis, energy, entropy };
            }
            finally
            {
                // Free GPU memory
                regionBuffer.Dispose();
                histogramBuffer.Dispose();
            }
        }

        // GPU kernel for histogram calculation
        private static void CalculateHistogramKernel(
            Index1D index,
            ArrayView1D<byte, Stride1D.Dense> region,
            ArrayView1D<int, Stride1D.Dense> histogram)
        {
            if (index.X < region.Length)
            {
                byte value = region[index.X];
                ILGPU.Atomic.Add(ref histogram[value], 1);
            }
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
            };

            // XY viewer mouse events for panning and rectangle drawing
            Point lastPos = Point.Empty;
            bool isPanning = false;

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
                        // Start rectangle drawing
                        isSelectingRectangle = true;
                        currentActiveView = ActiveView.XY; // Set active view to XY
                        UpdateActiveViewButtons();
                        startPoint = new Point((int)pointX, (int)pointY);
                        endPoint = startPoint;
                        statusLabel.Text = "Drawing selection rectangle...";
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
                if (isSelectingRectangle && currentActiveView == ActiveView.XY)
                {
                    // Update the end point of the rectangle as the mouse moves
                    float pointX = (e.X - xyPan.X) / xyZoom;
                    float pointY = (e.Y - xyPan.Y) / xyZoom;

                    // Constrain to image boundaries
                    pointX = Math.Max(0, Math.Min(pointX, mainForm.GetWidth() - 1));
                    pointY = Math.Max(0, Math.Min(pointY, mainForm.GetHeight() - 1));

                    endPoint = new Point((int)pointX, (int)pointY);

                    // Calculate the selection rectangle
                    int x = Math.Min(startPoint.X, endPoint.X);
                    int y = Math.Min(startPoint.Y, endPoint.Y);
                    int width = Math.Abs(endPoint.X - startPoint.X);
                    int height = Math.Abs(endPoint.Y - startPoint.Y);

                    selectionRectangle = new Rectangle(x, y, width, height);
                    xyViewer.Invalidate();
                }
                else if (isPanning && e.Button == MouseButtons.Right)
                {
                    // Calculate the move delta
                    int dx = e.X - lastPos.X;
                    int dy = e.Y - lastPos.Y;

                    // Update the pan position
                    xyPan.X += dx;
                    xyPan.Y += dy;
                    UpdateXYScrollbars();

                    lastPos = e.Location;
                    xyViewer.Invalidate();
                }
            };

            xyViewer.MouseUp += (s, e) =>
            {
                if (isSelectingRectangle && e.Button == MouseButtons.Left && currentActiveView == ActiveView.XY)
                {
                    isSelectingRectangle = false;

                    // Finalize the selection rectangle
                    if (selectionRectangle.Width > 5 && selectionRectangle.Height > 5)
                    {
                        statusLabel.Text = $"Selected region: ({selectionRectangle.X}, {selectionRectangle.Y}, {selectionRectangle.Width}x{selectionRectangle.Height})";
                    }
                    else
                    {
                        selectionRectangle = Rectangle.Empty;
                        statusLabel.Text = "Selection too small. Please select a larger region.";
                    }

                    xyViewer.Invalidate();
                }
                else if (isPanning && e.Button == MouseButtons.Right)
                {
                    isPanning = false;
                }
            };

            // Paint event for custom rendering
            xyViewer.Paint += (s, e) =>
            {
                // Clear background
                e.Graphics.Clear(Color.Black);

                if (xyViewer.Image != null)
                {
                    int imgWidth = xyViewer.Image.Width;
                    int imgHeight = xyViewer.Image.Height;

                    // Draw the image with proper zoom and pan
                    e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    e.Graphics.DrawImage(xyViewer.Image,
                        new Rectangle(
                            xyPan.X,
                            xyPan.Y,
                            (int)(imgWidth * xyZoom),
                            (int)(imgHeight * xyZoom)
                        ));

                    // If we have an active selection rectangle, draw it
                    if (selectionRectangle != Rectangle.Empty && currentActiveView == ActiveView.XY)
                    {
                        // Convert image coordinates to screen coordinates
                        int x = (int)(selectionRectangle.X * xyZoom) + xyPan.X;
                        int y = (int)(selectionRectangle.Y * xyZoom) + xyPan.Y;
                        int width = (int)(selectionRectangle.Width * xyZoom);
                        int height = (int)(selectionRectangle.Height * xyZoom);

                        using (Pen pen = new Pen(Color.Yellow, 2))
                        {
                            e.Graphics.DrawRectangle(pen, x, y, width, height);
                        }
                    }

                    // If we have a classification mask, overlay it
                    if (classificationMask != null && currentActiveView == ActiveView.XY)
                    {
                        // Overlay the mask on the image
                        int width = Math.Min(imgWidth, classificationMask.GetLength(0));
                        int height = Math.Min(imgHeight, classificationMask.GetLength(1));

                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                if (classificationMask[x, y] > 0)
                                {
                                    // Convert to screen coordinates
                                    int screenX = (int)(x * xyZoom) + xyPan.X;
                                    int screenY = (int)(y * xyZoom) + xyPan.Y;

                                    // Draw a semi-transparent overlay for matched pixels
                                    using (SolidBrush brush = new SolidBrush(Color.FromArgb(100, selectedMaterial.Color)))
                                    {
                                        e.Graphics.FillRectangle(brush, screenX, screenY, (int)xyZoom, (int)xyZoom);
                                    }
                                }
                            }
                        }
                    }

                    // Draw header and slice info
                    using (Font font = new Font("Arial", 12, FontStyle.Bold))
                    using (SolidBrush headerBrush = new SolidBrush(Color.Yellow))
                    {
                        e.Graphics.DrawString("XY", font, headerBrush, new PointF(5, 5));
                        e.Graphics.DrawString($"Slice: {xySlice}", font, headerBrush, new PointF(5, 25));
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
                if (e.Delta > 0)
                    xzZoom = Math.Min(10.0f, xzZoom * 1.1f);
                else
                    xzZoom = Math.Max(0.1f, xzZoom * 0.9f);

                UpdateXZScrollbars();
                xzViewer.Invalidate();
            };

            // XZ viewer mouse events for panning and rectangle selection
            Point lastPos = Point.Empty;
            bool isPanning = false;

            xzViewer.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    // Convert mouse coordinates to image coordinates
                    float pointX = (e.X - xzPan.X) / xzZoom;
                    float pointY = (e.Y - xzPan.Y) / xzZoom;

                    // Check if within image bounds
                    if (pointX >= 0 && pointX < mainForm.GetWidth() &&
                        pointY >= 0 && pointY < mainForm.GetDepth())
                    {
                        // Start rectangle drawing
                        isSelectingRectangle = true;
                        currentActiveView = ActiveView.XZ; // Set active view to XZ
                        UpdateActiveViewButtons();
                        startPoint = new Point((int)pointX, (int)pointY);
                        endPoint = startPoint;
                        statusLabel.Text = "Drawing selection rectangle in XZ view...";
                    }
                }
                else if (e.Button == MouseButtons.Right)
                {
                    isPanning = true;
                    lastPos = e.Location;
                }
            };

            xzViewer.MouseMove += (s, e) =>
            {
                if (isSelectingRectangle && currentActiveView == ActiveView.XZ)
                {
                    // Update the end point of the rectangle as the mouse moves
                    float pointX = (e.X - xzPan.X) / xzZoom;
                    float pointY = (e.Y - xzPan.Y) / xzZoom;

                    // Constrain to image boundaries
                    pointX = Math.Max(0, Math.Min(pointX, mainForm.GetWidth() - 1));
                    pointY = Math.Max(0, Math.Min(pointY, mainForm.GetDepth() - 1));

                    endPoint = new Point((int)pointX, (int)pointY);

                    // Calculate the selection rectangle
                    int x = Math.Min(startPoint.X, endPoint.X);
                    int y = Math.Min(startPoint.Y, endPoint.Y);
                    int width = Math.Abs(endPoint.X - startPoint.X);
                    int height = Math.Abs(endPoint.Y - startPoint.Y);

                    selectionRectangle = new Rectangle(x, y, width, height);
                    xzViewer.Invalidate();
                }
                else if (isPanning && e.Button == MouseButtons.Right)
                {
                    int dx = e.X - lastPos.X;
                    int dy = e.Y - lastPos.Y;

                    xzPan.X += dx;
                    xzPan.Y += dy;
                    UpdateXZScrollbars();

                    lastPos = e.Location;
                    xzViewer.Invalidate();
                }
            };

            xzViewer.MouseUp += (s, e) =>
            {
                if (isSelectingRectangle && e.Button == MouseButtons.Left && currentActiveView == ActiveView.XZ)
                {
                    isSelectingRectangle = false;

                    // Finalize the selection rectangle
                    if (selectionRectangle.Width > 5 && selectionRectangle.Height > 5)
                    {
                        statusLabel.Text = $"Selected XZ region: ({selectionRectangle.X}, {selectionRectangle.Y}, {selectionRectangle.Width}x{selectionRectangle.Height})";
                    }
                    else
                    {
                        selectionRectangle = Rectangle.Empty;
                        statusLabel.Text = "Selection too small. Please select a larger region.";
                    }

                    xzViewer.Invalidate();
                }
                else if (e.Button == MouseButtons.Right)
                {
                    isPanning = false;
                }
            };

            // Paint event for XZ view
            xzViewer.Paint += (s, e) =>
            {
                e.Graphics.Clear(Color.Black);

                if (xzViewer.Image != null)
                {
                    int imgWidth = xzViewer.Image.Width;
                    int imgHeight = xzViewer.Image.Height;

                    e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    e.Graphics.DrawImage(xzViewer.Image,
                        new Rectangle(
                            xzPan.X,
                            xzPan.Y,
                            (int)(imgWidth * xzZoom),
                            (int)(imgHeight * xzZoom)
                        ));

                    // If we have an active selection rectangle, draw it
                    if (selectionRectangle != Rectangle.Empty && currentActiveView == ActiveView.XZ)
                    {
                        // Convert image coordinates to screen coordinates
                        int x = (int)(selectionRectangle.X * xzZoom) + xzPan.X;
                        int y = (int)(selectionRectangle.Y * xzZoom) + xzPan.Y;
                        int width = (int)(selectionRectangle.Width * xzZoom);
                        int height = (int)(selectionRectangle.Height * xzZoom);

                        using (Pen pen = new Pen(Color.Yellow, 2))
                        {
                            e.Graphics.DrawRectangle(pen, x, y, width, height);
                        }
                    }

                    // If we have a classification mask, overlay it
                    if (classificationMask != null && currentActiveView == ActiveView.XZ)
                    {
                        // Overlay the mask on the image
                        int width = Math.Min(imgWidth, classificationMask.GetLength(0));
                        int height = Math.Min(imgHeight, classificationMask.GetLength(1));

                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                if (classificationMask[x, y] > 0)
                                {
                                    // Convert to screen coordinates
                                    int screenX = (int)(x * xzZoom) + xzPan.X;
                                    int screenY = (int)(y * xzZoom) + xzPan.Y;

                                    // Draw a semi-transparent overlay for matched pixels
                                    using (SolidBrush brush = new SolidBrush(Color.FromArgb(100, selectedMaterial.Color)))
                                    {
                                        e.Graphics.FillRectangle(brush, screenX, screenY, (int)xzZoom, (int)xzZoom);
                                    }
                                }
                            }
                        }
                    }

                    // Draw header and slice info
                    using (Font font = new Font("Arial", 12, FontStyle.Bold))
                    using (SolidBrush headerBrush = new SolidBrush(Color.Yellow))
                    {
                        e.Graphics.DrawString("XZ", font, headerBrush, new PointF(5, 5));
                        e.Graphics.DrawString($"Row: {xzRow}", font, headerBrush, new PointF(5, 25));
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
                if (e.Delta > 0)
                    yzZoom = Math.Min(10.0f, yzZoom * 1.1f);
                else
                    yzZoom = Math.Max(0.1f, yzZoom * 0.9f);

                UpdateYZScrollbars();
                yzViewer.Invalidate();
            };

            // YZ viewer mouse events for panning and selection
            Point lastPos = Point.Empty;
            bool isPanning = false;

            yzViewer.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    // Convert mouse coordinates to image coordinates
                    float pointX = (e.X - yzPan.X) / yzZoom;
                    float pointY = (e.Y - yzPan.Y) / yzZoom;

                    // Check if within image bounds
                    if (pointX >= 0 && pointX < mainForm.GetDepth() &&
                        pointY >= 0 && pointY < mainForm.GetHeight())
                    {
                        // Start rectangle drawing
                        isSelectingRectangle = true;
                        currentActiveView = ActiveView.YZ; // Set active view to YZ
                        UpdateActiveViewButtons();
                        startPoint = new Point((int)pointX, (int)pointY);
                        endPoint = startPoint;
                        statusLabel.Text = "Drawing selection rectangle in YZ view...";
                    }
                }
                else if (e.Button == MouseButtons.Right)
                {
                    isPanning = true;
                    lastPos = e.Location;
                }
            };

            yzViewer.MouseMove += (s, e) =>
            {
                if (isSelectingRectangle && currentActiveView == ActiveView.YZ)
                {
                    // Update the end point of the rectangle as the mouse moves
                    float pointX = (e.X - yzPan.X) / yzZoom;
                    float pointY = (e.Y - yzPan.Y) / yzZoom;

                    // Constrain to image boundaries
                    pointX = Math.Max(0, Math.Min(pointX, mainForm.GetDepth() - 1));
                    pointY = Math.Max(0, Math.Min(pointY, mainForm.GetHeight() - 1));

                    endPoint = new Point((int)pointX, (int)pointY);

                    // Calculate the selection rectangle
                    int x = Math.Min(startPoint.X, endPoint.X);
                    int y = Math.Min(startPoint.Y, endPoint.Y);
                    int width = Math.Abs(endPoint.X - startPoint.X);
                    int height = Math.Abs(endPoint.Y - startPoint.Y);

                    selectionRectangle = new Rectangle(x, y, width, height);
                    yzViewer.Invalidate();
                }
                else if (isPanning && e.Button == MouseButtons.Right)
                {
                    int dx = e.X - lastPos.X;
                    int dy = e.Y - lastPos.Y;

                    yzPan.X += dx;
                    yzPan.Y += dy;
                    UpdateYZScrollbars();

                    lastPos = e.Location;
                    yzViewer.Invalidate();
                }
            };

            yzViewer.MouseUp += (s, e) =>
            {
                if (isSelectingRectangle && e.Button == MouseButtons.Left && currentActiveView == ActiveView.YZ)
                {
                    isSelectingRectangle = false;

                    // Finalize the selection rectangle
                    if (selectionRectangle.Width > 5 && selectionRectangle.Height > 5)
                    {
                        statusLabel.Text = $"Selected YZ region: ({selectionRectangle.X}, {selectionRectangle.Y}, {selectionRectangle.Width}x{selectionRectangle.Height})";
                    }
                    else
                    {
                        selectionRectangle = Rectangle.Empty;
                        statusLabel.Text = "Selection too small. Please select a larger region.";
                    }

                    yzViewer.Invalidate();
                }
                else if (e.Button == MouseButtons.Right)
                {
                    isPanning = false;
                }
            };

            // Paint event for YZ view
            yzViewer.Paint += (s, e) =>
            {
                e.Graphics.Clear(Color.Black);

                if (yzViewer.Image != null)
                {
                    int imgWidth = yzViewer.Image.Width;
                    int imgHeight = yzViewer.Image.Height;

                    e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    e.Graphics.DrawImage(yzViewer.Image,
                        new Rectangle(
                            yzPan.X,
                            yzPan.Y,
                            (int)(imgWidth * yzZoom),
                            (int)(imgHeight * yzZoom)
                        ));

                    // If we have an active selection rectangle, draw it
                    if (selectionRectangle != Rectangle.Empty && currentActiveView == ActiveView.YZ)
                    {
                        // Convert image coordinates to screen coordinates
                        int x = (int)(selectionRectangle.X * yzZoom) + yzPan.X;
                        int y = (int)(selectionRectangle.Y * yzZoom) + yzPan.Y;
                        int width = (int)(selectionRectangle.Width * yzZoom);
                        int height = (int)(selectionRectangle.Height * yzZoom);

                        using (Pen pen = new Pen(Color.Yellow, 2))
                        {
                            e.Graphics.DrawRectangle(pen, x, y, width, height);
                        }
                    }

                    // If we have a classification mask, overlay it
                    if (classificationMask != null && currentActiveView == ActiveView.YZ)
                    {
                        // Overlay the mask on the image
                        int width = Math.Min(imgWidth, classificationMask.GetLength(0));
                        int height = Math.Min(imgHeight, classificationMask.GetLength(1));

                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                if (classificationMask[x, y] > 0)
                                {
                                    // Convert to screen coordinates
                                    int screenX = (int)(x * yzZoom) + yzPan.X;
                                    int screenY = (int)(y * yzZoom) + yzPan.Y;

                                    // Draw a semi-transparent overlay for matched pixels
                                    using (SolidBrush brush = new SolidBrush(Color.FromArgb(100, selectedMaterial.Color)))
                                    {
                                        e.Graphics.FillRectangle(brush, screenX, screenY, (int)yzZoom, (int)yzZoom);
                                    }
                                }
                            }
                        }
                    }

                    // Draw header and slice info
                    using (Font font = new Font("Arial", 12, FontStyle.Bold))
                    using (SolidBrush headerBrush = new SolidBrush(Color.Yellow))
                    {
                        e.Graphics.DrawString("YZ", font, headerBrush, new PointF(5, 5));
                        e.Graphics.DrawString($"Column: {yzCol}", font, headerBrush, new PointF(5, 25));
                    }
                }
            };
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

        private void UpdateSliceControls()
        {
            // Update XY slice controls
            if (lblSliceXY != null)
                lblSliceXY.Text = $"XY Slice: {xySlice} / {sliderXY.Maximum}";

            if (sliderXY != null && sliderXY.Value != xySlice)
                sliderXY.Value = xySlice;

            if (numXY != null && numXY.Value != xySlice)
                numXY.Value = xySlice;

            // Update XZ slice controls
            if (lblSliceXZ != null)
                lblSliceXZ.Text = $"XZ Row: {xzRow} / {sliderXZ.Maximum}";

            if (sliderXZ != null && sliderXZ.Value != xzRow)
                sliderXZ.Value = xzRow;

            if (numXZ != null && numXZ.Value != xzRow)
                numXZ.Value = xzRow;

            // Update YZ slice controls
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

        private void UpdateViewers()
        {
            try
            {
                // Update XY viewer
                using (Bitmap xySliceBitmap = CreateSliceBitmap(xySlice))
                {
                    if (xyViewer.Image != null)
                        xyViewer.Image.Dispose();

                    xyViewer.Image = new Bitmap(xySliceBitmap);
                    UpdateXYScrollbars();
                }

                // Update XZ viewer
                using (Bitmap xzSliceBitmap = CreateXZSliceBitmap(xzRow))
                {
                    if (xzViewer.Image != null)
                        xzViewer.Image.Dispose();

                    xzViewer.Image = new Bitmap(xzSliceBitmap);
                    UpdateXZScrollbars();
                }

                // Update YZ viewer
                using (Bitmap yzSliceBitmap = CreateYZSliceBitmap(yzCol))
                {
                    if (yzViewer.Image != null)
                        yzViewer.Image.Dispose();

                    yzViewer.Image = new Bitmap(yzSliceBitmap);
                    UpdateYZScrollbars();
                }

                // Repaint all viewers
                xyViewer.Invalidate();
                xzViewer.Invalidate();
                yzViewer.Invalidate();
            }
            catch (Exception ex)
            {
                Logger.Log($"[TextureClassifier] Error updating viewers: {ex.Message}");
            }
        }

        private unsafe Bitmap CreateSliceBitmap(int sliceZ)
        {
            // Try to get from cache first
            Bitmap cachedBitmap = xySliceCache.Get(sliceZ);
            if (cachedBitmap != null)
            {
                // Return a copy of the cached bitmap
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

            // Add to cache
            Bitmap cacheCopy = new Bitmap(bmp);
            xySliceCache.Add(sliceZ, cacheCopy);

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

            return bmp;
        }

        private void TrainClassifier()
        {
            if (selectionRectangle.IsEmpty)
            {
                MessageBox.Show("Please select a region first.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Disable the button to prevent multiple clicks
            btnTrain.Enabled = false;
            statusLabel.Text = "Starting feature extraction...";
            Application.DoEvents();

            // Run the intensive operation in a background thread
            Task.Run(() =>
            {
                try
                {
                    // Extract features on background thread
                    double[] extractedFeatures = ExtractFeaturesFromSelection();

                    // Update UI on main thread when done
                    textureForm.BeginInvoke(new Action(() =>
                    {
                        referenceFeatures = extractedFeatures;
                        btnApply.Enabled = true;
                        btnTrain.Enabled = true;
                        statusLabel.Text = "Features extracted successfully!";
                        ClassifyCurrentSlice(); // Show immediate results
                    }));
                }
                catch (Exception ex)
                {
                    textureForm.BeginInvoke(new Action(() =>
                    {
                        btnTrain.Enabled = true;
                        statusLabel.Text = "Error extracting features.";
                        MessageBox.Show($"Error: {ex.Message}", "Feature Extraction Error",
                                       MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                }
            });
        }

        private double[] ExtractFeaturesFromSelection()
        {
            try
            {
                // Get view data based on active view
                byte[,] viewData;
                int width, height;

                switch (currentActiveView)
                {
                    case ActiveView.XZ:
                        viewData = GetXZViewData();
                        width = mainForm.GetWidth();
                        height = mainForm.GetDepth();
                        break;
                    case ActiveView.YZ:
                        viewData = GetYZViewData();
                        width = mainForm.GetDepth();
                        height = mainForm.GetHeight();
                        break;
                    case ActiveView.XY:
                    default:
                        viewData = GetXYViewData();
                        width = mainForm.GetWidth();
                        height = mainForm.GetHeight();
                        break;
                }

                // Try GPU if enabled and initialized
                if (useGPU && gpuInitialized)
                {
                    if (rbGlcm.Checked)
                        return ExtractGLCMFeaturesGPU(viewData, width, height);
                    else if (rbLbp.Checked)
                        return ExtractLBPFeaturesGPU(viewData, width, height);
                    else if (rbHistogram.Checked)
                        return ExtractHistogramFeaturesGPU(viewData, width, height);
                    else
                        return ExtractGLCMFeaturesGPU(viewData, width, height); // Default
                }

                // Use CPU implementation if GPU failed or not available
                if (rbGlcm.Checked)
                    return ExtractGLCMFeatures(viewData, width, height);
                else if (rbLbp.Checked)
                    return ExtractLBPFeatures(viewData, width, height);
                else if (rbHistogram.Checked)
                    return ExtractHistogramFeatures(viewData, width, height);
                else
                    return ExtractGLCMFeatures(viewData, width, height); // Default
            }
            catch (Exception ex)
            {
                Logger.Log($"[TextureClassifier] Feature extraction failed: {ex.Message}. Falling back to basic method.");
                // Fall back to basic method
                return ExtractHistogramFeaturesBasic();
            }
        }

        // Get view data methods
        private byte[,] GetXYViewData()
        {
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            byte[,] data = new byte[width, height];

            Parallel.For(0, height, y => {
                for (int x = 0; x < width; x++)
                {
                    data[x, y] = mainForm.volumeData[x, y, xySlice];
                }
            });

            return data;
        }

        private byte[,] GetXZViewData()
        {
            int width = mainForm.GetWidth();
            int depth = mainForm.GetDepth();
            byte[,] data = new byte[width, depth];

            Parallel.For(0, depth, z => {
                for (int x = 0; x < width; x++)
                {
                    data[x, z] = mainForm.volumeData[x, xzRow, z];
                }
            });

            return data;
        }

        private byte[,] GetYZViewData()
        {
            int depth = mainForm.GetDepth();
            int height = mainForm.GetHeight();
            byte[,] data = new byte[depth, height];

            Parallel.For(0, height, y => {
                for (int z = 0; z < depth; z++)
                {
                    data[z, y] = mainForm.volumeData[yzCol, y, z];
                }
            });

            return data;
        }

        private double[] ExtractGLCMFeatures(byte[,] region, int width, int height)
        {
            // Sample only a subset of pixels for large regions
            const int MAX_PIXELS = 10000; // Set a reasonable limit

            int sampleStep = 1;
            int totalPixels = width * height;

            if (totalPixels > MAX_PIXELS)
            {
                // Calculate step size to get approximately MAX_PIXELS samples
                sampleStep = (int)Math.Sqrt((double)totalPixels / MAX_PIXELS);
                statusLabel.Text = $"Using sampling (step={sampleStep}) for large region...";
                Application.DoEvents();
            }

            // Extract only sampled pixels with actual selection rectangle
            int x = Math.Min(selectionRectangle.X, width - 1);
            int y = Math.Min(selectionRectangle.Y, height - 1);
            int selWidth = Math.Min(selectionRectangle.Width, width - x);
            int selHeight = Math.Min(selectionRectangle.Height, height - y);

            int sampledWidth = Math.Max(1, selWidth / sampleStep);
            int sampledHeight = Math.Max(1, selHeight / sampleStep);
            byte[,] sampledRegion = new byte[sampledWidth, sampledHeight];

            // Sample from the selection region
            for (int j = 0; j < selHeight; j += sampleStep)
            {
                int sampledY = j / sampleStep;
                if (sampledY >= sampledHeight) continue;

                for (int i = 0; i < selWidth; i += sampleStep)
                {
                    int sampledX = i / sampleStep;
                    if (sampledX >= sampledWidth) continue;

                    int srcX = x + i;
                    int srcY = y + j;

                    if (srcX < width && srcY < height)
                        sampledRegion[sampledX, sampledY] = region[srcX, srcY];
                }
            }

            // Initialize the GLCM matrices before parallel processing
            int[,] glcm0 = null;
            int[,] glcm45 = null;
            int[,] glcm90 = null;
            int[,] glcm135 = null;

            // Calculate all GLCMs in parallel
            Parallel.Invoke(
                () => { glcm0 = CalculateGLCM(sampledRegion, 1, 0); },   // 0°
                () => { glcm45 = CalculateGLCM(sampledRegion, 1, 1); },  // 45°
                () => { glcm90 = CalculateGLCM(sampledRegion, 0, 1); },  // 90°
                () => { glcm135 = CalculateGLCM(sampledRegion, -1, 1); } // 135°
            );

            // Initialize the feature arrays before parallel processing
            double[] features0 = null;
            double[] features45 = null;
            double[] features90 = null;
            double[] features135 = null;

            // Extract features in parallel
            Parallel.Invoke(
                () => { features0 = ExtractGLCMFeatures(glcm0); },
                () => { features45 = ExtractGLCMFeatures(glcm45); },
                () => { features90 = ExtractGLCMFeatures(glcm90); },
                () => { features135 = ExtractGLCMFeatures(glcm135); }
            );

            // Null safety check
            if (features0 == null || features45 == null || features90 == null || features135 == null)
            {
                // Fallback to sequential processing if parallel failed
                Logger.Log("[TextureClassifier] Parallel GLCM processing failed, falling back to sequential");

                if (glcm0 == null) glcm0 = CalculateGLCM(sampledRegion, 1, 0);
                if (glcm45 == null) glcm45 = CalculateGLCM(sampledRegion, 1, 1);
                if (glcm90 == null) glcm90 = CalculateGLCM(sampledRegion, 0, 1);
                if (glcm135 == null) glcm135 = CalculateGLCM(sampledRegion, -1, 1);

                features0 = ExtractGLCMFeatures(glcm0);
                features45 = ExtractGLCMFeatures(glcm45);
                features90 = ExtractGLCMFeatures(glcm90);
                features135 = ExtractGLCMFeatures(glcm135);
            }

            // Average the features from all directions
            double[] combinedFeatures = new double[features0.Length];
            for (int i = 0; i < features0.Length; i++)
            {
                combinedFeatures[i] = (features0[i] + features45[i] + features90[i] + features135[i]) / 4.0;
            }

            return combinedFeatures;
        }

        private int[,] CalculateGLCM(byte[,] region, int offsetX, int offsetY)
        {
            int width = region.GetLength(0);
            int height = region.GetLength(1);

            // Create a 256x256 GLCM matrix (for all possible gray levels)
            int[,] glcm = new int[256, 256];

            // Count occurrences of gray level pairs
            int pairCount = 0;
            for (int j = 0; j < height; j++)
            {
                for (int i = 0; i < width; i++)
                {
                    int ni = i + offsetX;
                    int nj = j + offsetY;

                    if (ni >= 0 && ni < width && nj >= 0 && nj < height)
                    {
                        int value = region[i, j];
                        int neighborValue = region[ni, nj];
                        glcm[value, neighborValue]++;
                        pairCount++;
                    }
                }
            }

            // Normalize the GLCM if any pairs were found
            if (pairCount > 0)
            {
                for (int i = 0; i < 256; i++)
                {
                    for (int j = 0; j < 256; j++)
                    {
                        glcm[i, j] = (int)((glcm[i, j] / (double)pairCount) * 1000000); // Scale for integer math
                    }
                }
            }

            return glcm;
        }

        private double[] ExtractGLCMFeatures(int[,] glcm)
        {
            // Get matrix size
            int matrixSize = glcm.GetLength(0);

            // Compute statistical properties from GLCM
            double contrast = 0.0;
            double dissimilarity = 0.0;
            double homogeneity = 0.0;
            double energy = 0.0;
            double correlation = 0.0;

            // Calculate mean and variance
            double mean_i = 0.0, mean_j = 0.0;
            double stddev_i = 0.0, stddev_j = 0.0;

            // Calculate means
            for (int i = 0; i < matrixSize; i++)
            {
                for (int j = 0; j < matrixSize; j++)
                {
                    mean_i += i * (glcm[i, j] / 1000000.0);
                    mean_j += j * (glcm[i, j] / 1000000.0);
                }
            }

            // Calculate standard deviations
            for (int i = 0; i < matrixSize; i++)
            {
                for (int j = 0; j < matrixSize; j++)
                {
                    stddev_i += Math.Pow(i - mean_i, 2) * (glcm[i, j] / 1000000.0);
                    stddev_j += Math.Pow(j - mean_j, 2) * (glcm[i, j] / 1000000.0);
                }
            }

            stddev_i = Math.Sqrt(stddev_i);
            stddev_j = Math.Sqrt(stddev_j);

            // Calculate texture features
            for (int i = 0; i < matrixSize; i++)
            {
                for (int j = 0; j < matrixSize; j++)
                {
                    double p = glcm[i, j] / 1000000.0;
                    contrast += p * Math.Pow(i - j, 2);
                    dissimilarity += p * Math.Abs(i - j);
                    homogeneity += p / (1 + Math.Pow(i - j, 2));
                    energy += p * p;

                    // Avoid division by zero
                    if (stddev_i > 0 && stddev_j > 0)
                    {
                        correlation += (p * (i - mean_i) * (j - mean_j)) / (stddev_i * stddev_j);
                    }
                }
            }

            // Return features as an array
            return new double[] { contrast, dissimilarity, homogeneity, energy, correlation };
        }

        private double[] ExtractLBPFeatures(byte[,] region, int width, int height)
        {
            // Get the region of interest from the current active view
            int x = Math.Min(selectionRectangle.X, width - 1);
            int y = Math.Min(selectionRectangle.Y, height - 1);
            int selWidth = Math.Min(selectionRectangle.Width, width - x);
            int selHeight = Math.Min(selectionRectangle.Height, height - y);

            if (selWidth < 3 || selHeight < 3)
                throw new ArgumentException("Selected region is too small for LBP analysis");

            // Extract the pixel values
            byte[,] sampledRegion = new byte[selWidth, selHeight];
            for (int j = 0; j < selHeight; j++)
            {
                for (int i = 0; i < selWidth; i++)
                {
                    int srcX = x + i;
                    int srcY = y + j;
                    if (srcX < width && srcY < height)
                        sampledRegion[i, j] = region[srcX, srcY];
                }
            }

            // Compute LBP
            int[] histogram = new int[256]; // For 8 neighbors, we have 2^8 = 256 possible patterns

            Parallel.For(1, selHeight - 1, j => {
                for (int i = 1; i < selWidth - 1; i++)
                {
                    byte centerValue = sampledRegion[i, j];
                    byte lbpValue = 0;

                    // Check all 8 neighbors
                    if (sampledRegion[i - 1, j - 1] >= centerValue) lbpValue |= 0x01;  // Top-left
                    if (sampledRegion[i, j - 1] >= centerValue) lbpValue |= 0x02;  // Top
                    if (sampledRegion[i + 1, j - 1] >= centerValue) lbpValue |= 0x04;  // Top-right
                    if (sampledRegion[i + 1, j] >= centerValue) lbpValue |= 0x08;  // Right
                    if (sampledRegion[i + 1, j + 1] >= centerValue) lbpValue |= 0x10;  // Bottom-right
                    if (sampledRegion[i, j + 1] >= centerValue) lbpValue |= 0x20;  // Bottom
                    if (sampledRegion[i - 1, j + 1] >= centerValue) lbpValue |= 0x40;  // Bottom-left
                    if (sampledRegion[i - 1, j] >= centerValue) lbpValue |= 0x80;  // Left

                    // Thread-safe update of histogram
                    System.Threading.Interlocked.Increment(ref histogram[lbpValue]);
                }
            });

            // Normalize the histogram
            double[] normalizedHistogram = new double[256];
            int totalPixels = (selWidth - 2) * (selHeight - 2); // Excluding border pixels

            if (totalPixels > 0)
            {
                for (int i = 0; i < 256; i++)
                {
                    normalizedHistogram[i] = histogram[i] / (double)totalPixels;
                }
            }

            return normalizedHistogram;
        }

        private double[] ExtractHistogramFeatures(byte[,] region, int width, int height)
        {
            // Get the region of interest
            int x = Math.Min(selectionRectangle.X, width - 1);
            int y = Math.Min(selectionRectangle.Y, height - 1);
            int selWidth = Math.Min(selectionRectangle.Width, width - x);
            int selHeight = Math.Min(selectionRectangle.Height, height - y);

            if (selWidth <= 0 || selHeight <= 0)
                throw new ArgumentException("Selected region is empty");

            // Compute histogram with 256 bins (one for each gray level)
            int[] histogram = new int[256];

            // Use parallel processing for better performance
            Parallel.For(0, selHeight, j => {
                for (int i = 0; i < selWidth; i++)
                {
                    int srcX = x + i;
                    int srcY = y + j;
                    if (srcX < width && srcY < height)
                    {
                        byte value = region[srcX, srcY];
                        System.Threading.Interlocked.Increment(ref histogram[value]);
                    }
                }
            });

            // Normalize the histogram
            double[] normalizedHistogram = new double[256];
            int totalPixels = selWidth * selHeight;

            if (totalPixels > 0)
            {
                for (int i = 0; i < 256; i++)
                {
                    normalizedHistogram[i] = histogram[i] / (double)totalPixels;
                }
            }

            // Calculate statistical features from the histogram
            double mean = 0.0;
            double stdDev = 0.0;
            double skewness = 0.0;
            double kurtosis = 0.0;
            double energy = 0.0;
            double entropy = 0.0;

            // Calculate mean
            for (int i = 0; i < 256; i++)
            {
                mean += i * normalizedHistogram[i];
            }

            // Calculate standard deviation, energy, and entropy
            for (int i = 0; i < 256; i++)
            {
                stdDev += Math.Pow(i - mean, 2) * normalizedHistogram[i];
                energy += normalizedHistogram[i] * normalizedHistogram[i];

                if (normalizedHistogram[i] > 0)
                    entropy -= normalizedHistogram[i] * Math.Log(normalizedHistogram[i], 2);
            }
            stdDev = Math.Sqrt(stdDev);

            // Calculate skewness and kurtosis
            if (stdDev > 0)
            {
                for (int i = 0; i < 256; i++)
                {
                    skewness += Math.Pow((i - mean) / stdDev, 3) * normalizedHistogram[i];
                    kurtosis += Math.Pow((i - mean) / stdDev, 4) * normalizedHistogram[i];
                }
            }
            kurtosis -= 3.0; // Excess kurtosis (normal distribution has kurtosis = 3)

            return new double[] { mean, stdDev, skewness, kurtosis, energy, entropy };
        }

        private double[] ExtractHistogramFeaturesBasic()
        {
            // Fallback method when other methods fail
            int[] histogram = new int[256];
            int totalPixels = 0;

            // Get data based on active view
            switch (currentActiveView)
            {
                case ActiveView.XZ:
                    // XZ view: x = x, y = z
                    {
                        int x = selectionRectangle.X;
                        int z = selectionRectangle.Y;
                        int width = selectionRectangle.Width;
                        int height = selectionRectangle.Height;

                        for (int dz = 0; dz < height && z + dz < mainForm.GetDepth(); dz++)
                        {
                            for (int dx = 0; dx < width && x + dx < mainForm.GetWidth(); dx++)
                            {
                                histogram[mainForm.volumeData[x + dx, xzRow, z + dz]]++;
                                totalPixels++;
                            }
                        }
                    }
                    break;

                case ActiveView.YZ:
                    // YZ view: x = z, y = y
                    {
                        int z = selectionRectangle.X;
                        int y = selectionRectangle.Y;
                        int width = selectionRectangle.Width;
                        int height = selectionRectangle.Height;

                        for (int dy = 0; dy < height && y + dy < mainForm.GetHeight(); dy++)
                        {
                            for (int dz = 0; dz < width && z + dz < mainForm.GetDepth(); dz++)
                            {
                                histogram[mainForm.volumeData[yzCol, y + dy, z + dz]]++;
                                totalPixels++;
                            }
                        }
                    }
                    break;

                case ActiveView.XY:
                default:
                    // XY view: x = x, y = y
                    {
                        int x = selectionRectangle.X;
                        int y = selectionRectangle.Y;
                        int width = selectionRectangle.Width;
                        int height = selectionRectangle.Height;

                        for (int dy = 0; dy < height && y + dy < mainForm.GetHeight(); dy++)
                        {
                            for (int dx = 0; dx < width && x + dx < mainForm.GetWidth(); dx++)
                            {
                                histogram[mainForm.volumeData[x + dx, y + dy, xySlice]]++;
                                totalPixels++;
                            }
                        }
                    }
                    break;
            }

            // Calculate basic statistics
            double[] features = new double[6]; // mean, stdDev, skewness, kurtosis, energy, entropy
            double mean = 0.0;

            // Normalize and calculate mean
            double[] normalizedHistogram = new double[256];
            if (totalPixels > 0)
            {
                for (int i = 0; i < 256; i++)
                {
                    normalizedHistogram[i] = histogram[i] / (double)totalPixels;
                    mean += i * normalizedHistogram[i];
                }
            }

            features[0] = mean;

            // Calculate other features
            double stdDev = 0;
            for (int i = 0; i < 256; i++)
            {
                stdDev += Math.Pow(i - mean, 2) * normalizedHistogram[i];
                features[4] += normalizedHistogram[i] * normalizedHistogram[i]; // energy

                if (normalizedHistogram[i] > 0)
                    features[5] -= normalizedHistogram[i] * Math.Log(normalizedHistogram[i], 2); // entropy
            }

            features[1] = Math.Sqrt(stdDev);

            // Skip complex calculations for skewness and kurtosis if stdDev is too small
            if (features[1] > 0.0001)
            {
                for (int i = 0; i < 256; i++)
                {
                    features[2] += Math.Pow((i - mean) / features[1], 3) * normalizedHistogram[i]; // skewness
                    features[3] += Math.Pow((i - mean) / features[1], 4) * normalizedHistogram[i]; // kurtosis
                }
                features[3] -= 3; // excess kurtosis
            }

            return features;
        }

        private double CalculateSimilarity(double[] featuresA, double[] featuresB)
        {
            // Different similarity metrics depending on the feature type
            if (rbHistogram.Checked || rbLbp.Checked)
            {
                // For histograms and LBP: Histogram intersection
                return CalculateHistogramIntersection(featuresA, featuresB);
            }
            else // GLCM
            {
                // For GLCM features: Cosine similarity
                return CalculateCosineSimilarity(featuresA, featuresB);
            }
        }

        private double CalculateHistogramIntersection(double[] histogramA, double[] histogramB)
        {
            double intersection = 0.0;
            int length = Math.Min(histogramA.Length, histogramB.Length);

            for (int i = 0; i < length; i++)
            {
                intersection += Math.Min(histogramA[i], histogramB[i]);
            }

            return intersection; // Range [0, 1] where 1 is perfect match
        }

        private double CalculateCosineSimilarity(double[] vectorA, double[] vectorB)
        {
            double dotProduct = 0.0;
            double normA = 0.0;
            double normB = 0.0;
            int length = Math.Min(vectorA.Length, vectorB.Length);

            for (int i = 0; i < length; i++)
            {
                dotProduct += vectorA[i] * vectorB[i];
                normA += vectorA[i] * vectorA[i];
                normB += vectorB[i] * vectorB[i];
            }

            if (normA <= 0.0 || normB <= 0.0)
                return 0.0;

            return dotProduct / (Math.Sqrt(normA) * Math.Sqrt(normB)); // Range [-1, 1], 1 is perfect match
        }

        private void ClassifyCurrentSlice()
        {
            if (referenceFeatures == null)
            {
                MessageBox.Show("Please extract features first by clicking 'Extract Features'.",
                                "No Features", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                statusLabel.Text = "Classifying current slice...";

                // Create a mask for the results based on active view
                int width, height;
                byte[,] viewData;

                switch (currentActiveView)
                {
                    case ActiveView.XZ:
                        width = mainForm.GetWidth();
                        height = mainForm.GetDepth();
                        viewData = GetXZViewData();
                        break;
                    case ActiveView.YZ:
                        width = mainForm.GetDepth();
                        height = mainForm.GetHeight();
                        viewData = GetYZViewData();
                        break;
                    case ActiveView.XY:
                    default:
                        width = mainForm.GetWidth();
                        height = mainForm.GetHeight();
                        viewData = GetXYViewData();
                        break;
                }

                classificationMask = new byte[width, height];

                // Use the specified patch size
                int patchRadius = patchSize / 2;

                // Process each pixel with a sliding window in parallel
                Parallel.For(patchRadius, height - patchRadius, y =>
                {
                    for (int x = patchRadius; x < width - patchRadius; x += 2) // Use stride of 2 for speed
                    {
                        // Extract features from this local region
                        double[] localFeatures = ExtractFeaturesFromPatch(viewData, x, y);

                        // Calculate similarity to the reference features
                        double similarity = CalculateSimilarity(localFeatures, referenceFeatures);

                        // If similarity is above threshold, mark this pixel
                        if (similarity >= threshold)
                        {
                            // Mark a block for better visibility
                            for (int dy = -1; dy <= 1; dy++)
                            {
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    int nx = x + dx;
                                    int ny = y + dy;

                                    if (nx >= 0 && nx < width && ny >= 0 && ny < height)
                                    {
                                        classificationMask[nx, ny] = selectedMaterial.ID;
                                    }
                                }
                            }
                        }
                    }
                });

                // Update the relevant viewer based on active view
                switch (currentActiveView)
                {
                    case ActiveView.XZ:
                        xzViewer.Invalidate();
                        break;
                    case ActiveView.YZ:
                        yzViewer.Invalidate();
                        break;
                    case ActiveView.XY:
                    default:
                        xyViewer.Invalidate();
                        break;
                }

                statusLabel.Text = "Classification complete. Adjust threshold if needed.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during classification: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[TextureClassifier] Classification error: {ex.Message}");
                statusLabel.Text = "Classification failed.";
            }
        }

        private double[] ExtractFeaturesFromPatch(byte[,] viewData, int centerX, int centerY)
        {
            int radius = patchSize / 2;
            byte[,] patch = new byte[patchSize, patchSize];

            // Extract the patch data
            for (int y = 0; y < patchSize; y++)
            {
                for (int x = 0; x < patchSize; x++)
                {
                    int pixelX = centerX - radius + x;
                    int pixelY = centerY - radius + y;

                    if (pixelX >= 0 && pixelX < viewData.GetLength(0) &&
                        pixelY >= 0 && pixelY < viewData.GetLength(1))
                    {
                        patch[x, y] = viewData[pixelX, pixelY];
                    }
                }
            }

            // Extract features based on selected method
            if (rbGlcm.Checked)
            {
                // For GLCM, just compute one direction for efficiency
                int[,] glcm = CalculateGLCM(patch, 1, 0);  // 0° direction
                return ExtractGLCMFeatures(glcm);
            }
            else if (rbLbp.Checked)
            {
                // LBP histogram
                int[] histogram = new int[256];

                for (int y = 1; y < patchSize - 1; y++)
                {
                    for (int x = 1; x < patchSize - 1; x++)
                    {
                        byte centerValue = patch[x, y];
                        byte lbpValue = 0;

                        if (patch[x - 1, y - 1] >= centerValue) lbpValue |= 0x01;
                        if (patch[x, y - 1] >= centerValue) lbpValue |= 0x02;
                        if (patch[x + 1, y - 1] >= centerValue) lbpValue |= 0x04;
                        if (patch[x + 1, y] >= centerValue) lbpValue |= 0x08;
                        if (patch[x + 1, y + 1] >= centerValue) lbpValue |= 0x10;
                        if (patch[x, y + 1] >= centerValue) lbpValue |= 0x20;
                        if (patch[x - 1, y + 1] >= centerValue) lbpValue |= 0x40;
                        if (patch[x - 1, y] >= centerValue) lbpValue |= 0x80;

                        histogram[lbpValue]++;
                    }
                }

                // Normalize the histogram
                double[] normalizedHistogram = new double[256];
                int totalPixels = (patchSize - 2) * (patchSize - 2);

                if (totalPixels > 0)
                {
                    for (int i = 0; i < 256; i++)
                    {
                        normalizedHistogram[i] = histogram[i] / (double)totalPixels;
                    }
                }

                return normalizedHistogram;
            }
            else // Histogram
            {
                // Basic histogram statistics
                int[] histogram = new int[256];

                for (int y = 0; y < patchSize; y++)
                {
                    for (int x = 0; x < patchSize; x++)
                    {
                        histogram[patch[x, y]]++;
                    }
                }

                // Normalize
                double[] normalizedHistogram = new double[256];
                int totalPixels = patchSize * patchSize;

                if (totalPixels > 0)
                {
                    for (int i = 0; i < 256; i++)
                    {
                        normalizedHistogram[i] = histogram[i] / (double)totalPixels;
                    }
                }

                // Statistical features
                double mean = 0.0;
                for (int i = 0; i < 256; i++) mean += i * normalizedHistogram[i];

                double stdDev = 0.0;
                double energy = 0.0;
                double entropy = 0.0;

                for (int i = 0; i < 256; i++)
                {
                    stdDev += Math.Pow(i - mean, 2) * normalizedHistogram[i];
                    energy += normalizedHistogram[i] * normalizedHistogram[i];

                    if (normalizedHistogram[i] > 0)
                        entropy -= normalizedHistogram[i] * Math.Log(normalizedHistogram[i], 2);
                }
                stdDev = Math.Sqrt(stdDev);

                double skewness = 0.0;
                double kurtosis = 0.0;

                if (stdDev > 0)
                {
                    for (int i = 0; i < 256; i++)
                    {
                        skewness += Math.Pow((i - mean) / stdDev, 3) * normalizedHistogram[i];
                        kurtosis += Math.Pow((i - mean) / stdDev, 4) * normalizedHistogram[i];
                    }
                }
                kurtosis -= 3.0;

                return new double[] { mean, stdDev, skewness, kurtosis, energy, entropy };
            }
        }

        private byte[,] ClassifySlice(int sliceZ)
        {
            // Get data for the specified slice
            byte[,] sliceData;
            int width, height;

            switch (currentActiveView)
            {
                case ActiveView.XZ:
                    // For XZ view, we need to classify a constant-Y plane
                    width = mainForm.GetWidth();
                    height = mainForm.GetDepth();
                    sliceData = new byte[width, height];

                    Parallel.For(0, height, z => {
                        for (int x = 0; x < width; x++)
                        {
                            sliceData[x, z] = mainForm.volumeData[x, xzRow, z];
                        }
                    });
                    break;

                case ActiveView.YZ:
                    // For YZ view, we need to classify a constant-X plane
                    width = mainForm.GetDepth();
                    height = mainForm.GetHeight();
                    sliceData = new byte[width, height];

                    Parallel.For(0, height, y => {
                        for (int z = 0; z < width; z++)
                        {
                            sliceData[z, y] = mainForm.volumeData[yzCol, y, z];
                        }
                    });
                    break;

                case ActiveView.XY:
                default:
                    // For XY view, we need a horizontal slice
                    width = mainForm.GetWidth();
                    height = mainForm.GetHeight();
                    sliceData = new byte[width, height];

                    Parallel.For(0, height, y => {
                        for (int x = 0; x < width; x++)
                        {
                            sliceData[x, y] = mainForm.volumeData[x, y, sliceZ];
                        }
                    });
                    break;
            }

            // Create mask to store classification results
            byte[,] sliceMask = new byte[width, height];

            // Use the specified patch size
            int patchRadius = patchSize / 2;

            // Process each pixel with a sliding window in parallel
            Parallel.For(patchRadius, height - patchRadius, y =>
            {
                // Skip some pixels for speed (stride of 2)
                for (int x = patchRadius; x < width - patchRadius; x += 2)
                {
                    // Extract features from this local region
                    double[] localFeatures = ExtractFeaturesFromPatch(sliceData, x, y);

                    // Calculate similarity to the reference features
                    double similarity = CalculateSimilarity(localFeatures, referenceFeatures);

                    // If similarity is above threshold, mark this pixel
                    if (similarity >= threshold)
                    {
                        // Mark a block of pixels for efficiency
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx;
                                int ny = y + dy;

                                if (nx >= 0 && nx < width && ny >= 0 && ny < height)
                                {
                                    sliceMask[nx, ny] = selectedMaterial.ID;
                                }
                            }
                        }
                    }
                }
            });

            return sliceMask;
        }

        private async void ApplyClassification()
        {
            if (referenceFeatures == null || classificationMask == null)
            {
                MessageBox.Show("Please extract features and classify first.",
                                "No Classification", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                // Show progress dialog
                ProgressForm progressForm = new ProgressForm("Applying texture classification...");
                progressForm.Show();

                // Determine the range to process
                int startSlice, endSlice;

                if (rbCurrentSlice.Checked)
                {
                    // Process only the current slice
                    startSlice = xySlice;
                    endSlice = xySlice;
                }
                else if (rbRange.Checked)
                {
                    // Process the specified range around the current slice
                    startSlice = Math.Max(0, xySlice - propagationRange);
                    endSlice = Math.Min(mainForm.GetDepth() - 1, xySlice + propagationRange);
                }
                else // Whole volume
                {
                    // Process the entire volume
                    startSlice = 0;
                    endSlice = mainForm.GetDepth() - 1;
                }

                // Total work to do
                int totalWork = endSlice - startSlice + 1;
                int completedWork = 0;
                object progressLock = new object();

                // Process slices based on active view
                await Task.Run(() => {
                    switch (currentActiveView)
                    {
                        case ActiveView.XZ:
                            // For XZ view (constant Y), we process each slice by varying Y
                            Parallel.For(0, mainForm.GetHeight(), new ParallelOptions
                            { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, y =>
                            {
                                byte[,] resultMask = ClassifyXZSlice(y);

                                // Apply the results to the volume
                                ApplyXZResultToVolume(resultMask, y);

                                // Update progress
                                lock (progressLock)
                                {
                                    completedWork++;
                                    progressForm.SafeUpdateProgress(completedWork, mainForm.GetHeight(), $"Processing XZ slice at Y={y}...");
                                }
                            });
                            break;

                        case ActiveView.YZ:
                            // For YZ view (constant X), we process each slice by varying X
                            Parallel.For(0, mainForm.GetWidth(), new ParallelOptions
                            { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, x =>
                            {
                                byte[,] resultMask = ClassifyYZSlice(x);

                                // Apply the results to the volume
                                ApplyYZResultToVolume(resultMask, x);

                                // Update progress
                                lock (progressLock)
                                {
                                    completedWork++;
                                    progressForm.SafeUpdateProgress(completedWork, mainForm.GetWidth(), $"Processing YZ slice at X={x}...");
                                }
                            });
                            break;

                        case ActiveView.XY:
                        default:
                            // For XY view, process a range of Z slices
                            Parallel.For(startSlice, endSlice + 1, new ParallelOptions
                            { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, z =>
                            {
                                byte[,] sliceMask = ClassifySlice(z);

                                // Apply the classification mask to the volume labels
                                for (int y = 0; y < mainForm.GetHeight(); y++)
                                {
                                    for (int x = 0; x < mainForm.GetWidth(); x++)
                                    {
                                        if (sliceMask[x, y] > 0)
                                        {
                                            mainForm.volumeLabels[x, y, z] = selectedMaterial.ID;
                                        }
                                    }
                                }

                                // Update progress with thread safety
                                lock (progressLock)
                                {
                                    completedWork++;
                                    progressForm.SafeUpdateProgress(completedWork, totalWork, $"Processing slice {z}...");
                                }
                            });
                            break;
                    }
                });

                // Update the main form's view
                mainForm.RenderViews();
                await mainForm.RenderOrthoViewsAsync();
                mainForm.SaveLabelsChk();

                // Close progress form
                progressForm.Close();

                // Update status
                statusLabel.Text = "Classification applied to volume successfully.";
                MessageBox.Show("Texture classification has been applied to the selected range.",
                                "Classification Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error applying classification: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[TextureClassifier] Classification application error: {ex.Message}");
                statusLabel.Text = "Failed to apply classification.";
            }
        }

        private byte[,] ClassifyXZSlice(int yPos)
        {
            // Create a slice for the XZ plane at the given Y position
            int width = mainForm.GetWidth();
            int depth = mainForm.GetDepth();
            byte[,] sliceData = new byte[width, depth];

            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    sliceData[x, z] = mainForm.volumeData[x, yPos, z];
                }
            }

            // Use the same classification logic
            byte[,] resultMask = new byte[width, depth];

            // Use the specified patch size
            int patchRadius = patchSize / 2;

            // Process with a stride of 2 for speed
            for (int z = patchRadius; z < depth - patchRadius; z += 2)
            {
                for (int x = patchRadius; x < width - patchRadius; x += 2)
                {
                    // Get features for this patch
                    double[] features = ExtractFeaturesFromPatch(sliceData, x, z);

                    // Check similarity
                    double similarity = CalculateSimilarity(features, referenceFeatures);

                    if (similarity >= threshold)
                    {
                        // Mark a block
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx;
                                int nz = z + dz;

                                if (nx >= 0 && nx < width && nz >= 0 && nz < depth)
                                {
                                    resultMask[nx, nz] = selectedMaterial.ID;
                                }
                            }
                        }
                    }
                }
            }

            return resultMask;
        }

        private void ApplyXZResultToVolume(byte[,] resultMask, int yPos)
        {
            int width = mainForm.GetWidth();
            int depth = mainForm.GetDepth();

            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (resultMask[x, z] > 0)
                    {
                        mainForm.volumeLabels[x, yPos, z] = selectedMaterial.ID;
                    }
                }
            }
        }

        private byte[,] ClassifyYZSlice(int xPos)
        {
            // Create a slice for the YZ plane at the given X position
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();
            byte[,] sliceData = new byte[depth, height];

            for (int y = 0; y < height; y++)
            {
                for (int z = 0; z < depth; z++)
                {
                    sliceData[z, y] = mainForm.volumeData[xPos, y, z];
                }
            }

            // Use the same classification logic
            byte[,] resultMask = new byte[depth, height];

            // Use the specified patch size
            int patchRadius = patchSize / 2;

            // Process with a stride of 2 for speed
            for (int y = patchRadius; y < height - patchRadius; y += 2)
            {
                for (int z = patchRadius; z < depth - patchRadius; z += 2)
                {
                    // Get features for this patch
                    double[] features = ExtractFeaturesFromPatch(sliceData, z, y);

                    // Check similarity
                    double similarity = CalculateSimilarity(features, referenceFeatures);

                    if (similarity >= threshold)
                    {
                        // Mark a block
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dz = -1; dz <= 1; dz++)
                            {
                                int nz = z + dz;
                                int ny = y + dy;

                                if (nz >= 0 && nz < depth && ny >= 0 && ny < height)
                                {
                                    resultMask[nz, ny] = selectedMaterial.ID;
                                }
                            }
                        }
                    }
                }
            }

            return resultMask;
        }

        private void ApplyYZResultToVolume(byte[,] resultMask, int xPos)
        {
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();

            for (int y = 0; y < height; y++)
            {
                for (int z = 0; z < depth; z++)
                {
                    if (resultMask[z, y] > 0)
                    {
                        mainForm.volumeLabels[xPos, y, z] = selectedMaterial.ID;
                    }
                }
            }
        }

        public void Show()
        {
            textureForm.Show();
        }
    }
}