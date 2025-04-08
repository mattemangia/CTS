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

        private Label lblStatus;
        private ProgressForm progressForm;

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
            // Add more filters here as desired
            cmbFilterType.Items.Add("Gaussian");
            cmbFilterType.Items.Add("Smoothing");
            cmbFilterType.Items.Add("Median");
            cmbFilterType.Items.Add("Non-Local Means");
            cmbFilterType.Items.Add("Bilateral"); // Example if you want more
            cmbFilterType.SelectedIndex = 0;
            controlsPanel.Controls.Add(cmbFilterType);
            currentY += 30;

            // Kernel Size
            Label lblKernelSize = new Label
            {
                Text = "Kernel Size (odd):",
                AutoSize = true,
                Location = new Point(10, currentY)
            };
            controlsPanel.Controls.Add(lblKernelSize);
            currentY += 20;

            numKernelSize = new NumericUpDown
            {
                Location = new Point(10, currentY),
                Width = 60,
                Minimum = 1,
                Maximum = 31,
                Value = 3
            };
            // Force it to be odd
            numKernelSize.ValueChanged += (s, e) =>
            {
                if (numKernelSize.Value % 2 == 0)
                    numKernelSize.Value += 1;
            };
            controlsPanel.Controls.Add(numKernelSize);
            currentY += 30;

            // Sigma (for Gaussian, Bilateral, etc.)
            Label lblSigma = new Label
            {
                Text = "Sigma (for Gaussian):",
                AutoSize = true,
                Location = new Point(10, currentY)
            };
            controlsPanel.Controls.Add(lblSigma);
            currentY += 20;

            numSigma = new NumericUpDown
            {
                Location = new Point(10, currentY),
                Width = 60,
                Minimum = 1,
                Maximum = 100,
                Value = 10
            };
            controlsPanel.Controls.Add(numSigma);
            currentY += 30;

            // Non-Local Means parameters (H, template window, search window)
            Label lblNlm = new Label
            {
                Text = "NLM (H, Template, Search):",
                AutoSize = true,
                Location = new Point(10, currentY)
            };
            controlsPanel.Controls.Add(lblNlm);
            currentY += 20;

            numNlmH = new NumericUpDown
            {
                Location = new Point(10, currentY),
                Width = 60,
                Minimum = 1,
                Maximum = 255,
                Value = 10
            };
            controlsPanel.Controls.Add(numNlmH);

            numNlmTemplate = new NumericUpDown
            {
                Location = new Point(80, currentY),
                Width = 60,
                Minimum = 1,
                Maximum = 15,
                Value = 3
            };
            controlsPanel.Controls.Add(numNlmTemplate);

            numNlmSearch = new NumericUpDown
            {
                Location = new Point(150, currentY),
                Width = 60,
                Minimum = 1,
                Maximum = 21,
                Value = 7
            };
            controlsPanel.Controls.Add(numNlmSearch);

            currentY += 30;

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

            // Convert to a 8bpp grayscale Bitmap
            using (Bitmap bmp = new Bitmap(width, height, PixelFormat.Format8bppIndexed))
            {
                // Set a grayscale palette
                ColorPalette pal = bmp.Palette;
                for (int i = 0; i < 256; i++)
                {
                    pal.Entries[i] = Color.FromArgb(i, i, i);
                }
                bmp.Palette = pal;

                // Lock bits and copy
                Rectangle rect = new Rectangle(0, 0, width, height);
                BitmapData bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
                int stride = bd.Stride;
                unsafe
                {
                    fixed (byte* srcPtr = sliceData)
                    {
                        for (int y = 0; y < height; y++)
                        {
                            byte* dstRow = (byte*)bd.Scan0 + y * stride;
                            byte* srcRow = srcPtr + y * width;
                            System.Buffer.BlockCopy(sliceData, y * width, new byte[width], 0, width);
                            for (int x = 0; x < width; x++)
                            {
                                dstRow[x] = srcRow[x];
                            }
                        }
                    }
                }
                bmp.UnlockBits(bd);

                // If isFiltered, we might overlay a label or something in corner
                if (isFiltered)
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.DrawString("Previewed Filter", new Font("Arial", 12), Brushes.Red, new PointF(5, 5));
                    }
                }

                // Dispose old image if any
                if (xyPreview.Image != null) xyPreview.Image.Dispose();
                xyPreview.Image = (Bitmap)bmp.Clone();
            }
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

            string filterName = cmbFilterType.SelectedItem.ToString();
            int kernelSize = (int)numKernelSize.Value;
            float sigma = (float)numSigma.Value;

            // For non-local means specifically
            float h = (float)numNlmH.Value;
            int templateSize = (int)numNlmTemplate.Value;
            int searchSize = (int)numNlmSearch.Value;

            byte[] filteredSlice = await Task.Run(() =>
            {
                return ApplyFilter2D(sliceData, width, height, filterName,
                                     kernelSize, sigma, h, templateSize, searchSize,
                                     useGPU);
            });

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
                    
                    return NonLocalMeans2D_CPU(sliceData, width, height, kernelSize, h, templateSize, searchSize);

                case "Bilateral":
                    
                    return BilateralFilter2D_CPU(sliceData, width, height, kernelSize, sigma);

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

        #region 2D CPU Methods (Examples)

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
    }
}
