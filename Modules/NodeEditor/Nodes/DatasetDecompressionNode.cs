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
    public class DatasetDecompressionNode : BaseNode
    {
        // Parameters
        public string InputPath { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public bool LoadAfterDecompression { get; set; } = true;
        public bool CreateSeparateNodes { get; set; } = true;

        // Output data references (for connecting to other nodes)
        private IGrayscaleVolumeData outputVolumeData;
        private ILabelVolumeData outputLabelData;

        // Public accessors for output pin data
        public IGrayscaleVolumeData VolumeData => outputVolumeData;
        public ILabelVolumeData LabelData => outputLabelData;

        // UI Controls
        private TextBox txtInputPath;
        private TextBox txtOutputPath;
        private Button btnSelectInput;
        private Button btnSelectOutput;
        private CheckBox chkLoadAfterDecompression;
        private CheckBox chkCreateSeparateNodes;
        private Label lblStatus;
        private ProgressBar progressBar;
        private Button btnDecompress;

        // Keep reference to compressor
        private ChunkedVolumeCompressor _compressor;

        // Constructor
        public DatasetDecompressionNode(Point position) : base(position)
        {
            Color = Color.FromArgb(120, 200, 120);
        }

        protected override void SetupPins()
        {
            // Output pins
            AddOutputPin("Volume", Color.LightBlue);
            AddOutputPin("Labels", Color.LightCoral);
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
                Text = "Dataset Decompression",
                Font = new Font("Arial", 12, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(10, 10),
                ForeColor = Color.White
            };
            panel.Controls.Add(titleLabel);

            int currentY = 40;

            // Input Path
            var lblInput = new Label
            {
                Text = "Input File (*.cts3d):",
                AutoSize = true,
                Location = new Point(10, currentY),
                ForeColor = Color.White
            };
            panel.Controls.Add(lblInput);
            currentY += 25;

            txtInputPath = new TextBox
            {
                Location = new Point(10, currentY),
                Width = 280,
                ReadOnly = true,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                Text = InputPath
            };
            panel.Controls.Add(txtInputPath);

            btnSelectInput = new Button
            {
                Text = "Browse...",
                Location = new Point(300, currentY - 2),
                Width = 80,
                Height = 25,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            btnSelectInput.Click += BtnSelectInput_Click;
            panel.Controls.Add(btnSelectInput);
            currentY += 30;

            // Output Path
            var lblOutput = new Label
            {
                Text = "Output Folder:",
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
            currentY += 35;

            // Options
            chkLoadAfterDecompression = new CheckBox
            {
                Text = "Load dataset after decompression",
                Location = new Point(10, currentY),
                Checked = LoadAfterDecompression,
                ForeColor = Color.White
            };
            chkLoadAfterDecompression.CheckedChanged += (s, e) => LoadAfterDecompression = chkLoadAfterDecompression.Checked;
            panel.Controls.Add(chkLoadAfterDecompression);
            currentY += 25;

            chkCreateSeparateNodes = new CheckBox
            {
                Text = "Create separate volume and label nodes",
                Location = new Point(10, currentY),
                Checked = CreateSeparateNodes,
                ForeColor = Color.White
            };
            chkCreateSeparateNodes.CheckedChanged += (s, e) => CreateSeparateNodes = chkCreateSeparateNodes.Checked;
            panel.Controls.Add(chkCreateSeparateNodes);
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
            currentY += 35;

            // Decompress button
            btnDecompress = new Button
            {
                Text = "Decompress",
                Location = new Point(10, currentY),
                Width = 120,
                Height = 30,
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                Font = new Font("Arial", 9, FontStyle.Bold)
            };
            btnDecompress.Click += (s, e) => Execute();
            panel.Controls.Add(btnDecompress);

            return panel;
        }

        private void BtnSelectInput_Click(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "CTS Compressed Files|*.cts3d";
                dialog.Title = "Select Compressed File";

                if (!string.IsNullOrEmpty(InputPath))
                {
                    dialog.InitialDirectory = Path.GetDirectoryName(InputPath);
                }

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    InputPath = dialog.FileName;
                    txtInputPath.Text = InputPath;

                    // Auto-generate output path
                    if (string.IsNullOrEmpty(OutputPath))
                    {
                        string dir = Path.GetDirectoryName(InputPath);
                        string name = Path.GetFileNameWithoutExtension(InputPath);
                        OutputPath = Path.Combine(dir, name + "_decompressed");
                        txtOutputPath.Text = OutputPath;
                    }
                }
            }
        }

        private void BtnSelectOutput_Click(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select Output Folder";

                if (!string.IsNullOrEmpty(OutputPath))
                {
                    dialog.SelectedPath = OutputPath;
                }

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    OutputPath = dialog.SelectedPath;
                    txtOutputPath.Text = OutputPath;
                }
            }
        }

        public override async void Execute()
        {
            if (string.IsNullOrEmpty(InputPath))
            {
                MessageBox.Show("Please select an input file to decompress.",
                    "Input Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrEmpty(OutputPath))
            {
                MessageBox.Show("Please select an output folder for the decompressed data.",
                    "Output Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!File.Exists(InputPath))
            {
                MessageBox.Show("The selected input file does not exist.",
                    "File Not Found", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Disable controls during processing
            btnDecompress.Enabled = false;
            btnSelectInput.Enabled = false;
            btnSelectOutput.Enabled = false;
            progressBar.Value = 0;
            lblStatus.Text = "Decompressing...";

            try
            {
                // Create compressor
                _compressor = new ChunkedVolumeCompressor();

                // Create a progress reporter
                var progress = new Progress<int>(value =>
                {
                    progressBar.Value = Math.Min(value, 100);
                    lblStatus.Text = $"Decompressing... {value}%";
                });

                // Start decompression
                var startTime = DateTime.Now;

                // Decompress file to the output directory
                await _compressor.DecompressAsync(InputPath, OutputPath, progress);

                var duration = DateTime.Now - startTime;

                lblStatus.Text = $"Decompression completed in {duration.TotalSeconds:F1}s";

                if (LoadAfterDecompression)
                {
                    // Load the decompressed data directly into this node
                    await LoadDecompressedDataAsync(OutputPath);

                    // Create separate nodes if requested
                    if (CreateSeparateNodes)
                    {
                        CreateNodeOutputs();
                    }

                    // Notify connected nodes that data is available
                    NotifyOutputNodesOfUpdate();
                }

                MessageBox.Show($"Decompression successful!\n\nTime: {duration.TotalSeconds:F1}s\n" +
                               $"Decompressed to: {OutputPath}",
                               "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Logger.Log($"[DatasetDecompressionNode] Error: {ex.Message}");
                lblStatus.Text = "Decompression failed";
                MessageBox.Show($"Decompression failed: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                // Re-enable controls
                btnDecompress.Enabled = true;
                btnSelectInput.Enabled = true;
                btnSelectOutput.Enabled = true;
            }
        }

        private async Task LoadDecompressedDataAsync(string folderPath)
        {
            try
            {
                // Check for the volume data in the target folder
                string volumePath = Path.Combine(folderPath, "volume.raw");
                string infoPath = Path.Combine(folderPath, "dataset.info");
                string labelsPath = Path.Combine(folderPath, "labels.raw");

                if (File.Exists(volumePath) && File.Exists(infoPath))
                {
                    // Read the dataset info to get dimensions
                    var datasetInfo = ReadDatasetInfo(infoPath);

                    // Load the volume data
                    outputVolumeData = await Task.Run(() => LoadVolume(volumePath, datasetInfo.Width, datasetInfo.Height, datasetInfo.Depth));

                    // Load labels if they exist
                    if (File.Exists(labelsPath))
                    {
                        outputLabelData = await Task.Run(() => LoadLabels(labelsPath, datasetInfo.Width, datasetInfo.Height, datasetInfo.Depth));
                    }

                    Logger.Log($"[DatasetDecompressionNode] Loaded data: {datasetInfo.Width}x{datasetInfo.Height}x{datasetInfo.Depth}");
                }
                else
                {
                    throw new FileNotFoundException("Missing required files in decompression output");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[DatasetDecompressionNode] Error loading decompressed data: {ex.Message}");
                throw;
            }
        }

        private DatasetInfo ReadDatasetInfo(string infoPath)
        {
            using (var reader = new StreamReader(infoPath))
            {
                string contents = reader.ReadToEnd();
                var lines = contents.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                var info = new DatasetInfo();

                foreach (var line in lines)
                {
                    var parts = line.Split('=');
                    if (parts.Length == 2)
                    {
                        string key = parts[0].Trim();
                        string value = parts[1].Trim();

                        if (key.Equals("width", StringComparison.OrdinalIgnoreCase))
                            info.Width = int.Parse(value);
                        else if (key.Equals("height", StringComparison.OrdinalIgnoreCase))
                            info.Height = int.Parse(value);
                        else if (key.Equals("depth", StringComparison.OrdinalIgnoreCase))
                            info.Depth = int.Parse(value);
                    }
                }

                return info;
            }
        }

        private IGrayscaleVolumeData LoadVolume(string volumePath, int width, int height, int depth)
        {
            try
            {
                // Create a chunked volume to store the data
                var volume = new ChunkedVolume(width, height, depth);

                // Read the raw file bytes
                byte[] data = File.ReadAllBytes(volumePath);

                // Fill the volume
                int index = 0;
                for (int z = 0; z < depth; z++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            volume[x, y, z] = data[index++];
                        }
                    }
                }

                return volume;
            }
            catch (Exception ex)
            {
                Logger.Log($"[DatasetDecompressionNode] Error loading volume: {ex.Message}");
                throw;
            }
        }

        private ILabelVolumeData LoadLabels(string labelsPath, int width, int height, int depth)
        {
            try
            {
                // Create a chunked label volume to store the data
                // Use the constructor that takes dimensions and memory mapping flag
                // We'll use in-memory mode (false) since we're just loading from a file
                int chunkDim = 32; // Default chunk dimension, adjust if needed
                var labels = new ChunkedLabelVolume(width, height, depth, chunkDim, false);

                // Read the raw file bytes
                byte[] data = File.ReadAllBytes(labelsPath);

                // Fill the volume
                int index = 0;
                for (int z = 0; z < depth; z++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            labels[x, y, z] = data[index++];
                        }
                    }
                }

                return labels;
            }
            catch (Exception ex)
            {
                Logger.Log($"[DatasetDecompressionNode] Error loading labels: {ex.Message}");
                throw;
            }
        }
        private void CreateNodeOutputs()
        {
            try
            {
                // Get the NodeEditorForm instance
                var nodeEditorForm = FindNodeEditorForm();
                if (nodeEditorForm == null)
                {
                    Logger.Log("[DatasetDecompressionNode] Could not find NodeEditorForm");
                    return;
                }

                // Access the nodes list from NodeEditorForm
                var nodesField = nodeEditorForm.GetType().GetField("nodes",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (nodesField == null)
                {
                    Logger.Log("[DatasetDecompressionNode] Could not access nodes field");
                    return;
                }

                var nodes = nodesField.GetValue(nodeEditorForm) as List<BaseNode>;
                if (nodes == null)
                {
                    Logger.Log("[DatasetDecompressionNode] nodes field is null or not a List<BaseNode>");
                    return;
                }

                // Create volume node if we have volume data
                if (outputVolumeData != null)
                {
                    var volumeNode = new VolumeDataNode(
                        new Point(this.Position.X + 200, this.Position.Y - 50));

                    // Set the data in the node
                    var volumeDataProperty = volumeNode.GetType().GetProperty("VolumeData");
                    if (volumeDataProperty != null)
                    {
                        volumeDataProperty.SetValue(volumeNode, outputVolumeData);
                    }

                    nodes.Add(volumeNode);
                }

                // Create label node if we have label data
                if (outputLabelData != null)
                {
                    var labelNode = new LabelDataNode(
                        new Point(this.Position.X + 200, this.Position.Y + 50));

                    // Set the data in the node
                    var labelDataProperty = labelNode.GetType().GetProperty("LabelData");
                    if (labelDataProperty != null)
                    {
                        labelDataProperty.SetValue(labelNode, outputLabelData);
                    }

                    nodes.Add(labelNode);
                }

                // Force a redraw of the node editor
                var invalidateMethod = nodeEditorForm.GetType().GetMethod("Invalidate",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                if (invalidateMethod != null)
                {
                    invalidateMethod.Invoke(nodeEditorForm, null);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[DatasetDecompressionNode] Error creating node outputs: {ex.Message}");
            }
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

        private void NotifyOutputNodesOfUpdate()
        {
            var connections = GetNodeConnections();
            if (connections == null) return;

            // Find output pins
            var volumePin = outputs.FirstOrDefault(p => p.Name == "Volume");
            var labelsPin = outputs.FirstOrDefault(p => p.Name == "Labels");

            if (volumePin != null)
            {
                // Find all nodes connected to the Volume output
                var connectedVolumeNodes = connections
                    .Where(c => c.From == volumePin)
                    .Select(c => c.To.Node)
                    .Distinct()
                    .ToList();

                // Execute connected nodes
                foreach (var node in connectedVolumeNodes)
                {
                    Logger.Log($"[DatasetDecompressionNode] Notifying volume connected node: {node.GetType().Name}");
                    node.Execute();
                }
            }

            if (labelsPin != null)
            {
                // Find all nodes connected to the Labels output
                var connectedLabelNodes = connections
                    .Where(c => c.From == labelsPin)
                    .Select(c => c.To.Node)
                    .Distinct()
                    .ToList();

                // Execute connected nodes
                foreach (var node in connectedLabelNodes)
                {
                    Logger.Log($"[DatasetDecompressionNode] Notifying label connected node: {node.GetType().Name}");
                    node.Execute();
                }
            }
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

        private class DatasetInfo
        {
            public int Width { get; set; }
            public int Height { get; set; }
            public int Depth { get; set; }
        }
    }
}
