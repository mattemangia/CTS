//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using ILGPU;
using ILGPU.Runtime;
using System;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS
{
    public partial class IntegrateResampleForm : Form
    {
        private Context context;
        private Device device;
        private Accelerator accelerator;
        private bool isGPU = true;
        private MainForm mainForm;
        private bool processingCancelled = false;

        // Form components
        private ComboBox cboDevices;

        private Button btnOK;
        private Button btnCancel;
        private NumericUpDown numResampleFactor;
        private Label lblResampleFactor;
        private CheckBox chkUseGPU;
        private Label lblDevices;
        private ProgressBar progressBar;
        private Label lblStatus;
        private Label lblInstructions;
        private GroupBox grpOutputOptions;
        private RadioButton radOverwrite;
        private RadioButton radNewVolume;
        private CheckBox chkExportBMP;
        private Button btnBrowseExport;
        private Label lblExportPath;

        // Properties for export options
        public bool OverwriteExisting { get; private set; } = true;

        public bool ExportAsBMP { get; private set; } = false;
        public string ExportPath { get; private set; } = "";

        public float ResampleFactor { get; private set; } = 1.0f;
        public bool Success { get; private set; } = false;

        public IntegrateResampleForm(MainForm main)
        {
            mainForm = main;
            InitializeComponent();
            InitializeAccelerators();
        }

        private void InitializeComponent()
        {
            try
            {
                this.Icon = Properties.Resources.favicon;
            }
            catch { }
            this.cboDevices = new ComboBox();
            this.btnOK = new Button();
            this.btnCancel = new Button();
            this.numResampleFactor = new NumericUpDown();
            this.lblResampleFactor = new Label();
            this.chkUseGPU = new CheckBox();
            this.lblDevices = new Label();
            this.progressBar = new ProgressBar();
            this.lblStatus = new Label();
            this.lblInstructions = new Label();
            this.grpOutputOptions = new GroupBox();
            this.radOverwrite = new RadioButton();
            this.radNewVolume = new RadioButton();
            this.chkExportBMP = new CheckBox();
            this.btnBrowseExport = new Button();
            this.lblExportPath = new Label();
            ((ISupportInitialize)this.numResampleFactor).BeginInit();
            this.grpOutputOptions.SuspendLayout();
            this.SuspendLayout();

            // Add the instructions label
            this.lblInstructions = new Label();
            this.lblInstructions.AutoSize = false;
            this.lblInstructions.Location = new Point(12, 180);
            this.lblInstructions.Size = new Size(350, 40);
            this.lblInstructions.TextAlign = ContentAlignment.MiddleLeft;
            this.lblInstructions.Text = "Enter resample factor (>1 to increase resolution, <1 to decrease), " +
                                     "select a device, choose output options, then click OK.";
            this.lblInstructions.BorderStyle = BorderStyle.FixedSingle;
            this.lblInstructions.BackColor = Color.LightYellow;
            this.lblInstructions.Font = new Font(this.Font, FontStyle.Regular);

            // lblResampleFactor
            this.lblResampleFactor.AutoSize = true;
            this.lblResampleFactor.Location = new Point(12, 15);
            this.lblResampleFactor.Name = "lblResampleFactor";
            this.lblResampleFactor.Size = new Size(97, 13);
            this.lblResampleFactor.TabIndex = 1;
            this.lblResampleFactor.Text = "Resample Factor:";

            // numResampleFactor
            this.numResampleFactor.DecimalPlaces = 2;
            this.numResampleFactor.Increment = new decimal(new int[] { 1, 0, 0, 131072 });
            this.numResampleFactor.Location = new Point(115, 13);
            this.numResampleFactor.Maximum = new decimal(new int[] { 10, 0, 0, 0 });
            this.numResampleFactor.Minimum = new decimal(new int[] { 1, 0, 0, 131072 });
            this.numResampleFactor.Name = "numResampleFactor";
            this.numResampleFactor.Size = new Size(248, 20);
            this.numResampleFactor.TabIndex = 2;
            this.numResampleFactor.Value = new decimal(new int[] { 1, 0, 0, 0 });

            // lblDevices
            this.lblDevices.AutoSize = true;
            this.lblDevices.Location = new Point(12, 44);
            this.lblDevices.Name = "lblDevices";
            this.lblDevices.Size = new Size(50, 13);
            this.lblDevices.TabIndex = 3;
            this.lblDevices.Text = "Device:";

            // cboDevices
            this.cboDevices.FormattingEnabled = true;
            this.cboDevices.Location = new Point(115, 41);
            this.cboDevices.Name = "cboDevices";
            this.cboDevices.Size = new Size(248, 21);
            this.cboDevices.TabIndex = 4;
            this.cboDevices.SelectedIndexChanged += new EventHandler(this.CboDevices_SelectedIndexChanged);

            // chkUseGPU
            this.chkUseGPU.AutoSize = true;
            this.chkUseGPU.Checked = true;
            this.chkUseGPU.CheckState = CheckState.Checked;
            this.chkUseGPU.Location = new Point(115, 68);
            this.chkUseGPU.Name = "chkUseGPU";
            this.chkUseGPU.Size = new Size(72, 17);
            this.chkUseGPU.TabIndex = 5;
            this.chkUseGPU.Text = "Use GPU";
            this.chkUseGPU.UseVisualStyleBackColor = true;
            this.chkUseGPU.CheckedChanged += new EventHandler(this.ChkUseGPU_CheckedChanged);

            // Group box for output options
            this.grpOutputOptions = new GroupBox();
            this.grpOutputOptions.Text = "Output Options";
            this.grpOutputOptions.Location = new Point(12, 91);
            this.grpOutputOptions.Size = new Size(350, 85);
            this.grpOutputOptions.TabIndex = 10;

            // Radio button for overwriting existing data
            this.radOverwrite = new RadioButton();
            this.radOverwrite.Text = "Overwrite existing volume";
            this.radOverwrite.Location = new Point(10, 20);
            this.radOverwrite.Size = new Size(160, 20);
            this.radOverwrite.Checked = true;
            this.radOverwrite.TabIndex = 11;

            // Radio button for creating new volume
            this.radNewVolume = new RadioButton();
            this.radNewVolume.Text = "Create new volume";
            this.radNewVolume.Location = new Point(180, 20);
            this.radNewVolume.Size = new Size(160, 20);
            this.radNewVolume.TabIndex = 12;

            // Checkbox for BMP export
            this.chkExportBMP = new CheckBox();
            this.chkExportBMP.Text = "Export as 8-bit BMP stack";
            this.chkExportBMP.Location = new Point(10, 45);
            this.chkExportBMP.Size = new Size(160, 20);
            this.chkExportBMP.TabIndex = 13;
            this.chkExportBMP.CheckedChanged += new EventHandler(this.ChkExportBMP_CheckedChanged);

            // Button for browsing export location
            this.btnBrowseExport = new Button();
            this.btnBrowseExport.Text = "Browse...";
            this.btnBrowseExport.Location = new Point(260, 45);
            this.btnBrowseExport.Size = new Size(80, 20);
            this.btnBrowseExport.TabIndex = 14;
            this.btnBrowseExport.Enabled = false;
            this.btnBrowseExport.Click += new EventHandler(this.BtnBrowseExport_Click);

            // Label for export path
            this.lblExportPath = new Label();
            this.lblExportPath.Text = "No export path selected";
            this.lblExportPath.Location = new Point(10, 65);
            this.lblExportPath.Size = new Size(330, 15);
            this.lblExportPath.Font = new Font(this.Font.FontFamily, 7);
            this.lblExportPath.TabIndex = 15;

            // Add controls to the group box
            this.grpOutputOptions.Controls.Add(this.radOverwrite);
            this.grpOutputOptions.Controls.Add(this.radNewVolume);
            this.grpOutputOptions.Controls.Add(this.chkExportBMP);
            this.grpOutputOptions.Controls.Add(this.btnBrowseExport);
            this.grpOutputOptions.Controls.Add(this.lblExportPath);

            // progressBar
            this.progressBar.Location = new Point(115, 230);
            this.progressBar.Name = "progressBar";
            this.progressBar.Size = new Size(248, 23);
            this.progressBar.TabIndex = 6;
            this.progressBar.Visible = false;

            // lblStatus
            this.lblStatus.AutoSize = true;
            this.lblStatus.Location = new Point(12, 235);
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new Size(40, 13);
            this.lblStatus.TabIndex = 7;
            this.lblStatus.Text = "Status:";
            this.lblStatus.Visible = false;

            // btnOK
            this.btnOK.Location = new Point(206, 265);
            this.btnOK.Name = "btnOK";
            this.btnOK.Size = new Size(75, 23);
            this.btnOK.TabIndex = 8;
            this.btnOK.Text = "OK";
            this.btnOK.UseVisualStyleBackColor = true;
            this.btnOK.Click += new EventHandler(this.BtnOK_Click);

            // btnCancel
            this.btnCancel.DialogResult = DialogResult.Cancel;
            this.btnCancel.Location = new Point(287, 265);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new Size(75, 23);
            this.btnCancel.TabIndex = 9;
            this.btnCancel.Text = "Cancel";
            this.btnCancel.UseVisualStyleBackColor = true;
            this.btnCancel.Click += new EventHandler(this.BtnCancel_Click);

            // IntegrateResampleForm
            this.AcceptButton = this.btnOK;
            this.AutoScaleDimensions = new SizeF(6F, 13F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.CancelButton = this.btnCancel;
            this.ClientSize = new Size(375, 300); // Increased height for new controls
            this.Controls.Add(this.grpOutputOptions);
            this.Controls.Add(this.btnCancel);
            this.Controls.Add(this.btnOK);
            this.Controls.Add(this.lblStatus);
            this.Controls.Add(this.progressBar);
            this.Controls.Add(this.chkUseGPU);
            this.Controls.Add(this.cboDevices);
            this.Controls.Add(this.lblDevices);
            this.Controls.Add(this.numResampleFactor);
            this.Controls.Add(this.lblResampleFactor);
            this.Controls.Add(this.lblInstructions);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "IntegrateResampleForm";
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "Integrate and Resample";
            this.FormClosing += new FormClosingEventHandler(this.IntegrateResampleForm_FormClosing);
            ((ISupportInitialize)this.numResampleFactor).EndInit();
            this.grpOutputOptions.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        private void InitializeAccelerators()
        {
            try
            {
                Logger.Log("[IntegrateResampleForm] Initializing accelerators");

                // Initialize ILGPU context
                context = Context.Create(builder => builder.Default().EnableAlgorithms());
                Logger.Log("[IntegrateResampleForm] ILGPU context created successfully");

                // Add CPU device first
                cboDevices.Items.Add("CPU Accelerator");
                Logger.Log("[IntegrateResampleForm] Added CPU accelerator option");

                // Add GPU devices if available - Using AcceleratorType.Cuda and AcceleratorType.OpenCL
                foreach (var device in context.Devices.Where(d =>
                    d.AcceleratorType == AcceleratorType.Cuda ||
                    d.AcceleratorType == AcceleratorType.OpenCL))
                {
                    cboDevices.Items.Add($"{device.Name} ({device.AcceleratorType})");
                    Logger.Log($"[IntegrateResampleForm] Found GPU device: {device.Name} - {device.AcceleratorType}");
                }

                if (cboDevices.Items.Count > 1)
                {
                    // Select first GPU device by default (index 1 assuming CPU is at index 0)
                    cboDevices.SelectedIndex = 1;
                    Logger.Log("[IntegrateResampleForm] Selected first GPU device");
                    chkUseGPU.Checked = true;
                    chkUseGPU.Enabled = true;
                }
                else
                {
                    // Fall back to CPU
                    cboDevices.SelectedIndex = 0;
                    Logger.Log("[IntegrateResampleForm] No GPU devices found, using CPU");
                    chkUseGPU.Checked = false;
                    chkUseGPU.Enabled = false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[IntegrateResampleForm] Error initializing accelerators: {ex.Message}");
                MessageBox.Show($"Error initializing accelerators: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void CboDevices_SelectedIndexChanged(object sender, EventArgs e)
        {
            int selectedIndex = cboDevices.SelectedIndex;

            // Enable GPU checkbox only if a GPU device is selected (assuming index 0 is CPU)
            isGPU = selectedIndex > 0;
            chkUseGPU.Enabled = isGPU;
            if (!isGPU)
                chkUseGPU.Checked = false;

            Logger.Log($"[IntegrateResampleForm] Selected device changed to: {cboDevices.SelectedItem}");
        }

        private void ChkUseGPU_CheckedChanged(object sender, EventArgs e)
        {
            isGPU = chkUseGPU.Checked;
            Logger.Log($"[IntegrateResampleForm] Use GPU setting changed to: {isGPU}");
        }

        private async void BtnOK_Click(object sender, EventArgs e)
        {
            try
            {
                ResampleFactor = (float)numResampleFactor.Value;
                Logger.Log($"[IntegrateResampleForm] Starting resampling with factor {ResampleFactor}");
                OverwriteExisting = radOverwrite.Checked;

                Logger.Log($"[IntegrateResampleForm] Starting resampling with factor {ResampleFactor}, " +
                          $"Overwrite={OverwriteExisting}, ExportBMP={ExportAsBMP}");
                // Disable UI during processing
                EnableControls(false);
                progressBar.Visible = true;
                lblStatus.Visible = true;
                lblStatus.Text = "Status: Initializing...";

                // Create the appropriate accelerator
                CreateAccelerator();

                // Perform the resampling operation asynchronously
                Success = await Task.Run(() => PerformResampling(ResampleFactor));

                if (Success)
                {
                    Logger.Log("[IntegrateResampleForm] Resampling completed successfully");
                    this.DialogResult = DialogResult.OK;
                    this.Close();
                }
                else if (!processingCancelled) // Only show error if not cancelled by user
                {
                    Logger.Log("[IntegrateResampleForm] Resampling failed");
                    MessageBox.Show("Resampling operation failed.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    EnableControls(true);
                    progressBar.Visible = false;
                    lblStatus.Visible = false;
                }
                else
                {
                    Logger.Log("[IntegrateResampleForm] Resampling was cancelled by user");
                    EnableControls(true);
                    progressBar.Visible = false;
                    lblStatus.Visible = false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[IntegrateResampleForm] Error in resampling: {ex.Message}");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                EnableControls(true);
                progressBar.Visible = false;
                lblStatus.Visible = false;
            }
        }

        private void CreateAccelerator()
        {
            // Dispose existing accelerator if any
            if (accelerator != null)
            {
                accelerator.Dispose();
                accelerator = null;
            }

            // Create appropriate accelerator based on selection
            if (isGPU && cboDevices.SelectedIndex > 0)
            {
                // GPU accelerator - find the corresponding GPU device
                int gpuIndex = cboDevices.SelectedIndex - 1; // -1 because CPU is at index 0

                // Fix for AcceleratorType.GPU and the comparison error
                var gpuDevices = context.Devices.Where(d =>
                    d.AcceleratorType == AcceleratorType.Cuda ||
                    d.AcceleratorType == AcceleratorType.OpenCL).ToList();

                if (gpuIndex < gpuDevices.Count)
                {
                    device = gpuDevices[gpuIndex];
                    Logger.Log($"[IntegrateResampleForm] Creating GPU accelerator: {device.Name}");
                    accelerator = device.CreateAccelerator(context);
                }
                else
                {
                    throw new InvalidOperationException("Selected GPU device not found");
                }
            }
            else
            {
                // CPU accelerator
                device = context.Devices.First(d => d.AcceleratorType == AcceleratorType.CPU);
                Logger.Log("[IntegrateResampleForm] Creating CPU accelerator");
                accelerator = device.CreateAccelerator(context);
            }

            Logger.Log($"[IntegrateResampleForm] Accelerator created: {accelerator.Name}");
        }

        private void BtnCancel_Click(object sender, EventArgs e)
        {
            Logger.Log("[IntegrateResampleForm] Cancel button clicked");
            processingCancelled = true;
            this.DialogResult = DialogResult.Cancel;
            this.Close();
        }

        private bool PerformResampling(float factor)
        {
            try
            {
                Logger.Log($"[IntegrateResampleForm] Starting resampling with factor {factor} using {(isGPU ? "GPU" : "CPU")}");

                if (mainForm.volumeData == null)
                {
                    this.Invoke((MethodInvoker)delegate
                    {
                        MessageBox.Show("No volume data loaded.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    });
                    return false;
                }

                // Get volume dimensions from ChunkedVolume
                int width = mainForm.volumeData.Width;
                int height = mainForm.volumeData.Height;
                int depth = mainForm.volumeData.Depth;

                if (width <= 0 || height <= 0 || depth <= 0)
                {
                    Logger.Log("[IntegrateResampleForm] Invalid volume dimensions");
                    return false;
                }

                // Calculate new dimensions - ensure they're at least 1
                int newWidth = Math.Max(1, (int)(width * factor));
                int newHeight = Math.Max(1, (int)(height * factor));
                int newDepth = Math.Max(1, (int)(depth * factor));

                Logger.Log($"[IntegrateResampleForm] Volume dimensions: {width}x{height}x{depth} -> {newWidth}x{newHeight}x{newDepth}");

                // Update UI on the main thread
                this.Invoke((MethodInvoker)delegate
                {
                    if (!this.IsDisposed)
                    {
                        progressBar.Value = 0;
                        lblStatus.Text = "Status: Preparing data...";
                    }
                });

                // Resampling logic based on GPU or CPU
                if (isGPU)
                {
                    return ResampleGPU(width, height, depth, newWidth, newHeight, newDepth);
                }
                else
                {
                    return ResampleCPU(width, height, depth, newWidth, newHeight, newDepth);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[IntegrateResampleForm] Error during resampling: {ex.Message}");
                this.Invoke((MethodInvoker)delegate
                {
                    if (!this.IsDisposed)
                    {
                        MessageBox.Show($"Resampling error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                });
                return false;
            }
        }
        private bool ResampleGPU(int width, int height, int depth, int newWidth, int newHeight, int newDepth)
        {
            // Ensure all dimensions are at least 1
            newWidth = Math.Max(1, newWidth);
            newHeight = Math.Max(1, newHeight);
            newDepth = Math.Max(1, newDepth);

            Logger.Log("[IntegrateResampleForm] Performing GPU resampling");
            Logger.Log($"[IntegrateResampleForm] Dimensions: {width}x{height}x{depth} -> {newWidth}x{newHeight}x{newDepth}");

            // Calculate sizes using long to avoid integer overflow
            long inputSizeLong = (long)width * (long)height * (long)depth;
            long outputSizeLong = (long)newWidth * (long)newHeight * (long)newDepth;

            Logger.Log($"[IntegrateResampleForm] Input size (bytes): {inputSizeLong}, Output size (bytes): {outputSizeLong}");

            // Check if the volume is too large for a single array (> 2GB)
            if (inputSizeLong >= int.MaxValue || outputSizeLong >= int.MaxValue)
            {
                Logger.Log("[IntegrateResampleForm] Volume too large for single-pass GPU processing, using slice-by-slice approach");
                return ResampleLargeVolumeGPU(width, height, depth, newWidth, newHeight, newDepth);
            }

            // Update status
            this.Invoke((MethodInvoker)delegate
            {
                if (!this.IsDisposed)
                {
                    lblStatus.Text = "Status: Preparing data...";
                    progressBar.Value = 10;
                }
            });

            try
            {
                // We can safely cast to int now because we checked above
                int totalInputSize = (int)inputSizeLong;
                int totalOutputSize = (int)outputSizeLong;

                // Create a new ChunkedVolume with the resampled dimensions
                int chunkDim = 256; // Default chunk size
                if (newWidth < 256 || newHeight < 256 || newDepth < 256)
                {
                    // For smaller volumes, use smaller chunk size
                    chunkDim = 64;
                }

                this.Invoke((MethodInvoker)delegate
                {
                    if (!this.IsDisposed)
                    {
                        lblStatus.Text = "Status: Creating new volume...";
                        progressBar.Value = 20;
                    }
                });

                // Create the new chunked volume for output
                ChunkedVolume newVolume = new ChunkedVolume(newWidth, newHeight, newDepth, chunkDim);

                this.Invoke((MethodInvoker)delegate
                {
                    if (!this.IsDisposed)
                    {
                        lblStatus.Text = "Status: Processing data...";
                        progressBar.Value = 30;
                    }
                });

                // Calculate scale factors - ensure we don't divide by zero
                float scaleX = width > 1 ? (width - 1.0f) / Math.Max(1.0f, newWidth - 1.0f) : 0;
                float scaleY = height > 1 ? (height - 1.0f) / Math.Max(1.0f, newHeight - 1.0f) : 0;
                float scaleZ = depth > 1 ? (depth - 1.0f) / Math.Max(1.0f, newDepth - 1.0f) : 0;

                // Process in 2D slices to avoid memory issues
                Parallel.For(0, newDepth, z =>
                {
                    if (processingCancelled)
                        return;

                    float srcZ = z * scaleZ;
                    int z0 = (int)Math.Floor(srcZ);
                    z0 = Math.Max(0, Math.Min(z0, depth - 1)); // Ensure z0 is within bounds
                    int z1 = Math.Min(z0 + 1, depth - 1);
                    float zFrac = srcZ - z0;

                    for (int y = 0; y < newHeight; y++)
                    {
                        float srcY = y * scaleY;
                        int y0 = (int)Math.Floor(srcY);
                        y0 = Math.Max(0, Math.Min(y0, height - 1)); // Ensure y0 is within bounds
                        int y1 = Math.Min(y0 + 1, height - 1);
                        float yFrac = srcY - y0;

                        for (int x = 0; x < newWidth; x++)
                        {
                            float srcX = x * scaleX;
                            int x0 = (int)Math.Floor(srcX);
                            x0 = Math.Max(0, Math.Min(x0, width - 1)); // Ensure x0 is within bounds
                            int x1 = Math.Min(x0 + 1, width - 1);
                            float xFrac = srcX - x0;

                            // Directly access from chunked volume instead of flattening
                            float c000 = mainForm.volumeData[x0, y0, z0];
                            float c001 = mainForm.volumeData[x0, y0, z1];
                            float c010 = mainForm.volumeData[x0, y1, z0];
                            float c011 = mainForm.volumeData[x0, y1, z1];
                            float c100 = mainForm.volumeData[x1, y0, z0];
                            float c101 = mainForm.volumeData[x1, y0, z1];
                            float c110 = mainForm.volumeData[x1, y1, z0];
                            float c111 = mainForm.volumeData[x1, y1, z1];

                            // Trilinear interpolation
                            float c00 = c000 * (1 - xFrac) + c100 * xFrac;
                            float c01 = c001 * (1 - xFrac) + c101 * xFrac;
                            float c10 = c010 * (1 - xFrac) + c110 * xFrac;
                            float c11 = c011 * (1 - xFrac) + c111 * xFrac;

                            float c0 = c00 * (1 - yFrac) + c10 * yFrac;
                            float c1 = c01 * (1 - yFrac) + c11 * yFrac;

                            float result = c0 * (1 - zFrac) + c1 * zFrac;

                            // Write directly to chunked volume
                            newVolume[x, y, z] = (byte)Math.Round(result);
                        }
                    }

                    // Progress update (thread-safe)
                    if (z % 5 == 0 || z == newDepth - 1)
                    {
                        int progress = 30 + (z * 60 / newDepth);
                        this.Invoke((MethodInvoker)delegate
                        {
                            if (!this.IsDisposed)
                            {
                                progressBar.Value = progress;
                                lblStatus.Text = $"Status: Processing slice {z}/{newDepth}...";
                            }
                        });
                    }
                });

                if (processingCancelled)
                    return false;

                // Update the volume in the main form
                this.Invoke((MethodInvoker)delegate
                {
                    if (!this.IsDisposed)
                    {
                        try
                        {
                            // Handle labels if they exist
                            if (mainForm.volumeLabels != null)
                            {
                                this.lblStatus.Text = "Status: Resampling labels...";
                                progressBar.Value = 90;

                                // Create a new label volume with the resampled dimensions
                                ChunkedLabelVolume newLabels = new ChunkedLabelVolume(
                                    newWidth, newHeight, newDepth, chunkDim,
                                    false); // In-memory, not memory-mapped

                                // Fill the new label volume using nearest-neighbor resampling
                                Parallel.For(0, newDepth, z =>
                                {
                                    if (processingCancelled)
                                        return;

                                    int origZ = Math.Min((int)Math.Floor(z / ResampleFactor), depth - 1);
                                    origZ = Math.Max(0, origZ); // Ensure non-negative

                                    for (int y = 0; y < newHeight; y++)
                                    {
                                        int origY = Math.Min((int)Math.Floor(y / ResampleFactor), height - 1);
                                        origY = Math.Max(0, origY); // Ensure non-negative

                                        for (int x = 0; x < newWidth; x++)
                                        {
                                            int origX = Math.Min((int)Math.Floor(x / ResampleFactor), width - 1);
                                            origX = Math.Max(0, origX); // Ensure non-negative

                                            newLabels[x, y, z] = mainForm.volumeLabels[origX, origY, origZ];
                                        }
                                    }
                                });

                                // Update the label volume
                                mainForm.volumeLabels = newLabels;
                            }

                            // Update the main form's volume data
                            mainForm.volumeData = newVolume;

                            // IMPORTANT: Update the pixel size based on resample factor
                            double currentPixelSize = mainForm.pixelSize;
                            double newPixelSize = currentPixelSize / ResampleFactor;
                            mainForm.UpdatePixelSize(newPixelSize);

                            Logger.Log($"[IntegrateResampleForm] Pixel size updated from {currentPixelSize:0.000000e-6} µm to {newPixelSize:0.000000e-6} µm");

                            // Notify MainForm that dimensions have changed
                            mainForm.OnDatasetChanged();

                            lblStatus.Text = "Status: Complete!";
                            progressBar.Value = 100;
                            Logger.Log("[IntegrateResampleForm] GPU resampling completed");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[IntegrateResampleForm] Error updating volume: {ex.Message}");
                            MessageBox.Show($"Error updating volume: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                });

                // Export BMP files if requested
                if (ExportAsBMP && !string.IsNullOrEmpty(ExportPath))
                {
                    this.Invoke((MethodInvoker)delegate
                    {
                        if (!this.IsDisposed)
                        {
                            lblStatus.Text = "Status: Exporting BMP stack...";
                            progressBar.Value = 95;
                        }
                    });

                    ExportBMPStack(newWidth, newHeight, newDepth);
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[IntegrateResampleForm] GPU resampling error: {ex.Message}");
                throw;
            }
        }

        private bool ResampleLargeVolumeGPU(int width, int height, int depth, int newWidth, int newHeight, int newDepth)
        {
            try
            {
                Logger.Log("[IntegrateResampleForm] Using slice-by-slice processing for large volume");

                // Calculate scale factors with boundary checking
                float scaleX = width > 1 ? (width - 1.0f) / Math.Max(1.0f, newWidth - 1.0f) : 0;
                float scaleY = height > 1 ? (height - 1.0f) / Math.Max(1.0f, newHeight - 1.0f) : 0;
                float scaleZ = depth > 1 ? (depth - 1.0f) / Math.Max(1.0f, newDepth - 1.0f) : 0;

                this.Invoke((MethodInvoker)delegate
                {
                    if (!this.IsDisposed)
                    {
                        lblStatus.Text = "Status: Initializing...";
                        progressBar.Value = 5;
                    }
                });

                // Create a new chunked volume for output
                int chunkDim = 256;
                ChunkedVolume newVolume = new ChunkedVolume(newWidth, newHeight, newDepth, chunkDim);

                // Process in Z-slices to minimize memory usage
                int batchSize = 10; // Process this many slices at once

                for (int batchStart = 0; batchStart < newDepth; batchStart += batchSize)
                {
                    if (processingCancelled)
                        return false;

                    int batchEnd = Math.Min(batchStart + batchSize, newDepth);

                    this.Invoke((MethodInvoker)delegate
                    {
                        if (!this.IsDisposed)
                        {
                            lblStatus.Text = $"Status: Processing slices {batchStart}-{batchEnd - 1} of {newDepth}...";
                            progressBar.Value = 5 + (batchStart * 85 / newDepth);
                        }
                    });

                    // Process each slice in the batch
                    Parallel.For(batchStart, batchEnd, z =>
                    {
                        if (processingCancelled)
                            return;

                        float srcZ = z * scaleZ;
                        int z0 = (int)Math.Floor(srcZ);
                        z0 = Math.Max(0, Math.Min(z0, depth - 1)); // Ensure z0 is within bounds
                        int z1 = Math.Min(z0 + 1, depth - 1);
                        float zFrac = srcZ - z0;

                        for (int y = 0; y < newHeight; y++)
                        {
                            float srcY = y * scaleY;
                            int y0 = (int)Math.Floor(srcY);
                            y0 = Math.Max(0, Math.Min(y0, height - 1)); // Ensure y0 is within bounds
                            int y1 = Math.Min(y0 + 1, height - 1);
                            float yFrac = srcY - y0;

                            for (int x = 0; x < newWidth; x++)
                            {
                                float srcX = x * scaleX;
                                int x0 = (int)Math.Floor(srcX);
                                x0 = Math.Max(0, Math.Min(x0, width - 1)); // Ensure x0 is within bounds
                                int x1 = Math.Min(x0 + 1, width - 1);
                                float xFrac = srcX - x0;

                                // Direct access from chunked volume
                                float c000 = mainForm.volumeData[x0, y0, z0];
                                float c001 = mainForm.volumeData[x0, y0, z1];
                                float c010 = mainForm.volumeData[x0, y1, z0];
                                float c011 = mainForm.volumeData[x0, y1, z1];
                                float c100 = mainForm.volumeData[x1, y0, z0];
                                float c101 = mainForm.volumeData[x1, y0, z1];
                                float c110 = mainForm.volumeData[x1, y1, z0];
                                float c111 = mainForm.volumeData[x1, y1, z1];

                                // Trilinear interpolation
                                float c00 = c000 * (1 - xFrac) + c100 * xFrac;
                                float c01 = c001 * (1 - xFrac) + c101 * xFrac;
                                float c10 = c010 * (1 - xFrac) + c110 * xFrac;
                                float c11 = c011 * (1 - xFrac) + c111 * xFrac;

                                float c0 = c00 * (1 - yFrac) + c10 * yFrac;
                                float c1 = c01 * (1 - yFrac) + c11 * yFrac;

                                float result = c0 * (1 - zFrac) + c1 * zFrac;

                                // Write directly to the new volume
                                newVolume[x, y, z] = (byte)Math.Round(result);
                            }
                        }
                    });
                }

                if (processingCancelled)
                    return false;

                // Handle labels if they exist
                this.Invoke((MethodInvoker)delegate
                {
                    if (!this.IsDisposed)
                    {
                        lblStatus.Text = "Status: Finalizing...";
                        progressBar.Value = 90;

                        try
                        {
                            // Handle labels if they exist
                            if (mainForm.volumeLabels != null)
                            {
                                lblStatus.Text = "Status: Resampling labels...";

                                // Create a new label volume with the resampled dimensions
                                ChunkedLabelVolume newLabels = new ChunkedLabelVolume(
                                    newWidth, newHeight, newDepth, chunkDim,
                                    false); // In-memory, not memory-mapped

                                // Process labels in batches too
                                for (int batchStart = 0; batchStart < newDepth; batchStart += batchSize)
                                {
                                    if (processingCancelled)
                                        return;

                                    int batchEnd = Math.Min(batchStart + batchSize, newDepth);

                                    Parallel.For(batchStart, batchEnd, z =>
                                    {
                                        int origZ = Math.Min((int)Math.Floor(z / ResampleFactor), depth - 1);
                                        origZ = Math.Max(0, origZ); // Ensure non-negative

                                        for (int y = 0; y < newHeight; y++)
                                        {
                                            int origY = Math.Min((int)Math.Floor(y / ResampleFactor), height - 1);
                                            origY = Math.Max(0, origY); // Ensure non-negative

                                            for (int x = 0; x < newWidth; x++)
                                            {
                                                int origX = Math.Min((int)Math.Floor(x / ResampleFactor), width - 1);
                                                origX = Math.Max(0, origX); // Ensure non-negative

                                                newLabels[x, y, z] = mainForm.volumeLabels[origX, origY, origZ];
                                            }
                                        }
                                    });
                                }

                                // Update the label volume
                                mainForm.volumeLabels = newLabels;
                            }

                            // Update the main form's volume data
                            mainForm.volumeData = newVolume;

                            // IMPORTANT: Update the pixel size based on resample factor
                            double currentPixelSize = mainForm.pixelSize;
                            double newPixelSize = currentPixelSize / ResampleFactor;
                            mainForm.UpdatePixelSize(newPixelSize);

                            Logger.Log($"[IntegrateResampleForm] Pixel size updated from {currentPixelSize:0.000000e-6} µm to {newPixelSize:0.000000e-6} µm");

                            // Notify MainForm that dimensions have changed
                            mainForm.OnDatasetChanged();

                            lblStatus.Text = "Status: Complete!";
                            progressBar.Value = 100;
                            Logger.Log("[IntegrateResampleForm] Large volume GPU resampling completed");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[IntegrateResampleForm] Error updating volume: {ex.Message}");
                            MessageBox.Show($"Error updating volume: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            throw;
                        }
                    }
                });

                // Export BMP files if requested
                if (ExportAsBMP && !string.IsNullOrEmpty(ExportPath))
                {
                    this.Invoke((MethodInvoker)delegate
                    {
                        if (!this.IsDisposed)
                        {
                            lblStatus.Text = "Status: Exporting BMP stack...";
                            progressBar.Value = 95;
                        }
                    });

                    ExportBMPStack(newWidth, newHeight, newDepth);
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[IntegrateResampleForm] Large volume GPU resampling error: {ex.Message}");
                throw;
            }
        }


        private bool ExportBMPStack(int width, int height, int depth)
        {
            try
            {
                // Create directory if it doesn't exist
                if (!Directory.Exists(ExportPath))
                {
                    Directory.CreateDirectory(ExportPath);
                }

                // Export each slice as a BMP
                Parallel.For(0, depth, z =>
                {
                    if (processingCancelled)
                        return;

                    try
                    {
                        // Create a bitmap for this slice
                        using (Bitmap sliceBmp = new Bitmap(width, height, PixelFormat.Format8bppIndexed))
                        {
                            // Set up a grayscale palette
                            ColorPalette palette = sliceBmp.Palette;
                            for (int i = 0; i < 256; i++)
                            {
                                palette.Entries[i] = Color.FromArgb(255, i, i, i);
                            }
                            sliceBmp.Palette = palette;

                            // Lock the bitmap for fast access
                            BitmapData bmpData = sliceBmp.LockBits(
                                new Rectangle(0, 0, width, height),
                                ImageLockMode.WriteOnly,
                                PixelFormat.Format8bppIndexed);

                            // Copy data from our volume to the bitmap
                            unsafe
                            {
                                byte* scanLine = (byte*)bmpData.Scan0;
                                for (int y = 0; y < height; y++)
                                {
                                    for (int x = 0; x < width; x++)
                                    {
                                        // Get the direct index into the bitmap data
                                        int bmpIndex = x + y * bmpData.Stride;

                                        // Ensure index is in range
                                        if (bmpIndex < bmpData.Stride * height)
                                        {
                                            scanLine[bmpIndex] = mainForm.volumeData[x, y, z];
                                        }
                                    }
                                }
                            }

                            // Unlock and save the bitmap
                            sliceBmp.UnlockBits(bmpData);

                            // Generate a zero-padded filename
                            string filename = Path.Combine(ExportPath, $"slice_{z:D5}.bmp");
                            sliceBmp.Save(filename, ImageFormat.Bmp);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[IntegrateResampleForm] Error exporting slice {z}: {ex.Message}");
                    }
                });

                this.Invoke((MethodInvoker)delegate
                {
                    if (!this.IsDisposed)
                    {
                        MessageBox.Show($"BMP stack exported to: {ExportPath}",
                            "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                });

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[IntegrateResampleForm] Error exporting BMP stack: {ex.Message}");
                this.Invoke((MethodInvoker)delegate
                {
                    if (!this.IsDisposed)
                    {
                        MessageBox.Show($"Error exporting BMP stack: {ex.Message}",
                            "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                });
                return false;
            }
        }
        private bool ResampleCPU(int width, int height, int depth, int newWidth, int newHeight, int newDepth)
        {
            Logger.Log("[IntegrateResampleForm] Performing CPU resampling");
            Logger.Log($"[IntegrateResampleForm] Dimensions: {width}x{height}x{depth} -> {newWidth}x{newHeight}x{newDepth}");

            // Calculate sizes using long to avoid integer overflow
            long inputSizeLong = (long)width * (long)height * (long)depth;
            long outputSizeLong = (long)newWidth * (long)newHeight * (long)newDepth;

            Logger.Log($"[IntegrateResampleForm] Input size (bytes): {inputSizeLong}, Output size (bytes): {outputSizeLong}");

            try
            {
                // Calculate scale factors with boundary checking
                float scaleX = width > 1 ? (width - 1.0f) / Math.Max(1.0f, newWidth - 1.0f) : 0;
                float scaleY = height > 1 ? (height - 1.0f) / Math.Max(1.0f, newHeight - 1.0f) : 0;
                float scaleZ = depth > 1 ? (depth - 1.0f) / Math.Max(1.0f, newDepth - 1.0f) : 0;

                this.Invoke((MethodInvoker)delegate
                {
                    if (!this.IsDisposed)
                    {
                        lblStatus.Text = "Status: Creating new volume...";
                        progressBar.Value = 5;
                    }
                });

                // Create a new ChunkedVolume with the resampled dimensions
                int chunkDim = 256; // Default chunk size
                if (newWidth < 256 || newHeight < 256 || newDepth < 256)
                {
                    // For smaller volumes, use smaller chunk size
                    chunkDim = 64;
                }

                ChunkedVolume newVolume = new ChunkedVolume(newWidth, newHeight, newDepth, chunkDim);

                this.Invoke((MethodInvoker)delegate
                {
                    if (!this.IsDisposed)
                    {
                        lblStatus.Text = "Status: Performing CPU interpolation...";
                        progressBar.Value = 10;
                    }
                });

                // Process in batches to show progress and reduce memory pressure
                int batchSize = 10; // Process this many slices at once

                for (int batchStart = 0; batchStart < newDepth; batchStart += batchSize)
                {
                    if (processingCancelled)
                        return false;

                    int batchEnd = Math.Min(batchStart + batchSize, newDepth);

                    // Process each slice in the batch
                    Parallel.For(batchStart, batchEnd, z =>
                    {
                        if (processingCancelled)
                            return;

                        float srcZ = z * scaleZ;
                        int z0 = (int)Math.Floor(srcZ);
                        z0 = Math.Max(0, Math.Min(z0, depth - 1)); // Ensure z0 is within bounds
                        int z1 = Math.Min(z0 + 1, depth - 1);
                        float zFrac = srcZ - z0;

                        for (int y = 0; y < newHeight; y++)
                        {
                            float srcY = y * scaleY;
                            int y0 = (int)Math.Floor(srcY);
                            y0 = Math.Max(0, Math.Min(y0, height - 1)); // Ensure y0 is within bounds
                            int y1 = Math.Min(y0 + 1, height - 1);
                            float yFrac = srcY - y0;

                            for (int x = 0; x < newWidth; x++)
                            {
                                float srcX = x * scaleX;
                                int x0 = (int)Math.Floor(srcX);
                                x0 = Math.Max(0, Math.Min(x0, width - 1)); // Ensure x0 is within bounds
                                int x1 = Math.Min(x0 + 1, width - 1);
                                float xFrac = srcX - x0;

                                // Get the values of the eight surrounding voxels from the chunked volume
                                float c000 = mainForm.volumeData[x0, y0, z0];
                                float c001 = mainForm.volumeData[x0, y0, z1];
                                float c010 = mainForm.volumeData[x0, y1, z0];
                                float c011 = mainForm.volumeData[x0, y1, z1];
                                float c100 = mainForm.volumeData[x1, y0, z0];
                                float c101 = mainForm.volumeData[x1, y0, z1];
                                float c110 = mainForm.volumeData[x1, y1, z0];
                                float c111 = mainForm.volumeData[x1, y1, z1];

                                // Interpolate along x
                                float c00 = c000 * (1 - xFrac) + c100 * xFrac;
                                float c01 = c001 * (1 - xFrac) + c101 * xFrac;
                                float c10 = c010 * (1 - xFrac) + c110 * xFrac;
                                float c11 = c011 * (1 - xFrac) + c111 * xFrac;

                                // Interpolate along y
                                float c0 = c00 * (1 - yFrac) + c10 * yFrac;
                                float c1 = c01 * (1 - yFrac) + c11 * yFrac;

                                // Interpolate along z
                                float result = c0 * (1 - zFrac) + c1 * zFrac;

                                // Store the result in the new chunked volume
                                newVolume[x, y, z] = (byte)Math.Round(result);
                            }
                        }
                    });

                    // Update progress
                    int progress = 10 + (batchEnd * 80 / newDepth);
                    this.Invoke((MethodInvoker)delegate
                    {
                        if (!this.IsDisposed)
                        {
                            progressBar.Value = progress;
                            lblStatus.Text = $"Status: Processing slices {batchStart}-{batchEnd - 1} of {newDepth}...";
                        }
                    });
                }

                this.Invoke((MethodInvoker)delegate
                {
                    if (!this.IsDisposed)
                    {
                        lblStatus.Text = "Status: Finalizing...";
                        progressBar.Value = 90;

                        try
                        {
                            // Handle labels if they exist
                            if (mainForm.volumeLabels != null)
                            {
                                lblStatus.Text = "Status: Resampling labels...";

                                // Create a new label volume with the resampled dimensions
                                ChunkedLabelVolume newLabels = new ChunkedLabelVolume(
                                    newWidth, newHeight, newDepth, chunkDim,
                                    false); // In-memory, not memory-mapped

                                // Process labels in batches too
                                for (int batchStart = 0; batchStart < newDepth; batchStart += batchSize)
                                {
                                    int batchEnd = Math.Min(batchStart + batchSize, newDepth);

                                    Parallel.For(batchStart, batchEnd, z =>
                                    {
                                        int origZ = Math.Min((int)Math.Floor(z / ResampleFactor), depth - 1);
                                        origZ = Math.Max(0, origZ); // Ensure non-negative

                                        for (int y = 0; y < newHeight; y++)
                                        {
                                            int origY = Math.Min((int)Math.Floor(y / ResampleFactor), height - 1);
                                            origY = Math.Max(0, origY); // Ensure non-negative

                                            for (int x = 0; x < newWidth; x++)
                                            {
                                                int origX = Math.Min((int)Math.Floor(x / ResampleFactor), width - 1);
                                                origX = Math.Max(0, origX); // Ensure non-negative

                                                newLabels[x, y, z] = mainForm.volumeLabels[origX, origY, origZ];
                                            }
                                        }
                                    });
                                }

                                // Update the label volume
                                mainForm.volumeLabels = newLabels;
                            }

                            // Update the main form's volume data
                            mainForm.volumeData = newVolume;

                            // IMPORTANT: Update the pixel size based on resample factor
                            double currentPixelSize = mainForm.pixelSize;
                            double newPixelSize = currentPixelSize / ResampleFactor;
                            mainForm.UpdatePixelSize(newPixelSize);

                            Logger.Log($"[IntegrateResampleForm] Pixel size updated from {currentPixelSize:0.000000e-6} µm to {newPixelSize:0.000000e-6} µm");

                            // Notify MainForm that dimensions have changed
                            mainForm.OnDatasetChanged();

                            lblStatus.Text = "Status: Complete!";
                            progressBar.Value = 100;
                            Logger.Log("[IntegrateResampleForm] CPU resampling completed successfully");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[IntegrateResampleForm] Error updating volume data: {ex.Message}");
                            MessageBox.Show($"Error updating volume data: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            throw;
                        }
                    }
                });

                // Export BMP files if requested
                if (ExportAsBMP && !string.IsNullOrEmpty(ExportPath))
                {
                    this.Invoke((MethodInvoker)delegate
                    {
                        if (!this.IsDisposed)
                        {
                            lblStatus.Text = "Status: Exporting BMP stack...";
                            progressBar.Value = 95;
                        }
                    });

                    ExportBMPStack(newWidth, newHeight, newDepth);
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[IntegrateResampleForm] CPU resampling error: {ex.Message}");
                throw;
            }
        }


        private void EnableControls(bool enable)
        {
            cboDevices.Enabled = enable;
            numResampleFactor.Enabled = enable;
            chkUseGPU.Enabled = enable && cboDevices.SelectedIndex > 0; // Only enable GPU checkbox for GPU devices
            btnOK.Enabled = enable;
            btnCancel.Enabled = true; // Always allow cancellation
        }

        private void IntegrateResampleForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            Logger.Log("[IntegrateResampleForm] Form closing");
            processingCancelled = true;
            try
            {
                if (accelerator != null)
                {
                    accelerator.Dispose();
                    accelerator = null;
                }

                if (context != null)
                {
                    context.Dispose();
                    context = null;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[IntegrateResampleForm] Error during cleanup: {ex.Message}");
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Logger.Log("[IntegrateResampleForm] Form closed");
            base.OnFormClosed(e);
        }

        private void ChkExportBMP_CheckedChanged(object sender, EventArgs e)
        {
            ExportAsBMP = chkExportBMP.Checked;
            btnBrowseExport.Enabled = ExportAsBMP;

            if (ExportAsBMP && string.IsNullOrEmpty(ExportPath))
            {
                BtnBrowseExport_Click(sender, e);
            }

            Logger.Log($"[IntegrateResampleForm] Export BMP option changed to: {ExportAsBMP}");
        }

        private void BtnBrowseExport_Click(object sender, EventArgs e)
        {
            using (var folderDialog = new FolderBrowserDialog())
            {
                folderDialog.Description = "Select folder to save BMP stack";
                folderDialog.ShowNewFolderButton = true;

                if (folderDialog.ShowDialog() == DialogResult.OK)
                {
                    ExportPath = folderDialog.SelectedPath;
                    lblExportPath.Text = ExportPath;
                    Logger.Log($"[IntegrateResampleForm] Export path set to: {ExportPath}");
                }
                else if (string.IsNullOrEmpty(ExportPath))
                {
                    chkExportBMP.Checked = false;
                }
            }
        }
    }
}