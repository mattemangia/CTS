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
    public class BrightnessContrastNode : BaseNode
    {
        // Adjustment parameters
        public int Brightness { get; set; } = 0;
        public int Contrast { get; set; } = 100;
        public byte BlackPoint { get; set; } = 0;
        public byte WhitePoint { get; set; } = 255;

        // UI controls
        private NumericUpDown brightnessNumeric;
        private NumericUpDown contrastNumeric;
        private NumericUpDown blackPointNumeric;
        private NumericUpDown whitePointNumeric;

        // Node input/output data
        private IGrayscaleVolumeData inputVolumeData;
        private IGrayscaleVolumeData outputVolumeData;

        // Public property to expose output data to other nodes
        public IGrayscaleVolumeData VolumeData => outputVolumeData;

        public BrightnessContrastNode(Point position) : base(position)
        {
            Color = Color.FromArgb(100, 150, 255); // Blue theme for processing nodes
        }

        protected override void SetupPins()
        {
            AddInputPin("Volume", Color.LightBlue);
            AddOutputPin("Volume", Color.LightBlue);
        }
        public override Dictionary<string, string> GetNodeParameters()
        {
            var parameters = new Dictionary<string, string>
            {
                ["Brightness"] = Brightness.ToString(),
                ["Contrast"] = Contrast.ToString(),
                ["BlackPoint"] = BlackPoint.ToString(),
                ["WhitePoint"] = WhitePoint.ToString()
            };
            return parameters;
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
                Text = "Brightness & Contrast",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White,
                Font = new Font("Arial", 10, FontStyle.Bold)
            };

            // Brightness controls
            var brightnessLabel = new Label
            {
                Text = "Brightness:",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            brightnessNumeric = new NumericUpDown
            {
                Minimum = -128,
                Maximum = 128,
                Value = Brightness,
                Dock = DockStyle.Top,
                Height = 25,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            brightnessNumeric.ValueChanged += (s, e) => Brightness = (int)brightnessNumeric.Value;

            // Contrast controls
            var contrastLabel = new Label
            {
                Text = "Contrast:",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            contrastNumeric = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 200,
                Value = Contrast,
                Dock = DockStyle.Top,
                Height = 25,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            contrastNumeric.ValueChanged += (s, e) => Contrast = (int)contrastNumeric.Value;

            // Black point controls
            var blackPointLabel = new Label
            {
                Text = "Black Point:",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            blackPointNumeric = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 254,
                Value = BlackPoint,
                Dock = DockStyle.Top,
                Height = 25,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            blackPointNumeric.ValueChanged += (s, e) => {
                BlackPoint = (byte)blackPointNumeric.Value;
                // Ensure blackPoint < whitePoint
                if (BlackPoint >= WhitePoint)
                {
                    WhitePoint = (byte)Math.Min(255, BlackPoint + 1);
                    whitePointNumeric.Value = WhitePoint;
                }
            };

            // White point controls
            var whitePointLabel = new Label
            {
                Text = "White Point:",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            whitePointNumeric = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 255,
                Value = WhitePoint,
                Dock = DockStyle.Top,
                Height = 25,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            whitePointNumeric.ValueChanged += (s, e) => {
                WhitePoint = (byte)whitePointNumeric.Value;
                // Ensure blackPoint < whitePoint
                if (BlackPoint >= WhitePoint)
                {
                    BlackPoint = (byte)Math.Max(0, WhitePoint - 1);
                    blackPointNumeric.Value = BlackPoint;
                }
            };

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

            var processButton = new Button
            {
                Text = "Process Dataset",
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
            panel.Controls.Add(whitePointNumeric);
            panel.Controls.Add(whitePointLabel);
            panel.Controls.Add(blackPointNumeric);
            panel.Controls.Add(blackPointLabel);
            panel.Controls.Add(contrastNumeric);
            panel.Controls.Add(contrastLabel);
            panel.Controls.Add(brightnessNumeric);
            panel.Controls.Add(brightnessLabel);
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
                    MessageBox.Show("No volume data available to process. Please connect a node providing volume data.",
                        "Process Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Show progress dialog
                using (var progress = new ProgressFormWithProgress("Applying brightness and contrast adjustments..."))
                {
                    progress.Show();

                    // Use the progress form directly as IProgress<int>
                    try
                    {
                        // Process the dataset with current adjustment settings
                        outputVolumeData = await Task.Run(() =>
                            ProcessVolumeHeadless(
                                inputVolumeData,
                                Brightness,
                                Contrast,
                                BlackPoint,
                                WhitePoint,
                                progress)); // Pass progress directly as IProgress<int>

                        MessageBox.Show("Dataset processed successfully with brightness/contrast adjustments.",
                            "Process Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to process dataset: {ex.Message}",
                            "Process Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    finally
                    {
                        progress.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error preparing to process dataset: {ex.Message}",
                    "Process Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Process a volume with brightness/contrast adjustments
        private ChunkedVolume ProcessVolumeHeadless(
            IGrayscaleVolumeData inputVolume,
            int brightness,
            int contrast,
            byte blackPoint,
            byte whitePoint,
            IProgress<int> progress = null)
        {
            // Create a new volume to hold the adjusted data
            int width = inputVolume.Width;
            int height = inputVolume.Height;
            int depth = inputVolume.Depth;

            // Use 64 as the default chunk dimension if we can't determine it from the input
            int chunkDim = 64;

            // Try to get the chunk dimension from the input if it's a ChunkedVolume
            if (inputVolume is ChunkedVolume chunkedInput)
            {
                chunkDim = chunkedInput.ChunkDim;
            }

            ChunkedVolume newVolume = new ChunkedVolume(width, height, depth, chunkDim);

            // Process all slices
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        byte origValue = inputVolume[x, y, z];

                        // Apply adjustment using the same algorithm from BrightnessContrastForm
                        int adjustedValue = ApplyAdjustment(origValue, blackPoint, whitePoint, brightness, contrast);
                        newVolume[x, y, z] = (byte)Math.Max(0, Math.Min(255, adjustedValue));
                    }
                }

                // Report progress if requested
                progress?.Report((z + 1) * 100 / depth);
            }

            Logger.Log($"[BrightnessContrastNode] Processed volume with brightness={brightness}, contrast={contrast}, blackPoint={blackPoint}, whitePoint={whitePoint}");
            return newVolume;
        }

        // Implement the same adjustment algorithm used in BrightnessContrastForm
        private int ApplyAdjustment(byte value, byte bPoint, byte wPoint, int bright, int cont)
        {
            // Map the value from [blackPoint, whitePoint] to [0, 255]
            double normalized = 0;
            if (wPoint > bPoint)
            {
                normalized = (value - bPoint) / (double)(wPoint - bPoint);
            }
            normalized = Math.Max(0, Math.Min(1, normalized));

            // Apply contrast (percentage)
            double contrasted = (normalized - 0.5) * (cont / 100.0) + 0.5;
            contrasted = Math.Max(0, Math.Min(1, contrasted));

            // Apply brightness (offset)
            int result = (int)(contrasted * 255) + bright;
            return result;
        }
    }
}