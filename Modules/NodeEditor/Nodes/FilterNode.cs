using CTS.NodeEditor;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Linq;

namespace CTS.Modules.NodeEditor.Nodes
{
    public class FilterNode : BaseNode
    {
        // Filter parameters
        public string FilterType { get; set; } = "Gaussian";
        public int KernelSize { get; set; } = 3;
        public float Sigma { get; set; } = 1.0f;
        public bool UseGPU { get; set; } = true;

        // Additional filter-specific parameters
        public float SigmaSpatial { get; set; } = 3.0f;
        public float SigmaRange { get; set; } = 25.0f;
        public float NlmH { get; set; } = 10.0f;
        public int TemplateSize { get; set; } = 3;
        public int SearchSize { get; set; } = 7;
        public float UnsharpAmount { get; set; } = 1.5f;
        public bool NormalizeEdges { get; set; } = true;
        public bool Is3D { get; set; } = false;

        // UI Controls
        private ComboBox cmbFilterType;
        private NumericUpDown numKernelSize;
        private NumericUpDown numSigma;
        private NumericUpDown numSigmaSpatial;
        private NumericUpDown numSigmaRange;
        private NumericUpDown numNlmH;
        private NumericUpDown numTemplateSize;
        private NumericUpDown numSearchSize;
        private NumericUpDown numUnsharpAmount;
        private CheckBox chkUseGPU;
        private CheckBox chkNormalizeEdges;
        private RadioButton rb2DOnly;
        private RadioButton rb3D;
        private Dictionary<string, Panel> filterPanels;
        private Label lblDimensions;

        // Storage for input/output data
        private IGrayscaleVolumeData inputVolume;
        private IGrayscaleVolumeData outputVolume;
        private int width;
        private int height;
        private int depth;

        // Public access to processed volume
        public IGrayscaleVolumeData FilteredVolume => outputVolume;

        // Constructor for node editor
        public FilterNode(Point position) : base(position)
        {
            Color = Color.FromArgb(100, 180, 255); // Blue theme for processing nodes
        }

        // Secondary constructor for direct usage without GUI
        public FilterNode(string filterType, int kernelSize, float sigma, bool useGPU, bool is3D = false)
            : this(new Point(0, 0))
        {
            FilterType = filterType;
            KernelSize = kernelSize;
            Sigma = sigma;
            UseGPU = useGPU;
            Is3D = is3D;
        }

