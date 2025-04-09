using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Algorithms;
using ILGPU.Runtime.CPU;
using System.Collections.Generic;
using System.Threading;

namespace CTSegmenter
{
    /// <summary>
    /// A form that allows the user to apply various filters (Gaussian, median, Non-Local Means, etc.)
    /// to the currently loaded volume. Supports ILGPU-based acceleration (if GPU is available)
    /// or a CPU fallback.
    /// </summary>
    public class FilterManager
    {
        private Form filterForm;
        private MainForm mainForm;

        // ILGPU-related fields
        private Context gpuContext;
        private Accelerator accelerator;
        private bool useGPU = true;
        private bool gpuInitialized = false;
        private CheckBox chkUseGPU;

        // UI components
        private Panel previewPanel;
        private PictureBox xyPreview;
        private Panel controlsPanel;
        private ComboBox cmbFilterType;
        private NumericUpDown numKernelSize;
        private NumericUpDown numSigma;
        private NumericUpDown numNlmH;          // Example parameter for Non-Local Means
        private NumericUpDown numNlmTemplate;   // Template window size for Non-Local Means
        private NumericUpDown numNlmSearch;     // Search window size
        private RadioButton rb2DOnly;
        private RadioButton rb3D;
        private Button btnPreview;
        private Button btnApplyAll;
        private Button btnClose;
        private CheckBox chkOverwrite;
        private Button btnSelectFolder;
        private TextBox txtOutputFolder;
        private CheckBox chkEdgeNormalize;

        //Preview Slice navigation
        private TrackBar sliceTrackBar;
        private Label lblSliceNumber;
        private System.Threading.Timer renderTimer;
        private bool sliderDragging = false;
        private int pendingSliceValue = -1;
        private const int RENDER_DELAY_MS = 50; // Debounce rendering during sliding

        //ROI Fields
        private bool useRoi = false;
        private Rectangle roi = new Rectangle(100, 100, 200, 200); // Default ROI size and position
        private bool isDraggingRoi = false;
        private bool isResizingRoi = false;
        private Point lastMousePos;
        private const int RESIZE_HANDLE_SIZE = 10; // Size of the resize handle
        private CheckBox chkUseRoi;

        private NumericUpDown numSigmaRange;
        private NumericUpDown numSigmaSpatial;
        private NumericUpDown numUnsharpAmount;

        private Label lblStatus;
        private ProgressForm progressForm;

        private float zoomFactor = 1.0f;
        private const float ZOOM_INCREMENT = 0.1f;
        private const float MIN_ZOOM = 0.1f;
        private const float MAX_ZOOM = 5.0f; // Reduced max zoom to avoid overflow
        private Point zoomOrigin = Point.Empty;
        private bool isPanning = false;
        private Point lastPanPoint;
        private Button btnResetZoom;

        /// <summary>
        /// Constructor that takes a reference to the MainForm, so we can access the loaded volume data.
        /// </summary>
        public FilterManager(MainForm mainForm)
        {
            this.mainForm = mainForm;
            InitializeGPU();
            InitializeForm();
        }

        #region GPU Init / Dispose

        private void InitializeGPU()
        {
            try
            {
                gpuContext = Context.Create(builder => builder.Default().EnableAlgorithms());
                Accelerator bestDevice = null;

                // Try for a GPU device first
                foreach (var dev in gpuContext.Devices)
                {
                    if (dev.AcceleratorType != AcceleratorType.CPU)
                    {
                        bestDevice = dev.CreateAccelerator(gpuContext);
                        Logger.Log($"[FilterManager] Using GPU accelerator: {dev.Name}");
                        break;
                    }
                }
                // If no GPU found, fallback to CPU
                if (bestDevice == null)
                {
                    bestDevice = gpuContext.GetCPUDevice(0).CreateAccelerator(gpuContext);
                    Logger.Log("[FilterManager] Falling back to CPU accelerator");
                }

                accelerator = bestDevice;
                gpuInitialized = true;
                useGPU = (accelerator.AcceleratorType != AcceleratorType.CPU);
            }
            catch (Exception ex)
            {
                Logger.Log($"[FilterManager] GPU initialization failed: {ex}");
                gpuInitialized = false;
                useGPU = false;
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

        #endregion

        #region Form Initialization

        private void InitializeForm()
        {
            // Main window
            filterForm = new Form
            {
                Text = "Filter Manager",
                Size = new Size(1000, 700),
                StartPosition = FormStartPosition.CenterScreen
            };
            try
            {
                string iconPath = System.IO.Path.Combine(Application.StartupPath, "favicon.ico");
                if (File.Exists(iconPath))
                    filterForm.Icon = new Icon(iconPath);
            }
            catch { /* ignore icon load errors */ }

            // TableLayout: left = preview, right = controls
            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));

            // Preview panel (left)
            previewPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
            xyPreview = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black
            };
            previewPanel.Controls.Add(xyPreview);
            mainLayout.Controls.Add(previewPanel, 0, 0);

            // Controls panel (right)
            controlsPanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true
            };
            mainLayout.Controls.Add(controlsPanel, 1, 0);

            // Build the controls in the controlsPanel
            int currentY = 10;

            // GPU usage checkbox
            chkUseGPU = new CheckBox
            {
                Text = (gpuInitialized && useGPU)
                    ? $"Use GPU acceleration ({accelerator.Name})"
                    : "GPU unavailable - using CPU",
                AutoSize = true,
                Location = new Point(10, currentY),
                Enabled = gpuInitialized
            };
            chkUseGPU.Checked = useGPU;
            chkUseGPU.CheckedChanged += (s, e) =>
            {
                useGPU = chkUseGPU.Checked;
                Logger.Log("[FilterManager] useGPU changed to " + useGPU);
            };
            controlsPanel.Controls.Add(chkUseGPU);
            currentY += 30;

            // Filter type combo
            Label lblFilterType = new Label
            {
                Text = "Filter Type:",
                AutoSize = true,
                Location = new Point(10, currentY)
            };
            controlsPanel.Controls.Add(lblFilterType);
            currentY += 20;

            cmbFilterType = new ComboBox
            {
                Location = new Point(10, currentY),
                Width = 200,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            // Add filters
            cmbFilterType.Items.Add("Gaussian");
            cmbFilterType.Items.Add("Smoothing");
            cmbFilterType.Items.Add("Median");
            cmbFilterType.Items.Add("Non-Local Means");
            cmbFilterType.Items.Add("Bilateral");
            cmbFilterType.Items.Add("Unsharp Mask");
            cmbFilterType.Items.Add("Edge Detection");

            cmbFilterType.SelectedIndex = 0;
            controlsPanel.Controls.Add(cmbFilterType);
            currentY += 30;

            // Create parameter panels - one for each filter type
            // We'll create all panels now but only show the one for the selected filter

            // Common parameter for most filters - Kernel Size
            Panel commonPanel = new Panel
            {
                Location = new Point(10, currentY),
                Width = 300,
                Height = 50,
                Visible = true
            };

            Label lblKernelSize = new Label
            {
                Text = "Kernel Size (odd):",
                AutoSize = true,
                Location = new Point(0, 0)
            };
            commonPanel.Controls.Add(lblKernelSize);

            numKernelSize = new NumericUpDown
            {
                Location = new Point(0, 20),
                Width = 60,
                Minimum = 1,
                Maximum = 31,
                Value = 3
            };
            numKernelSize.ValueChanged += (s, e) =>
            {
                if (numKernelSize.Value % 2 == 0)
                    numKernelSize.Value += 1;
            };
            commonPanel.Controls.Add(numKernelSize);
            controlsPanel.Controls.Add(commonPanel);

            currentY += 60;

            // 1. Gaussian Parameters Panel
            Panel gaussianPanel = new Panel
            {
                Location = new Point(10, currentY),
                Width = 300,
                Height = 50,
                Visible = false
            };

            Label lblSigma = new Label
            {
                Text = "Sigma:",
                AutoSize = true,
                Location = new Point(0, 0)
            };
            gaussianPanel.Controls.Add(lblSigma);

            numSigma = new NumericUpDown
            {
                Location = new Point(0, 20),
                Width = 60,
                Minimum = 1,
                Maximum = 100,
                DecimalPlaces = 1,
                Increment = 0.1m,
                Value = 1.0m
            };
            gaussianPanel.Controls.Add(numSigma);
            controlsPanel.Controls.Add(gaussianPanel);

            // 2. Non-Local Means Parameters Panel
            Panel nlmPanel = new Panel
            {
                Location = new Point(10, currentY),
                Width = 300,
                Height = 80,
                Visible = false
            };

            Label lblNlmH = new Label
            {
                Text = "Filter Strength (h):",
                AutoSize = true,
                Location = new Point(0, 0)
            };
            nlmPanel.Controls.Add(lblNlmH);

            numNlmH = new NumericUpDown
            {
                Location = new Point(0, 20),
                Width = 60,
                Minimum = 1,
                Maximum = 255,
                DecimalPlaces = 1,
                Increment = 0.5m,
                Value = 10
            };
            nlmPanel.Controls.Add(numNlmH);

            Label lblNlmTemplate = new Label
            {
                Text = "Template Radius:",
                AutoSize = true,
                Location = new Point(100, 0)
            };
            nlmPanel.Controls.Add(lblNlmTemplate);

            numNlmTemplate = new NumericUpDown
            {
                Location = new Point(100, 20),
                Width = 60,
                Minimum = 1,
                Maximum = 15,
                Value = 3
            };
            nlmPanel.Controls.Add(numNlmTemplate);

            Label lblNlmSearch = new Label
            {
                Text = "Search Radius:",
                AutoSize = true,
                Location = new Point(200, 0)
            };
            nlmPanel.Controls.Add(lblNlmSearch);

            numNlmSearch = new NumericUpDown
            {
                Location = new Point(200, 20),
                Width = 60,
                Minimum = 1,
                Maximum = 21,
                Value = 7
            };
            nlmPanel.Controls.Add(numNlmSearch);

            controlsPanel.Controls.Add(nlmPanel);

            // 3. Bilateral Parameters Panel
            Panel bilateralPanel = new Panel
            {
                Location = new Point(10, currentY),
                Width = 300,
                Height = 80,
                Visible = false
            };

            Label lblSigmaSpatial = new Label
            {
                Text = "Spatial Sigma:",
                AutoSize = true,
                Location = new Point(0, 0)
            };
            bilateralPanel.Controls.Add(lblSigmaSpatial);
            NumericUpDown numSigmaSpatial = new NumericUpDown
            {
                Location = new Point(0, 20),
                Width = 60,
                Minimum = 0.1m,
                Maximum = 100,
                DecimalPlaces = 1,
                Increment = 0.1m,
                Value = 3.0m  // Default value that works well for spatial sigma
            };
            bilateralPanel.Controls.Add(numSigmaSpatial);

            numSigma.DecimalPlaces = 1;
            numSigma.Increment = 0.1m;

            // We'll reuse numSigma for spatial sigma and add a new one for range

            Label lblSigmaRange = new Label
            {
                Text = "Range Sigma:",
                AutoSize = true,
                Location = new Point(100, 0)
            };
            bilateralPanel.Controls.Add(lblSigmaRange);

            numSigmaRange = new NumericUpDown
            {
                Location = new Point(100, 20),
                Width = 60,
                Minimum = 1,
                Maximum = 100,
                DecimalPlaces = 1,
                Increment = 0.1m,
                Value = 25.0m
            };
            bilateralPanel.Controls.Add(numSigmaRange);

            controlsPanel.Controls.Add(bilateralPanel);

            // 4. Unsharp Mask Parameters Panel
            Panel unsharpPanel = new Panel
            {
                Location = new Point(10, currentY),
                Width = 300,
                Height = 80,
                Visible = false
            };

            Label lblUnsharpAmount = new Label
            {
                Text = "Sharpening Amount:",
                AutoSize = true,
                Location = new Point(0, 0)
            };
            unsharpPanel.Controls.Add(lblUnsharpAmount);

            numUnsharpAmount = new NumericUpDown
            {
                Location = new Point(0, 20),
                Width = 60,
                Minimum = 0.1m,
                Maximum = 10.0m,
                DecimalPlaces = 2,
                Increment = 0.1m,
                Value = 1.5m
            };
            unsharpPanel.Controls.Add(numUnsharpAmount);

            Label lblUnsharpSigma = new Label
            {
                Text = "Blur Sigma:",
                AutoSize = true,
                Location = new Point(100, 0)
            };
            unsharpPanel.Controls.Add(lblUnsharpSigma);

            // We'll reuse numSigma for Gaussian/blur sigma

            controlsPanel.Controls.Add(unsharpPanel);

            Panel edgePanel = new Panel
            {
                Location = new Point(10, currentY),
                Width = 300,
                Height = 50,
                Visible = false
            };

            Label lblEdgeNormalize = new Label
            {
                Text = "Normalize Result:",
                AutoSize = true,
                Location = new Point(0, 0)
            };
            edgePanel.Controls.Add(lblEdgeNormalize);

            chkEdgeNormalize = new CheckBox
            {
                Text = "Enable",
                Location = new Point(0, 20),
                Checked = true
            };
            edgePanel.Controls.Add(chkEdgeNormalize);

            controlsPanel.Controls.Add(edgePanel);


            currentY += 100; // Allow space for the tallest parameter panel

            // Dictionary to map filter names to their parameter panels
            var filterPanels = new Dictionary<string, Panel>
    {
        { "Gaussian", gaussianPanel },
        { "Non-Local Means", nlmPanel },
        { "Bilateral", bilateralPanel },
        { "Unsharp Mask", unsharpPanel }
        // Smoothing and Median just use the kernel size
    };
            filterPanels.Add("Edge Detection", edgePanel);
            // Handler to show/hide parameter panels based on selection
            cmbFilterType.SelectedIndexChanged += (s, e) =>
            {
                string selectedFilter = cmbFilterType.SelectedItem.ToString();

                // Hide all parameter panels
                foreach (var panel in filterPanels.Values)
                {
                    panel.Visible = false;
                }

                // Show the panel for the selected filter
                if (filterPanels.ContainsKey(selectedFilter))
                {
                    filterPanels[selectedFilter].Visible = true;
                }
            };

            // Show panel for initially selected filter
            if (filterPanels.ContainsKey(cmbFilterType.SelectedItem.ToString()))
            {
                filterPanels[cmbFilterType.SelectedItem.ToString()].Visible = true;
            }
            // Region of Interest controls
            Label lblRoiInfo = new Label
            {
                Text = "Define a region to preview filters more quickly:",
                Location = new Point(10, currentY),
                AutoSize = true
            };
            controlsPanel.Controls.Add(lblRoiInfo);
            currentY += 20;

            chkUseRoi = new CheckBox
            {
                Text = "Use Region of Interest for Preview",
                Location = new Point(10, currentY),
                AutoSize = true,
                Checked = useRoi
            };
            chkUseRoi.CheckedChanged += (s, e) =>
            {
                useRoi = chkUseRoi.Checked;

                // Initialize ROI if needed
                if (useRoi && (roi.Width <= 0 || roi.Height <= 0))
                {
                    InitializeRoi();
                }

                // Force repaint to show/hide ROI
                xyPreview.Invalidate();
            };
            controlsPanel.Controls.Add(chkUseRoi);
            currentY += 30;

            // Add mouse and paint event handlers to xyPreview
            xyPreview.MouseDown += XyPreview_MouseDown;
            xyPreview.MouseMove += XyPreview_MouseMove;
            xyPreview.MouseUp += XyPreview_MouseUp;
            xyPreview.Paint += XyPreview_Paint;
            // 2D vs 3D
            Label lblDim = new Label
            {
                Text = "Filter Dimension:",
                Location = new Point(10, currentY),
                AutoSize = true
            };
            controlsPanel.Controls.Add(lblDim);
            currentY += 20;

            rb2DOnly = new RadioButton
            {
                Text = "2D slices only (Z-direction)",
                Location = new Point(10, currentY),
                AutoSize = true,
                Checked = true
            };
            controlsPanel.Controls.Add(rb2DOnly);
            currentY += 20;

            rb3D = new RadioButton
            {
                Text = "Full 3D (All directions)",
                Location = new Point(10, currentY),
                AutoSize = true
            };
            controlsPanel.Controls.Add(rb3D);
            currentY += 30;

            // Overwrite vs output
            chkOverwrite = new CheckBox
            {
                Text = "Overwrite Original Dataset?",
                Location = new Point(10, currentY),
                AutoSize = true,
                Checked = false
            };
            controlsPanel.Controls.Add(chkOverwrite);
            currentY += 30;

            // Output folder
            Label lblOutput = new Label
            {
                Text = "Output Folder (if not overwriting):",
                Location = new Point(10, currentY),
                AutoSize = true
            };
            controlsPanel.Controls.Add(lblOutput);
            currentY += 20;

            txtOutputFolder = new TextBox
            {
                Location = new Point(10, currentY),
                Width = 200
            };
            controlsPanel.Controls.Add(txtOutputFolder);

            btnSelectFolder = new Button
            {
                Text = "...",
                Location = new Point(220, currentY),
                Width = 30
            };
            btnSelectFolder.Click += (s, e) =>
            {
                using (var fbd = new FolderBrowserDialog())
                {
                    if (fbd.ShowDialog() == DialogResult.OK)
                    {
                        txtOutputFolder.Text = fbd.SelectedPath;
                    }
                }
            };
            controlsPanel.Controls.Add(btnSelectFolder);
            currentY += 40;

            // Preview and Apply
            btnPreview = new Button
            {
                Text = "Preview Filter",
                Location = new Point(10, currentY),
                Width = 120,
                Height = 30
            };
            btnPreview.Click += async (s, e) => await PreviewFilter();
            controlsPanel.Controls.Add(btnPreview);

            btnApplyAll = new Button
            {
                Text = "Apply to All",
                Location = new Point(140, currentY),
                Width = 120,
                Height = 30
            };
            btnApplyAll.Click += async (s, e) => await ApplyToAllSlices();
            controlsPanel.Controls.Add(btnApplyAll);

            currentY += 50;

            // Status label
            lblStatus = new Label
            {
                Text = "Ready",
                Location = new Point(10, currentY),
                AutoSize = true
            };
            controlsPanel.Controls.Add(lblStatus);
            currentY += 30;

            // Close
            btnClose = new Button
            {
                Text = "Close",
                Location = new Point(10, currentY),
                Width = 80
            };
            btnClose.Click += (s, e) => filterForm.Close();
            controlsPanel.Controls.Add(btnClose);

            // Set up the main layout
            filterForm.Controls.Add(mainLayout);

            // Initialize with the correct panel visible
            if (filterPanels.ContainsKey(cmbFilterType.SelectedItem.ToString()))
            {
                filterPanels[cmbFilterType.SelectedItem.ToString()].Visible = true;
            }

            AddSliceNavigationControls();
            SetupZoomFunctionality();
            // Render an initial preview
            RenderPreviewSlice();

            // Show form
            filterForm.Show();
        }

