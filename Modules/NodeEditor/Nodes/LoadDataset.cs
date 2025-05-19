using CTS.NodeEditor;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO.Compression;
using System.IO;
using System.Text;

namespace CTS.Modules.NodeEditor.Nodes
{
    /// <summary>
    /// Node that loads a dataset from disk or from a cluster
    /// </summary>
    public class LoadDatasetNode : BaseNode
    {
        private string _datasetPath = "";
        private bool _useCluster = false;
        private DatasetTransferClient _transferClient;
        private CancellationTokenSource _cancellationTokenSource;
        private string _currentTransferId;
        private string _currentDatasetId;
        private string _transferStatus = "Idle";
        private float _transferProgress = 0;
        private bool _transferInProgress = false;
        private double _pixelSize = 1e-6;
        public double PixelSize                
        {
            get => _pixelSize;
            set => _pixelSize = value;
        }

        // Temporary storage for dataset during transfer
        private IGrayscaleVolumeData _tempVolume;
        private ILabelVolumeData _tempLabels;

        public LoadDatasetNode(Point position) : base(position)
        {
            // Set size properties from base class
            Size = new Size(180, 120);

            // Set default cluster state from NodeEditor
            if (NodeEditorForm.Instance != null)
            {
                _useCluster = NodeEditorForm.Instance.UseCluster;
            }

            // Initialize transfer client if using cluster
            if (_useCluster)
            {
                InitializeTransferClient();
            }
        }

