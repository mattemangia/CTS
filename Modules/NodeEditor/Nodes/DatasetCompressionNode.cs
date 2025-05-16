using CTS.Compression;
using CTS.NodeEditor;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS.Modules.NodeEditor.Nodes
{
    public class DatasetCompressionNode : BaseNode
    {
        // Compression parameters
        public string OutputPath { get; set; } = "";
        public int CompressionLevel { get; set; } = 5;
        public bool UsePredictiveCoding { get; set; } = true;
        public bool UseRunLengthEncoding { get; set; } = true;

        // UI Controls
        private TextBox txtOutputPath;
        private Button btnSelectOutput;
        private NumericUpDown numCompressionLevel;
        private CheckBox chkPredictiveCoding;
        private CheckBox chkRunLengthEncoding;
        private Label lblStatus;
        private Label lblFileSize;
        private Label lblRatio;
        private ProgressBar progressBar;
        private Button btnCompress;
        private Label lblInputInfo;

        // Keep reference to compressor
        private ChunkedVolumeCompressor _compressor;

        // Constructor
        public DatasetCompressionNode(Point position) : base(position)
        {
            Color = Color.FromArgb(255, 120, 120);
        }

        protected override void SetupPins()
        {
            // Input pins
            AddInputPin("Volume", Color.LightBlue);
            AddInputPin("Labels", Color.LightCoral);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48),
                AutoScroll = true
            };

            // Title
            var titleLabel = new Label
            {
                Text = "Dataset Compression",
                Font = new Font("Arial", 12, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(10, 10),
                ForeColor = Color.White
            };
            panel.Controls.Add(titleLabel);

            int currentY = 40;

            // Input information
            lblInputInfo = new Label
            {
                Text = "No input connected",
                AutoSize = true,
                Location = new Point(10, currentY),
                ForeColor = Color.White
            };
            panel.Controls.Add(lblInputInfo);
            currentY += 30;

            // Output Path
            var lblOutput = new Label
            {
                Text = "Output Path:",
                AutoSize = true,
                Location = new Point(10, currentY),
                ForeColor = Color.White
            };
            panel.Controls.Add(lblOutput);
            currentY += 25;

            txtOutputPath = new TextBox
            {
                Location = new Point(10, currentY),
                Width = 280,
                ReadOnly = true,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                Text = OutputPath
            };
            panel.Controls.Add(txtOutputPath);

            btnSelectOutput = new Button
            {
                Text = "Browse...",
                Location = new Point(300, currentY - 2),
                Width = 80,
                Height = 25,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            btnSelectOutput.Click += BtnSelectOutput_Click;
            panel.Controls.Add(btnSelectOutput);
            currentY += 30;

            // Compression Settings
            var settingsLabel = new Label
            {
                Text = "Compression Settings",
                Font = new Font("Arial", 10, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(10, currentY),
                ForeColor = Color.White
            };
            panel.Controls.Add(settingsLabel);
            currentY += 30;

            var lblLevel = new Label
            {
                Text = "Compression Level:",
                AutoSize = true,
                Location = new Point(10, currentY),
                ForeColor = Color.White
            };
            panel.Controls.Add(lblLevel);

            numCompressionLevel = new NumericUpDown
            {
                Location = new Point(120, currentY),
                Width = 60,
                Minimum = 1,
                Maximum = 9,
                Value = CompressionLevel,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            numCompressionLevel.ValueChanged += (s, e) => CompressionLevel = (int)numCompressionLevel.Value;
            panel.Controls.Add(numCompressionLevel);
            currentY += 30;

            chkPredictiveCoding = new CheckBox
            {
                Text = "3D Predictive Coding",
                Location = new Point(10, currentY),
                Checked = UsePredictiveCoding,
                ForeColor = Color.White
            };
            chkPredictiveCoding.CheckedChanged += (s, e) => UsePredictiveCoding = chkPredictiveCoding.Checked;
            panel.Controls.Add(chkPredictiveCoding);
            currentY += 25;

            chkRunLengthEncoding = new CheckBox
            {
                Text = "Run-Length Encoding",
                Location = new Point(10, currentY),
                Checked = UseRunLengthEncoding,
                ForeColor = Color.White
            };
            chkRunLengthEncoding.CheckedChanged += (s, e) => UseRunLengthEncoding = chkRunLengthEncoding.Checked;
            panel.Controls.Add(chkRunLengthEncoding);
            currentY += 35;

            // Progress section
            progressBar = new ProgressBar
            {
                Location = new Point(10, currentY),
                Width = 370,
                Height = 20
            };
            panel.Controls.Add(progressBar);
            currentY += 25;

            lblStatus = new Label
            {
                Text = "Ready",
                Location = new Point(10, currentY),
                AutoSize = true,
                ForeColor = Color.White
            };
            panel.Controls.Add(lblStatus);
            currentY += 25;

            lblFileSize = new Label
            {
                Text = "",
                Location = new Point(10, currentY),
                AutoSize = true,
                ForeColor = Color.White
            };
            panel.Controls.Add(lblFileSize);
            currentY += 25;

            lblRatio = new Label
            {
                Text = "",
                Location = new Point(10, currentY),
                AutoSize = true,
                ForeColor = Color.White
            };
            panel.Controls.Add(lblRatio);
            currentY += 35;

            // Compress button
            btnCompress = new Button
            {
                Text = "Compress",
                Location = new Point(10, currentY),
                Width = 120,
                Height = 30,
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                Font = new Font("Arial", 9, FontStyle.Bold)
            };
            btnCompress.Click += (s, e) => Execute();
            panel.Controls.Add(btnCompress);

            // Check input connections on panel creation
            UpdateInputInfo();

            return panel;
        }

        private void UpdateInputInfo()
        {
            var volume = GetInputVolume();
            var labels = GetInputLabels();

            if (volume != null && labels != null)
            {
                lblInputInfo.Text = $"Connected: Volume {volume.Width}×{volume.Height}×{volume.Depth} and Labels";
            }
            else if (volume != null)
            {
                lblInputInfo.Text = $"Connected: Volume {volume.Width}×{volume.Height}×{volume.Depth}";
            }
            else if (labels != null)
            {
                lblInputInfo.Text = $"Connected: Labels {labels.Width}×{labels.Height}×{labels.Depth}";
            }
            else
            {
                lblInputInfo.Text = "No input connected";
            }
        }

        private void BtnSelectOutput_Click(object sender, EventArgs e)
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "CTS Compressed Files|*.cts3d";
                dialog.Title = "Save Compressed File";

                if (!string.IsNullOrEmpty(OutputPath))
                {
                    dialog.InitialDirectory = Path.GetDirectoryName(OutputPath);
                    dialog.FileName = Path.GetFileName(OutputPath);
                }

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    OutputPath = dialog.FileName;
                    txtOutputPath.Text = OutputPath;
                }
            }
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

        public override async void Execute()
        {
            // Get input data from connected nodes
            var inputVolume = GetInputVolume();
            var inputLabels = GetInputLabels();

            if (inputVolume == null && inputLabels == null)
            {
                MessageBox.Show("No volume or label data is connected. Please connect at least one input.",
                    "Compression Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrEmpty(OutputPath))
            {
                MessageBox.Show("Please select an output path for the compressed file.",
                    "Output Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Disable controls during processing
            btnCompress.Enabled = false;
            btnSelectOutput.Enabled = false;
            progressBar.Value = 0;
            lblStatus.Text = "Compressing...";
            lblFileSize.Text = "";
            lblRatio.Text = "";

            try
            {
                // Create a compressor with selected settings
                _compressor = new ChunkedVolumeCompressor(
                    CompressionLevel,
                    UsePredictiveCoding,
                    UseRunLengthEncoding);

                // Create a progress reporter
                var progress = new Progress<int>(value =>
                {
                    progressBar.Value = Math.Min(value, 100);
                    lblStatus.Text = $"Compressing... {value}%";
                });

                // Start compression
                var startTime = DateTime.Now;

                // Since ChunkedVolumeCompressor doesn't have a method for compressing in-memory data directly,
                // we need to use a MainForm-based approach to compress the loaded volume

                // Find MainForm (required by the compressor)
                var mainForm = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
                if (mainForm == null)
                {
                    throw new InvalidOperationException("Could not find MainForm instance required for compression");
                }

                // Store current MainForm data
                var originalVolumeData = mainForm.volumeData;
                var originalLabels = mainForm.volumeLabels;

                try
                {
                    // Temporarily set our volume data into MainForm
                    mainForm.volumeData = inputVolume;
                    mainForm.volumeLabels = inputLabels;

                    // Use the existing method that works with MainForm
                    await _compressor.CompressLoadedVolumeAsync(mainForm, OutputPath, progress);

                    // Calculate file sizes and compression ratio
                    long inputSize = CalculateInputSize(inputVolume, inputLabels);
                    FileInfo outputFile = new FileInfo(OutputPath);
                    double ratio = (double)outputFile.Length / inputSize * 100;

                    var duration = DateTime.Now - startTime;
                    lblStatus.Text = $"Compression completed in {duration.TotalSeconds:F1}s";
                    lblFileSize.Text = $"Size: {FormatFileSize(inputSize)} → {FormatFileSize(outputFile.Length)}";
                    lblRatio.Text = $"Compression ratio: {ratio:F1}%";

                    MessageBox.Show($"Compression successful!\n\nTime: {duration.TotalSeconds:F1}s\n" +
                                $"Original: {FormatFileSize(inputSize)}\n" +
                                $"Compressed: {FormatFileSize(outputFile.Length)}\n" +
                                $"Ratio: {ratio:F1}%",
                                "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                finally
                {
                    // Always restore original MainForm data
                    mainForm.volumeData = originalVolumeData;
                    mainForm.volumeLabels = originalLabels;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[DatasetCompressionNode] Error: {ex.Message}");
                lblStatus.Text = "Compression failed";
                MessageBox.Show($"Compression failed: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                // Re-enable controls
                btnCompress.Enabled = true;
                btnSelectOutput.Enabled = true;
            }
        }
        private long CalculateInputSize(IGrayscaleVolumeData volume, ILabelVolumeData labels)
        {
            long size = 0;

            if (volume != null)
            {
                size += 36; // Header size for volume data
                size += (long)volume.Width * volume.Height * volume.Depth; // 1 byte per voxel
            }

            if (labels != null)
            {
                size += 16; // Header size for label data
                size += (long)labels.Width * labels.Height * labels.Depth; // 1 byte per voxel
            }

            return size;
        }

        
        private IGrayscaleVolumeData GetInputVolume()
        {
            // Find the node connected to the Volume input pin
            var connections = GetNodeConnections();
            if (connections == null) return null;

            // Find the input pin named "Volume"
            var volumePin = inputs.FirstOrDefault(p => p.Name == "Volume");
            if (volumePin == null) return null;

            // Find a connection to this pin
            var connection = connections.FirstOrDefault(c => c.To == volumePin);
            if (connection == null) return null;

            // Get the source node
            var sourceNode = connection.From.Node;

            // First, check common types directly
            if (sourceNode is VolumeDataNode volumeNode)
            {
                var property = volumeNode.GetType().GetProperty("VolumeData");
                return property?.GetValue(volumeNode) as IGrayscaleVolumeData;
            }
            else if (sourceNode is DatasetDecompressionNode decompressionNode)
            {
                var property = decompressionNode.GetType().GetProperty("VolumeData");
                return property?.GetValue(decompressionNode) as IGrayscaleVolumeData;
            }
            else if (sourceNode is CurrentDatasetNode currentDatasetNode)
            {
                var property = currentDatasetNode.GetType().GetProperty("VolumeData");
                return property?.GetValue(currentDatasetNode) as IGrayscaleVolumeData;
            }

            // Try to get the volume data from the source node using reflection
            var volumeProperty = sourceNode.GetType().GetProperty("VolumeData") ??
                                 sourceNode.GetType().GetProperty("FilteredVolume") ??
                                 sourceNode.GetType().GetProperty("OutputVolume");

            if (volumeProperty != null)
            {
                var value = volumeProperty.GetValue(sourceNode);
                if (value is IGrayscaleVolumeData volumeData)
                    return volumeData;
            }

            return null;
        }

        private ILabelVolumeData GetInputLabels()
        {
            // Find the node connected to the Labels input pin
            var connections = GetNodeConnections();
            if (connections == null) return null;

            // Find the input pin named "Labels"
            var labelsPin = inputs.FirstOrDefault(p => p.Name == "Labels");
            if (labelsPin == null) return null;

            // Find a connection to this pin
            var connection = connections.FirstOrDefault(c => c.To == labelsPin);
            if (connection == null) return null;

            // Get the source node
            var sourceNode = connection.From.Node;

            // First, check common types directly
            if (sourceNode is LabelDataNode labelNode)
            {
                var property = labelNode.GetType().GetProperty("LabelData");
                return property?.GetValue(labelNode) as ILabelVolumeData;
            }
            else if (sourceNode is DatasetDecompressionNode decompressionNode)
            {
                var property = decompressionNode.GetType().GetProperty("LabelData");
                return property?.GetValue(decompressionNode) as ILabelVolumeData;
            }
            else if (sourceNode is CurrentLabelNode currentLabelNode)
            {
                var property = currentLabelNode.GetType().GetProperty("LabelData");
                return property?.GetValue(currentLabelNode) as ILabelVolumeData;
            }

            // Try to get the label data from the source node using reflection
            var labelProperty = sourceNode.GetType().GetProperty("LabelData") ??
                               sourceNode.GetType().GetProperty("Labels") ??
                               sourceNode.GetType().GetProperty("OutputLabels");

            if (labelProperty != null)
            {
                var value = labelProperty.GetValue(sourceNode);
                if (value is ILabelVolumeData labelData)
                    return labelData;
            }

            return null;
        }

        private List<NodeConnection> GetNodeConnections()
        {
            // Find the node editor form
            var nodeEditor = FindNodeEditorForm();
            if (nodeEditor == null) return null;

            // Get connections from node editor using reflection
            var connectionsField = nodeEditor.GetType().GetField("connections",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (connectionsField == null) return null;

            return connectionsField.GetValue(nodeEditor) as List<NodeConnection>;
        }

        private Control FindNodeEditorForm()
        {
            // Find the NodeEditorForm in the controls hierarchy
            foreach (Form form in Application.OpenForms)
            {
                var result = FindControlOfType(form, "NodeEditorForm");
                if (result != null)
                    return result;
            }
            return null;
        }

        private Control FindControlOfType(Control parent, string typeName)
        {
            // Check if the control's type name matches what we're looking for
            if (parent.GetType().Name == typeName)
                return parent;

            // Recursively search through child controls
            foreach (Control child in parent.Controls)
            {
                var result = FindControlOfType(child, typeName);
                if (result != null)
                    return result;
            }

            return null;
        }
    }
}