        #endregion

        #region Preview Logic

        /// <summary>
        /// Renders the unfiltered XY slice in the preview box.
        /// We'll call this initially and also after applying a filter for preview.
        /// </summary>
        private void RenderPreviewSlice(byte[] sliceData = null, bool isFiltered = false)
        {
            if (mainForm.volumeData == null) return;

            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int z = mainForm.CurrentSlice;

            // If no slice data provided, read it from the volume:
            if (sliceData == null)
            {
                sliceData = new byte[width * height];
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        sliceData[y * width + x] = mainForm.volumeData[x, y, z];
                    }
                }
            }

            // Create a TEMPORARY 8bpp bitmap for processing
            using (Bitmap tempBmp = new Bitmap(width, height, PixelFormat.Format8bppIndexed))
            {
                // Set a grayscale palette
                ColorPalette pal = tempBmp.Palette;
                for (int i = 0; i < 256; i++)
                {
                    pal.Entries[i] = Color.FromArgb(i, i, i);
                }
                tempBmp.Palette = pal;

                // Lock bits and copy
                Rectangle rect = new Rectangle(0, 0, width, height);
                BitmapData bd = tempBmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
                int stride = bd.Stride;
                unsafe
                {
                    fixed (byte* srcPtr = sliceData)
                    {
                        for (int y = 0; y < height; y++)
                        {
                            byte* dstRow = (byte*)bd.Scan0 + y * stride;
                            byte* srcRow = srcPtr + y * width;
                            for (int x = 0; x < width; x++)
                            {
                                dstRow[x] = srcRow[x];
                            }
                        }
                    }
                }
                tempBmp.UnlockBits(bd);

                // Create a 32bpp bitmap for display (we can use Graphics with this format)
                Bitmap displayBmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(displayBmp))
                {
                    g.DrawImage(tempBmp, 0, 0);

                    // If isFiltered, add the text overlay
                    if (isFiltered)
                    {
                        g.DrawString("Previewed Filter", new Font("Arial", 12), Brushes.Red, new PointF(5, 5));
                    }
                }

                // Dispose old image if any
                if (xyPreview.Image != null) xyPreview.Image.Dispose();
                xyPreview.Image = displayBmp;
            }

            // Initialize ROI if needed
            if (roi.Width <= 0 || roi.Height <= 0)
            {
                InitializeRoi();
            }

            // Make sure the ROI gets drawn if it's active
            if (useRoi)
            {
                xyPreview.Invalidate();
            }
        }


        private ProgressForm GetPreviewProgressForm(string filterName)
        {
            // Only create progress indicator for expensive filters
            if (filterName == "Non-Local Means" ||
                (filterName == "Bilateral" && mainForm.GetWidth() * mainForm.GetHeight() > 500000))
            {
                var pForm = new ProgressForm($"Previewing {filterName}...");
                pForm.Show();
                return pForm;
            }
            return null;
        }
        /// <summary>
        /// Called when the user presses "Preview Filter" – applies the chosen filter just to the currently displayed XY slice,
        /// so they can see how it would look, without altering the rest of the volume.
        /// </summary>
        private async Task PreviewFilter()
        {
            if (mainForm.volumeData == null) return;

            lblStatus.Text = "Filtering preview slice...";
            filterForm.Cursor = Cursors.WaitCursor;

            // Get all filter parameters up front
            string filterName = cmbFilterType.SelectedItem.ToString();
            int kernelSize = (int)numKernelSize.Value;
            float sigma = 1.0f;
            float sigmaSpatial = 3.0f;
            float sigmaRange = 25.0f;
            float h = 10.0f;
            int templateSize = 3;
            int searchSize = 7;
            float unsharpAmount = 1.5f;
            bool normalizeEdges = true;

            // Get values from UI controls if they exist and are visible
            if (numSigma != null && numSigma.Visible) sigma = (float)numSigma.Value;
            if (numSigmaSpatial != null && numSigmaSpatial.Visible) sigmaSpatial = (float)numSigmaSpatial.Value;
            if (numSigmaRange != null && numSigmaRange.Visible) sigmaRange = (float)numSigmaRange.Value;
            if (numNlmH != null && numNlmH.Visible) h = (float)numNlmH.Value;
            if (numNlmTemplate != null && numNlmTemplate.Visible) templateSize = (int)numNlmTemplate.Value;
            if (numNlmSearch != null && numNlmSearch.Visible) searchSize = (int)numNlmSearch.Value;
            if (numUnsharpAmount != null && numUnsharpAmount.Visible) unsharpAmount = (float)numUnsharpAmount.Value;
            if (chkEdgeNormalize != null && chkEdgeNormalize.Visible) normalizeEdges = chkEdgeNormalize.Checked;

            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int z = mainForm.CurrentSlice;

            // Extract the XY slice into an array
            byte[] sliceData = new byte[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    sliceData[y * width + x] = mainForm.volumeData[x, y, z];
                }
            }

            // Create a copy of original slice for later
            byte[] originalSlice = new byte[sliceData.Length];
            Array.Copy(sliceData, originalSlice, sliceData.Length);
            byte[] filteredSlice;

            

            if (useRoi)
            {
                // Process only the ROI
                // Ensure ROI is within bounds
                Rectangle validRoi = new Rectangle(
                    Math.Max(0, Math.Min(width - 1, roi.X)),
                    Math.Max(0, Math.Min(height - 1, roi.Y)),
                    Math.Min(width - roi.X, roi.Width),
                    Math.Min(height - roi.Y, roi.Height)
                );

                // Extract ROI data
                byte[] roiData = new byte[validRoi.Width * validRoi.Height];
                int idx = 0;
                for (int y = validRoi.Y; y < validRoi.Y + validRoi.Height; y++)
                {
                    for (int x = validRoi.X; x < validRoi.X + validRoi.Width; x++)
                    {
                        roiData[idx++] = sliceData[y * width + x];
                    }
                }

                // Filter just the ROI data
                byte[] filteredRoi = await Task.Run(() =>
                {
                    return ApplyFilter2D(roiData, validRoi.Width, validRoi.Height, filterName,
                                     kernelSize, sigma, h, templateSize, searchSize,
                                     useGPU);
                });

                // Merge back into full slice
                filteredSlice = new byte[width * height];
                Array.Copy(originalSlice, filteredSlice, originalSlice.Length);

                idx = 0;
                for (int y = validRoi.Y; y < validRoi.Y + validRoi.Height; y++)
                {
                    for (int x = validRoi.X; x < validRoi.X + validRoi.Width; x++)
                    {
                        filteredSlice[y * width + x] = filteredRoi[idx++];
                    }
                }
            }
            else
            {
                ProgressForm previewProgress = null;
                try
                {
                    previewProgress = GetPreviewProgressForm(filterName);

                    // Then in the filter application code:
                    filteredSlice = await Task.Run(() =>
                    {
                        return ApplyFilter2D(sliceData, width, height, filterName,
                                         kernelSize, sigma, h, templateSize, searchSize,
                                         useGPU);
                    });
                }
                finally
                {
                    previewProgress?.Close();
                }
            }
            lastFilteredSlice = filteredSlice;
            // Render the result
            RenderPreviewSlice(filteredSlice, true);

            lblStatus.Text = "Preview done.";
            filterForm.Cursor = Cursors.Default;
        }


        #endregion

        #region Apply To All Slices

        /// <summary>
        /// Called when the user presses "Apply to All".
        /// This either applies the filter to each XY slice (Z direction only, 2D)
        /// or to the entire 3D volume in a truly volumetric manner, depending on the user selection.
        /// Then it either overwrites the existing dataset or exports to the selected folder as 8-bit BMP images.
        /// </summary>
        private async Task ApplyToAllSlices()
        {
            if (mainForm.volumeData == null) return;
            lblStatus.Text = "Filtering entire volume...";
            filterForm.Cursor = Cursors.WaitCursor;

            string filterName = cmbFilterType.SelectedItem.ToString();
            int kernelSize = (int)numKernelSize.Value;
            float sigma = (float)numSigma.Value;
            float h = (float)numNlmH.Value;
            int templateSize = (int)numNlmTemplate.Value;
            int searchSize = (int)numNlmSearch.Value;

            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();

            bool overwrite = chkOverwrite.Checked;
            string outputFolder = txtOutputFolder.Text.Trim();

            // Validate output folder if not overwriting
            if (!overwrite)
            {
                if (string.IsNullOrEmpty(outputFolder) || !Directory.Exists(outputFolder))
                {
                    MessageBox.Show("Please select a valid output folder or check 'Overwrite Original Dataset.'",
                                    "Invalid Folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    filterForm.Cursor = Cursors.Default;
                    return;
                }
            }

            // Show progress bar
            progressForm = new ProgressForm("Applying filter to all slices...");
            progressForm.Show();

            // If user wants 2D approach, apply slice-by-slice in Z
            // If user wants 3D approach, we do a volumetric approach
            bool do3D = rb3D.Checked;

            try
            {
                if (do3D)
                {
                    // We do a genuine 3D filter. 
                    await Task.Run(() =>
                    {
                        ApplyFilter3D(filterName, kernelSize, sigma, h,
                                      templateSize, searchSize, useGPU,
                                      progressForm);
                    });
                }
                else
                {
                    // We do slice-by-slice 2D filtering
                    await Task.Run(() =>
                    {
                        for (int z = 0; z < depth; z++)
                        {
                            // Read slice
                            byte[] sliceData = new byte[width * height];
                            for (int y = 0; y < height; y++)
                            {
                                for (int x = 0; x < width; x++)
                                {
                                    sliceData[y * width + x] = mainForm.volumeData[x, y, z];
                                }
                            }
                            // Filter it
                            byte[] filtered = ApplyFilter2D(
                                sliceData, width, height,
                                filterName, kernelSize, sigma,
                                h, templateSize, searchSize,
                                useGPU
                            );
                            // Write back
                            for (int y = 0; y < height; y++)
                            {
                                for (int x = 0; x < width; x++)
                                {
                                    if (overwrite)
                                        mainForm.volumeData[x, y, z] = filtered[y * width + x];
                                }
                            }

                            // If exporting
                            if (!overwrite)
                            {
                                SaveSliceAsBMP(filtered, width, height, z, outputFolder);
                            }

                            progressForm.SafeUpdateProgress(z + 1, depth,
                                $"Filtering slice {z + 1} / {depth}");
                        }
                    });
                }

                MessageBox.Show("Filtering completed!", "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Logger.Log("[FilterManager] Filtering completed for entire volume.");
            }
            catch (Exception ex)
            {
                Logger.Log("[FilterManager] Error applying filter to all slices: " + ex);
                MessageBox.Show("Error during filtering:\n" + ex.Message,
                    "Filtering Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                progressForm.Close();
                progressForm = null;
                lblStatus.Text = "Done";
                filterForm.Cursor = Cursors.Default;
            }
        }

        /// <summary>
        /// Saves one XY slice as an 8-bit BMP in the user-specified folder.
        /// This is used if the user chooses not to overwrite the existing dataset.
        /// </summary>
        private void SaveSliceAsBMP(byte[] slice, int width, int height, int sliceIndex, string folder)
        {
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            string fileName = Path.Combine(folder, $"slice_{sliceIndex:0000}.bmp");

            // Create 8bpp grayscale BMP
            using (Bitmap bmp = new Bitmap(width, height, PixelFormat.Format8bppIndexed))
            {
                ColorPalette cp = bmp.Palette;
                for (int i = 0; i < 256; i++)
                {
                    cp.Entries[i] = Color.FromArgb(i, i, i);
                }
                bmp.Palette = cp;

                // Lock and copy
                Rectangle rect = new Rectangle(0, 0, width, height);
                BitmapData bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
                int stride = bd.Stride;
                unsafe
                {
                    fixed (byte* srcPtr = slice)
                    {
                        for (int y = 0; y < height; y++)
                        {
                            byte* dstRow = (byte*)bd.Scan0 + y * stride;
                            byte* srcRow = srcPtr + y * width;
                            for (int x = 0; x < width; x++)
                            {
                                dstRow[x] = srcRow[x];
                            }
                        }
                    }
                }
                bmp.UnlockBits(bd);

                bmp.Save(fileName, ImageFormat.Bmp);
            }
        }

        #endregion

        #region Filter Implementation

        /// <summary>
        /// Applies a 2D filter (Gaussian, median, NLM, etc.) to a single slice.
        /// If using GPU is true, tries GPU acceleration. Otherwise uses CPU fallback.
        /// </summary>
        private byte[] ApplyFilter2D(
            byte[] sliceData,
            int width,
            int height,
            string filterName,
            int kernelSize,
            float sigma,
            float h,
            int templateSize,
            int searchSize,
            bool useGPUFlag)
        {
            float sigmaSpatial = (numSigmaSpatial != null) ? (float)numSigmaSpatial.Value : 3.0f;
            float sigmaRange = (numSigmaRange != null) ? (float)numSigmaRange.Value : 25.0f;
            float unsharpAmount = (numUnsharpAmount != null) ? (float)numUnsharpAmount.Value : 1.5f;
            bool normalizeEdges = (chkEdgeNormalize != null) ? chkEdgeNormalize.Checked : true;
            switch (filterName)
            {
                case "Gaussian":
                    if (useGPUFlag && gpuInitialized)
                        return GaussianFilter2D_GPU(sliceData, width, height, kernelSize, sigma);
                    else
                        return GaussianFilter2D_CPU(sliceData, width, height, kernelSize, sigma);

                case "Smoothing":
                    
                    if (useGPUFlag && gpuInitialized)
                        return SmoothingFilter2D_GPU(sliceData, width, height, kernelSize);
                    else
                        return SmoothingFilter2D_CPU(sliceData, width, height, kernelSize);

                case "Median":
                    if (useGPUFlag && gpuInitialized)
                        return MedianFilter2D_GPU(sliceData, width, height, kernelSize);
                    else
                        return MedianFilter2D_CPU(sliceData, width, height, kernelSize);

                case "Non-Local Means":
                    if (useGPUFlag && gpuInitialized)
                        return NonLocalMeans2D_GPU(sliceData, width, height, h, templateSize, searchSize);
                    else
                        return NonLocalMeans2D_CPU(sliceData, width, height, kernelSize, h, templateSize, searchSize);

                case "Bilateral":
                    if (useGPUFlag && gpuInitialized)
                        return BilateralFilter2D_GPU(sliceData, width, height, kernelSize, sigmaSpatial, sigmaRange);
                    else
                        return BilateralFilter2D_CPU(sliceData, width, height, kernelSize, sigmaSpatial, sigmaRange);

                case "Unsharp Mask":
                    if (useGPUFlag && gpuInitialized)
                        return UnsharpMask2D_GPU(sliceData, width, height, unsharpAmount, kernelSize / 2, sigma);
                    else
                        return UnsharpMask2D_CPU(sliceData, width, height, unsharpAmount, kernelSize / 2, sigma);
                case "Edge Detection":
                    if (useGPUFlag && gpuInitialized)
                        return EdgeDetection2D_GPU(sliceData, width, height, normalizeEdges);
                    else
                        return EdgeDetection2D_CPU(sliceData, width, height, normalizeEdges);
                default:
                    return sliceData; // unfiltered
            }
        }

        /// <summary>
        /// Applies a genuine 3D filter (Gaussian or Median) to the entire volume in a single GPU pass,
        /// or CPU fallback if GPU is unavailable. Overwrites or exports BMP as needed.
        /// </summary>
        private void ApplyFilter3D(
            string filterName,
            int kernelSize,
            float sigma,
            float h,          // unused in 3D Gaussian/Median, but present for consistency
            int templateSize, // unused, for consistency
            int searchSize,   // unused, for consistency
            bool useGPUFlag,
            ProgressForm pForm)
        {
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();
            bool overwrite = chkOverwrite.Checked;
            string outputFolder = txtOutputFolder.Text.Trim();

            // 1) Read the entire volume into a 1D byte[] array
            byte[] srcVolume = new byte[width * height * depth];
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        srcVolume[z * (width * height) + y * width + x] = mainForm.volumeData[x, y, z];
                    }
                }
            }

            // 2) Filter the volume (GPU if available, else CPU)
            byte[] dstVolume;
            if (filterName == "Gaussian")
            {
                if (useGPUFlag && gpuInitialized)
                {
                    pForm.SafeUpdateProgress(0, 1, "Running 3D Gaussian on GPU...");
                    dstVolume = GaussianFilter3D_GPU(srcVolume, width, height, depth, kernelSize, sigma);
                }
                else
                {
                    pForm.SafeUpdateProgress(0, 1, "Running 3D Gaussian on CPU...");
                    dstVolume = GaussianFilter3D_CPU_Full(srcVolume, width, height, depth, kernelSize, sigma, pForm);
                }
            }
            else if (filterName == "Median")
            {
                if (useGPUFlag && gpuInitialized)
                {
                    pForm.SafeUpdateProgress(0, 1, "Running 3D Median on GPU...");
                    dstVolume = MedianFilter3D_GPU(srcVolume, width, height, depth, kernelSize);
                }
                else
                {
                    pForm.SafeUpdateProgress(0, 1, "Running 3D Median on CPU...");
                    dstVolume = MedianFilter3D_CPU_Full(srcVolume, width, height, depth, kernelSize, pForm);
                }
            }
            else if (filterName == "Non-Local Means")
            {
                // Use the specialized NLM3DFilter class that handles both CPU and GPU cases
                using (var nlmFilter = new NLM3DFilter(useGPUFlag))
                {
                    string modeText = useGPUFlag ? "GPU" : "CPU";
                    pForm.SafeUpdateProgress(0, 1, $"Running 3D Non-Local Means on {modeText}...");

                    // The NLM3DFilter.RunNLM3D method handles both CPU and GPU 
                    // implementation selection internally, now with progress updates
                    dstVolume = nlmFilter.RunNLM3D(
                        srcVolume,
                        width,
                        height,
                        depth,
                        templateSize,
                        searchSize,
                        h,
                        useGPUFlag,
                        pForm);
                }
            }
            else if (filterName == "Bilateral")
            {
                float sigmaSpatial = (numSigmaSpatial != null) ? (float)numSigmaSpatial.Value : 3.0f;
                float sigmaRange = (numSigmaRange != null) ? (float)numSigmaRange.Value : 25.0f;

                if (useGPUFlag && gpuInitialized)
                {
                    pForm.SafeUpdateProgress(0, 1, "Running 3D Bilateral filter on GPU...");
                    dstVolume = BilateralFilter3D_GPU(srcVolume, width, height, depth, kernelSize, sigmaSpatial, sigmaRange);
                }
                else
                {
                    pForm.SafeUpdateProgress(0, 1, "Running 3D Bilateral filter on CPU...");
                    dstVolume = BilateralFilter3D_CPU(srcVolume, width, height, depth, kernelSize, sigmaSpatial, sigmaRange, pForm);
                }
            }
            else if (filterName == "Unsharp Mask")
            {
                float unsharpAmount = (float)numUnsharpAmount.Value;

                if (useGPUFlag && gpuInitialized)
                {
                    pForm.SafeUpdateProgress(0, 1, "Running 3D Unsharp Mask on GPU...");
                    dstVolume = UnsharpMask3D_GPU(srcVolume, width, height, depth, unsharpAmount, kernelSize / 2, sigma);
                }
                else
                {
                    pForm.SafeUpdateProgress(0, 1, "Running 3D Unsharp Mask on CPU...");
                    dstVolume = UnsharpMask3D_CPU(srcVolume, width, height, depth, unsharpAmount, kernelSize / 2, sigma, pForm);
                }
            }
            else if (filterName == "Edge Detection")
            {
                bool normalizeEdges = chkEdgeNormalize.Checked;

                if (useGPUFlag && gpuInitialized)
                {
                    pForm.SafeUpdateProgress(0, 1, "Running 3D Edge Detection on GPU...");
                    dstVolume = EdgeDetection3D_GPU(srcVolume, width, height, depth, normalizeEdges);
                }
                else
                {
                    pForm.SafeUpdateProgress(0, 1, "Running 3D Edge Detection on CPU...");
                    dstVolume = EdgeDetection3D_CPU(srcVolume, width, height, depth, normalizeEdges, pForm);
                }
            }
            else
            {
                // For demonstration, do nothing or fallback CPU if not recognized
                Logger.Log("[FilterManager] 3D filter not implemented for " + filterName + ". Using CPU fallback pass-through.");
                dstVolume = srcVolume; // no change
            }

            // 3) Overwrite or export slices
            if (overwrite)
            {
                // Write back to mainForm.volumeData
                pForm.SafeUpdateProgress(0, 1, "Writing filtered volume back to memory...");
                for (int z = 0; z < depth; z++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            mainForm.volumeData[x, y, z] = dstVolume[z * (width * height) + y * width + x];
                        }
                    }
                }
            }
            else
            {
                // Export as BMP slices
                pForm.SafeUpdateProgress(0, 1, "Exporting 3D filtered volume as BMP slices...");
                if (!Directory.Exists(outputFolder))
                    Directory.CreateDirectory(outputFolder);
                for (int z = 0; z < depth; z++)
                {
                    // Extract slice z
                    byte[] slice = new byte[width * height];
                    int sliceOffset = z * width * height;
                    Array.Copy(dstVolume, sliceOffset, slice, 0, width * height);

                    SaveSliceAsBMP(slice, width, height, z, outputFolder);
                    pForm.SafeUpdateProgress(z + 1, depth, $"Exporting slice {z + 1}/{depth}");
                }
            }
        }


        #endregion

        #region 3D Bilateral Filter

        /// <summary>
        /// 3D bilateral filter for the entire volume (CPU implementation)
        /// </summary>
        private byte[] BilateralFilter3D_CPU(byte[] src, int width, int height, int depth,
                                            int kSize, float sigmaSpatial, float sigmaRange,
                                            ProgressForm pForm)
        {
            byte[] dst = new byte[src.Length];
            int radius = kSize / 2;
            int sliceSize = width * height;

            // Pre-compute spatial weights (3D Gaussian based on distance)
            float[] spatialKernel = new float[kSize * kSize * kSize];
            float spatialFactor = -0.5f / (sigmaSpatial * sigmaSpatial);

            int kidx = 0;
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        float dist2 = dx * dx + dy * dy + dz * dz;
                        spatialKernel[kidx++] = (float)Math.Exp(dist2 * spatialFactor);
                    }
                }
            }

            // Range factor for intensity differences
            float rangeFactor = -0.5f / (sigmaRange * sigmaRange);

            // Process each voxel
            for (int z = 0; z < depth; z++)
            {
                pForm.SafeUpdateProgress(z, depth, $"3D Bilateral filter on slice {z + 1}/{depth}");

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int centerIdx = z * sliceSize + y * width + x;
                        int centerVal = src[centerIdx];
                        float sum = 0;
                        float weightSum = 0;

                        kidx = 0;
                        for (int dz = -radius; dz <= radius; dz++)
                        {
                            int nz = z + dz;
                            if (nz < 0) nz = 0;
                            if (nz >= depth) nz = depth - 1;

                            for (int dy = -radius; dy <= radius; dy++)
                            {
                                int ny = y + dy;
                                if (ny < 0) ny = 0;
                                if (ny >= height) ny = height - 1;

                                for (int dx = -radius; dx <= radius; dx++)
                                {
                                    int nx = x + dx;
                                    if (nx < 0) nx = 0;
                                    if (nx >= width) nx = width - 1;

                                    int neighborIdx = nz * sliceSize + ny * width + nx;
                                    int neighborVal = src[neighborIdx];

                                    // Spatial weight
                                    float spatialWeight = spatialKernel[kidx++];

                                    // Range weight
                                    float intensityDiff = centerVal - neighborVal;
                                    float rangeWeight = (float)Math.Exp(intensityDiff * intensityDiff * rangeFactor);

                                    // Combined weight
                                    float weight = spatialWeight * rangeWeight;

                                    weightSum += weight;
                                    sum += weight * neighborVal;
                                }
                            }
                        }

                        if (weightSum > 0.0f)
                        {
                            dst[centerIdx] = (byte)Math.Min(255, Math.Max(0, Math.Round(sum / weightSum)));
                        }
                        else
                        {
                            dst[centerIdx] = src[centerIdx]; // Fallback to original
                        }
                    }
                }
            }

            return dst;
        }

        /// <summary>
        /// 3D bilateral filter using GPU acceleration
        /// </summary>
        private byte[] BilateralFilter3D_GPU(byte[] src, int width, int height, int depth,
                                    int kSize, float sigmaSpatial, float sigmaRange)
        {
            byte[] dst = new byte[src.Length];
            int radius = kSize / 2;

            // Show progress before GPU kernel - can't show during GPU processing
            if (progressForm != null)
                progressForm.SafeUpdateProgress(0, 1, "Running 3D Bilateral filter on GPU...");

            using (var bufferSrc = accelerator.Allocate1D<byte>(src.Length))
            using (var bufferDst = accelerator.Allocate1D<byte>(dst.Length))
            {
                bufferSrc.CopyFromCPU(src);

                var kernel = accelerator.LoadAutoGroupedStreamKernel<
                    Index1D,
                    ArrayView<byte>,
                    ArrayView<byte>,
                    int, int, int, int,
                    float, float>(BilateralFilter3D_Kernel);

                kernel(
                    src.Length,
                    bufferSrc.View,
                    bufferDst.View,
                    radius,
                    width,
                    height,
                    depth,
                    sigmaSpatial,
                    sigmaRange);

                accelerator.Synchronize();
                bufferDst.CopyToCPU(dst);
            }

            return dst;
        }

        /// <summary>
        /// ILGPU kernel for 3D bilateral filtering
        /// </summary>
        static void BilateralFilter3D_Kernel(
            Index1D idx,
            ArrayView<byte> src,
            ArrayView<byte> dst,
            int radius,
            int width,
            int height,
            int depth,
            float sigmaSpatial,
            float sigmaRange)
        {
            if (idx >= src.Length)
                return;

            int sliceSize = width * height;

            // Convert 1D index to 3D coordinates
            int z = (int)(idx / sliceSize);
            int remainder = (int)(idx % sliceSize);
            int y = remainder / width;
            int x = remainder % width;

            float spatialFactor = -0.5f / (sigmaSpatial * sigmaSpatial);
            float rangeFactor = -0.5f / (sigmaRange * sigmaRange);

            int centerVal = src[idx];
            float sum = 0.0f;
            float weightSum = 0.0f;

            for (int dz = -radius; dz <= radius; dz++)
            {
                int nz = z + dz;
                if (nz < 0) nz = 0;
                if (nz >= depth) nz = depth - 1;

                for (int dy = -radius; dy <= radius; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0) ny = 0;
                    if (ny >= height) ny = height - 1;

                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int nx = x + dx;
                        if (nx < 0) nx = 0;
                        if (nx >= width) nx = width - 1;

                        // Spatial weight (based on distance)
                        float dist2 = dx * dx + dy * dy + dz * dz;
                        float spatialWeight = XMath.Exp(dist2 * spatialFactor);

                        // Range weight (based on intensity difference)
                        int neighborIdx = nz * sliceSize + ny * width + nx;
                        int neighborVal = src[neighborIdx];
                        float intensityDiff = centerVal - neighborVal;
                        float rangeWeight = XMath.Exp(intensityDiff * intensityDiff * rangeFactor);

                        // Combined weight
                        float weight = spatialWeight * rangeWeight;

                        weightSum += weight;
                        sum += weight * neighborVal;
                    }
                }
            }

            if (weightSum > 0.0f)
            {
                int result = (int)(sum / weightSum + 0.5f);
                if (result < 0) result = 0;
                if (result > 255) result = 255;
                dst[idx] = (byte)result;
            }
            else
            {
                dst[idx] = src[idx]; // Fallback to original
            }
        }
        #endregion

        #region 3D Unsharp Masking

        /// <summary>
        /// CPU implementation of 3D unsharp masking
        /// </summary>
        private byte[] UnsharpMask3D_CPU(byte[] src, int width, int height, int depth,
                                        float amount, int gaussianRadius, float gaussianSigma,
                                        ProgressForm pForm)
        {
            // First apply 3D Gaussian blur to get the low-pass version
            pForm.SafeUpdateProgress(0, 1, "Calculating 3D Gaussian blur for unsharp mask...");
            byte[] blurred = GaussianFilter3D_CPU_Full(src, width, height, depth,
                                                     2 * gaussianRadius + 1, gaussianSigma, pForm);

            // Create the output and add the high-pass (original - blurred) scaled by amount
            byte[] dst = new byte[src.Length];

            pForm.SafeUpdateProgress(0, 1, "Applying unsharp mask to volume...");
            for (int i = 0; i < src.Length; i++)
            {
                // Show progress periodically
                if (i % (src.Length / 100) == 0)
                {
                    int percent = (i * 100) / src.Length;
                    pForm.SafeUpdateProgress(percent, 100, $"Unsharp masking: {percent}%");
                }

                // Calculate the high-pass component (original - blurred)
                int highPass = src[i] - blurred[i];

                // Add scaled high-pass to original
                int result = (int)(src[i] + amount * highPass);

                // Clamp to valid range
                if (result > 255) result = 255;
                if (result < 0) result = 0;

                dst[i] = (byte)result;
            }

            return dst;
        }

        /// <summary>
        /// GPU implementation of 3D unsharp masking
        /// </summary>
        private byte[] UnsharpMask3D_GPU(byte[] src, int width, int height, int depth,
                                        float amount, int gaussianRadius, float gaussianSigma)
        {
            byte[] dst = new byte[src.Length];

            // First get the blurred version using our existing 3D Gaussian GPU filter
            byte[] blurred = GaussianFilter3D_GPU(src, width, height, depth, 2 * gaussianRadius + 1, gaussianSigma);

            using (var bufferSrc = accelerator.Allocate1D<byte>(src.Length))
            using (var bufferBlurred = accelerator.Allocate1D<byte>(blurred.Length))
            using (var bufferDst = accelerator.Allocate1D<byte>(dst.Length))
            {
                bufferSrc.CopyFromCPU(src);
                bufferBlurred.CopyFromCPU(blurred);

                var kernel = accelerator.LoadAutoGroupedStreamKernel<
                    Index1D,
                    ArrayView<byte>,
                    ArrayView<byte>,
                    ArrayView<byte>,
                    float>(UnsharpMask3D_Kernel); // Reuse the same kernel as 2D - it's identical

                kernel(
                    src.Length,
                    bufferSrc.View,
                    bufferBlurred.View,
                    bufferDst.View,
                    amount);

                accelerator.Synchronize();
                bufferDst.CopyToCPU(dst);
            }

            return dst;
        }

        /// <summary>
        /// ILGPU kernel for 3D unsharp masking (same as 2D version)
        /// </summary>
        static void UnsharpMask3D_Kernel(
            Index1D idx,
            ArrayView<byte> src,
            ArrayView<byte> blurred,
            ArrayView<byte> dst,
            float amount)
        {
            // This is identical to the 2D version since we're just operating on 1D arrays
            if (idx >= src.Length)
                return;

            // Calculate high-pass component
            int highPass = src[idx] - blurred[idx];

            // Add scaled high-pass to original
            int result = (int)(src[idx] + amount * highPass);

            // Clamp result
            if (result > 255) result = 255;
            if (result < 0) result = 0;

            dst[idx] = (byte)result;
        }
        #endregion

        #region ROI Implementation




        /// <summary>
        /// Initializes the ROI to a reasonable default size and position based on the current image
        /// </summary>
        private void InitializeRoi()
        {
            if (xyPreview.Image == null) return;

            int roiWidth = xyPreview.Image.Width / 3;
            int roiHeight = xyPreview.Image.Height / 3;
            int roiX = (xyPreview.Image.Width - roiWidth) / 2;
            int roiY = (xyPreview.Image.Height - roiHeight) / 2;

            roi = new Rectangle(roiX, roiY, roiWidth, roiHeight);
        }

        /// <summary>
        /// Converts a point from screen (PictureBox) coordinates to image coordinates
        /// </summary>
        private Point ConvertToImageCoordinates(Point screenPoint)
        {
            if (xyPreview.Image == null) return screenPoint;

            if (xyPreview.SizeMode == PictureBoxSizeMode.Normal)
            {
                // For manual zoom mode
                int imageX = (int)((screenPoint.X - zoomOrigin.X) / zoomFactor);
                int imageY = (int)((screenPoint.Y - zoomOrigin.Y) / zoomFactor);
                return new Point(imageX, imageY);
            }
            else
            {
                // For PictureBox's automatic zoom mode
                float scaleX = (float)xyPreview.Image.Width / xyPreview.ClientSize.Width;
                float scaleY = (float)xyPreview.Image.Height / xyPreview.ClientSize.Height;

                if (xyPreview.SizeMode == PictureBoxSizeMode.Zoom)
                {
                    // Calculate actual image area within the PictureBox
                    float imageRatio = (float)xyPreview.Image.Width / xyPreview.Image.Height;
                    float controlRatio = (float)xyPreview.Width / xyPreview.Height;

                    if (imageRatio > controlRatio)
                    {
                        // Image is wider than control (relative to height)
                        float scaledHeight = xyPreview.Width / imageRatio;
                        float yOffset = (xyPreview.Height - scaledHeight) / 2;

                        if (screenPoint.Y < yOffset || screenPoint.Y > yOffset + scaledHeight)
                            return new Point(-1, -1); // Outside image area

                        return new Point(
                            (int)(screenPoint.X * xyPreview.Image.Width / xyPreview.Width),
                            (int)((screenPoint.Y - yOffset) * xyPreview.Image.Height / scaledHeight)
                        );
                    }
                    else
                    {
                        // Image is taller than control (relative to width)
                        float scaledWidth = xyPreview.Height * imageRatio;
                        float xOffset = (xyPreview.Width - scaledWidth) / 2;

                        if (screenPoint.X < xOffset || screenPoint.X > xOffset + scaledWidth)
                            return new Point(-1, -1); // Outside image area

                        return new Point(
                            (int)((screenPoint.X - xOffset) * xyPreview.Image.Width / scaledWidth),
                            (int)(screenPoint.Y * xyPreview.Image.Height / xyPreview.Height)
                        );
                    }
                }
                else
                {
                    // For other modes (which we shouldn't be using)
                    return new Point(
                        (int)(screenPoint.X * scaleX),
                        (int)(screenPoint.Y * scaleY)
                    );
                }
            }
        }

        /// <summary>
        /// Converts a rectangle from image coordinates to screen (PictureBox) coordinates
        /// </summary>
        private Rectangle ConvertToScreenRectangle(Rectangle imageRect)
        {
            if (xyPreview.Image == null) return imageRect;

            if (xyPreview.SizeMode == PictureBoxSizeMode.Normal)
            {
                // For manual zoom mode
                return new Rectangle(
                    (int)(imageRect.X * zoomFactor) + zoomOrigin.X,
                    (int)(imageRect.Y * zoomFactor) + zoomOrigin.Y,
                    (int)(imageRect.Width * zoomFactor),
                    (int)(imageRect.Height * zoomFactor)
                );
            }
            else
            {
                // For PictureBox's automatic zoom mode
                return GetDisplayRectangle(imageRect);
            }
        }



        /// <summary>
        /// Converts image coordinates to display coordinates for rendering the ROI
        /// </summary>
        private Rectangle GetDisplayRectangle(Rectangle imageRect)
        {
            if (xyPreview.Image == null) return imageRect;

            // Calculate scaling factor and position based on PictureBox's SizeMode
            float imageAspect = (float)xyPreview.Image.Width / xyPreview.Image.Height;
            float controlAspect = (float)xyPreview.Width / xyPreview.Height;

            Rectangle fitRect;
            float scale;

            if (imageAspect > controlAspect)
            {
                // Image is wider than control (relative to height)
                scale = (float)xyPreview.Width / xyPreview.Image.Width;
                int scaledHeight = (int)(xyPreview.Image.Height * scale);
                int yOffset = (xyPreview.Height - scaledHeight) / 2;
                fitRect = new Rectangle(0, yOffset, xyPreview.Width, scaledHeight);
            }
            else
            {
                // Image is taller than control (relative to width)
                scale = (float)xyPreview.Height / xyPreview.Image.Height;
                int scaledWidth = (int)(xyPreview.Image.Width * scale);
                int xOffset = (xyPreview.Width - scaledWidth) / 2;
                fitRect = new Rectangle(xOffset, 0, scaledWidth, xyPreview.Height);
            }

            // Convert image coordinates to display coordinates
            return new Rectangle(
                fitRect.X + (int)(imageRect.X * scale),
                fitRect.Y + (int)(imageRect.Y * scale),
                (int)(imageRect.Width * scale),
                (int)(imageRect.Height * scale)
            );
        }

        /// <summary>
        /// Modified paint handler for both zooming and ROI
        /// </summary>
        private void XyPreview_Paint(object sender, PaintEventArgs e)
        {
            // Skip custom drawing if we're using the built-in zoom
            if (xyPreview.SizeMode != PictureBoxSizeMode.Normal)
            {
                // Still draw ROI if needed
                if (useRoi) DrawROIOnDefaultMode(e);
                return;
            }

            if (xyPreview.Image == null) return;

            try
            {
                // Calculate safe dimensions to avoid overflow
                int safeWidth = (int)Math.Min(int.MaxValue / 2, xyPreview.Image.Width * zoomFactor);
                int safeHeight = (int)Math.Min(int.MaxValue / 2, xyPreview.Image.Height * zoomFactor);

                // Enable high quality rendering
                e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;

                // Source rectangle (entire image)
                Rectangle srcRect = new Rectangle(0, 0, xyPreview.Image.Width, xyPreview.Image.Height);

                // Destination rectangle with current zoom and pan
                Rectangle destRect = new Rectangle(
                    zoomOrigin.X,
                    zoomOrigin.Y,
                    safeWidth,
                    safeHeight);

                // Draw the image
                e.Graphics.DrawImage(xyPreview.Image, destRect, srcRect, GraphicsUnit.Pixel);

                // Draw ROI if enabled
                if (useRoi) DrawZoomedROI(e);
            }
            catch (Exception ex)
            {
                // Log error and draw a message
                Logger.Log($"[FilterManager] Error drawing zoomed image: {ex.Message}");
                e.Graphics.Clear(Color.Black);
                e.Graphics.DrawString("Error drawing zoomed image. Resetting zoom.",
                    new Font("Arial", 12), Brushes.Red, new Point(10, 10));

                // Reset zoom on next cycle to recover
                filterForm.BeginInvoke(new Action(() => ResetZoom()));
            }
        }

        /// <summary>
        /// Draws the ROI when in manual zoom mode
        /// </summary>
        private void DrawZoomedROI(PaintEventArgs e)
        {
            if (!useRoi) return;

            // Get ROI rectangle in screen coordinates
            Rectangle screenRoi = ConvertToScreenRectangle(roi);

            // Draw ROI rectangle
            using (Pen pen = new Pen(Color.Yellow, 2))
            {
                e.Graphics.DrawRectangle(pen, screenRoi);
            }

            // Draw resize handle
            using (Brush brush = new SolidBrush(Color.Yellow))
            {
                e.Graphics.FillRectangle(brush,
                    screenRoi.Right - RESIZE_HANDLE_SIZE,
                    screenRoi.Bottom - RESIZE_HANDLE_SIZE,
                    RESIZE_HANDLE_SIZE,
                    RESIZE_HANDLE_SIZE);
            }
        }
        /// <summary>
        /// Draws the ROI when in automatic zoom mode
        /// </summary>
        private void DrawROIOnDefaultMode(PaintEventArgs e)
        {
            if (!useRoi || xyPreview.Image == null) return;

            // Get ROI rectangle in screen coordinates
            Rectangle screenRoi = ConvertToScreenRectangle(roi);

            // Draw ROI rectangle
            using (Pen pen = new Pen(Color.Yellow, 2))
            {
                e.Graphics.DrawRectangle(pen, screenRoi);
            }

            // Draw resize handle
            using (Brush brush = new SolidBrush(Color.Yellow))
            {
                e.Graphics.FillRectangle(brush,
                    screenRoi.Right - RESIZE_HANDLE_SIZE,
                    screenRoi.Bottom - RESIZE_HANDLE_SIZE,
                    RESIZE_HANDLE_SIZE,
                    RESIZE_HANDLE_SIZE);
            }
        }



        /// <summary>
        /// Mouse down handler for ROI interaction
        /// </summary>
        private void XyPreview_MouseDown(object sender, MouseEventArgs e)
        {
            if (!useRoi || xyPreview.Image == null) return;

            // Convert click coordinates to image coordinates
            Point imagePoint = ConvertToImageCoordinates(e.Location);
            if (imagePoint.X < 0) return; // Outside of image area

            // Convert ROI to screen coordinates
            Rectangle screenRoi = ConvertToScreenRectangle(roi);

            // Check if clicking on resize handle
            Rectangle resizeHandle = new Rectangle(
                screenRoi.Right - RESIZE_HANDLE_SIZE,
                screenRoi.Bottom - RESIZE_HANDLE_SIZE,
                RESIZE_HANDLE_SIZE,
                RESIZE_HANDLE_SIZE);

            if (resizeHandle.Contains(e.Location))
            {
                isResizingRoi = true;
                isDraggingRoi = false;
            }
            // Check if clicking inside ROI (for dragging)
            else if (screenRoi.Contains(e.Location))
            {
                isDraggingRoi = true;
                isResizingRoi = false;
            }
            else
            {
                isDraggingRoi = false;
                isResizingRoi = false;
            }

            lastMousePos = e.Location;
        }

        /// <summary>
        /// Mouse move handler for ROI interaction
        /// </summary>
        private void XyPreview_MouseMove(object sender, MouseEventArgs e)
        {
            if (!useRoi || xyPreview.Image == null) return;

            if (isDraggingRoi || isResizingRoi)
            {
                // Convert current and last mouse positions to image coordinates
                Point currentImagePoint = ConvertToImageCoordinates(e.Location);
                Point lastImagePoint = ConvertToImageCoordinates(lastMousePos);

                // Calculate the delta in image coordinates
                int deltaX = currentImagePoint.X - lastImagePoint.X;
                int deltaY = currentImagePoint.Y - lastImagePoint.Y;

                if (isDraggingRoi)
                {
                    // Move the ROI, ensuring it stays within the image bounds
                    roi.X = Math.Max(0, Math.Min(xyPreview.Image.Width - roi.Width, roi.X + deltaX));
                    roi.Y = Math.Max(0, Math.Min(xyPreview.Image.Height - roi.Height, roi.Y + deltaY));
                }
                else if (isResizingRoi)
                {
                    // Resize the ROI, ensuring it stays within bounds and maintains minimum size
                    int minSize = 20; // Minimum ROI size in image coordinates
                    int newWidth = Math.Max(minSize, Math.Min(xyPreview.Image.Width - roi.X, roi.Width + deltaX));
                    int newHeight = Math.Max(minSize, Math.Min(xyPreview.Image.Height - roi.Y, roi.Height + deltaY));

                    roi.Width = newWidth;
                    roi.Height = newHeight;
                }

                // Update last mouse position
                lastMousePos = e.Location;

                // Redraw
                xyPreview.Invalidate();
            }
        }

        /// <summary>
        /// Mouse up handler for ROI interaction
        /// </summary>
        private void XyPreview_MouseUp(object sender, MouseEventArgs e)
        {
            isDraggingRoi = false;
            isResizingRoi = false;
        }

        /// <summary>
        /// Updates the mouse handlers to ensure ROI and zooming work together
        /// </summary>
        private void UpdateMouseHandlers()
        {
            // First remove any existing handlers to avoid duplicates
            xyPreview.MouseDown -= XyPreview_MouseDown;
            xyPreview.MouseMove -= XyPreview_MouseMove;
            xyPreview.MouseUp -= XyPreview_MouseUp;
            xyPreview.MouseDown -= XyPreview_MouseDownForPan;
            xyPreview.MouseMove -= XyPreview_MouseMoveForPan;
            xyPreview.MouseUp -= XyPreview_MouseUpForPan;

            // Add handlers for ROI directly
            xyPreview.MouseDown += XyPreview_MouseDown;
            xyPreview.MouseMove += XyPreview_MouseMove;
            xyPreview.MouseUp += XyPreview_MouseUp;

            // Add handlers for panning/zooming
            xyPreview.MouseDown += XyPreview_MouseDownForPan;
            xyPreview.MouseMove += XyPreview_MouseMoveForPan;
            xyPreview.MouseUp += XyPreview_MouseUpForPan;
        }

        #endregion


        #region 2D CPU Methods 

        private byte[] GaussianFilter2D_CPU(byte[] src, int width, int height, int kSize, float sigma)
        {
            byte[] dst = new byte[src.Length];

            // Build the Gaussian kernel
            float[] kernel = BuildGaussianKernel(kSize, sigma);
            int radius = kSize / 2;

            // First pass: horizontal
            byte[] tmp = new byte[src.Length];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float sum = 0;
                    float wsum = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int xx = x + k;
                        if (xx < 0) xx = 0;
                        if (xx >= width) xx = width - 1;
                        float w = kernel[k + radius];
                        sum += w * src[y * width + xx];
                        wsum += w;
                    }
                    tmp[y * width + x] = (byte)(sum / wsum + 0.5f);
                }
            }

            // Second pass: vertical
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float sum = 0;
                    float wsum = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int yy = y + k;
                        if (yy < 0) yy = 0;
                        if (yy >= height) yy = height - 1;
                        float w = kernel[k + radius];
                        sum += w * tmp[yy * width + x];
                        wsum += w;
                    }
                    dst[y * width + x] = (byte)(sum / wsum + 0.5f);
                }
            }

            return dst;
        }

        private byte[] SmoothingFilter2D_CPU(byte[] src, int width, int height, int kSize)
        {
            byte[] dst = new byte[src.Length];
            int radius = kSize / 2;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int count = 0;
                    int sum = 0;
                    for (int yy = -radius; yy <= radius; yy++)
                    {
                        for (int xx = -radius; xx <= radius; xx++)
                        {
                            int nx = x + xx;
                            int ny = y + yy;
                            if (nx < 0) nx = 0;
                            if (nx >= width) nx = width - 1;
                            if (ny < 0) ny = 0;
                            if (ny >= height) ny = height - 1;
                            sum += src[ny * width + nx];
                            count++;
                        }
                    }
                    dst[y * width + x] = (byte)(sum / count);
                }
            }
            return dst;
        }

        private byte[] MedianFilter2D_CPU(byte[] src, int width, int height, int kSize)
        {
            byte[] dst = new byte[src.Length];
            int radius = kSize / 2;
            // Temporary array for sorting
            int area = kSize * kSize;
            byte[] window = new byte[area];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = 0;
                    for (int yy = -radius; yy <= radius; yy++)
                    {
                        for (int xx = -radius; xx <= radius; xx++)
                        {
                            int nx = x + xx;
                            int ny = y + yy;
                            if (nx < 0) nx = 0;
                            if (nx >= width) nx = width - 1;
                            if (ny < 0) ny = 0;
                            if (ny >= height) ny = height - 1;
                            window[idx++] = src[ny * width + nx];
                        }
                    }
                    Array.Sort(window);
                    dst[y * width + x] = window[area / 2]; // median
                }
            }
            return dst;
        }

        private byte[] NonLocalMeans2D_CPU(byte[] src, int width, int height,
            int kSize, float h, int templateSize, int searchSize)
        {
            // This is a simplified / naive CPU version for demonstration.
            // Real NLM is quite expensive. We'll do a smaller approach for demonstration.

            byte[] dst = new byte[src.Length];
            Array.Copy(src, dst, src.Length);

            int radiusTemplate = templateSize / 2;
            int radiusSearch = searchSize / 2;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float sumWeights = 0f;
                    float sumVals = 0f;
                    // Center patch
                    for (int yy = -radiusSearch; yy <= radiusSearch; yy++)
                    {
                        for (int xx = -radiusSearch; xx <= radiusSearch; xx++)
                        {
                            int ny = y + yy;
                            int nx = x + xx;
                            if (nx < 0) nx = 0;
                            if (nx >= width) nx = width - 1;
                            if (ny < 0) ny = 0;
                            if (ny >= height) ny = height - 1;

                            float dist2 = PatchDistance(src, width, height, x, y, nx, ny, radiusTemplate);
                            float w = (float)Math.Exp(-dist2 / (h * h));
                            sumWeights += w;
                            sumVals += w * src[ny * width + nx];
                        }
                    }
                    dst[y * width + x] = (byte)Math.Min(255, Math.Max(0, sumVals / sumWeights));
                }
            }
            return dst;
        }

        /// <summary>
        /// Simple Bilateral filter in 2D (CPU).
        /// </summary>
        private byte[] BilateralFilter2D_CPU(byte[] src, int width, int height, int kSize, float sigma)
        {
            byte[] dst = new byte[src.Length];
            int radius = kSize / 2;
            float[] gauss = BuildGaussianKernel(kSize, sigma);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float sum = 0;
                    float wsum = 0;
                    int centerVal = src[y * width + x];
                    for (int yy = -radius; yy <= radius; yy++)
                    {
                        for (int xx = -radius; xx <= radius; xx++)
                        {
                            int ny = y + yy;
                            int nx = x + xx;
                            if (nx < 0) nx = 0;
                            if (nx >= width) nx = width - 1;
                            if (ny < 0) ny = 0;
                            if (ny >= height) ny = height - 1;
                            int neighborVal = src[ny * width + nx];
                            float g1 = gauss[yy + radius] * gauss[xx + radius]; // domain filter
                            float diff = centerVal - neighborVal;
                            float g2 = (float)Math.Exp(-0.5f * (diff * diff) / (sigma * sigma)); // range filter
                            float w = g1 * g2;
                            sum += w * neighborVal;
                            wsum += w;
                        }
                    }
                    dst[y * width + x] = (byte)Math.Min(255, Math.Max(0, sum / wsum));
                }
            }

            return dst;
        }

        // Helper for NonLocalMeans (simple sum of squared differences for patch)
        private float PatchDistance(byte[] src, int width, int height,
            int x1, int y1, int x2, int y2, int radius)
        {
            float dist2 = 0f;
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int nx1 = x1 + dx; if (nx1 < 0) nx1 = 0; if (nx1 >= width) nx1 = width - 1;
                    int ny1 = y1 + dy; if (ny1 < 0) ny1 = 0; if (ny1 >= height) ny1 = height - 1;
                    int nx2 = x2 + dx; if (nx2 < 0) nx2 = 0; if (nx2 >= width) nx2 = width - 1;
                    int ny2 = y2 + dy; if (ny2 < 0) ny2 = 0; if (ny2 >= height) ny2 = height - 1;
                    int diff = src[ny1 * width + nx1] - src[ny2 * width + nx2];
                    dist2 += diff * diff;
                }
            }
            return dist2;
        }

        #endregion

        #region 3D CPU Methods (Examples)

        private void GaussianFilter3D_CPU(byte[,,] vol, int w, int h, int d, int kSize, float sigma, ProgressForm pForm)
        {
            // Similar concept to 2D, but now you do 3 passes or directly 3D.
            // For simplicity, do a naive 3D convolution. This can be large for big volumes.

            int radius = kSize / 2;
            float[] kernel = BuildGaussianKernel(kSize, sigma);

            byte[,,] temp = new byte[w, h, d];
            // One naive pass
            for (int z = 0; z < d; z++)
            {
                pForm.SafeUpdateProgress(z, d, "GaussianFilter3D pass (Z-plane)...");
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float sum = 0;
                        float wsum = 0;
                        for (int zz = -radius; zz <= radius; zz++)
                        {
                            int nz = z + zz;
                            if (nz < 0) nz = 0;
                            if (nz >= d) nz = d - 1;
                            float kw = kernel[zz + radius];
                            sum += kw * vol[x, y, nz];
                            wsum += kw;
                        }
                        temp[x, y, z] = (byte)(sum / wsum + 0.5f);
                    }
                }
            }
            // Next pass Y, next pass X, etc., or do a direct 3D. Omitted here for brevity.
            // We'll just copy 'temp' back to 'vol' for demonstration.

            for (int z = 0; z < d; z++)
            {
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        vol[x, y, z] = temp[x, y, z];
                    }
                }
            }
        }

        private void MedianFilter3D_CPU(byte[,,] vol, int w, int h, int d, int kSize, ProgressForm pForm)
        {
            int radius = kSize / 2;
            byte[,,] temp = new byte[w, h, d];
            int size = kSize * kSize * kSize;
            byte[] window = new byte[size];

            for (int z = 0; z < d; z++)
            {
                pForm.SafeUpdateProgress(z, d, $"MedianFilter3D on slice {z}/{d}");
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int idx = 0;
                        for (int zz = -radius; zz <= radius; zz++)
                        {
                            for (int yy = -radius; yy <= radius; yy++)
                            {
                                for (int xx = -radius; xx <= radius; xx++)
                                {
                                    int nx = x + xx; if (nx < 0) nx = 0; if (nx >= w) nx = w - 1;
                                    int ny = y + yy; if (ny < 0) ny = 0; if (ny >= h) ny = h - 1;
                                    int nz = z + zz; if (nz < 0) nz = 0; if (nz >= d) nz = d - 1;
                                    window[idx++] = vol[nx, ny, nz];
                                }
                            }
                        }
                        Array.Sort(window);
                        temp[x, y, z] = window[size / 2];
                    }
                }
            }
            // Copy back
            for (int z = 0; z < d; z++)
            {
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        vol[x, y, z] = temp[x, y, z];
                    }
                }
            }
        }

        #endregion

        #region 2D GPU Methods 
        /// <summary>
        /// Builds a complete 2D Gaussian kernel of size kSize x kSize and returns it as a
        /// flattened float[]. Also returns the sum of all weights (kernelSum) if needed.
        /// </summary>
        private float[] Build2DGaussianKernel(int kSize, float sigma, out float kernelSum)
        {
            float[] kernel = new float[kSize * kSize];
            int radius = kSize / 2;
            float sigma2 = 2f * sigma * sigma;
            kernelSum = 0f;

            for (int y = 0; y < kSize; y++)
            {
                int dy = y - radius;
                for (int x = 0; x < kSize; x++)
                {
                    int dx = x - radius;
                    float dist2 = dx * dx + dy * dy;
                    float val = (float)Math.Exp(-dist2 / sigma2);
                    kernel[y * kSize + x] = val;
                    kernelSum += val;
                }
            }

            // Normalize so sum of all weights = 1.0
            for (int i = 0; i < kernel.Length; i++)
            {
                kernel[i] /= kernelSum;
            }
            kernelSum = 1f; // after normalization
            return kernel;
        }
        private byte[] NonLocalMeans2D_GPU(byte[] src, int width, int height,
                                  float h, int templateSize, int searchSize)
        {
            byte[] dst = new byte[src.Length];

            using (var bufferSrc = accelerator.Allocate1D<byte>(src.Length))
            using (var bufferDst = accelerator.Allocate1D<byte>(dst.Length))
            {
                bufferSrc.CopyFromCPU(src);

                // Load and launch the NLM 2D kernel
                var nlmKernel = accelerator.LoadAutoGroupedStreamKernel<
                    Index1D,
                    ArrayView<byte>,
                    ArrayView<byte>,
                    int, int,
                    int, int, float>(NLM2D_Kernel);

                nlmKernel(
                    src.Length,
                    bufferSrc.View,
                    bufferDst.View,
                    width,
                    height,
                    templateSize,
                    searchSize,
                    h);

                accelerator.Synchronize();
                bufferDst.CopyToCPU(dst);
            }

            return dst;
        }
        private byte[] GaussianFilter2D_GPU(byte[] src, int width, int height, int kSize, float sigma)
        {
            int radius = kSize / 2;
            float[] hostKernel = Build2DGaussianKernel(kSize, sigma, out _);

            // Prepare output array
            byte[] dst = new byte[src.Length];

            // Allocate GPU buffers (1D)
            using (var bufferSrc = accelerator.Allocate1D<byte>(src.Length))
            using (var bufferDst = accelerator.Allocate1D<byte>(dst.Length))
            using (var bufferKernel = accelerator.Allocate1D<float>(hostKernel.Length))
            {
                // Copy input image & kernel to GPU
                bufferSrc.CopyFromCPU(src);
                bufferKernel.CopyFromCPU(hostKernel);

                // Load our custom 1D kernel
                //   Index1D   => each thread handles one pixel index
                //   ArrayView<byte> => source image
                //   ArrayView<byte> => destination image
                //   ArrayView<float> => 2D kernel, flattened
                //   int radius, int kSize => kernel radius & dimension
                //   int width, int height => image dimensions
                var convKernel = accelerator.LoadAutoGroupedStreamKernel<
                    Index1D,
                    ArrayView<byte>,  // src
                    ArrayView<byte>,  // dst
                    ArrayView<float>, // kernel
                    int, int, int, int>(GaussianConvolutionKernel);

                // Launch: total # of pixels = width*height
                int totalPixels = width * height;
                convKernel(totalPixels, bufferSrc.View, bufferDst.View, bufferKernel.View,
                           radius, kSize, width, height);

                // Wait for GPU to finish, then copy back
                accelerator.Synchronize();
                bufferDst.CopyToCPU(dst);
            }

            return dst;
        }

        static void NLM2D_Kernel(
    Index1D idx,
    ArrayView<byte> src,
    ArrayView<byte> dst,
    int width,
    int height,
    int templateSize,
    int searchSize,
    float h)
        {
            if (idx >= src.Length)
                return;

            int x = idx % width;
            int y = idx / width;

            float sumWeights = 0f;
            float sumVals = 0f;
            byte centerValue = src[y * width + x];

            for (int dy = -searchSize; dy <= searchSize; dy++)
            {
                for (int dx = -searchSize; dx <= searchSize; dx++)
                {
                    int nx = x + dx;
                    int ny = y + dy;

                    if (nx < 0) nx = 0;
                    if (nx >= width) nx = width - 1;
                    if (ny < 0) ny = 0;
                    if (ny >= height) ny = height - 1;

                    // Calculate patch distance
                    float dist2 = 0f;
                    int patchCount = 0;
                    for (int ty = -templateSize; ty <= templateSize; ty++)
                    {
                        for (int tx = -templateSize; tx <= templateSize; tx++)
                        {
                            int px1 = x + tx;
                            int py1 = y + ty;
                            int px2 = nx + tx;
                            int py2 = ny + ty;

                            if (px1 < 0) px1 = 0;
                            if (px1 >= width) px1 = width - 1;
                            if (py1 < 0) py1 = 0;
                            if (py1 >= height) py1 = height - 1;

                            if (px2 < 0) px2 = 0;
                            if (px2 >= width) px2 = width - 1;
                            if (py2 < 0) py2 = 0;
                            if (py2 >= height) py2 = height - 1;

                            float diff = src[py1 * width + px1] - src[py2 * width + px2];
                            dist2 += diff * diff;
                            patchCount++;
                        }
                    }

                    // Normalize by patch size
                    if (patchCount > 0)
                        dist2 /= patchCount;

                    // Weight calculation
                    float w = XMath.Exp(-dist2 / (h * h));
                    sumWeights += w;
                    sumVals += w * src[ny * width + nx];
                }
            }

            if (sumWeights > 0)
            {
                float result = sumVals / sumWeights;
                int val = (int)(result + 0.5f);
                if (val < 0) val = 0;
                if (val > 255) val = 255;
                dst[idx] = (byte)val;
            }
            else
            {
                dst[idx] = centerValue;
            }
        }

        /// <summary>
        /// A fully working 2D Gaussian convolution kernel. For each pixel (x, y),
        /// it loops over the local window of size (kSize x kSize) and multiplies
        /// each neighbor by the corresponding kernel weight. Then writes the
        /// resulting sum back to dst.
        /// 
        /// src      = original image bytes
        /// dst      = filtered image bytes
        /// kernel   = 2D Gaussian weights, flattened (kSize*kSize)
        /// radius   = half the kernel size (kSize/2)
        /// width, height = image dimensions
        /// </summary>
        static void GaussianConvolutionKernel(
    Index1D idx,                // This is the pixel index in [0..width*height-1]
    ArrayView<byte> src,        // flattened source image
    ArrayView<byte> dst,        // flattened destination image
    ArrayView<float> kernel,    // 2D kernel, flattened (size = kSize*kSize)
    int radius,                 // half the kernel dimension
    int kSize,                  // full kernel dimension (2*radius + 1)
    int width,
    int height)
        {
            // If idx >= src.Length, do nothing (safety check)
            if (idx >= src.Length)
                return;

            // Convert 1D index -> (x, y)
            int x = idx % width;
            int y = idx / width;

            float accum = 0f;
            // Loop over neighbors within [x-radius..x+radius] and [y-radius..y+radius]
            for (int dy = -radius; dy <= radius; dy++)
            {
                int yy = y + dy;
                // clamp y
                if (yy < 0) yy = 0;
                else if (yy >= height) yy = height - 1;

                for (int dx = -radius; dx <= radius; dx++)
                {
                    int xx = x + dx;
                    // clamp x
                    if (xx < 0) xx = 0;
                    else if (xx >= width) xx = width - 1;

                    // figure out kernel offset
                    int kRow = dy + radius; // 0..kSize-1
                    int kCol = dx + radius; // 0..kSize-1
                    float w = kernel[kRow * kSize + kCol];

                    float neighborVal = src[yy * width + xx];
                    accum += neighborVal * w;
                }
            }

            // Round & clamp
            int pixelVal = (int)(accum + 0.5f);
            if (pixelVal < 0) pixelVal = 0;
            if (pixelVal > 255) pixelVal = 255;

            dst[idx] = (byte)pixelVal;
        }

        private byte[] SmoothingFilter2D_GPU(byte[] src, int width, int height, int kSize)
        {
            byte[] dst = new byte[src.Length];
            // In a real scenario, you write a smoothing kernel here.
            using (var bufferSrc = accelerator.Allocate1D<byte>(src.Length))
            using (var bufferDst = accelerator.Allocate1D<byte>(dst.Length))
            {
                bufferSrc.CopyFromCPU(src);
                // For demonstration, let's do the same copy approach:
                var copyKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<byte>>(CopyKernel);
                copyKernel(src.Length, bufferSrc.View, bufferDst.View);

                accelerator.Synchronize();
                bufferDst.CopyToCPU(dst);
            }
            return dst;
        }

        private byte[] MedianFilter2D_GPU(byte[] src, int width, int height, int kSize)
        {
            byte[] dst = new byte[src.Length];
            // In a real scenario, you'd implement the median kernel in ILGPU
            using (var bufferSrc = accelerator.Allocate1D<byte>(src.Length))
            using (var bufferDst = accelerator.Allocate1D<byte>(dst.Length))
            {
                bufferSrc.CopyFromCPU(src);
                var copyKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<byte>>(CopyKernel);
                copyKernel(src.Length, bufferSrc.View, bufferDst.View);

                accelerator.Synchronize();
                bufferDst.CopyToCPU(dst);
            }
            return dst;
        }

        // Example kernel that just copies data. 
        // Replace with real convolution/memory pattern for GPU filtering.
        static void CopyKernel(Index1D idx, ArrayView<byte> src, ArrayView<byte> dst)
        {
            if (idx < src.Length)
                dst[idx] = src[idx];
        }

        #endregion

        #region Edge Detection Filter

        /// <summary>
        /// Edge detection using Sobel operator (2D CPU implementation)
        /// </summary>
        private byte[] EdgeDetection2D_CPU(byte[] src, int width, int height, bool normalize = true)
        {
            byte[] dst = new byte[src.Length];

            // Define Sobel operators
            int[,] sobelX = new int[,] { { -1, 0, 1 }, { -2, 0, 2 }, { -1, 0, 1 } };
            int[,] sobelY = new int[,] { { -1, -2, -1 }, { 0, 0, 0 }, { 1, 2, 1 } };

            // Find max gradient value for normalization (if requested)
            int maxGradient = 0;

            // Compute gradients
            int[] gradients = new int[src.Length];
            for (int y = 1; y < height - 1; y++)
            {
                for (int x = 1; x < width - 1; x++)
                {
                    int pixelX = 0, pixelY = 0;

                    // Apply sobel operator
                    for (int ky = -1; ky <= 1; ky++)
                    {
                        for (int kx = -1; kx <= 1; kx++)
                        {
                            int pixel = src[(y + ky) * width + (x + kx)];
                            pixelX += pixel * sobelX[ky + 1, kx + 1];
                            pixelY += pixel * sobelY[ky + 1, kx + 1];
                        }
                    }

                    // Calculate gradient magnitude
                    int gradient = (int)Math.Sqrt(pixelX * pixelX + pixelY * pixelY);
                    gradients[y * width + x] = gradient;

                    // Track max for normalization
                    if (gradient > maxGradient)
                        maxGradient = gradient;
                }
            }

            // Normalize and copy to output
            if (normalize && maxGradient > 0)
            {
                // Normalize to 0-255 range
                float scale = 255.0f / maxGradient;
                for (int i = 0; i < gradients.Length; i++)
                {
                    dst[i] = (byte)Math.Min(255, Math.Max(0, gradients[i] * scale));
                }
            }
            else
            {
                // Just clamp to 0-255
                for (int i = 0; i < gradients.Length; i++)
                {
                    dst[i] = (byte)Math.Min(255, Math.Max(0, gradients[i]));
                }
            }

            return dst;
        }

        /// <summary>
        /// Edge detection using Sobel operator (2D GPU implementation)
        /// </summary>
        private byte[] EdgeDetection2D_GPU(byte[] src, int width, int height, bool normalize = true)
        {
            byte[] dst = new byte[src.Length];

            using (var bufferSrc = accelerator.Allocate1D<byte>(src.Length))
            using (var bufferDst = accelerator.Allocate1D<byte>(dst.Length))
            {
                bufferSrc.CopyFromCPU(src);

                // First pass: calculate gradients
                var gradientKernel = accelerator.LoadAutoGroupedStreamKernel<
                    Index1D,
                    ArrayView<byte>,
                    ArrayView<int>,
                    int, int>(EdgeDetection2D_GradientKernel);

                using (var bufferGradients = accelerator.Allocate1D<int>(src.Length))
                {
                    // Clear gradients buffer
                    accelerator.Synchronize();

                    // Calculate gradients
                    gradientKernel(
                        src.Length,
                        bufferSrc.View,
                        bufferGradients.View,
                        width,
                        height);

                    accelerator.Synchronize();

                    // Now normalize and convert to byte
                    if (normalize)
                    {
                        // Find max gradient (reduction)
                        int[] gradients = new int[src.Length];
                        bufferGradients.CopyToCPU(gradients);
                        int maxGradient = 0;

                        for (int i = 0; i < gradients.Length; i++)
                        {
                            if (gradients[i] > maxGradient)
                                maxGradient = gradients[i];
                        }

                        // Normalize
                        if (maxGradient > 0)
                        {
                            var normalizeKernel = accelerator.LoadAutoGroupedStreamKernel<
                                Index1D,
                                ArrayView<int>,
                                ArrayView<byte>,
                                int>(NormalizeKernel);

                            normalizeKernel(
                                src.Length,
                                bufferGradients.View,
                                bufferDst.View,
                                maxGradient);
                        }
                        else
                        {
                            // Clear output if no edges
                            var clearKernel = accelerator.LoadAutoGroupedStreamKernel<
                                Index1D,
                                ArrayView<byte>>(ClearKernel);

                            clearKernel(
                                src.Length,
                                bufferDst.View);
                        }
                    }
                    else
                    {
                        // Just clamp
                        var clampKernel = accelerator.LoadAutoGroupedStreamKernel<
                            Index1D,
                            ArrayView<int>,
                            ArrayView<byte>>(ClampKernel);

                        clampKernel(
                            src.Length,
                            bufferGradients.View,
                            bufferDst.View);
                    }
                }

                accelerator.Synchronize();
                bufferDst.CopyToCPU(dst);
            }

            return dst;
        }

        /// <summary>
        /// ILGPU kernel for calculating Sobel gradients in 2D
        /// </summary>
        static void EdgeDetection2D_GradientKernel(
            Index1D idx,
            ArrayView<byte> src,
            ArrayView<int> gradients,
            int width,
            int height)
        {
            if (idx >= src.Length)
                return;

            int x = idx % width;
            int y = idx / width;

            // Skip border pixels
            if (x == 0 || x == width - 1 || y == 0 || y == height - 1)
            {
                gradients[idx] = 0;
                return;
            }

            // Sobel kernels
            int pixelX = 0, pixelY = 0;

            // Top row
            pixelX += -1 * src[(y - 1) * width + (x - 1)];
            pixelX += 0 * src[(y - 1) * width + (x)];
            pixelX += 1 * src[(y - 1) * width + (x + 1)];

            pixelY += -1 * src[(y - 1) * width + (x - 1)];
            pixelY += -2 * src[(y - 1) * width + (x)];
            pixelY += -1 * src[(y - 1) * width + (x + 1)];

            // Middle row
            pixelX += -2 * src[(y) * width + (x - 1)];
            pixelX += 0 * src[(y) * width + (x)];
            pixelX += 2 * src[(y) * width + (x + 1)];

            pixelY += 0 * src[(y) * width + (x - 1)];
            pixelY += 0 * src[(y) * width + (x)];
            pixelY += 0 * src[(y) * width + (x + 1)];

            // Bottom row
            pixelX += -1 * src[(y + 1) * width + (x - 1)];
            pixelX += 0 * src[(y + 1) * width + (x)];
            pixelX += 1 * src[(y + 1) * width + (x + 1)];

            pixelY += 1 * src[(y + 1) * width + (x - 1)];
            pixelY += 2 * src[(y + 1) * width + (x)];
            pixelY += 1 * src[(y + 1) * width + (x + 1)];

            // Calculate gradient magnitude
            gradients[idx] = (int)XMath.Sqrt((float)(pixelX * pixelX + pixelY * pixelY));
        }

        /// <summary>
        /// Edge detection using Sobel operator in 3D (CPU implementation)
        /// </summary>
        private byte[] EdgeDetection3D_CPU(byte[] src, int width, int height, int depth,
                                          bool normalize = true, ProgressForm pForm = null)
        {
            byte[] dst = new byte[src.Length];
            int sliceSize = width * height;

            // Define 3D Sobel operators (applied independently in 3 directions)
            int[,,] sobelX = new int[3, 3, 3];
            int[,,] sobelY = new int[3, 3, 3];
            int[,,] sobelZ = new int[3, 3, 3];

            // Initialize 3D Sobel operators
            for (int z = 0; z < 3; z++)
            {
                for (int y = 0; y < 3; y++)
                {
                    for (int x = 0; x < 3; x++)
                    {
                        // X gradient (similar to 2D but extended to 3D)
                        sobelX[z, y, x] = 0;
                        if (x == 0) sobelX[z, y, x] = -1;
                        if (x == 2) sobelX[z, y, x] = 1;
                        if (y == 1) sobelX[z, y, x] *= 2;

                        // Y gradient
                        sobelY[z, y, x] = 0;
                        if (y == 0) sobelY[z, y, x] = -1;
                        if (y == 2) sobelY[z, y, x] = 1;
                        if (x == 1) sobelY[z, y, x] *= 2;

                        // Z gradient
                        sobelZ[z, y, x] = 0;
                        if (z == 0) sobelZ[z, y, x] = -1;
                        if (z == 2) sobelZ[z, y, x] = 1;
                        if (x == 1 && y == 1) sobelZ[z, y, x] *= 2;
                    }
                }
            }

            // Find max gradient value for normalization
            int maxGradient = 0;
            int[] gradients = new int[src.Length];

            // Update progress
            if (pForm != null)
                pForm.SafeUpdateProgress(0, depth, "Calculating 3D edges...");

            // Apply 3D Sobel operator
            for (int z = 1; z < depth - 1; z++)
            {
                if (pForm != null)
                    pForm.SafeUpdateProgress(z, depth, $"Processing slice {z}/{depth}");

                for (int y = 1; y < height - 1; y++)
                {
                    for (int x = 1; x < width - 1; x++)
                    {
                        int pixelX = 0, pixelY = 0, pixelZ = 0;

                        // Apply 3D convolution
                        for (int kz = -1; kz <= 1; kz++)
                        {
                            for (int ky = -1; ky <= 1; ky++)
                            {
                                for (int kx = -1; kx <= 1; kx++)
                                {
                                    int pixel = src[(z + kz) * sliceSize + (y + ky) * width + (x + kx)];

                                    pixelX += pixel * sobelX[kz + 1, ky + 1, kx + 1];
                                    pixelY += pixel * sobelY[kz + 1, ky + 1, kx + 1];
                                    pixelZ += pixel * sobelZ[kz + 1, ky + 1, kx + 1];
                                }
                            }
                        }

                        // Calculate gradient magnitude (3D)
                        int gradient = (int)Math.Sqrt(pixelX * pixelX + pixelY * pixelY + pixelZ * pixelZ);
                        int idx = z * sliceSize + y * width + x;
                        gradients[idx] = gradient;

                        if (gradient > maxGradient)
                            maxGradient = gradient;
                    }
                }
            }

            // Normalize and copy to output
            if (pForm != null)
                pForm.SafeUpdateProgress(0, 1, "Normalizing edge data...");

            if (normalize && maxGradient > 0)
            {
                // Normalize to 0-255 range
                float scale = 255.0f / maxGradient;
                for (int i = 0; i < gradients.Length; i++)
                {
                    dst[i] = (byte)Math.Min(255, Math.Max(0, gradients[i] * scale));
                }
            }
            else
            {
                // Just clamp to 0-255
                for (int i = 0; i < gradients.Length; i++)
                {
                    dst[i] = (byte)Math.Min(255, Math.Max(0, gradients[i]));
                }
            }

            return dst;
        }

        /// <summary>
        /// Edge detection using Sobel operator in 3D (GPU implementation)
        /// </summary>
        private byte[] EdgeDetection3D_GPU(byte[] src, int width, int height, int depth, bool normalize = true)
        {
            byte[] dst = new byte[src.Length];

            using (var bufferSrc = accelerator.Allocate1D<byte>(src.Length))
            using (var bufferDst = accelerator.Allocate1D<byte>(dst.Length))
            {
                bufferSrc.CopyFromCPU(src);

                // First pass: calculate gradients
                var gradientKernel = accelerator.LoadAutoGroupedStreamKernel<
                    Index1D,
                    ArrayView<byte>,
                    ArrayView<int>,
                    int, int, int>(EdgeDetection3D_GradientKernel);

                using (var bufferGradients = accelerator.Allocate1D<int>(src.Length))
                {
                    // Calculate gradients
                    gradientKernel(
                        src.Length,
                        bufferSrc.View,
                        bufferGradients.View,
                        width,
                        height,
                        depth);

                    accelerator.Synchronize();

                    // Now normalize and convert to byte
                    if (normalize)
                    {
                        // Find max gradient (reduction)
                        int[] gradients = new int[src.Length];
                        bufferGradients.CopyToCPU(gradients);
                        int maxGradient = 0;

                        for (int i = 0; i < gradients.Length; i++)
                        {
                            if (gradients[i] > maxGradient)
                                maxGradient = gradients[i];
                        }

                        // Normalize
                        if (maxGradient > 0)
                        {
                            var normalizeKernel = accelerator.LoadAutoGroupedStreamKernel<
                                Index1D,
                                ArrayView<int>,
                                ArrayView<byte>,
                                int>(NormalizeKernel);

                            normalizeKernel(
                                src.Length,
                                bufferGradients.View,
                                bufferDst.View,
                                maxGradient);
                        }
                        else
                        {
                            // Clear output if no edges
                            var clearKernel = accelerator.LoadAutoGroupedStreamKernel<
                                Index1D,
                                ArrayView<byte>>(ClearKernel);

                            clearKernel(
                                src.Length,
                                bufferDst.View);
                        }
                    }
                    else
                    {
                        // Just clamp
                        var clampKernel = accelerator.LoadAutoGroupedStreamKernel<
                            Index1D,
                            ArrayView<int>,
                            ArrayView<byte>>(ClampKernel);

                        clampKernel(
                            src.Length,
                            bufferGradients.View,
                            bufferDst.View);
                    }
                }

                accelerator.Synchronize();
                bufferDst.CopyToCPU(dst);
            }

            return dst;
        }

        /// <summary>
        /// ILGPU kernel for calculating Sobel gradients in 3D
        /// </summary>
        static void EdgeDetection3D_GradientKernel(
            Index1D idx,
            ArrayView<byte> src,
            ArrayView<int> gradients,
            int width,
            int height,
            int depth)
        {
            if (idx >= src.Length)
                return;

            int sliceSize = width * height;
            int z = (int)(idx / sliceSize);
            int remainder = (int)(idx % sliceSize);
            int y = remainder / width;
            int x = remainder % width;

            // Skip border voxels
            if (x == 0 || x == width - 1 || y == 0 || y == height - 1 || z == 0 || z == depth - 1)
            {
                gradients[idx] = 0;
                return;
            }

            // 3D Sobel
            int pixelX = 0, pixelY = 0, pixelZ = 0;

            // Apply the operator - this is a simple 3D version
            // We can use the same logic as 2D but extend to 3 dimensions

            // Apply X gradient
            pixelX += -1 * src[(z) * sliceSize + (y - 1) * width + (x - 1)];
            pixelX += -2 * src[(z) * sliceSize + (y) * width + (x - 1)];
            pixelX += -1 * src[(z) * sliceSize + (y + 1) * width + (x - 1)];

            pixelX += 1 * src[(z) * sliceSize + (y - 1) * width + (x + 1)];
            pixelX += 2 * src[(z) * sliceSize + (y) * width + (x + 1)];
            pixelX += 1 * src[(z) * sliceSize + (y + 1) * width + (x + 1)];

            // Apply Y gradient
            pixelY += -1 * src[(z) * sliceSize + (y - 1) * width + (x - 1)];
            pixelY += -2 * src[(z) * sliceSize + (y - 1) * width + (x)];
            pixelY += -1 * src[(z) * sliceSize + (y - 1) * width + (x + 1)];

            pixelY += 1 * src[(z) * sliceSize + (y + 1) * width + (x - 1)];
            pixelY += 2 * src[(z) * sliceSize + (y + 1) * width + (x)];
            pixelY += 1 * src[(z) * sliceSize + (y + 1) * width + (x + 1)];

            // Apply Z gradient (using adjacent slices)
            pixelZ += -1 * src[(z - 1) * sliceSize + (y - 1) * width + (x)];
            pixelZ += -1 * src[(z - 1) * sliceSize + (y) * width + (x - 1)];
            pixelZ += -2 * src[(z - 1) * sliceSize + (y) * width + (x)];
            pixelZ += -1 * src[(z - 1) * sliceSize + (y) * width + (x + 1)];
            pixelZ += -1 * src[(z - 1) * sliceSize + (y + 1) * width + (x)];

            pixelZ += 1 * src[(z + 1) * sliceSize + (y - 1) * width + (x)];
            pixelZ += 1 * src[(z + 1) * sliceSize + (y) * width + (x - 1)];
            pixelZ += 2 * src[(z + 1) * sliceSize + (y) * width + (x)];
            pixelZ += 1 * src[(z + 1) * sliceSize + (y) * width + (x + 1)];
            pixelZ += 1 * src[(z + 1) * sliceSize + (y + 1) * width + (x)];

            // Calculate gradient magnitude (3D)
            gradients[idx] = (int)XMath.Sqrt((float)(pixelX * pixelX + pixelY * pixelY + pixelZ * pixelZ));
        }

        /// <summary>
        /// ILGPU kernel for normalizing gradient values to 0-255 range
        /// </summary>
        static void NormalizeKernel(
            Index1D idx,
            ArrayView<int> gradients,
            ArrayView<byte> dst,
            int maxGradient)
        {
            if (idx >= gradients.Length)
                return;

            float normalizedValue = (float)gradients[idx] * 255.0f / maxGradient;
            int result = (int)(normalizedValue + 0.5f);

            if (result < 0) result = 0;
            if (result > 255) result = 255;

            dst[idx] = (byte)result;
        }

        /// <summary>
        /// ILGPU kernel for clamping gradient values to 0-255 range
        /// </summary>
        static void ClampKernel(
            Index1D idx,
            ArrayView<int> gradients,
            ArrayView<byte> dst)
        {
            if (idx >= gradients.Length)
                return;

            int value = gradients[idx];
            if (value < 0) value = 0;
            if (value > 255) value = 255;

            dst[idx] = (byte)value;
        }

        /// <summary>
        /// ILGPU kernel for clearing an array (setting to zero)
        /// </summary>
        static void ClearKernel(
            Index1D idx,
            ArrayView<byte> dst)
        {
            if (idx < dst.Length)
                dst[idx] = 0;
        }

        #endregion


        #region Bilateral Filter

        /// <summary>
        /// CPU implementation of the bilateral filter - preserves edges while removing noise
        /// </summary>
        private byte[] BilateralFilter2D_CPU(byte[] src, int width, int height, int kSize, float sigmaSpatial, float sigmaRange)
        {
            byte[] dst = new byte[src.Length];
            int radius = kSize / 2;

            // Pre-compute spatial weights (Gaussian based on distance)
            float[] spatialKernel = new float[kSize * kSize];
            float spatialFactor = -0.5f / (sigmaSpatial * sigmaSpatial);

            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    float dist2 = dx * dx + dy * dy;
                    int idx = (dy + radius) * kSize + (dx + radius);
                    spatialKernel[idx] = (float)Math.Exp(dist2 * spatialFactor);
                }
            }

            // Range factor for intensity differences
            float rangeFactor = -0.5f / (sigmaRange * sigmaRange);

            // Process each pixel
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int centerVal = src[y * width + x];
                    float sum = 0;
                    float weightSum = 0;

                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0) ny = 0;
                        if (ny >= height) ny = height - 1;

                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int nx = x + dx;
                            if (nx < 0) nx = 0;
                            if (nx >= width) nx = width - 1;

                            int neighborVal = src[ny * width + nx];

                            // Spatial weight
                            int kidx = (dy + radius) * kSize + (dx + radius);
                            float spatialWeight = spatialKernel[kidx];

                            // Range weight
                            float intensityDiff = centerVal - neighborVal;
                            float rangeWeight = (float)Math.Exp(intensityDiff * intensityDiff * rangeFactor);

                            // Combined weight
                            float weight = spatialWeight * rangeWeight;

                            weightSum += weight;
                            sum += weight * neighborVal;
                        }
                    }

                    if (weightSum > 0.0f)
                    {
                        dst[y * width + x] = (byte)Math.Min(255, Math.Max(0, Math.Round(sum / weightSum)));
                    }
                    else
                    {
                        dst[y * width + x] = src[y * width + x]; // Fallback to original
                    }
                }
            }

            return dst;
        }

        /// <summary>
        /// GPU implementation of the bilateral filter
        /// </summary>
        private byte[] BilateralFilter2D_GPU(byte[] src, int width, int height, int kSize, float sigmaSpatial, float sigmaRange)
        {
            byte[] dst = new byte[src.Length];
            int radius = kSize / 2;

            using (var bufferSrc = accelerator.Allocate1D<byte>(src.Length))
            using (var bufferDst = accelerator.Allocate1D<byte>(dst.Length))
            {
                bufferSrc.CopyFromCPU(src);

                var kernel = accelerator.LoadAutoGroupedStreamKernel<
                    Index1D,
                    ArrayView<byte>,
                    ArrayView<byte>,
                    int, int, int,
                    float, float>(BilateralFilter2D_Kernel);

                kernel(
                    src.Length,
                    bufferSrc.View,
                    bufferDst.View,
                    radius,
                    width,
                    height,
                    sigmaSpatial,
                    sigmaRange);

                accelerator.Synchronize();
                bufferDst.CopyToCPU(dst);
            }

            return dst;
        }

        /// <summary>
        /// ILGPU kernel for bilateral filtering
        /// </summary>
        static void BilateralFilter2D_Kernel(
            Index1D idx,
            ArrayView<byte> src,
            ArrayView<byte> dst,
            int radius,
            int width,
            int height,
            float sigmaSpatial,
            float sigmaRange)
        {
            if (idx >= src.Length)
                return;

            int x = idx % width;
            int y = idx / width;

            float spatialFactor = -0.5f / (sigmaSpatial * sigmaSpatial);
            float rangeFactor = -0.5f / (sigmaRange * sigmaRange);

            int centerVal = src[y * width + x];
            float sum = 0.0f;
            float weightSum = 0.0f;

            for (int dy = -radius; dy <= radius; dy++)
            {
                int ny = y + dy;
                if (ny < 0) ny = 0;
                if (ny >= height) ny = height - 1;

                for (int dx = -radius; dx <= radius; dx++)
                {
                    int nx = x + dx;
                    if (nx < 0) nx = 0;
                    if (nx >= width) nx = width - 1;

                    // Spatial weight (based on distance)
                    float dist2 = dx * dx + dy * dy;
                    float spatialWeight = XMath.Exp(dist2 * spatialFactor);

                    // Range weight (based on intensity difference)
                    int neighborVal = src[ny * width + nx];
                    float intensityDiff = centerVal - neighborVal;
                    float rangeWeight = XMath.Exp(intensityDiff * intensityDiff * rangeFactor);

                    // Combined weight
                    float weight = spatialWeight * rangeWeight;

                    weightSum += weight;
                    sum += weight * neighborVal;
                }
            }

            if (weightSum > 0.0f)
            {
                int result = (int)(sum / weightSum + 0.5f);
                if (result < 0) result = 0;
                if (result > 255) result = 255;
                dst[idx] = (byte)result;
            }
            else
            {
                dst[idx] = src[idx]; // Fallback to original
            }
        }
        #endregion

        #region Unsharp Masking

        /// <summary>
        /// CPU implementation of unsharp masking - enhances edges by adding high-frequency components
        /// </summary>
        private byte[] UnsharpMask2D_CPU(byte[] src, int width, int height, float amount, int gaussianRadius, float gaussianSigma)
        {
            // First apply Gaussian blur to get the low-pass version
            byte[] blurred = GaussianFilter2D_CPU(src, width, height, 2 * gaussianRadius + 1, gaussianSigma);

            // Create the output and add the high-pass (original - blurred) scaled by amount
            byte[] dst = new byte[src.Length];

            for (int i = 0; i < src.Length; i++)
            {
                // Calculate the high-pass component (original - blurred)
                int highPass = src[i] - blurred[i];

                // Add scaled high-pass to original
                int result = (int)(src[i] + amount * highPass);

                // Clamp to valid range
                if (result > 255) result = 255;
                if (result < 0) result = 0;

                dst[i] = (byte)result;
            }

            return dst;
        }

        /// <summary>
        /// GPU implementation of unsharp masking
        /// </summary>
        private byte[] UnsharpMask2D_GPU(byte[] src, int width, int height, float amount, int gaussianRadius, float gaussianSigma)
        {
            byte[] dst = new byte[src.Length];

            // First get the blurred version using our existing Gaussian GPU filter
            byte[] blurred = GaussianFilter2D_GPU(src, width, height, 2 * gaussianRadius + 1, gaussianSigma);

            using (var bufferSrc = accelerator.Allocate1D<byte>(src.Length))
            using (var bufferBlurred = accelerator.Allocate1D<byte>(blurred.Length))
            using (var bufferDst = accelerator.Allocate1D<byte>(dst.Length))
            {
                bufferSrc.CopyFromCPU(src);
                bufferBlurred.CopyFromCPU(blurred);

                var kernel = accelerator.LoadAutoGroupedStreamKernel<
                    Index1D,
                    ArrayView<byte>,
                    ArrayView<byte>,
                    ArrayView<byte>,
                    float>(UnsharpMask2D_Kernel);

                kernel(
                    src.Length,
                    bufferSrc.View,
                    bufferBlurred.View,
                    bufferDst.View,
                    amount);

                accelerator.Synchronize();
                bufferDst.CopyToCPU(dst);
            }

            return dst;
        }

        /// <summary>
        /// ILGPU kernel for unsharp masking
        /// </summary>
        static void UnsharpMask2D_Kernel(
            Index1D idx,
            ArrayView<byte> src,
            ArrayView<byte> blurred,
            ArrayView<byte> dst,
            float amount)
        {
            if (idx >= src.Length)
                return;

            // Calculate high-pass component
            int highPass = src[idx] - blurred[idx];

            // Add scaled high-pass to original
            int result = (int)(src[idx] + amount * highPass);

            // Clamp result
            if (result > 255) result = 255;
            if (result < 0) result = 0;

            dst[idx] = (byte)result;
        }
        #endregion


        #region Helpers

        private float[] BuildGaussianKernel(int size, float sigma)
        {
            float[] kernel = new float[size];
            int r = size / 2;
            float sum = 0f;
            float coeff = 1f / (2f * (float)Math.PI * sigma * sigma); // 2D reference but we'll just do 1D
            for (int i = 0; i < size; i++)
            {
                int x = i - r;
                float val = (float)Math.Exp(-(x * x) / (2f * sigma * sigma));
                kernel[i] = val;
                sum += val;
            }
            // Normalize
            for (int i = 0; i < size; i++)
            {
                kernel[i] /= sum;
            }
            return kernel;
        }
        public void Show()
        {
            filterForm?.Show();
        }

        #endregion
        #region 3DKernels
        /// <summary>
        /// Builds a naive 3D Gaussian kernel of size kSize^3, normalized so sum=1.
        /// Example: if kSize=3, we get a small 3x3x3 kernel. If kSize=5, we get 5x5x5, etc.
        /// </summary>
        private float[] Build3DGaussianKernel(int kSize, float sigma)
        {
            float[] kernel = new float[kSize * kSize * kSize];
            int radius = kSize / 2;
            float sigma2 = 2f * sigma * sigma;
            float sum = 0f;

            int index = 0;
            for (int z = -radius; z <= radius; z++)
            {
                for (int y = -radius; y <= radius; y++)
                {
                    for (int x = -radius; x <= radius; x++)
                    {
                        float dist2 = x * x + y * y + z * z;
                        float val = (float)Math.Exp(-dist2 / sigma2);
                        kernel[index++] = val;
                        sum += val;
                    }
                }
            }

            // Normalize so total sum = 1
            for (int i = 0; i < kernel.Length; i++)
            {
                kernel[i] /= sum;
            }
            return kernel;
        }

        /// <summary>
        /// Runs a naive 3D Gaussian filter on the GPU:
        /// - Expects a flattened volume array (size=width*height*depth).
        /// - Builds a 3D kernel, launches a kernel that does a triple nested loop over neighbors.
        /// </summary>
        private byte[] GaussianFilter3D_GPU(byte[] src, int width, int height, int depth, int kSize, float sigma)
        {
            int totalVoxels = width * height * depth;
            byte[] dst = new byte[totalVoxels];
            int radius = kSize / 2;

            // Build 3D Gaussian kernel on CPU
            float[] hostKernel = Build3DGaussianKernel(kSize, sigma);

            // Allocate GPU buffers
            using (var bufferSrc = accelerator.Allocate1D<byte>(src.Length))
            using (var bufferDst = accelerator.Allocate1D<byte>(dst.Length))
            using (var bufferKernel = accelerator.Allocate1D<float>(hostKernel.Length))
            {
                bufferSrc.CopyFromCPU(src);
                bufferKernel.CopyFromCPU(hostKernel);

                // Load and launch the 3D convolution kernel
                var conv3DKernel = accelerator.LoadAutoGroupedStreamKernel<
                    Index1D,
                    ArrayView<byte>,   // src
                    ArrayView<byte>,   // dst
                    ArrayView<float>,  // kernel
                    int, int, int, int>(Gaussian3D_Kernel);

                conv3DKernel(
                    totalVoxels,
                    bufferSrc.View,
                    bufferDst.View,
                    bufferKernel.View,
                    radius,
                    kSize,
                    width,
                    height
                );

                accelerator.Synchronize();
                bufferDst.CopyToCPU(dst);
            }

            return dst;
        }

        /// <summary>
        /// The actual GPU 3D Gaussian kernel. Each thread handles exactly one voxel index.
        /// Flattened volume: index = x + y*width + z*(width*height).
        /// We'll do a triple loop over [-radius..radius] in x, y, z, read from src, multiply by kernel.
        /// </summary>
        static void Gaussian3D_Kernel(
            Index1D threadIndex,        // 0..(width*height*depth-1)
            ArrayView<byte> src,
            ArrayView<byte> dst,
            ArrayView<float> kernel3D,  // length = kSize^3
            int radius,
            int kSize,
            int width,
            int height)
        {
            int depth = (int)(src.Length / (width * height));
            if (threadIndex >= src.Length)
                return;

            // Compute (x,y,z)
            int z = (int)(threadIndex / (width * height));
            int rem = (int)(threadIndex % (width * height));
            int y = rem / width;
            int x = rem % width;

            float accum = 0f;

            // For indexing into kernel: we do a smaller triple loop
            // We map (dx,dy,dz) -> index in [0..kSize^3-1]
            // We'll do it as:
            //   kernelIndex = (dz + radius)* (kSize^2) + (dy + radius)*kSize + (dx + radius)
            // with dx,dy,dz in [-radius..radius].
            int i = 0;
            int kernelIndex = 0; // We'll compute it inside the loops
            for (int dz = -radius; dz <= radius; dz++)
            {
                int z2 = z + dz;
                // Clamp
                if (z2 < 0) z2 = 0;
                else if (z2 >= depth) z2 = depth - 1;

                int zOffset = (dz + radius) * (kSize * kSize); // chunk of kSize^2

                for (int dy = -radius; dy <= radius; dy++)
                {
                    int y2 = y + dy;
                    if (y2 < 0) y2 = 0;
                    else if (y2 >= height) y2 = height - 1;

                    int yOffset = (dy + radius) * kSize;

                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int x2 = x + dx;
                        if (x2 < 0) x2 = 0;
                        else if (x2 >= width) x2 = width - 1;

                        // figure out kernel index
                        int kernelPos = zOffset + yOffset + (dx + radius);

                        float w = kernel3D[kernelPos];
                        float neighborVal = src[x2 + y2 * width + z2 * (width * height)];
                        accum += w * neighborVal;
                    }
                }
            }

            // clamp result
            int val = (int)(accum + 0.5f);
            if (val < 0) val = 0;
            if (val > 255) val = 255;

            dst[threadIndex] = (byte)val;
        }
        /// <summary>
        /// Runs a naive 3D Median filter on the GPU:
        /// - Expects a flattened volume array (width*height*depth).
        /// - For each voxel, we do a triple nested loop in x,y,z, push neighbors into a local array,
        ///   sort it, pick the middle. COMPLETELY naive but no placeholders.
        /// </summary>
        private byte[] MedianFilter3D_GPU(byte[] src, int width, int height, int depth, int kSize)
        {
            int totalVoxels = width * height * depth;
            byte[] dst = new byte[totalVoxels];
            int radius = kSize / 2;

            using (var bufferSrc = accelerator.Allocate1D<byte>(src.Length))
            using (var bufferDst = accelerator.Allocate1D<byte>(dst.Length))
            {
                bufferSrc.CopyFromCPU(src);

                // We'll create a kernel that does the triple nested loop,
                // builds a local array, calls "Sort" on it, then picks the middle.
                var median3DKernel = accelerator.LoadAutoGroupedStreamKernel<
                    Index1D,
                    ArrayView<byte>,
                    ArrayView<byte>,
                    int, int, int, int>(Median3D_Kernel);

                median3DKernel(
                    totalVoxels,
                    bufferSrc.View,
                    bufferDst.View,
                    radius,
                    kSize,
                    width,
                    height
                );

                accelerator.Synchronize();
                bufferDst.CopyToCPU(dst);
            }

            return dst;
        }

        /// <summary>
        /// A naive 3D median kernel in ILGPU, with no placeholders:
        /// Each thread (Index1D) processes one voxel. We gather up to kSize^3 neighbors,
        /// store them in a local array, sort, pick the middle. 
        /// For large kSize, this is expensive but fully functional.
        /// 
        /// Flattened volume index -> (x,y,z). Then triple loop over [-radius..radius].
        /// </summary>
        static void Median3D_Kernel(
            Index1D threadIndex,
            ArrayView<byte> src,
            ArrayView<byte> dst,
            int radius,
            int kSize,
            int width,
            int height)
        {
            int depth = (int)(src.Length / (width * height));
            if (threadIndex >= src.Length)
                return;

            // Flattened -> (x,y,z)
            int z = (int)(threadIndex / (width * height));
            int rem = (int)(threadIndex % (width * height));
            int y = rem / width;
            int x = rem % width;

            // We gather kSize^3 neighbors in a local array. 
            // For safety, we'll do dynamic stackalloc here:
            int windowSize = kSize * kSize * kSize;
            Span<byte> window = stackalloc byte[windowSize];

            int idx = 0;
            for (int dz = -radius; dz <= radius; dz++)
            {
                int z2 = z + dz;
                if (z2 < 0) z2 = 0;
                else if (z2 >= depth) z2 = depth - 1;

                for (int dy = -radius; dy <= radius; dy++)
                {
                    int y2 = y + dy;
                    if (y2 < 0) y2 = 0;
                    else if (y2 >= height) y2 = height - 1;

                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int x2 = x + dx;
                        if (x2 < 0) x2 = 0;
                        else if (x2 >= width) x2 = width - 1;

                        byte neighborVal = src[x2 + y2 * width + z2 * (width * height)];
                        window[idx++] = neighborVal;
                    }
                }
            }

            // Sort the local array. ILGPU supports 'XMath.Sort', but it's easier to do bubble or
            // do a naive approach. We'll do a simple insertion sort in place. 
            // For big kSize, you'd want something more optimized, but here's a direct approach:
            for (int i = 1; i < idx; i++)
            {
                byte key = window[i];
                int j = i - 1;
                while (j >= 0 && window[j] > key)
                {
                    window[j + 1] = window[j];
                    j--;
                }
                window[j + 1] = key;
            }

            // The median is window[idx/2], 
            // since idx == kSize^3
            byte medianVal = window[idx / 2];
            dst[threadIndex] = medianVal;
        }
        /// <summary>
        /// CPU-based 3D Gaussian for a flattened volume. No placeholders: triple nested loops,
        /// plus triple nested neighbor loops. Very expensive for large kernel but fully naive.
        /// </summary>
        private byte[] GaussianFilter3D_CPU_Full(byte[] src, int width, int height, int depth, int kSize, float sigma, ProgressForm pForm)
        {
            byte[] dst = new byte[src.Length];
            int radius = kSize / 2;
            float[] kernel3D = Build3DGaussianKernel(kSize, sigma); // same builder as used in GPU

            for (int z = 0; z < depth; z++)
            {
                pForm.SafeUpdateProgress(z, depth, $"CPU 3D Gaussian: slice {z + 1}/{depth}");
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        float accum = 0f;
                        // neighbor loops
                        int kIndex = 0;
                        int idxDst = z * width * height + y * width + x;

                        int kernelPos = 0;
                        int index = 0;

                        int i = 0;
                        int outIndex = 0;

                        int offsetBase = z * (width * height);

                        float sumVal = 0f;
                        int bigIndex = 0;

                        float val = 0f;

                        float total = 0f;

                        // We'll do it more directly:
                        float localSum = 0f;
                        int kernelIndexBaseZ = 0;
                        int zIndex = 0;

                        for (int dz = -radius; dz <= radius; dz++)
                        {
                            int nz = z + dz;
                            if (nz < 0) nz = 0;
                            else if (nz >= depth) nz = depth - 1;

                            int zOffset = nz * width * height;
                            int kernelZ = (dz + radius) * (kSize * kSize);

                            for (int dy = -radius; dy <= radius; dy++)
                            {
                                int ny = y + dy;
                                if (ny < 0) ny = 0;
                                else if (ny >= height) ny = height - 1;

                                int yOffset = ny * width;
                                int kernelY = (dy + radius) * kSize;

                                for (int dx = -radius; dx <= radius; dx++)
                                {
                                    int nx = x + dx;
                                    if (nx < 0) nx = 0;
                                    else if (nx >= width) nx = width - 1;

                                    int kernelX = dx + radius;
                                    int kPos = kernelZ + kernelY + kernelX;

                                    float w = kernel3D[kPos];
                                    byte neighborVal = src[zOffset + yOffset + nx];
                                    accum += w * neighborVal;
                                }
                            }
                        }

                        int finalVal = (int)(accum + 0.5f);
                        if (finalVal < 0) finalVal = 0;
                        if (finalVal > 255) finalVal = 255;
                        dst[idxDst] = (byte)finalVal;
                    }
                }
            }

            return dst;
        }

        /// <summary>
        /// CPU-based 3D median for a flattened volume: for each voxel, gather neighbors,
        /// sort them, pick the middle. Fully naive but no placeholders.
        /// </summary>
        private byte[] MedianFilter3D_CPU_Full(byte[] src, int width, int height, int depth, int kSize, ProgressForm pForm)
        {
            byte[] dst = new byte[src.Length];
            int radius = kSize / 2;
            int windowSize = kSize * kSize * kSize;
            byte[] window = new byte[windowSize];

            for (int z = 0; z < depth; z++)
            {
                pForm.SafeUpdateProgress(z, depth, $"CPU 3D Median: slice {z + 1}/{depth}");
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int idxDst = z * width * height + y * width + x;
                        int idx = 0;

                        for (int dz = -radius; dz <= radius; dz++)
                        {
                            int nz = z + dz;
                            if (nz < 0) nz = 0;
                            else if (nz >= depth) nz = depth - 1;

                            for (int dy = -radius; dy <= radius; dy++)
                            {
                                int ny = y + dy;
                                if (ny < 0) ny = 0;
                                else if (ny >= height) ny = height - 1;

                                for (int dx = -radius; dx <= radius; dx++)
                                {
                                    int nx = x + dx;
                                    if (nx < 0) nx = 0;
                                    else if (nx >= width) nx = width - 1;

                                    window[idx++] = src[nz * width * height + ny * width + nx];
                                }
                            }
                        }

                        // Sort window to get the median
                        Array.Sort(window, 0, idx);
                        byte medianVal = window[idx / 2];
                        dst[idxDst] = medianVal;
                    }
                }
            }
            return dst;
        }

        #endregion
        /// <summary>
        /// Optimized version of slice rendering to minimize UI thread work
        /// </summary>
        private void AddSliceNavigationControls()
        {
            // Add a panel for the slice navigation controls at the top of the preview panel
            Panel sliceNavPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 50,
                BackColor = Color.FromArgb(50, 50, 50)
            };
            previewPanel.Controls.Add(sliceNavPanel);

            // Reposition the XY preview to fit below the navigation panel
            xyPreview.Dock = DockStyle.Fill;

            // Label for "Slice:"
            Label lblSlice = new Label
            {
                Text = "Slice:",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 17)
            };
            sliceNavPanel.Controls.Add(lblSlice);

            // Trackbar for slice selection
            sliceTrackBar = new TrackBar
            {
                Location = new Point(60, 10),
                Width = sliceNavPanel.Width - 160,
                Minimum = 0,
                Maximum = Math.Max(0, mainForm.GetDepth() - 1),
                Value = mainForm.CurrentSlice,
                TickFrequency = Math.Max(1, mainForm.GetDepth() / 20),
                SmallChange = 1,
                LargeChange = 10,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };

            // Current slice number label
            lblSliceNumber = new Label
            {
                Text = $"{mainForm.CurrentSlice + 1}/{mainForm.GetDepth()}",
                ForeColor = Color.White,
                AutoSize = true,
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                Location = new Point(sliceNavPanel.Width - 90, 17)
            };

            // Setup the timer for deferred rendering (10ms to ensure responsive UI but quick updates)
            renderTimer = new System.Threading.Timer(RenderPendingSlice, null, Timeout.Infinite, Timeout.Infinite);

            // Keyboard navigation for slices
            filterForm.KeyDown += (s, e) => {
                if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down)
                {
                    if (sliceTrackBar.Value > sliceTrackBar.Minimum)
                        sliceTrackBar.Value--;
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up)
                {
                    if (sliceTrackBar.Value < sliceTrackBar.Maximum)
                        sliceTrackBar.Value++;
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.PageDown)
                {
                    sliceTrackBar.Value = Math.Max(sliceTrackBar.Minimum, sliceTrackBar.Value - 10);
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.PageUp)
                {
                    sliceTrackBar.Value = Math.Min(sliceTrackBar.Maximum, sliceTrackBar.Value + 10);
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Home)
                {
                    sliceTrackBar.Value = sliceTrackBar.Minimum;
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.End)
                {
                    sliceTrackBar.Value = sliceTrackBar.Maximum;
                    e.Handled = true;
                }
            };

            // Configure events with performance in mind
            sliceTrackBar.ValueChanged += (s, e) => {
                // Update the slice number immediately for better responsiveness feel
                lblSliceNumber.Text = $"{sliceTrackBar.Value + 1}/{mainForm.GetDepth()}";

                // Save the value to render shortly
                pendingSliceValue = sliceTrackBar.Value;

                // Schedule rendering with a very short delay to batch rapid changes
                renderTimer.Change(10, Timeout.Infinite);
            };

            // Add controls
            sliceNavPanel.Controls.Add(sliceTrackBar);
            sliceNavPanel.Controls.Add(lblSliceNumber);

            // Make form focusable to enable keyboard navigation
            filterForm.KeyPreview = true;
        }
        /// <summary>
        /// Optimized slice rendering that minimizes memory allocations and UI thread work
        /// </summary>
        private void RenderPendingSlice(object state)
        {
            if (pendingSliceValue == -1 || filterForm == null || filterForm.IsDisposed) return;

            int sliceToRender = pendingSliceValue;
            pendingSliceValue = -1;

            try
            {
                // Create the bitmap on a background thread
                Bitmap newBitmap = CreateBitmapDirectlyFromVolume(sliceToRender);

                // Update UI on the UI thread but keep it minimal
                filterForm.BeginInvoke(new Action(() => {
                    try
                    {
                        if (filterForm == null || filterForm.IsDisposed) return;

                        // Update the MainForm's current slice 
                        mainForm.CurrentSlice = sliceToRender;

                        // Swap the image
                        Image oldImage = xyPreview.Image;
                        xyPreview.Image = newBitmap;

                        // Clean up the old image *after* setting the new one
                        if (oldImage != null) oldImage.Dispose();

                        // Redraw with proper zoom
                        xyPreview.Invalidate();

                        // Update label to reflect the current slice
                        lblSliceNumber.Text = $"{sliceToRender + 1}/{mainForm.GetDepth()}";
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[FilterManager] Error updating UI after slice load: {ex.Message}");
                    }
                }));
            }
            catch (Exception ex)
            {
                Logger.Log($"[FilterManager] Error in RenderPendingSlice: {ex.Message}");
            }
        }
        /// <summary>
        /// Creates a bitmap directly from the volume data without intermediate copies
        /// </summary>
        private Bitmap CreateBitmapDirectlyFromVolume(int sliceIndex)
        {
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();

            // Create bitmap directly as 32-bit for faster rendering
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);

            // Lock bits and copy - access pixels directly for speed
            BitmapData bmpData = bmp.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);

            unsafe
            {
                byte* bmpPtr = (byte*)bmpData.Scan0;
                int stride = bmpData.Stride;

                // Optimized loop for 32-bit pixels - direct access to volume data
                for (int y = 0; y < height; y++)
                {
                    byte* row = bmpPtr + (y * stride);

                    for (int x = 0; x < width; x++)
                    {
                        // Get pixel directly from volume - only a single lookup per pixel
                        byte pixelValue = mainForm.volumeData[x, y, sliceIndex];

                        // Set BGRA values (Format32bppArgb is actually BGRA in memory)
                        row[x * 4 + 0] = pixelValue; // B
                        row[x * 4 + 1] = pixelValue; // G
                        row[x * 4 + 2] = pixelValue; // R
                        row[x * 4 + 3] = 255;        // A (always fully opaque)
                    }
                }
            }

            bmp.UnlockBits(bmpData);
            return bmp;
        }

        /// <summary>
        /// Loads slice data from the volume faster by accessing the array directly
        /// </summary>
        private byte[] LoadSliceData(int sliceIndex)
        {
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            byte[] sliceData = new byte[width * height];

            // Copy data directly from the volume
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    sliceData[y * width + x] = mainForm.volumeData[x, y, sliceIndex];
                }
            }

            return sliceData;
        }

        /// <summary>
        /// Creates a bitmap from slice data with minimal processing
        /// </summary>
        private Bitmap CreateBitmapFromSliceData(byte[] sliceData, int width, int height)
        {
            // Create bitmap directly as 32-bit for faster rendering
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);

            // Lock bits and copy - access pixels directly for speed
            BitmapData bmpData = bmp.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);

            unsafe
            {
                byte* bmpPtr = (byte*)bmpData.Scan0;
                int stride = bmpData.Stride;

                // Optimized loop for 32-bit pixels
                for (int y = 0; y < height; y++)
                {
                    byte* row = bmpPtr + (y * stride);

                    for (int x = 0; x < width; x++)
                    {
                        int pixelIndex = y * width + x;
                        byte pixelValue = sliceData[pixelIndex];

                        // Set BGRA values (Format32bppArgb is actually BGRA in memory)
                        row[x * 4 + 0] = pixelValue; // B
                        row[x * 4 + 1] = pixelValue; // G
                        row[x * 4 + 2] = pixelValue; // R
                        row[x * 4 + 3] = 255;        // A (always fully opaque)
                    }
                }
            }

            bmp.UnlockBits(bmpData);
            return bmp;
        }

        // Add field to track if we have a filtered slice displayed
        private byte[] lastFilteredSlice = null;
        private void UpdateForNewVolumeData()
        {
            if (sliceTrackBar != null)
            {
                sliceTrackBar.Minimum = 0;
                sliceTrackBar.Maximum = Math.Max(0, mainForm.GetDepth() - 1);
                sliceTrackBar.Value = mainForm.CurrentSlice;
                lblSliceNumber.Text = $"{mainForm.CurrentSlice + 1}/{mainForm.GetDepth()}";
            }

            lastFilteredSlice = null;
            RenderPreviewSlice();
        }
        public void UpdateVolumeData()
        {
            UpdateForNewVolumeData();
        }

        /// <summary>
        /// Resets the zoom factor and position to default
        /// </summary>
        private void ResetZoom()
        {
            // Reset zoom state
            zoomFactor = 1.0f;
            zoomOrigin = Point.Empty;

            // Switch back to built-in Zoom mode
            xyPreview.SizeMode = PictureBoxSizeMode.Zoom;
            xyPreview.Invalidate();
        }

        /// <summary>
        /// Handles mouse wheel events for zooming
        /// </summary>
        private void XyPreview_MouseWheel(object sender, MouseEventArgs e)
        {
            if (xyPreview.Image == null) return;

            // When first zooming, switch from automatic sizing to manual rendering
            if (xyPreview.SizeMode != PictureBoxSizeMode.Normal)
            {
                xyPreview.SizeMode = PictureBoxSizeMode.Normal;
                FitImageToPictureBox();
            }

            // Store the mouse position relative to the image
            Point mousePos = e.Location;

            // Calculate new zoom factor
            float oldZoom = zoomFactor;
            if (e.Delta > 0)
            {
                // Zoom in
                zoomFactor = Math.Min(MAX_ZOOM, zoomFactor * 1.1f);
            }
            else
            {
                // Zoom out
                zoomFactor = Math.Max(MIN_ZOOM, zoomFactor / 1.1f);
            }

            // Apply the zoom with the mouse position as focus
            ApplyZoomWithFocus(mousePos, oldZoom);
        }
        /// <summary>
        /// Calculates the initial zoom to fit the image to the PictureBox
        /// </summary>
        private void FitImageToPictureBox()
        {
            if (xyPreview.Image == null) return;

            // Calculate the scale to fit the image fully in the control
            float scaleX = (float)xyPreview.ClientSize.Width / xyPreview.Image.Width;
            float scaleY = (float)xyPreview.ClientSize.Height / xyPreview.Image.Height;
            zoomFactor = Math.Min(scaleX, scaleY);

            // Center the image
            int xOffset = (int)((xyPreview.ClientSize.Width - (xyPreview.Image.Width * zoomFactor)) / 2);
            int yOffset = (int)((xyPreview.ClientSize.Height - (xyPreview.Image.Height * zoomFactor)) / 2);

            // Store the inverse of the offset in zoomOrigin (since we apply negative origin)
            zoomOrigin = new Point(-xOffset, -yOffset);

            xyPreview.Invalidate();
        }

        /// <summary>
        /// Handles mouse down for panning with middle button only
        /// </summary>
        private void XyPreview_MouseDownForPan(object sender, MouseEventArgs e)
        {
            // Only enable panning with middle mouse button, leaving left button for ROI
            if (e.Button == MouseButtons.Middle)
            {
                isPanning = true;
                lastPanPoint = e.Location;
                xyPreview.Cursor = Cursors.Hand;
            }
        }

        /// <summary>
        /// Handles mouse move for panning with middle button only
        /// </summary>
        private void XyPreview_MouseMoveForPan(object sender, MouseEventArgs e)
        {
            // Only process panning with middle button, leaving left button for ROI
            if (isPanning && e.Button == MouseButtons.Middle)
            {
                // Calculate the delta and update the zoom origin
                int deltaX = e.X - lastPanPoint.X;
                int deltaY = e.Y - lastPanPoint.Y;

                // Adjust the zoom origin (add delta to make dragging feel natural)
                zoomOrigin.X += deltaX;
                zoomOrigin.Y += deltaY;

                // Update last pan point
                lastPanPoint = e.Location;

                // Redraw
                xyPreview.Invalidate();
            }
        }

        /// <summary>
        /// Converts a mouse event from zoomed coordinates to original image coordinates
        /// </summary>
        private MouseEventArgs ConvertZoomedToOriginalMouseEvent(MouseEventArgs e)
        {
            if (zoomFactor <= 0) return e;

            int originalX = (int)((e.X - zoomOrigin.X) / zoomFactor);
            int originalY = (int)((e.Y - zoomOrigin.Y) / zoomFactor);

            return new MouseEventArgs(
                e.Button, e.Clicks, originalX, originalY, e.Delta);
        }

        /// <summary>
        /// Handles mouse up for panning
        /// </summary>
        private void XyPreview_MouseUpForPan(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Middle)
            {
                isPanning = false;
                xyPreview.Cursor = Cursors.Default;
            }
            else if (useRoi && e.Button == MouseButtons.Left) // Handle ROI dragging with left button
            {
                XyPreview_MouseUp(sender, ConvertZoomedToOriginalMouseEvent(e));
            }
        }

        /// <summary>
        /// Applies the current zoom factor with the given focus point
        /// </summary>
        private void ApplyZoomWithFocus(Point focusPoint, float oldZoom)
        {
            if (xyPreview.Image == null) return;

            try
            {
                // Calculate the image point under the cursor
                float imageX = (focusPoint.X - zoomOrigin.X) / oldZoom;
                float imageY = (focusPoint.Y - zoomOrigin.Y) / oldZoom;

                // Calculate new origin to keep the cursor over the same image point
                zoomOrigin.X = (int)(focusPoint.X - imageX * zoomFactor);
                zoomOrigin.Y = (int)(focusPoint.Y - imageY * zoomFactor);

                // Redraw
                xyPreview.Invalidate();
            }
            catch (Exception ex)
            {
                Logger.Log($"[FilterManager] Error in ApplyZoomWithFocus: {ex.Message}");
                ResetZoom(); // Reset on error
            }
        }

        /// <summary>
        /// Applies the current zoom factor with the given focus point
        /// </summary>
        private void ApplyZoomWithFocus(Point focusPoint)
        {
            if (xyPreview.Image == null) return;

            // Calculate where the focus point should be after zoom (in image coordinates)
            float focusRatioX = (focusPoint.X + zoomOrigin.X) / (xyPreview.Width * zoomFactor);
            float focusRatioY = (focusPoint.Y + zoomOrigin.Y) / (xyPreview.Height * zoomFactor);

            // Calculate the new image size
            int newWidth = (int)(xyPreview.Image.Width * zoomFactor);
            int newHeight = (int)(xyPreview.Image.Height * zoomFactor);

            // Calculate the new origin to keep the focus point at the same position
            zoomOrigin.X = (int)(focusRatioX * newWidth - focusPoint.X);
            zoomOrigin.Y = (int)(focusRatioY * newHeight - focusPoint.Y);

            // Repaint
            xyPreview.Invalidate();
        }

        /// <summary>
        /// Applies the current zoom factor
        /// </summary>
        private void ApplyZoom()
        {
            xyPreview.Invalidate();
        }

        /// <summary>
        /// Enhanced paint handler that supports zooming and ROI
        /// </summary>
        private void XyPreview_PaintWithZoom(object sender, PaintEventArgs e)
        {
            if (xyPreview.Image == null) return;

            // Enable high quality rendering
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

            // Calculate the zoom rectangle (where to draw the image)
            Rectangle srcRect = new Rectangle(0, 0, xyPreview.Image.Width, xyPreview.Image.Height);

            int zoomedWidth = (int)(xyPreview.Image.Width * zoomFactor);
            int zoomedHeight = (int)(xyPreview.Image.Height * zoomFactor);

            // Calculate the destination rectangle with zoom origin
            Rectangle destRect = new Rectangle(
                -zoomOrigin.X,
                -zoomOrigin.Y,
                zoomedWidth,
                zoomedHeight);

            // Draw the image with zooming
            e.Graphics.DrawImage(xyPreview.Image, destRect, srcRect, GraphicsUnit.Pixel);

            // If ROI is active, draw it with zoom accounted for
            if (useRoi)
            {
                // Calculate ROI rectangle in zoomed coordinates
                Rectangle zoomedRoi = new Rectangle(
                    (int)(roi.X * zoomFactor) - zoomOrigin.X,
                    (int)(roi.Y * zoomFactor) - zoomOrigin.Y,
                    (int)(roi.Width * zoomFactor),
                    (int)(roi.Height * zoomFactor));

                // Draw ROI rectangle
                using (Pen pen = new Pen(Color.Yellow, 2))
                {
                    e.Graphics.DrawRectangle(pen, zoomedRoi);
                }

                // Draw resize handle
                using (Brush brush = new SolidBrush(Color.Yellow))
                {
                    e.Graphics.FillRectangle(brush,
                        zoomedRoi.Right - RESIZE_HANDLE_SIZE,
                        zoomedRoi.Bottom - RESIZE_HANDLE_SIZE,
                        RESIZE_HANDLE_SIZE,
                        RESIZE_HANDLE_SIZE);
                }
            }
        }
        /// <summary>
        /// Sets up the mouse wheel zoom and pan functionality
        /// </summary>
        private void SetupZoomFunctionality()
        {
            // Set PictureBox to Zoom mode initially
            xyPreview.SizeMode = PictureBoxSizeMode.Zoom;

            // Add mouse wheel handler for zooming
            xyPreview.MouseWheel += XyPreview_MouseWheel;

            // Update all mouse handlers to ensure compatibility
            UpdateMouseHandlers();

            // Add a reset zoom button to the preview panel
            btnResetZoom = new Button
            {
                Text = "Reset Zoom",
                Size = new Size(80, 23),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Location = new Point(previewPanel.Width - 90, previewPanel.Height - 30)
            };
            btnResetZoom.Click += (s, e) => ResetZoom();
            previewPanel.Controls.Add(btnResetZoom);
        }
    }
}
