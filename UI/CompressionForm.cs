using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS.Compression
{
    public partial class CompressionForm : Form
    {
        private MainForm _mainForm;
        private VolumetricCompressor _compressor;

        private TextBox txtInputPath;
        private TextBox txtOutputPath;
        private Button btnBrowseInput;
        private Button btnBrowseOutput;
        private ProgressBar progressBar;
        private Label lblStatus;
        private Button btnCompress;
        private Button btnDecompress;
        private NumericUpDown numBlockSize;
        private NumericUpDown numMinNodeSize;
        private NumericUpDown numVarianceThreshold;
        private CheckBox chkUseDefaults;
        private Label lblInfo;

        public CompressionForm(MainForm mainForm)
        {
            _mainForm = mainForm;
            _compressor = new VolumetricCompressor();
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "CTS 3D Volumetric Compression";
            this.Size = new Size(600, 400);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;

            // Input path
            Label lblInput = new Label
            {
                Text = "Input Path:",
                Location = new Point(20, 20),
                AutoSize = true
            };

            txtInputPath = new TextBox
            {
                Location = new Point(100, 18),
                Width = 380,
                ReadOnly = true
            };

            btnBrowseInput = new Button
            {
                Text = "Browse...",
                Location = new Point(490, 16),
                Width = 80
            };
            btnBrowseInput.Click += BtnBrowseInput_Click;

            // Output path
            Label lblOutput = new Label
            {
                Text = "Output Path:",
                Location = new Point(20, 50),
                AutoSize = true
            };

            txtOutputPath = new TextBox
            {
                Location = new Point(100, 48),
                Width = 380,
                ReadOnly = true
            };

            btnBrowseOutput = new Button
            {
                Text = "Browse...",
                Location = new Point(490, 46),
                Width = 80
            };
            btnBrowseOutput.Click += BtnBrowseOutput_Click;

            // Settings panel
            GroupBox settingsGroup = new GroupBox
            {
                Text = "Compression Settings",
                Location = new Point(20, 90),
                Size = new Size(550, 120)
            };

            chkUseDefaults = new CheckBox
            {
                Text = "Use Default Settings (Recommended)",
                Location = new Point(20, 20),
                Width = 250,
                Checked = true
            };
            chkUseDefaults.CheckedChanged += ChkUseDefaults_CheckedChanged;

            Label lblBlockSize = new Label
            {
                Text = "Block Size:",
                Location = new Point(20, 50),
                AutoSize = true
            };

            numBlockSize = new NumericUpDown
            {
                Location = new Point(120, 48),
                Width = 80,
                Minimum = 16,
                Maximum = 128,
                Value = 64,
                Enabled = false
            };

            Label lblMinNode = new Label
            {
                Text = "Min Node Size:",
                Location = new Point(220, 50),
                AutoSize = true
            };

            numMinNodeSize = new NumericUpDown
            {
                Location = new Point(320, 48),
                Width = 80,
                Minimum = 1,
                Maximum = 8,
                Value = 2,
                Enabled = false
            };

            Label lblVariance = new Label
            {
                Text = "Variance Threshold:",
                Location = new Point(20, 80),
                AutoSize = true
            };

            numVarianceThreshold = new NumericUpDown
            {
                Location = new Point(120, 78),
                Width = 80,
                Minimum = 1,
                Maximum = 50,
                Value = 5,
                Enabled = false
            };

            settingsGroup.Controls.AddRange(new Control[]
            {
                chkUseDefaults, lblBlockSize, numBlockSize, lblMinNode, numMinNodeSize,
                lblVariance, numVarianceThreshold
            });

            // Info label
            lblInfo = new Label
            {
                Text = "CTS 3D compression uses adaptive octree subdivision for optimal compression of volumetric data.",
                Location = new Point(20, 220),
                Size = new Size(550, 40),
                ForeColor = Color.DarkBlue
            };

            // Progress bar
            progressBar = new ProgressBar
            {
                Location = new Point(20, 270),
                Size = new Size(550, 23),
                Style = ProgressBarStyle.Continuous
            };

            // Status label
            lblStatus = new Label
            {
                Text = "Ready",
                Location = new Point(20, 300),
                AutoSize = true
            };

            // Buttons
            btnCompress = new Button
            {
                Text = "Compress",
                Location = new Point(370, 320),
                Size = new Size(90, 30)
            };
            btnCompress.Click += BtnCompress_Click;

            btnDecompress = new Button
            {
                Text = "Decompress",
                Location = new Point(480, 320),
                Size = new Size(90, 30)
            };
            btnDecompress.Click += BtnDecompress_Click;

            // Add controls to form
            this.Controls.AddRange(new Control[]
            {
                lblInput, txtInputPath, btnBrowseInput,
                lblOutput, txtOutputPath, btnBrowseOutput,
                settingsGroup, lblInfo, progressBar, lblStatus,
                btnCompress, btnDecompress
            });
        }

        private void ChkUseDefaults_CheckedChanged(object sender, EventArgs e)
        {
            bool useCustom = !chkUseDefaults.Checked;
            numBlockSize.Enabled = useCustom;
            numMinNodeSize.Enabled = useCustom;
            numVarianceThreshold.Enabled = useCustom;
        }

        private void BtnBrowseInput_Click(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "CTS Compressed Files (*.cts3d)|*.cts3d|All Files (*.*)|*.*";
                dialog.Title = "Select Input File";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    txtInputPath.Text = dialog.FileName;

                    // Auto-generate output path
                    string dir = Path.GetDirectoryName(dialog.FileName);
                    string name = Path.GetFileNameWithoutExtension(dialog.FileName);

                    if (Path.GetExtension(dialog.FileName).ToLower() == ".cts3d")
                    {
                        // Decompression - create output folder
                        txtOutputPath.Text = Path.Combine(dir, name + "_extracted");
                    }
                    else
                    {
                        // Compression - create output file
                        txtOutputPath.Text = Path.Combine(dir, name + ".cts3d");
                    }
                }
            }
        }

        private void BtnBrowseOutput_Click(object sender, EventArgs e)
        {
            if (Path.GetExtension(txtInputPath.Text).ToLower() == ".cts3d")
            {
                // Decompression - select folder
                using (var dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "Select Output Folder";

                    if (dialog.ShowDialog() == DialogResult.OK)
                    {
                        txtOutputPath.Text = dialog.SelectedPath;
                    }
                }
            }
            else
            {
                // Compression - select file
                using (var dialog = new SaveFileDialog())
                {
                    dialog.Filter = "CTS Compressed Files (*.cts3d)|*.cts3d";
                    dialog.Title = "Save Compressed File As";

                    if (dialog.ShowDialog() == DialogResult.OK)
                    {
                        txtOutputPath.Text = dialog.FileName;
                    }
                }
            }
        }

        private async void BtnCompress_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(txtInputPath.Text) || string.IsNullOrEmpty(txtOutputPath.Text))
            {
                MessageBox.Show("Please select input and output paths.", "Missing Paths",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnCompress.Enabled = false;
            btnDecompress.Enabled = false;
            progressBar.Value = 0;
            lblStatus.Text = "Compressing...";

            try
            {
                // Create compressor with settings
                if (!chkUseDefaults.Checked)
                {
                    _compressor = new VolumetricCompressor(
                        (int)numBlockSize.Value,
                        (int)numMinNodeSize.Value,
                        (byte)numVarianceThreshold.Value);
                }

                var progress = new Progress<int>(value =>
                {
                    progressBar.Value = value;
                    lblStatus.Text = $"Compressing... {value}%";
                });

                await _compressor.CompressVolumeAsync(txtInputPath.Text, txtOutputPath.Text, progress);

                // Show compression statistics
                FileInfo inputFile = new FileInfo(txtInputPath.Text);
                FileInfo outputFile = new FileInfo(txtOutputPath.Text);
                double ratio = (double)outputFile.Length / inputFile.Length;

                lblStatus.Text = $"Compression complete. Ratio: {ratio:P2}";
                MessageBox.Show($"Compression successful!\n\nOriginal size: {inputFile.Length:N0} bytes\n" +
                               $"Compressed size: {outputFile.Length:N0} bytes\n" +
                               $"Compression ratio: {ratio:P2}",
                               "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Compression failed";
                Logger.Log($"[CompressionForm] Error: {ex.Message}");
                MessageBox.Show($"Compression failed: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnCompress.Enabled = true;
                btnDecompress.Enabled = true;
            }
        }

        private async void BtnDecompress_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(txtInputPath.Text) || string.IsNullOrEmpty(txtOutputPath.Text))
            {
                MessageBox.Show("Please select input and output paths.", "Missing Paths",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnCompress.Enabled = false;
            btnDecompress.Enabled = false;
            progressBar.Value = 0;
            lblStatus.Text = "Decompressing...";

            try
            {
                // Create output directory if it doesn't exist
                Directory.CreateDirectory(txtOutputPath.Text);

                var progress = new Progress<int>(value =>
                {
                    progressBar.Value = value;
                    lblStatus.Text = $"Decompressing... {value}%";
                });

                await _compressor.DecompressVolumeAsync(txtInputPath.Text, txtOutputPath.Text, progress);

                lblStatus.Text = "Decompression complete";
                MessageBox.Show("Decompression successful!", "Success",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);

                // Offer to load the decompressed dataset
                var result = MessageBox.Show("Would you like to load the decompressed dataset?",
                    "Load Dataset", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    await _mainForm.LoadDatasetAsync(txtOutputPath.Text);
                    this.Close();
                }
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Decompression failed";
                Logger.Log($"[CompressionForm] Error: {ex.Message}");
                MessageBox.Show($"Decompression failed: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnCompress.Enabled = true;
                btnDecompress.Enabled = true;
            }
        }
    }
}