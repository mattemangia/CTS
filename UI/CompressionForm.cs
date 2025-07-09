//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using Krypton.Docking;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS.Compression
{
    public class VolumeCompressionForm : Form
    {
        private MainForm _mainForm;
        private ChunkedVolumeCompressor _compressor;
        private bool _useLoadedVolume = false;
        private RadioButton rbCurrentVolume;
        private RadioButton rbExternalFile;
        private GroupBox grpSource;
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

        public VolumeCompressionForm(MainForm mainForm, bool decompressMode = false)
        {
            _mainForm = mainForm;
            InitializeComponent();

            if (decompressMode)
            {
                // Configure form for decompression mode
                this.Text = "CTS Volume Decompression";
                btnDecompress.BackColor = Color.FromArgb(0, 120, 215);
                btnCompress.BackColor = SystemColors.Control;

                // Hide source selection for decompression
                grpSource.Visible = false;
                this.Height -= 60;
            }
            else
            {
                // Configure form for compression mode
                this.Text = "CTS Volume Compression";
                btnCompress.BackColor = Color.FromArgb(0, 120, 215);
                btnDecompress.BackColor = SystemColors.Control;

                // Enable source selection
                grpSource.Visible = true;

                // Check if a volume is currently loaded
                if (_mainForm.volumeData != null || _mainForm.volumeLabels != null)
                {
                    rbCurrentVolume.Enabled = true;
                    rbCurrentVolume.Checked = true;
                    txtInputPath.Text = _mainForm.CurrentPath;
                    UpdateOutputPath();
                }
                else
                {
                    rbCurrentVolume.Enabled = false;
                    rbExternalFile.Checked = true;
                }
            }
        }

        private void InitializeComponent()
        {
            this.Text = "CTS Volume Compression";
            this.Size = new Size(600, 520); // Increased height for source selection
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

            // Source selection group
            grpSource = new GroupBox
            {
                Text = "Data Source",
                Location = new Point(20, 40),
                Size = new Size(540, 60)
            };

            rbCurrentVolume = new RadioButton
            {
                Text = "Use Currently Loaded Volume",
                Location = new Point(20, 25),
                Width = 200,
                AutoSize = true
            };
            rbCurrentVolume.CheckedChanged += (s, e) => UpdateSourceSelection();

            rbExternalFile = new RadioButton
            {
                Text = "Select External File",
                Location = new Point(250, 25),
                Width = 150,
                AutoSize = true
            };
            rbExternalFile.CheckedChanged += (s, e) => UpdateSourceSelection();

            grpSource.Controls.Add(rbCurrentVolume);
            grpSource.Controls.Add(rbExternalFile);

            // Input section
            Label lblInput = new Label
            {
                Text = "Input Volume:",
                Location = new Point(20, 110),
                AutoSize = true
            };

            txtInputPath = new TextBox
            {
                Location = new Point(20, 130),
                Width = 450,
                ReadOnly = true
            };

            btnSelectInput = new Button
            {
                Text = "Browse...",
                Location = new Point(480, 128),
                Width = 80,
                Height = 25
            };
            btnSelectInput.Click += BtnSelectInput_Click;

            // Output section
            Label lblOutput = new Label
            {
                Text = "Output Path:",
                Location = new Point(20, 170),
                AutoSize = true
            };

            txtOutputPath = new TextBox
            {
                Location = new Point(20, 190),
                Width = 450,
                ReadOnly = true
            };

            btnSelectOutput = new Button
            {
                Text = "Browse...",
                Location = new Point(480, 188),
                Width = 80,
                Height = 25
            };
            btnSelectOutput.Click += BtnSelectOutput_Click;

            // Settings section
            GroupBox grpSettings = new GroupBox
            {
                Text = "Compression Settings",
                Location = new Point(20, 230),
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
                Location = new Point(20, 340),
                Width = 540,
                Height = 23,
                Style = ProgressBarStyle.Continuous
            };

            lblStatus = new Label
            {
                Text = "Ready",
                Location = new Point(20, 370),
                AutoSize = true
            };

            lblFileSize = new Label
            {
                Text = "",
                Location = new Point(20, 390),
                AutoSize = true
            };

            lblRatio = new Label
            {
                Text = "",
                Location = new Point(300, 390),
                AutoSize = true
            };

            // Buttons
            btnCompress = new Button
            {
                Text = "Compress",
                Location = new Point(370, 420),
                Width = 90,
                Height = 30
            };
            btnCompress.Click += BtnCompress_Click;

            btnDecompress = new Button
            {
                Text = "Decompress",
                Location = new Point(470, 420),
                Width = 90,
                Height = 30
            };
            btnDecompress.Click += BtnDecompress_Click;

            // Add controls
            this.Controls.AddRange(new Control[]
            {
                lblTitle, grpSource, lblInput, txtInputPath, btnSelectInput,
                lblOutput, txtOutputPath, btnSelectOutput,
                grpSettings, progressBar, lblStatus, lblFileSize, lblRatio,
                btnCompress, btnDecompress
            });
        }

        private ControlForm FindControlForm()
        {
            // ControlForm is not a Form, it's a KryptonPanel
            // We need to search in the MainForm's controls
            return FindControlInContainer(_mainForm);
        }
        private ControlForm FindControlInContainer(Control container)
        {
            if (container is ControlForm)
                return container as ControlForm;

            foreach (Control child in container.Controls)
            {
                var result = FindControlInContainer(child);
                if (result != null)
                    return result;
            }

            // Check in docking manager if available
            if (container is Form form)
            {
                var dockingManager = form.Controls.OfType<KryptonDockingManager>().FirstOrDefault();
                if (dockingManager != null)
                {
                    // Search through docked pages
                    foreach (var page in dockingManager.Pages)
                    {
                        foreach (Control control in page.Controls)
                        {
                            if (control is ControlForm)
                                return control as ControlForm;

                            var result = FindControlInContainer(control);
                            if (result != null)
                                return result;
                        }
                    }
                }
            }

            return null;
        }

        private void UpdateSourceSelection()
        {
            if (rbCurrentVolume.Checked)
            {
                _useLoadedVolume = true;
                txtInputPath.Text = _mainForm.CurrentPath;
                btnSelectInput.Enabled = false;
                UpdateOutputPath();
            }
            else
            {
                _useLoadedVolume = false;
                txtInputPath.Text = "";
                btnSelectInput.Enabled = true;
            }
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

            try
            {
                string dir = Path.GetDirectoryName(txtInputPath.Text);
                string name = Path.GetFileNameWithoutExtension(txtInputPath.Text);

                if (txtInputPath.Text.EndsWith(".cts3d", StringComparison.OrdinalIgnoreCase))
                {
                    // Decompression
                    txtOutputPath.Text = Path.Combine(dir, name + "_decompressed");
                }
                else
                {
                    // Compression
                    if (name.Equals("volume", StringComparison.OrdinalIgnoreCase))
                    {
                        // If it's volume.bin, use parent folder name
                        DirectoryInfo parentDir = Directory.GetParent(dir);
                        if (parentDir != null)
                        {
                            txtOutputPath.Text = Path.Combine(dir, parentDir.Name + ".cts3d");
                        }
                        else
                        {
                            txtOutputPath.Text = Path.Combine(dir, "compressed.cts3d");
                        }
                    }
                    else
                    {
                        txtOutputPath.Text = Path.Combine(dir, name + ".cts3d");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[UpdateOutputPath] Error: {ex.Message}");
                // Don't throw, just leave output path blank
            }
        }

        private bool ValidateInputs()
        {
            if (_useLoadedVolume)
            {
                if (_mainForm.volumeData == null && _mainForm.volumeLabels == null)
                {
                    MessageBox.Show("No volume is currently loaded.", "No Data",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }
            else
            {
                if (string.IsNullOrEmpty(txtInputPath.Text))
                {
                    MessageBox.Show("Please select an input file.", "Missing Input",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }

                if (!File.Exists(txtInputPath.Text) && !Directory.Exists(txtInputPath.Text))
                {
                    MessageBox.Show("Input path does not exist.", "Invalid Input",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }

            if (string.IsNullOrEmpty(txtOutputPath.Text))
            {
                MessageBox.Show("Please specify an output path.", "Missing Output",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            return true;
        }

        private async void BtnCompress_Click(object sender, EventArgs e)
        {
            if (!ValidateInputs())
                return;

            btnCompress.Enabled = false;
            btnDecompress.Enabled = false;
            btnSelectInput.Enabled = false;
            btnSelectOutput.Enabled = false;
            lblFileSize.Text = "";
            lblRatio.Text = "";
            progressBar.Value = 0;

            try
            {
                _compressor = new ChunkedVolumeCompressor(
                    (int)numCompressionLevel.Value,
                    chkPredictiveCoding.Checked,
                    chkRunLengthEncoding.Checked);

                var progress = new Progress<int>(value =>
                {
                    if (!this.IsDisposed && this.IsHandleCreated)
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            progressBar.Value = Math.Min(value, 100);
                            lblStatus.Text = $"Compressing... {value}%";
                        }));
                    }
                });

                var startTime = DateTime.Now;

                if (_useLoadedVolume)
                {
                    // Compress the currently loaded volume
                    await _compressor.CompressLoadedVolumeAsync(_mainForm, txtOutputPath.Text, progress);
                }
                else
                {
                    // Compress external file
                    await _compressor.CompressAsync(txtInputPath.Text, txtOutputPath.Text, progress);
                }

                var duration = DateTime.Now - startTime;

                // Calculate compression ratio
                long inputSize;
                if (_useLoadedVolume)
                {
                    inputSize = CalculateLoadedVolumeSize();
                }
                else
                {
                    inputSize = GetInputSize(txtInputPath.Text);
                }

                FileInfo outputFile = new FileInfo(txtOutputPath.Text);
                double ratio = (double)outputFile.Length / inputSize * 100;

                if (!this.IsDisposed && this.IsHandleCreated)
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        lblStatus.Text = $"Compression completed in {duration.TotalSeconds:F1}s";
                        lblFileSize.Text = $"Size: {FormatFileSize(inputSize)} → {FormatFileSize(outputFile.Length)}";
                        lblRatio.Text = $"Compression ratio: {ratio:F1}%";

                        MessageBox.Show($"Compression successful!\n\nTime: {duration.TotalSeconds:F1}s\n" +
                                       $"Original: {FormatFileSize(inputSize)}\n" +
                                       $"Compressed: {FormatFileSize(outputFile.Length)}\n" +
                                       $"Ratio: {ratio:F1}%",
                                       "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }));
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[VolumeCompressionForm] Error: {ex.Message}\n{ex.StackTrace}");

                if (!this.IsDisposed && this.IsHandleCreated)
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        lblStatus.Text = "Compression failed";
                        MessageBox.Show($"Compression failed:\n\n{ex.Message}", "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                }
            }
            finally
            {
                if (!this.IsDisposed && this.IsHandleCreated)
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        btnCompress.Enabled = true;
                        btnDecompress.Enabled = true;
                        btnSelectInput.Enabled = true;
                        btnSelectOutput.Enabled = true;
                    }));
                }
            }
        }

        private long CalculateLoadedVolumeSize()
        {
            long size = 0;

            if (_mainForm.volumeData != null)
            {
                var volume = _mainForm.volumeData;
                size += 36; // Header size
                size += (long)volume.ChunkCountX * volume.ChunkCountY * volume.ChunkCountZ *
                        volume.ChunkDim * volume.ChunkDim * volume.ChunkDim;
            }

            if (_mainForm.volumeLabels != null)
            {
                var labels = _mainForm.volumeLabels;
                size += 16; // Header size
                size += (long)labels.ChunkCountX * labels.ChunkCountY * labels.ChunkCountZ *
                        labels.ChunkDim * labels.ChunkDim * labels.ChunkDim;
            }

            return size;
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

                    // Find and update the ControlForm
                    var controlForm = FindControlForm();
                    if (controlForm != null)
                    {
                        // Refresh the controls and material list
                        controlForm.RefreshMaterialList();
                        controlForm.InitializeSliceControls();
                        Logger.Log("[VolumeCompressionForm] Updated ControlForm after decompression");
                    }

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