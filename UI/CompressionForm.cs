using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS.Compression
{
    public class VolumeCompressionForm : Form
    {
        private MainForm _mainForm;
        private ChunkedVolumeCompressor _compressor;

        private TextBox txtInputPath;
        private TextBox txtOutputPath;
        private Button btnSelectInput;
        private Button btnSelectOutput;
        private Button btnCompress;
        private Button btnDecompress;
        private ProgressBar progressBar;
        private Label lblStatus;
        private NumericUpDown numCompressionLevel;
        private CheckBox chkPredictiveCoding;
        private CheckBox chkRunLengthEncoding;
        private Label lblFileSize;
        private Label lblRatio;

        public VolumeCompressionForm(MainForm mainForm)
        {
            _mainForm = mainForm;
            InitializeComponent();

            // Set default input path to current dataset
            if (!string.IsNullOrEmpty(_mainForm.CurrentPath))
            {
                txtInputPath.Text = _mainForm.CurrentPath;
                UpdateOutputPath();
            }
        }

        private void InitializeComponent()
        {
            this.Text = "CTS Volume Compression";
            this.Size = new Size(600, 450);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;

            // Title
            Label lblTitle = new Label
            {
                Text = "CTS 3D Volume Compression",
                Font = new Font("Arial", 12, FontStyle.Bold),
                Location = new Point(20, 10),
                AutoSize = true
            };

            // Input section
            Label lblInput = new Label
            {
                Text = "Input Volume:",
                Location = new Point(20, 50),
                AutoSize = true
            };

            txtInputPath = new TextBox
            {
                Location = new Point(20, 70),
                Width = 450,
                ReadOnly = true
            };

            btnSelectInput = new Button
            {
                Text = "Browse...",
                Location = new Point(480, 68),
                Width = 80,
                Height = 25
            };
            btnSelectInput.Click += BtnSelectInput_Click;

            // Output section
            Label lblOutput = new Label
            {
                Text = "Output Path:",
                Location = new Point(20, 110),
                AutoSize = true
            };

            txtOutputPath = new TextBox
            {
                Location = new Point(20, 130),
                Width = 450,
                ReadOnly = true
            };

            btnSelectOutput = new Button
            {
                Text = "Browse...",
                Location = new Point(480, 128),
                Width = 80,
                Height = 25
            };
            btnSelectOutput.Click += BtnSelectOutput_Click;

            // Settings section
            GroupBox grpSettings = new GroupBox
            {
                Text = "Compression Settings",
                Location = new Point(20, 170),
                Size = new Size(540, 90)
            };

            Label lblLevel = new Label
            {
                Text = "Compression Level:",
                Location = new Point(20, 25),
                AutoSize = true
            };

            numCompressionLevel = new NumericUpDown
            {
                Location = new Point(140, 23),
                Width = 60,
                Minimum = 1,
                Maximum = 9,
                Value = 5
            };

            chkPredictiveCoding = new CheckBox
            {
                Text = "3D Predictive Coding",
                Location = new Point(220, 25),
                Width = 150,
                Checked = true
            };

            chkRunLengthEncoding = new CheckBox
            {
                Text = "Run-Length Encoding",
                Location = new Point(380, 25),
                Width = 150,
                Checked = true
            };

            grpSettings.Controls.AddRange(new Control[] { lblLevel, numCompressionLevel, chkPredictiveCoding, chkRunLengthEncoding });

            // Progress section
            progressBar = new ProgressBar
            {
                Location = new Point(20, 280),
                Width = 540,
                Height = 23,
                Style = ProgressBarStyle.Continuous
            };

            lblStatus = new Label
            {
                Text = "Ready",
                Location = new Point(20, 310),
                AutoSize = true
            };

            lblFileSize = new Label
            {
                Text = "",
                Location = new Point(20, 330),
                AutoSize = true
            };

            lblRatio = new Label
            {
                Text = "",
                Location = new Point(300, 330),
                AutoSize = true
            };

            // Buttons
            btnCompress = new Button
            {
                Text = "Compress",
                Location = new Point(370, 360),
                Width = 90,
                Height = 30
            };
            btnCompress.Click += BtnCompress_Click;

            btnDecompress = new Button
            {
                Text = "Decompress",
                Location = new Point(470, 360),
                Width = 90,
                Height = 30
            };
            btnDecompress.Click += BtnDecompress_Click;

            // Add controls
            this.Controls.AddRange(new Control[]
            {
                lblTitle, lblInput, txtInputPath, btnSelectInput,
                lblOutput, txtOutputPath, btnSelectOutput,
                grpSettings, progressBar, lblStatus, lblFileSize, lblRatio,
                btnCompress, btnDecompress
            });
        }

        private void BtnSelectInput_Click(object sender, EventArgs e)
        {
            // Check if selecting compressed file or volume folder
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "CTS Files|*.cts3d|Volume Files|volume.bin|All Files|*.*";
                dialog.Title = "Select Input";

                if (!string.IsNullOrEmpty(txtInputPath.Text))
                {
                    dialog.InitialDirectory = Path.GetDirectoryName(txtInputPath.Text);
                }

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    txtInputPath.Text = dialog.FileName;
                    UpdateOutputPath();
                }
            }
        }

        private void BtnSelectOutput_Click(object sender, EventArgs e)
        {
            if (txtInputPath.Text.EndsWith(".cts3d"))
            {
                // Decompression - select output folder
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
                // Compression - select output file
                using (var dialog = new SaveFileDialog())
                {
                    dialog.Filter = "CTS Compressed Files|*.cts3d";
                    dialog.Title = "Save Compressed File";

                    if (dialog.ShowDialog() == DialogResult.OK)
                    {
                        txtOutputPath.Text = dialog.FileName;
                    }
                }
            }
        }

        private void UpdateOutputPath()
        {
            if (string.IsNullOrEmpty(txtInputPath.Text))
                return;

            string dir = Path.GetDirectoryName(txtInputPath.Text);
            string name = Path.GetFileNameWithoutExtension(txtInputPath.Text);

            if (txtInputPath.Text.EndsWith(".cts3d"))
            {
                // Decompression
                txtOutputPath.Text = Path.Combine(dir, name + "_decompressed");
            }
            else
            {
                // Compression
                if (name == "volume")
                {
                    // If it's volume.bin, use parent folder name
                    string parentDir = Directory.GetParent(dir).Name;
                    txtOutputPath.Text = Path.Combine(dir, parentDir + ".cts3d");
                }
                else
                {
                    txtOutputPath.Text = Path.Combine(dir, name + ".cts3d");
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
            lblFileSize.Text = "";
            lblRatio.Text = "";

            try
            {
                _compressor = new ChunkedVolumeCompressor(
                    (int)numCompressionLevel.Value,
                    chkPredictiveCoding.Checked,
                    chkRunLengthEncoding.Checked);

                var progress = new Progress<int>(value =>
                {
                    progressBar.Value = value;
                    lblStatus.Text = $"Compressing... {value}%";
                });

                var startTime = DateTime.Now;
                await _compressor.CompressAsync(txtInputPath.Text, txtOutputPath.Text, progress);
                var duration = DateTime.Now - startTime;

                // Calculate compression ratio
                long inputSize = GetInputSize(txtInputPath.Text);
                FileInfo outputFile = new FileInfo(txtOutputPath.Text);
                double ratio = (double)outputFile.Length / inputSize * 100;

                lblStatus.Text = $"Compression completed in {duration.TotalSeconds:F1}s";
                lblFileSize.Text = $"Size: {FormatFileSize(inputSize)} → {FormatFileSize(outputFile.Length)}";
                lblRatio.Text = $"Compression ratio: {ratio:F1}%";

                MessageBox.Show($"Compression successful!\n\nTime: {duration.TotalSeconds:F1}s\n" +
                               $"Original: {FormatFileSize(inputSize)}\n" +
                               $"Compressed: {FormatFileSize(outputFile.Length)}\n" +
                               $"Ratio: {ratio:F1}%",
                               "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Compression failed";
                Logger.Log($"[VolumeCompressionForm] Error: {ex.Message}");
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
            lblFileSize.Text = "";
            lblRatio.Text = "";

            try
            {
                _compressor = new ChunkedVolumeCompressor();

                var progress = new Progress<int>(value =>
                {
                    progressBar.Value = value;
                    lblStatus.Text = $"Decompressing... {value}%";
                });

                var startTime = DateTime.Now;
                await _compressor.DecompressAsync(txtInputPath.Text, txtOutputPath.Text, progress);
                var duration = DateTime.Now - startTime;

                lblStatus.Text = $"Decompression completed in {duration.TotalSeconds:F1}s";

                var result = MessageBox.Show("Decompression successful!\n\nWould you like to load the dataset?",
                    "Success", MessageBoxButtons.YesNo, MessageBoxIcon.Information);

                if (result == DialogResult.Yes)
                {
                    await _mainForm.LoadDatasetAsync(txtOutputPath.Text);
                    this.Close();
                }
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Decompression failed";
                Logger.Log($"[VolumeCompressionForm] Error: {ex.Message}");
                MessageBox.Show($"Decompression failed: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnCompress.Enabled = true;
                btnDecompress.Enabled = true;
            }
        }

        private long GetInputSize(string path)
        {
            if (Directory.Exists(path))
            {
                // It's a folder - sum up volume.bin and labels.bin
                long size = 0;
                string volumePath = Path.Combine(path, "volume.bin");
                string labelsPath = Path.Combine(path, "labels.bin");

                if (File.Exists(volumePath))
                    size += new FileInfo(volumePath).Length;
                if (File.Exists(labelsPath))
                    size += new FileInfo(labelsPath).Length;

                return size;
            }
            else if (File.Exists(path))
            {
                // It's a file - check for labels in same directory
                long size = new FileInfo(path).Length;

                if (path.EndsWith("volume.bin"))
                {
                    string labelsPath = Path.Combine(Path.GetDirectoryName(path), "labels.bin");
                    if (File.Exists(labelsPath))
                        size += new FileInfo(labelsPath).Length;
                }

                return size;
            }

            return 0;
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int order = 0;

            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size = size / 1024;
            }

            return $"{size:F2} {sizes[order]}";
        }
    }
}