        protected override void SetupPins()
        {
            AddInputPin("Volume", Color.LightBlue);
            AddOutputPin("FilteredVolume", Color.LightBlue);
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
                Text = "Image Filter",
                Font = new Font("Arial", 12, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(10, 10),
                ForeColor = Color.White
            };
            panel.Controls.Add(titleLabel);

            int currentY = 40;

            // Dimensions label
            lblDimensions = new Label
            {
                Text = "Volume: Not connected",
                AutoSize = true,
                Location = new Point(10, currentY),
                ForeColor = Color.White
            };
            panel.Controls.Add(lblDimensions);
            currentY += 30;

            // Filter Type Selection
            var filterTypeLabel = new Label
            {
                Text = "Filter Type:",
                AutoSize = true,
                Location = new Point(10, currentY),
                ForeColor = Color.White
            };
            panel.Controls.Add(filterTypeLabel);
            currentY += 25;

            cmbFilterType = new ComboBox
            {
                Location = new Point(10, currentY),
                Width = 200,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };

            // Add filter types
            cmbFilterType.Items.AddRange(new object[] {
                "Gaussian",
                "Smoothing",
                "Median",
                "Non-Local Means",
                "Bilateral",
                "Unsharp Mask",
                "Edge Detection"
            });

            cmbFilterType.SelectedIndex = 0;
            cmbFilterType.SelectedIndexChanged += FilterType_Changed;
            panel.Controls.Add(cmbFilterType);
            currentY += 30;

            // Common Parameters
            var kernelSizeLabel = new Label
            {
                Text = "Kernel Size (odd value):",
                AutoSize = true,
                Location = new Point(10, currentY),
                ForeColor = Color.White
            };
            panel.Controls.Add(kernelSizeLabel);
            currentY += 25;

            numKernelSize = new NumericUpDown
            {
                Location = new Point(10, currentY),
                Width = 60,
                Minimum = 1,
                Maximum = 31,
                Value = KernelSize,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            numKernelSize.ValueChanged += (s, e) => {
                if (numKernelSize.Value % 2 == 0)
                    numKernelSize.Value += 1;
                KernelSize = (int)numKernelSize.Value;
            };
            panel.Controls.Add(numKernelSize);
            currentY += 30;

            // Create filter-specific parameter panels
            filterPanels = new Dictionary<string, Panel>();

            // 1. Gaussian Panel
            Panel gaussianPanel = CreateFilterPanel("Gaussian", panel, ref currentY);
            var sigmaLabel = new Label
            {
                Text = "Sigma:",
                AutoSize = true,
                Location = new Point(10, 10),
                ForeColor = Color.White
            };
            gaussianPanel.Controls.Add(sigmaLabel);

            numSigma = new NumericUpDown
            {
                Location = new Point(10, 35),
                Width = 60,
                Minimum = 0.1m,
                Maximum = 100,
                DecimalPlaces = 1,
                Increment = 0.1m,
                Value = (decimal)Sigma,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            numSigma.ValueChanged += (s, e) => Sigma = (float)numSigma.Value;
            gaussianPanel.Controls.Add(numSigma);
            filterPanels.Add("Gaussian", gaussianPanel);

            // 2. Smoothing Panel (no additional params)
            Panel smoothingPanel = CreateFilterPanel("Smoothing", panel, ref currentY);
            filterPanels.Add("Smoothing", smoothingPanel);

            // 3. Median Panel (no additional params)
            Panel medianPanel = CreateFilterPanel("Median", panel, ref currentY);
            filterPanels.Add("Median", medianPanel);

            // 4. Non-Local Means Panel
            Panel nlmPanel = CreateFilterPanel("Non-Local Means", panel, ref currentY, 150);

            var hLabel = new Label
            {
                Text = "Filter Strength (h):",
                AutoSize = true,
                Location = new Point(10, 10),
                ForeColor = Color.White
            };
            nlmPanel.Controls.Add(hLabel);

            numNlmH = new NumericUpDown
            {
                Location = new Point(120, 10),
                Width = 60,
                Minimum = 1,
                Maximum = 255,
                DecimalPlaces = 1,
                Increment = 0.5m,
                Value = (decimal)NlmH,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            numNlmH.ValueChanged += (s, e) => NlmH = (float)numNlmH.Value;
            nlmPanel.Controls.Add(numNlmH);

            var templateLabel = new Label
            {
                Text = "Template Radius:",
                AutoSize = true,
                Location = new Point(10, 40),
                ForeColor = Color.White
            };
            nlmPanel.Controls.Add(templateLabel);

            numTemplateSize = new NumericUpDown
            {
                Location = new Point(120, 40),
                Width = 60,
                Minimum = 1,
                Maximum = 15,
                Value = TemplateSize,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            numTemplateSize.ValueChanged += (s, e) => TemplateSize = (int)numTemplateSize.Value;
            nlmPanel.Controls.Add(numTemplateSize);

            var searchLabel = new Label
            {
                Text = "Search Radius:",
                AutoSize = true,
                Location = new Point(10, 70),
                ForeColor = Color.White
            };
            nlmPanel.Controls.Add(searchLabel);

            numSearchSize = new NumericUpDown
            {
                Location = new Point(120, 70),
                Width = 60,
                Minimum = 1,
                Maximum = 21,
                Value = SearchSize,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            numSearchSize.ValueChanged += (s, e) => SearchSize = (int)numSearchSize.Value;
            nlmPanel.Controls.Add(numSearchSize);

            filterPanels.Add("Non-Local Means", nlmPanel);

            // 5. Bilateral Panel
            Panel bilateralPanel = CreateFilterPanel("Bilateral", panel, ref currentY, 120);

            var sigmaSpatialLabel = new Label
            {
                Text = "Spatial Sigma:",
                AutoSize = true,
                Location = new Point(10, 10),
                ForeColor = Color.White
            };
            bilateralPanel.Controls.Add(sigmaSpatialLabel);

            numSigmaSpatial = new NumericUpDown
            {
                Location = new Point(120, 10),
                Width = 60,
                Minimum = 0.1m,
                Maximum = 100,
                DecimalPlaces = 1,
                Increment = 0.1m,
                Value = (decimal)SigmaSpatial,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            numSigmaSpatial.ValueChanged += (s, e) => SigmaSpatial = (float)numSigmaSpatial.Value;
            bilateralPanel.Controls.Add(numSigmaSpatial);

            var sigmaRangeLabel = new Label
            {
                Text = "Range Sigma:",
                AutoSize = true,
                Location = new Point(10, 40),
                ForeColor = Color.White
            };
            bilateralPanel.Controls.Add(sigmaRangeLabel);

            numSigmaRange = new NumericUpDown
            {
                Location = new Point(120, 40),
                Width = 60,
                Minimum = 0.1m,
                Maximum = 100,
                DecimalPlaces = 1,
                Increment = 0.1m,
                Value = (decimal)SigmaRange,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            numSigmaRange.ValueChanged += (s, e) => SigmaRange = (float)numSigmaRange.Value;
            bilateralPanel.Controls.Add(numSigmaRange);

            filterPanels.Add("Bilateral", bilateralPanel);

            // 6. Unsharp Mask Panel
            Panel unsharpPanel = CreateFilterPanel("Unsharp Mask", panel, ref currentY, 70);

            var unsharpAmountLabel = new Label
            {
                Text = "Sharpening Amount:",
                AutoSize = true,
                Location = new Point(10, 10),
                ForeColor = Color.White
            };
            unsharpPanel.Controls.Add(unsharpAmountLabel);

            numUnsharpAmount = new NumericUpDown
            {
                Location = new Point(120, 10),
                Width = 60,
                Minimum = 0.1m,
                Maximum = 10.0m,
                DecimalPlaces = 2,
                Increment = 0.1m,
                Value = (decimal)UnsharpAmount,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            numUnsharpAmount.ValueChanged += (s, e) => UnsharpAmount = (float)numUnsharpAmount.Value;
            unsharpPanel.Controls.Add(numUnsharpAmount);

            var unsharpSigmaLabel = new Label
            {
                Text = "Uses the Kernel Size and Sigma parameters for blurring.",
                AutoSize = true,
                Location = new Point(10, 40),
                ForeColor = Color.White,
                Font = new Font("Arial", 8, FontStyle.Italic)
            };
            unsharpPanel.Controls.Add(unsharpSigmaLabel);

            filterPanels.Add("Unsharp Mask", unsharpPanel);

            // 7. Edge Detection Panel
            Panel edgePanel = CreateFilterPanel("Edge Detection", panel, ref currentY, 50);

            chkNormalizeEdges = new CheckBox
            {
                Text = "Normalize Result",
                Location = new Point(10, 10),
                AutoSize = true,
                Checked = NormalizeEdges,
                ForeColor = Color.White
            };
            chkNormalizeEdges.CheckedChanged += (s, e) => NormalizeEdges = chkNormalizeEdges.Checked;
            edgePanel.Controls.Add(chkNormalizeEdges);

            filterPanels.Add("Edge Detection", edgePanel);

            // Hide all panels initially
            foreach (var p in filterPanels.Values)
            {
                p.Visible = false;
            }

            // Show panel for initially selected filter
            if (filterPanels.ContainsKey(FilterType))
            {
                filterPanels[FilterType].Visible = true;
            }

            currentY += 180; // Space for the tallest panel

            // Add 2D/3D radio buttons
            var dimensionLabel = new Label
            {
                Text = "Filter Dimension:",
                AutoSize = true,
                Location = new Point(10, currentY),
                ForeColor = Color.White,
                Font = new Font("Arial", 9, FontStyle.Bold)
            };
            panel.Controls.Add(dimensionLabel);
            currentY += 25;

            rb2DOnly = new RadioButton
            {
                Text = "2D slices only (Z-direction)",
                Location = new Point(10, currentY),
                AutoSize = true,
                Checked = !Is3D,
                ForeColor = Color.White
            };
            rb2DOnly.CheckedChanged += (s, e) => Is3D = !rb2DOnly.Checked;
            panel.Controls.Add(rb2DOnly);
            currentY += 25;

            rb3D = new RadioButton
            {
                Text = "Full 3D (All directions)",
                Location = new Point(10, currentY),
                AutoSize = true,
                Checked = Is3D,
                ForeColor = Color.White
            };
            rb3D.CheckedChanged += (s, e) => Is3D = rb3D.Checked;
            panel.Controls.Add(rb3D);
            currentY += 35;

            // Add GPU checkbox
            chkUseGPU = new CheckBox
            {
                Text = "Use GPU Acceleration (if available)",
                Location = new Point(10, currentY),
                AutoSize = true,
                Checked = UseGPU,
                ForeColor = Color.White
            };
            chkUseGPU.CheckedChanged += (s, e) => UseGPU = chkUseGPU.Checked;
            panel.Controls.Add(chkUseGPU);
            currentY += 30;

            // Add Apply button
            var applyButton = new Button
            {
                Text = "Apply Filter",
                Location = new Point(10, currentY),
                Width = 120,
                Height = 30,
                BackColor = Color.FromArgb(100, 180, 100),
                ForeColor = Color.White,
                Font = new Font("Arial", 9, FontStyle.Bold)
            };
            applyButton.Click += (s, e) => Execute();
            panel.Controls.Add(applyButton);

            // Check for connected input
            UpdateInputData();

            return panel;
        }

        private void FilterType_Changed(object sender, EventArgs e)
        {
            // Get the new filter type
            FilterType = cmbFilterType.SelectedItem.ToString();

            // Hide all parameter panels
            foreach (var panel in filterPanels.Values)
            {
                panel.Visible = false;
            }

            // Show the panel for selected filter
            if (filterPanels.ContainsKey(FilterType))
            {
                filterPanels[FilterType].Visible = true;
            }
        }

        private Panel CreateFilterPanel(string title, Panel parent, ref int currentY, int height = 90)
        {
            Panel panel = new Panel
            {
                Location = new Point(10, currentY),
                Width = 220,
                Height = height,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(50, 50, 55),
                Visible = false
            };

            parent.Controls.Add(panel);
            return panel;
        }

        private void UpdateInputData()
        {
            inputVolume = GetInputVolume();
            if (inputVolume != null)
            {
                width = inputVolume.Width;
                height = inputVolume.Height;
                depth = inputVolume.Depth;

                if (lblDimensions != null)
                {
                    lblDimensions.Text = $"Volume: {width}×{height}×{depth}";
                }

                Logger.Log($"[FilterNode] Connected to volume {width}×{height}×{depth}");
            }
            else
            {
                width = height = depth = 0;
                if (lblDimensions != null)
                {
                    lblDimensions.Text = "Volume: Not connected";
                }

                Logger.Log("[FilterNode] No input volume connected");
            }
        }

        public override async void Execute()
        {
            try
            {
                // Get the input volume from the connected node
                UpdateInputData();

                if (inputVolume == null)
                {
                    MessageBox.Show("No input volume is connected. Please connect a volume to the input pin.",
                        "Processing Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Show progress dialog
                using (var progress = new ProgressFormWithProgress("Applying filter..."))
                {
                    progress.Show();

                    try
                    {
                        // Create a copy of the input data to work with
                        outputVolume = new ChunkedVolume(width, height, depth);
                        CopyVolumeData(inputVolume, outputVolume);

                        

                        var mainForm = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
                        if (mainForm == null)
                        {
                            MessageBox.Show("Cannot find MainForm. Please ensure the application is properly loaded.",
                                "Processing Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }

                        // Create FilterManager without showing its UI
                        FilterManager filterManager = new FilterManager(mainForm, false);

                        // Extract the volume data into a byte array for processing
                        byte[] volumeData = ExtractVolumeData(inputVolume);
                        byte[] processedData = null;

                        await Task.Run(() =>
                        {
                            if (Is3D)
                            {
                                // Apply 3D filter directly to our own data
                                processedData = filterManager.ApplyFilter3D(
                                    volumeData,
                                    width, height, depth,
                                    FilterType,
                                    KernelSize,
                                    Sigma,
                                    NlmH,
                                    TemplateSize,
                                    SearchSize,
                                    UseGPU,
                                    progress);
                            }
                            else
                            {
                                // Process slice by slice for 2D filtering
                                processedData = new byte[volumeData.Length];

                                int sliceSize = width * height;
                                for (int z = 0; z < depth; z++)
                                {
                                    // Extract slice
                                    byte[] sliceData = new byte[sliceSize];
                                    Array.Copy(volumeData, z * sliceSize, sliceData, 0, sliceSize);

                                    // Process slice directly with our data
                                    byte[] processedSlice = filterManager.ApplyFilter2D(
                                        sliceData,
                                        width, height,
                                        FilterType,
                                        KernelSize,
                                        Sigma,
                                        NlmH,
                                        TemplateSize,
                                        SearchSize,
                                        UseGPU);

                                    // Copy back to volume
                                    Array.Copy(processedSlice, 0, processedData, z * sliceSize, sliceSize);

                                    // Update progress
                                    progress.SafeUpdateProgress(z + 1, depth, $"Processing slice {z + 1}/{depth}");
                                }
                            }
                        });

                        // Update output volume with processed data
                        if (processedData != null)
                        {
                            UpdateVolumeWithProcessedData(outputVolume, processedData);
                        }

                        // Notify connected nodes that new data is available
                        NotifyOutputNodesOfUpdate();

                        MessageBox.Show($"{FilterType} filter applied successfully!",
                            "Processing Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error applying filter: {ex.Message}",
                            "Processing Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error initializing filter: {ex.Message}",
                    "Processing Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private byte[] ExtractVolumeData(IGrayscaleVolumeData volume)
        {
            byte[] data = new byte[width * height * depth];
            int index = 0;

            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        data[index++] = volume[x, y, z];
                    }
                }
            }

            return data;
        }

        private void UpdateVolumeWithProcessedData(IGrayscaleVolumeData volume, byte[] data)
        {
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

            // Try to get the volume data from different types of source nodes

            if (sourceNode is CurrentDatasetNode currentDatasetNode)
                return currentDatasetNode.VolumeData;

            if (sourceNode is FilterNode filterNode)
                return filterNode.FilteredVolume;

            // Try to access volume data through reflection for other node types
            var volumeProperty = sourceNode.GetType().GetProperty("FilteredVolume") ??
                                 sourceNode.GetType().GetProperty("VolumeData") ??
                                 sourceNode.GetType().GetProperty("OutputVolume");

            if (volumeProperty != null)
            {
                var value = volumeProperty.GetValue(sourceNode);
                if (value is IGrayscaleVolumeData volume)
                    return volume;
            }

            return null;
        }

        private void CopyVolumeData(IGrayscaleVolumeData source, IGrayscaleVolumeData target)
        {
            if (source == null || target == null) return;

            int w = source.Width;
            int h = source.Height;
            int d = source.Depth;

            for (int z = 0; z < d; z++)
            {
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        target[x, y, z] = source[x, y, z];
                    }
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

        private Control FindNodeEditorForm()
        {
            // Look for the parent NodeEditorForm
            Control parent = null;
            Control current = this.CreatePropertyPanel()?.Parent;

            while (current != null)
            {
                if (current.GetType().Name == "NodeEditorForm")
                {
                    parent = current;
                    break;
                }
                current = current.Parent;
            }

            return parent;
        }

        private void NotifyOutputNodesOfUpdate()
        {
            var connections = GetNodeConnections();
            if (connections == null) return;

            // Find the output pin named "FilteredVolume"
            var outputPin = outputs.FirstOrDefault(p => p.Name == "FilteredVolume");
            if (outputPin == null) return;

            // Find all connections from this output pin
            var connectedNodes = connections
                .Where(c => c.From == outputPin)
                .Select(c => c.To.Node)
                .Distinct()
                .ToList();

            // Execute each connected node
            foreach (var node in connectedNodes)
            {
                Logger.Log($"[FilterNode] Notifying connected node: {node.GetType().Name}");
            }
        }
    }
}
