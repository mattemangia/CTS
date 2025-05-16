using CTS.NodeEditor;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS.Modules.NodeEditor.Nodes
{
    /// <summary>
    /// A class to represent a threshold range and its associated material
    /// </summary>
    public class ThresholdRange
    {
        public int MinValue { get; set; }
        public int MaxValue { get; set; }
        public string MaterialName { get; set; }
        public Color MaterialColor { get; set; }
        public byte MaterialID { get; set; }

        public ThresholdRange(int min, int max, string name, Color color, byte id)
        {
            MinValue = min;
            MaxValue = max;
            MaterialName = name;
            MaterialColor = color;
            MaterialID = id;
        }

        public override string ToString()
        {
            return $"{MinValue}-{MaxValue}: {MaterialName}";
        }

        public bool Contains(byte value)
        {
            return value >= MinValue && value <= MaxValue;
        }
    }

    public class ManualThresholdingNode : BaseNode
    {
        private List<ThresholdRange> thresholdRanges = new List<ThresholdRange>();
        private ListView rangeListView;
        private NumericUpDown minValueInput;
        private NumericUpDown maxValueInput;
        private TextBox materialNameInput;
        private Panel colorPreviewPanel;
        private Color selectedColor = Color.Red;

        // Node input/output data
        private IGrayscaleVolumeData inputVolumeData;
        private ILabelVolumeData outputLabelData;
        private List<Material> outputMaterials = new List<Material>();

        // Public properties to expose output data to other nodes
        public ILabelVolumeData LabelData => outputLabelData;
        public List<Material> Materials => outputMaterials;

        public ManualThresholdingNode(Point position) : base(position)
        {
            Color = Color.FromArgb(120, 180, 255); // Blue theme for processing nodes

            // Add a default range as an example
            thresholdRanges.Add(new ThresholdRange(128, 255, "Material 1", Color.Red, 1));
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
                Text = "Manual Thresholding",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White,
                Font = new Font("Arial", 10, FontStyle.Bold)
            };

            // Create list view for threshold ranges
            rangeListView = new ListView
            {
                Dock = DockStyle.Top,
                Height = 120,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };

            rangeListView.Columns.Add("Range", 80);
            rangeListView.Columns.Add("Material", 120);

            // Populate list view with existing ranges
            RefreshRangeListView();

            // Inputs for new range
            var rangeGroupBox = new GroupBox
            {
                Text = "Add New Range",
                Dock = DockStyle.Top,
                Height = 140,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(45, 45, 48)
            };

            var minLabel = new Label
            {
                Text = "Min Value:",
                Location = new Point(10, 20),
                Size = new Size(70, 20),
                ForeColor = Color.White
            };

            minValueInput = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 254,
                Value = 128,
                Location = new Point(80, 20),
                Size = new Size(60, 20),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };

            var maxLabel = new Label
            {
                Text = "Max Value:",
                Location = new Point(10, 45),
                Size = new Size(70, 20),
                ForeColor = Color.White
            };

            maxValueInput = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 255,
                Value = 255,
                Location = new Point(80, 45),
                Size = new Size(60, 20),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };

            // Ensure min < max
            minValueInput.ValueChanged += (s, e) =>
            {
                if (minValueInput.Value >= maxValueInput.Value)
                    maxValueInput.Value = minValueInput.Value + 1;
            };

            maxValueInput.ValueChanged += (s, e) =>
            {
                if (maxValueInput.Value <= minValueInput.Value)
                    minValueInput.Value = maxValueInput.Value - 1;
            };

            var nameLabel = new Label
            {
                Text = "Material:",
                Location = new Point(10, 70),
                Size = new Size(70, 20),
                ForeColor = Color.White
            };

            materialNameInput = new TextBox
            {
                Text = $"Material {thresholdRanges.Count + 1}",
                Location = new Point(80, 70),
                Size = new Size(100, 20),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };

            var colorLabel = new Label
            {
                Text = "Color:",
                Location = new Point(10, 95),
                Size = new Size(70, 20),
                ForeColor = Color.White
            };

            colorPreviewPanel = new Panel
            {
                Location = new Point(80, 95),
                Size = new Size(20, 20),
                BackColor = selectedColor,
                BorderStyle = BorderStyle.FixedSingle
            };

            var colorButton = new Button
            {
                Text = "...",
                Location = new Point(105, 95),
                Size = new Size(25, 20),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            colorButton.Click += (s, e) =>
            {
                using (var colorDialog = new ColorDialog())
                {
                    colorDialog.Color = selectedColor;
                    if (colorDialog.ShowDialog() == DialogResult.OK)
                    {
                        selectedColor = colorDialog.Color;
                        colorPreviewPanel.BackColor = selectedColor;
                    }
                }
            };

            // Add and remove buttons
            var addButton = new Button
            {
                Text = "Add Range",
                Location = new Point(150, 20),
                Size = new Size(80, 25),
                BackColor = Color.FromArgb(60, 100, 60),
                ForeColor = Color.White
            };
            addButton.Click += (s, e) => AddThresholdRange();

            var removeButton = new Button
            {
                Text = "Remove",
                Location = new Point(150, 50),
                Size = new Size(80, 25),
                BackColor = Color.FromArgb(100, 60, 60),
                ForeColor = Color.White
            };
            removeButton.Click += (s, e) => RemoveSelectedRange();

            // Add controls to the group box
            rangeGroupBox.Controls.Add(minLabel);
            rangeGroupBox.Controls.Add(minValueInput);
            rangeGroupBox.Controls.Add(maxLabel);
            rangeGroupBox.Controls.Add(maxValueInput);
            rangeGroupBox.Controls.Add(nameLabel);
            rangeGroupBox.Controls.Add(materialNameInput);
            rangeGroupBox.Controls.Add(colorLabel);
            rangeGroupBox.Controls.Add(colorPreviewPanel);
            rangeGroupBox.Controls.Add(colorButton);
            rangeGroupBox.Controls.Add(addButton);
            rangeGroupBox.Controls.Add(removeButton);

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
                Text = "Threshold Dataset",
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
            panel.Controls.Add(rangeGroupBox);
            panel.Controls.Add(rangeListView);
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

        private void RefreshRangeListView()
        {
            rangeListView.Items.Clear();
            foreach (var range in thresholdRanges)
            {
                var item = new ListViewItem($"{range.MinValue}-{range.MaxValue}");
                item.SubItems.Add(range.MaterialName);
                item.BackColor = Color.FromArgb(40, range.MaterialColor.R, range.MaterialColor.G, range.MaterialColor.B);
                item.ForeColor = Color.White;
                item.Tag = range;
                rangeListView.Items.Add(item);
            }
        }

        private void AddThresholdRange()
        {
            int min = (int)minValueInput.Value;
            int max = (int)maxValueInput.Value;
            string name = materialNameInput.Text;
            Color color = selectedColor;

            // Calculate next available material ID
            byte id = 1;
            if (thresholdRanges.Any())
                id = (byte)Math.Min(255, thresholdRanges.Max(r => r.MaterialID) + 1);

            var newRange = new ThresholdRange(min, max, name, color, id);
            thresholdRanges.Add(newRange);

            // Update UI
            RefreshRangeListView();

            // Update material name for next entry
            materialNameInput.Text = $"Material {thresholdRanges.Count + 1}";

            // Select a new color for next entry
            selectedColor = GetNextColor();
            colorPreviewPanel.BackColor = selectedColor;
        }

        private void RemoveSelectedRange()
        {
            if (rangeListView.SelectedItems.Count > 0)
            {
                var range = rangeListView.SelectedItems[0].Tag as ThresholdRange;
                if (range != null)
                {
                    thresholdRanges.Remove(range);
                    RefreshRangeListView();
                }
            }
        }

        private Color GetNextColor()
        {
            // Generate a new color that's different from existing ones
            Random random = new Random();

            // Predefined color list for better visual distinction
            Color[] predefinedColors = new Color[]
            {
                Color.Red, Color.Blue, Color.Green, Color.Yellow, Color.Purple,
                Color.Orange, Color.Cyan, Color.Magenta, Color.Brown, Color.Pink
            };

            if (thresholdRanges.Count < predefinedColors.Length)
                return predefinedColors[thresholdRanges.Count % predefinedColors.Length];

            // If we've used all predefined colors, generate a random one
            return Color.FromArgb(
                random.Next(100, 255),
                random.Next(100, 255),
                random.Next(100, 255));
        }

        public override async void Execute()
        {
            try
            {
                // Check if we have ranges defined
                if (thresholdRanges.Count == 0)
                {
                    MessageBox.Show("Please add at least one threshold range before processing.",
                        "No Ranges Defined", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Make sure we have input data
                if (inputVolumeData == null)
                {
                    GetInputData();
                }

                if (inputVolumeData == null)
                {
                    MessageBox.Show("No volume data available to threshold. Please connect a node providing volume data.",
                        "Thresholding Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Show progress dialog
                using (var progress = new ProgressFormWithProgress("Applying threshold ranges..."))
                {
                    progress.Show();

                    try
                    {
                        // Process the dataset with the defined threshold ranges
                        outputLabelData = await Task.Run(() =>
                            ApplyThresholds(inputVolumeData, thresholdRanges, progress));

                        // Create materials list
                        outputMaterials = new List<Material>();

                        // Always add Exterior material (ID 0)
                        outputMaterials.Add(new Material("Exterior", Color.Black, 0, 0, 0) { IsExterior = true });

                        // Add the segmented materials
                        foreach (var range in thresholdRanges)
                        {
                            outputMaterials.Add(new Material(
                                range.MaterialName,
                                range.MaterialColor,
                                (byte)range.MinValue,
                                (byte)range.MaxValue,
                                range.MaterialID));
                        }

                        MessageBox.Show($"Dataset segmented successfully with {thresholdRanges.Count} materials.",
                            "Thresholding Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to threshold dataset: {ex.Message}",
                            "Thresholding Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    finally
                    {
                        progress.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error preparing to threshold dataset: {ex.Message}",
                    "Thresholding Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private ChunkedLabelVolume ApplyThresholds(IGrayscaleVolumeData sourceVolume,
            List<ThresholdRange> ranges, IProgress<int> progress = null)
        {
            int width = sourceVolume.Width;
            int height = sourceVolume.Height;
            int depth = sourceVolume.Depth;

            // Create a new label volume
            ChunkedLabelVolume labelVolume = new ChunkedLabelVolume(width, height, depth, 64, false);

            // Sort ranges for faster processing
            var sortedRanges = ranges.OrderBy(r => r.MinValue).ToList();

            // Process the volume
            Parallel.For(0, depth, z =>
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        byte value = sourceVolume[x, y, z];
                        byte labelValue = 0; // Default to exterior (0)

                        // Find the first range that contains this value
                        foreach (var range in sortedRanges)
                        {
                            if (range.Contains(value))
                            {
                                labelValue = range.MaterialID;
                                break;
                            }
                        }

                        labelVolume[x, y, z] = labelValue;
                    }
                }

                // Update progress periodically
                if (z % 10 == 0)
                {
                    progress?.Report((z * 100 / depth));
                }
            });

            progress?.Report(100); // Completed
            return labelVolume;
        }
    }
}