        protected override void SetupPins()
        {
            // Create output pins
            AddOutputPin("Volume", Color.LightBlue);
            AddOutputPin("Labels", Color.LightCoral);
        }
        public override Dictionary<string, string> GetNodeParameters()
        {
            var parameters = new Dictionary<string, string>
            {
                ["DatasetPath"] = _datasetPath,
                ["PixelSize"] = _pixelSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            return parameters;
        }
        private void InitializeTransferClient()
        {
            try
            {
                // Get cluster settings from MainForm or config
                string serverIp = "localhost"; // Default
                int serverPort = 8000; // Default

                // Access MainForm directly from the NodeEditorForm.Instance
                var mainForm = NodeEditorForm.Instance?.mainForm;

                // Try to get connection info from MainForm
                if (mainForm?.ComputeEndpoints != null && mainForm.ComputeEndpoints.Count > 0)
                {
                    var endpoint = mainForm.ComputeEndpoints[0];
                    serverIp = endpoint.IP; // Using IP property instead of ServerAddress
                    serverPort = endpoint.Port;
                }

                _transferClient = new DatasetTransferClient(serverIp, serverPort);

                // Subscribe to events
                _transferClient.TransferProgressChanged += (sender, e) =>
                {
                    _transferProgress = e.ProgressPercentage;
                    _transferStatus = $"{e.Status} ({e.ProgressPercentage:F1}%)";
                };

                _transferClient.TransferStatusChanged += (sender, e) =>
                {
                    _transferStatus = e.Status;

                    // If completed, set the final progress
                    if (e.Status == "Completed" || e.Status == "Downloaded")
                    {
                        _transferProgress = 100;
                        _transferInProgress = false;
                    }
                    else if (e.Status == "Failed" || e.Status == "Error" || e.Status == "Cancelled")
                    {
                        _transferInProgress = false;
                    }
                };

                Logger.Log("[LoadDatasetNode] Transfer client initialized");
            }
            catch (Exception ex)
            {
                Logger.Log($"[LoadDatasetNode] Error initializing transfer client: {ex.Message}");
                _useCluster = false;
            }
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel();
            panel.Dock = DockStyle.Fill;

            // Dataset path selection
            var pathLabel = new Label { Text = "Dataset Path:", Location = new Point(10, 10), AutoSize = true };
            var pathTextBox = new TextBox
            {
                Text = _datasetPath,
                Location = new Point(10, 30),
                Width = 300
            };

            var browseButton = new Button
            {
                Text = "Browse...",
                Location = new Point(320, 29),
                Width = 80
            };

            browseButton.Click += (s, e) =>
            {
                using (var dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "Select Dataset Folder";
                    if (!string.IsNullOrEmpty(_datasetPath))
                    {
                        dialog.SelectedPath = _datasetPath;
                    }

                    if (dialog.ShowDialog() == DialogResult.OK)
                    {
                        _datasetPath = dialog.SelectedPath;
                        pathTextBox.Text = _datasetPath;
                    }
                }
            };
            var pixelSizeLabel = new Label
            {
                Text = "Pixel Size (µm):",
                Location = new Point(10, 90),
                AutoSize = true
            };
            var pixelSizeTextBox = new TextBox
            {
                Text = _pixelSize.ToString("G"),
                Location = new Point(120, 88),
                Width = 80
            };
            pixelSizeTextBox.Leave += (s, e) =>
            {
                if (double.TryParse(pixelSizeTextBox.Text,
                                    System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    out double val) && val > 0)
                {
                    _pixelSize = val;
                }
                else
                {
                    // revert to last good value
                    pixelSizeTextBox.Text = _pixelSize.ToString("G");
                }
            };
            // Cluster processing options
            var useClusterCheckbox = new CheckBox
            {
                Text = "Use compute cluster for processing",
                Location = new Point(10, 60),
                Checked = _useCluster,
                AutoSize = true
            };

            useClusterCheckbox.CheckedChanged += (s, e) =>
            {
                _useCluster = useClusterCheckbox.Checked;

                // Initialize transfer client if not already done
                if (_useCluster && _transferClient == null)
                {
                    InitializeTransferClient();
                }
            };

            // Status display
            var statusLabel = new Label
            {
                Text = "Status:",
                Location = new Point(10, 90),
                AutoSize = true
            };

            var statusValueLabel = new Label
            {
                Text = _transferStatus,
                Location = new Point(60, 90),
                AutoSize = true
            };

            // Progress bar for transfer
            var progressBar = new ProgressBar
            {
                Location = new Point(10, 110),
                Width = 390,
                Height = 20,
                Minimum = 0,
                Maximum = 100,
                Value = (int)_transferProgress
            };

            // Load button
            var loadButton = new Button
            {
                Text = "Load Dataset",
                Location = new Point(10, 140),
                Width = 120,
                Height = 30
            };

            loadButton.Click += async (s, e) =>
            {
                // Prevent multiple simultaneous loads
                if (_transferInProgress)
                {
                    MessageBox.Show("A transfer is already in progress.", "Transfer in progress",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (string.IsNullOrEmpty(_datasetPath))
                {
                    MessageBox.Show("Please select a dataset path.", "Missing path",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Disable button during load
                loadButton.Enabled = false;

                try
                {
                    _transferInProgress = true;
                    _transferProgress = 0;
                    _transferStatus = "Starting...";
                    statusValueLabel.Text = _transferStatus;

                    // Setup progress updates on UI thread
                    var timer = new System.Windows.Forms.Timer();
                    timer.Interval = 500; // Update every 500ms
                    timer.Tick += (ts, te) =>
                    {
                        statusValueLabel.Text = _transferStatus;
                        progressBar.Value = Math.Min(100, (int)_transferProgress);

                        // Stop timer when transfer completes
                        if (!_transferInProgress)
                        {
                            timer.Stop();
                            loadButton.Enabled = true;
                        }
                    };
                    timer.Start();

                    // Create cancellation token
                    _cancellationTokenSource = new CancellationTokenSource();

                    if (_useCluster)
                    {
                        // Load dataset using cluster
                        await LoadUsingClusterAsync(_cancellationTokenSource.Token);
                    }
                    else
                    {
                        // Load dataset locally
                        await LoadLocallyAsync(_cancellationTokenSource.Token);
                    }

                    // Update output data
                    SetOutputData("Volume", _tempVolume);
                    SetOutputData("Labels", _tempLabels);

                    // Update status
                    _transferStatus = "Completed";
                    _transferProgress = 100;
                    _transferInProgress = false;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[LoadDatasetNode] Error loading dataset: {ex.Message}");
                    _transferStatus = $"Error: {ex.Message}";
                    _transferInProgress = false;

                    // Display error to user
                    MessageBox.Show($"Error loading dataset: {ex.Message}", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    loadButton.Enabled = true;
                }
            };

            // Cancel button
            var cancelButton = new Button
            {
                Text = "Cancel",
                Location = new Point(140, 140),
                Width = 80,
                Height = 30
            };

            cancelButton.Click += (s, e) =>
            {
                if (_transferInProgress && _cancellationTokenSource != null)
                {
                    _cancellationTokenSource.Cancel();
                    _transferStatus = "Cancelling...";
                    statusValueLabel.Text = _transferStatus;
                }
            };

            // Add controls to panel
            panel.Controls.Add(pathLabel);
            panel.Controls.Add(pathTextBox);
            panel.Controls.Add(browseButton);
            panel.Controls.Add(pixelSizeLabel);
            panel.Controls.Add(pixelSizeTextBox);
            panel.Controls.Add(useClusterCheckbox);
            panel.Controls.Add(statusLabel);
            panel.Controls.Add(statusValueLabel);
            panel.Controls.Add(progressBar);
            panel.Controls.Add(loadButton);
            panel.Controls.Add(cancelButton);

            return panel;
        }

        private async Task LoadLocallyAsync(CancellationToken cancellationToken)
        {
            _transferStatus = "Loading locally...";

            // Create a progress form
            ProgressFormWithProgress progressForm = null;
            using (progressForm = new ProgressFormWithProgress("Loading dataset..."))
            {
                progressForm.Show();

                // Load the dataset from disk using the correct method
                var result = await FileOperations.LoadDatasetAsync(
                    _datasetPath,
                    true, // Use memory mapping
                    1e-6, // Default pixel size
                    1,    // No binning
                    progressForm);

                _tempVolume = result.volumeData;
                _tempLabels = result.volumeLabels;
            }

            _transferStatus = "Loaded locally";
            _transferProgress = 100;
        }

        private async Task LoadUsingClusterAsync(CancellationToken cancellationToken)
        {
            try
            {
                _transferStatus = "Initializing cluster transfer...";

                // Create a progress form for initial loading 
                ProgressFormWithProgress progressForm = null;
                using (progressForm = new ProgressFormWithProgress("Loading dataset..."))
                {
                    progressForm.Show();

                    // First load locally to get dimensions
                    var tempResult = await FileOperations.LoadDatasetAsync(
                        _datasetPath,
                        true, // Use memory mapping
                        1e-6, // Default pixel size
                        1,    // No binning
                        progressForm);

                    // Get dimensions from loaded data
                    int width = tempResult.width;
                    int height = tempResult.height;
                    int depth = tempResult.depth;
                    double pixelSize = tempResult.pixelSize;

                    // Keep temporary references to the data
                    _tempVolume = tempResult.volumeData;
                    _tempLabels = tempResult.volumeLabels;

                    if (_transferClient != null)
                    {
                        // Prepare metadata for transfer using the DatasetMetadata from correct namespace
                        // which is what the transfer client expects
                        var metadata = new CTS.NodeEditor.DatasetMetadata
                        {
                            Name = System.IO.Path.GetFileName(_datasetPath),
                            Width = width,
                            Height = height,
                            Depth = depth,
                            ChunkDim = 256, // Use server's preferred chunk size
                            PixelSize = pixelSize,
                            BitDepth = 8 // Assuming 8-bit data
                        };

                        // Calculate chunk count
                        int chunkCountX = (width + metadata.ChunkDim - 1) / metadata.ChunkDim;
                        int chunkCountY = (height + metadata.ChunkDim - 1) / metadata.ChunkDim;
                        int chunkCountZ = (depth + metadata.ChunkDim - 1) / metadata.ChunkDim;
                        metadata.VolumeChunks = chunkCountX * chunkCountY * chunkCountZ;

                        if (_transferClient != null)
                        {
                            // Initialize transfer on server
                            _transferStatus = "Initializing server transfer...";
                            _currentTransferId = await _transferClient.InitializeTransferAsync(metadata);
                            _currentDatasetId = _currentTransferId; // For simplicity, use same ID

                            // Upload volume data
                            _transferStatus = "Uploading volume data...";
                            bool volumeUploaded = await _transferClient.UploadVolumeAsync(
                                _currentTransferId,
                                tempResult.volumeData,
                                cancellationToken);

                            if (!volumeUploaded)
                            {
                                throw new Exception("Failed to upload volume data");
                            }

                            // Upload label data if available
                            if (tempResult.volumeLabels != null)
                            {
                                _transferStatus = "Uploading label data...";
                                bool labelsUploaded = await _transferClient.UploadLabelsAsync(
                                    _currentTransferId,
                                    tempResult.volumeLabels,
                                    cancellationToken);

                                if (!labelsUploaded)
                                {
                                    Logger.Log("[LoadDatasetNode] Warning: Failed to upload labels, continuing with volume only");
                                }
                            }

                            // Complete the transfer
                            _transferStatus = "Completing transfer...";
                            await _transferClient.CompleteTransferAsync(_currentTransferId);
                        }
                        else
                        {
                            _transferStatus = "Cluster transfer unavailable, using local data";
                        }
                    }

                    _transferStatus = "Transfer completed";
                    _transferProgress = 100;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[LoadDatasetNode] Error during cluster transfer: {ex.Message}");
                throw;
            }
        }

        public override void Execute()
        {
            // The loading is handled by the UI actions, so this method just needs to
            // ensure the output data is properly set
            if (_tempVolume != null)
            {
                SetOutputData("Volume", _tempVolume);
            }

            if (_tempLabels != null)
            {
                SetOutputData("Labels", _tempLabels);
            }
        }
    }

}
