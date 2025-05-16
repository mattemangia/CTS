using CTS.NodeEditor;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS.Modules.NodeEditor.Nodes
{
    public class BinarizeNode : BaseNode
    {
        // Threshold parameters
        public int ManualThreshold { get; set; } = 128;
        public bool UseOtsu { get; set; } = true;
        public bool InvertOutput { get; set; } = false;
        public string MaterialName { get; set; } = "Segmented Material";
        public Color MaterialColor { get; set; } = Color.Red;

        // UI controls
        private NumericUpDown thresholdNumeric;
        private CheckBox useOtsuCheckbox;
        private CheckBox invertOutputCheckbox;
        private TextBox materialNameTextBox;
        private Button colorPickerButton;

        // Node input/output data
        private IGrayscaleVolumeData inputVolumeData;
        private ILabelVolumeData outputLabelData;
        private List<Material> outputMaterials;

        // Public properties to expose output data to other nodes
        public ILabelVolumeData LabelData => outputLabelData;
        public List<Material> Materials => outputMaterials;

        public BinarizeNode(Point position) : base(position)
        {
            Color = Color.FromArgb(100, 150, 255); // Blue theme for processing nodes

            // Initialize materials list with default exterior material
            outputMaterials = new List<Material>
            {
                new Material("Exterior", Color.Black, 0, 0, 0) { IsExterior = true },
                new Material(MaterialName, MaterialColor, 0, 255, 1)
            };
        }

        protected override void SetupPins()
        {
            AddInputPin("Volume", Color.LightBlue);
            AddOutputPin("Labels", Color.LightCoral);
            AddOutputPin("Materials", Color.Orange);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };

            // Title
            var titleLabel = new Label
            {
                Text = "Binarize (Threshold)",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White,
                Font = new Font("Arial", 10, FontStyle.Bold)
            };

            // Otsu checkbox
            useOtsuCheckbox = new CheckBox
            {
                Text = "Use Otsu Thresholding",
                Checked = UseOtsu,
                Dock = DockStyle.Top,
                Height = 25,
                ForeColor = Color.White
            };
            useOtsuCheckbox.CheckedChanged += (s, e) => {
                UseOtsu = useOtsuCheckbox.Checked;
                thresholdNumeric.Enabled = !UseOtsu;
            };

            // Manual threshold controls
            var thresholdLabel = new Label
            {
                Text = "Manual Threshold:",
                Dock = DockStyle.Top,
                Height = 25,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            thresholdNumeric = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 255,
                Value = ManualThreshold,
                Dock = DockStyle.Top,
                Height = 25,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                Enabled = !UseOtsu
            };
            thresholdNumeric.ValueChanged += (s, e) => ManualThreshold = (int)thresholdNumeric.Value;

            // Invert output checkbox
            invertOutputCheckbox = new CheckBox
            {
                Text = "Invert Output",
                Checked = InvertOutput,
                Dock = DockStyle.Top,
                Height = 25,
                ForeColor = Color.White
            };
            invertOutputCheckbox.CheckedChanged += (s, e) => InvertOutput = invertOutputCheckbox.Checked;

            // Material name controls
            var materialNameLabel = new Label
            {
                Text = "Material Name:",
                Dock = DockStyle.Top,
                Height = 25,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            materialNameTextBox = new TextBox
            {
                Text = MaterialName,
                Dock = DockStyle.Top,
                Height = 25,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            materialNameTextBox.TextChanged += (s, e) => {
                MaterialName = materialNameTextBox.Text;
                // Update the material name in the list
                if (outputMaterials != null && outputMaterials.Count > 1)
                {
                    outputMaterials[1].Name = MaterialName;
                }
            };

            // Material color controls
            var materialColorLabel = new Label
            {
                Text = "Material Color:",
                Dock = DockStyle.Top,
                Height = 25,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            var colorPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = Color.FromArgb(45, 45, 48)
            };

            var colorPreview = new Panel
            {
                Width = 24,
                Height = 24,
                BackColor = MaterialColor,
                Margin = new Padding(3),
                BorderStyle = BorderStyle.FixedSingle
            };

            colorPickerButton = new Button
            {
                Text = "Select Color",
                Location = new Point(30, 0),
                Width = 120,
                Height = 24,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            colorPickerButton.Click += (s, e) => {
                using (var colorDialog = new ColorDialog())
                {
                    colorDialog.Color = MaterialColor;
                    if (colorDialog.ShowDialog() == DialogResult.OK)
                    {
                        MaterialColor = colorDialog.Color;
                        colorPreview.BackColor = MaterialColor;

                        // Update the material color in the list
                        if (outputMaterials != null && outputMaterials.Count > 1)
                        {
                            outputMaterials[1].Color = MaterialColor;
                        }
                    }
                }
            };

            colorPanel.Controls.Add(colorPreview);
            colorPanel.Controls.Add(colorPickerButton);

            // Update data from inputs
            var updateButton = new Button
            {
                Text = "Update Input",
                Dock = DockStyle.Top,
                Height = 30,
                Margin = new Padding(5, 5, 5, 0),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            updateButton.Click += (s, e) => {
                GetInputData();
            };

            // Process button
            var processButton = new Button
            {
                Text = "Binarize Dataset",
                Dock = DockStyle.Top,
                Height = 35,
                Margin = new Padding(5, 10, 5, 0),
                BackColor = Color.FromArgb(100, 180, 100), // Green for process
                ForeColor = Color.White,
                Font = new Font("Arial", 9, FontStyle.Bold)
            };
            processButton.Click += (s, e) => Execute();

            // Add controls to panel (in reverse order because of DockStyle.Top)
            panel.Controls.Add(processButton);
            panel.Controls.Add(updateButton);
            panel.Controls.Add(colorPanel);
            panel.Controls.Add(materialColorLabel);
            panel.Controls.Add(materialNameTextBox);
            panel.Controls.Add(materialNameLabel);
            panel.Controls.Add(invertOutputCheckbox);
            panel.Controls.Add(thresholdNumeric);
            panel.Controls.Add(thresholdLabel);
            panel.Controls.Add(useOtsuCheckbox);
            panel.Controls.Add(titleLabel);

            return panel;
        }

        // Method to get data from input pins
        private void GetInputData()
        {
            // Clear existing input data
            inputVolumeData = null;

            // Get connected nodes
            var connectedNodes = GetConnectedInputNodes();

            // Process each connected node based on pin name
            foreach (var connection in connectedNodes)
            {
                if (connection.Key == "Volume")
                {
                    var node = connection.Value;

                    // Try to get volume data from the connected node through reflection
                    Type nodeType = node.GetType();

                    // First check for direct VolumeData property
                    var volumeDataProperty = nodeType.GetProperty("VolumeData");
                    if (volumeDataProperty != null)
                    {
                        inputVolumeData = volumeDataProperty.GetValue(node) as IGrayscaleVolumeData;
                    }

                    // If not found, try common alternatives
                    if (inputVolumeData == null)
                    {
                        // Try other common property names
                        string[] possiblePropertyNames = { "Volume", "GrayscaleData", "Data" };
                        foreach (var propName in possiblePropertyNames)
                        {
                            var property = nodeType.GetProperty(propName);
                            if (property != null)
                            {
                                inputVolumeData = property.GetValue(node) as IGrayscaleVolumeData;
                                if (inputVolumeData != null)
                                    break;
                            }
                        }
                    }
                }
            }

            if (inputVolumeData != null)
            {
                MessageBox.Show("Successfully retrieved input volume data!",
                    "Input Updated", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show("No valid input volume data found! Make sure a node providing volume data is connected to the Volume input.",
                    "Input Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // Helper method to get nodes connected to input pins
        private Dictionary<string, BaseNode> GetConnectedInputNodes()
        {
            var result = new Dictionary<string, BaseNode>();

            // Get the node editor instance
            var nodeEditor = FindNodeEditorForm();
            if (nodeEditor == null) return result;

            // Get connections list through reflection
            var connectionsField = nodeEditor.GetType().GetField("connections",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (connectionsField == null) return result;

            var connections = connectionsField.GetValue(nodeEditor) as List<NodeConnection>;
            if (connections == null) return result;

            // Find connections to this node's input pins
            foreach (var input in this.inputs)
            {
                foreach (var conn in connections)
                {
                    if (conn.To == input)
                    {
                        // Found a connection to this input
                        result[input.Name] = conn.From.Node;
                        break;
                    }
                }
            }

            return result;
        }

        private Control FindNodeEditorForm()
        {
            // Look for the parent NodeEditorForm
            Control parent = this.GetPropertyPanel()?.Parent;
            while (parent != null && parent.GetType().Name != "NodeEditorForm")
            {
                parent = parent.Parent;
            }
            return parent;
        }

        // Helper method to get the property panel for this node
        private Control GetPropertyPanel()
        {
            // Find the property panel for this node in the NodeEditorForm
            var nodeEditor = FindNodeEditorForm();
            if (nodeEditor == null) return null;

            // Get propertiesPanel through reflection
            var propertiesPanelField = nodeEditor.GetType().GetField("propertiesPanel",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (propertiesPanelField == null) return null;

            return propertiesPanelField.GetValue(nodeEditor) as Control;
        }

        public override async void Execute()
        {
            try
            {
                // Make sure we have input data
                if (inputVolumeData == null)
                {
                    GetInputData();
                }

                if (inputVolumeData == null)
                {
                    MessageBox.Show("No volume data available to binarize. Please connect a node providing volume data.",
                        "Binarize Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Show progress dialog
                using (var progress = new ProgressFormWithProgress("Binarizing dataset..."))
                {
                    progress.Show();

                    try
                    {
                        // Determine threshold value
                        int thresholdValue = ManualThreshold;
                        if (UseOtsu)
                        {
                            // Calculate Otsu threshold
                            thresholdValue = await Task.Run(() => CalculateOtsuThreshold(inputVolumeData, progress));
                            Logger.Log($"[BinarizeNode] Calculated Otsu threshold: {thresholdValue}");
                        }

                        // Binarize the dataset
                        outputLabelData = await Task.Run(() =>
                            BinarizeVolume(inputVolumeData, thresholdValue, InvertOutput, progress));

                        // Create materials list with exterior and segmented material
                        outputMaterials = new List<Material>();

                        // Always add Exterior material (ID 0)
                        outputMaterials.Add(new Material("Exterior", Color.Black, 0, 0, 0) { IsExterior = true });

                        // Add the segmented material (ID 1)
                        outputMaterials.Add(new Material(MaterialName, MaterialColor, 0, 255, 1));

                        MessageBox.Show($"Dataset binarized successfully using threshold value: {thresholdValue}",
                            "Binarize Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to binarize dataset: {ex.Message}",
                            "Binarize Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    finally
                    {
                        progress.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error preparing to binarize dataset: {ex.Message}",
                    "Binarize Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private int CalculateOtsuThreshold(IGrayscaleVolumeData volume, IProgress<int> progress = null)
        {
            // Create histogram
            int[] histogram = new int[256];
            int totalPixels = 0;

            int width = volume.Width;
            int height = volume.Height;
            int depth = volume.Depth;

            // Count slices for progress
            int totalSlices = depth;
            int processedSlices = 0;

            // Build histogram
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        byte value = volume[x, y, z];
                        histogram[value]++;
                        totalPixels++;
                    }
                }

                // Update progress every slice
                processedSlices++;
                if (processedSlices % 5 == 0 && progress != null)
                {
                    progress.Report(processedSlices * 50 / totalSlices); // First half of progress for histogram
                }
            }

            // Compute threshold using Otsu's method
            double sum = 0;
            for (int t = 0; t < 256; t++)
                sum += t * histogram[t];

            double sumB = 0;
            int wB = 0;
            int wF = 0;

            double maxVariance = 0;
            int threshold = 0;

            for (int t = 0; t < 256; t++)
            {
                wB += histogram[t]; // Weight background
                if (wB == 0) continue;

                wF = totalPixels - wB; // Weight foreground
                if (wF == 0) break;

                sumB += t * histogram[t];

                double mB = sumB / wB; // Mean background
                double mF = (sum - sumB) / wF; // Mean foreground

                // Calculate between-class variance
                double variance = wB * wF * (mB - mF) * (mB - mF);

                // Update threshold if variance is higher
                if (variance > maxVariance)
                {
                    maxVariance = variance;
                    threshold = t;
                }

                // Update progress for Otsu calculation
                if (t % 10 == 0 && progress != null)
                {
                    progress.Report(50 + t * 10 / 256); // Second half of progress for Otsu
                }
            }

            progress?.Report(60); // Otsu calculation completed

            return threshold;
        }

        private ChunkedLabelVolume BinarizeVolume(IGrayscaleVolumeData sourceVolume, int threshold, bool invert, IProgress<int> progress = null)
        {
            int width = sourceVolume.Width;
            int height = sourceVolume.Height;
            int depth = sourceVolume.Depth;

            // Create a new label volume
            ChunkedLabelVolume labelVolume = new ChunkedLabelVolume(width, height, depth, 64, false);

            progress?.Report(65); // Start binarization

            // Process the volume
            Parallel.For(0, depth, z =>
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        byte value = sourceVolume[x, y, z];

                        // Apply threshold (with inversion option)
                        byte labelValue;
                        if (invert)
                            labelValue = (byte)(value < threshold ? 1 : 0);
                        else
                            labelValue = (byte)(value >= threshold ? 1 : 0);

                        labelVolume[x, y, z] = labelValue;
                    }
                }

                // Update progress periodically
                if (z % 10 == 0)
                {
                    progress?.Report(65 + (z * 35 / depth)); // Remaining progress (65% to 100%)
                }
            });

            progress?.Report(100); // Completed
            return labelVolume;
        }
    }
}