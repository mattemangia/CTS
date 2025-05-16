using CTS.NodeEditor;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS.Modules.NodeEditor.Nodes
{
    public class ExtractMaterialsNode : BaseNode
    {
        private CheckedListBox materialSelectionList;
        private List<Material> selectedMaterials = new List<Material>();
        private ILabelVolumeData inputLabelData;
        private List<Material> inputMaterials;
        private byte defaultBackgroundID = 0; // Default to Exterior material ID (usually 0)

        // Output data that will be passed to connected nodes
        private ILabelVolumeData outputLabelData;
        private List<Material> outputMaterials;

        public ExtractMaterialsNode(Point position) : base(position)
        {
            Color = Color.FromArgb(180, 100, 255); // Purple theme for extraction
        }

        protected override void SetupPins()
        {
            AddInputPin("Labels", Color.LightCoral);
            AddInputPin("Materials", Color.Orange);
            AddOutputPin("ExtractedLabels", Color.LightCoral);
            AddOutputPin("ExtractedMaterials", Color.Orange);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48)
            };

            var titleLabel = new Label
            {
                Text = "Extract Materials",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White,
                Font = new Font("Arial", 10, FontStyle.Bold)
            };

            var descriptionLabel = new Label
            {
                Text = "Select materials to extract:",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            materialSelectionList = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                CheckOnClick = true,
                Height = 200
            };

            materialSelectionList.ItemCheck += (s, e) => {
                // Handle in a delayed fashion since the CheckedItems
                // collection hasn't been updated yet when this event fires
                panel.BeginInvoke(new Action(() => UpdateSelectedMaterials()));
            };

            var backgroundLabel = new Label
            {
                Text = "Background Material:",
                Dock = DockStyle.Top,
                Height = 25,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            var backgroundComboBox = new ComboBox
            {
                Dock = DockStyle.Top,
                Height = 25,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            backgroundComboBox.SelectedIndexChanged += (s, e) => {
                if (backgroundComboBox.SelectedItem is Material material)
                {
                    defaultBackgroundID = material.ID;
                }
            };

            // The backgroundComboBox will be populated in the RefreshMaterialsList method

            var refreshButton = new Button
            {
                Text = "Update Materials List",
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            refreshButton.Click += (s, e) => {
                // Fetch data from input pins instead of MainForm
                GetInputData();
                RefreshMaterialsList(inputMaterials);
                UpdateBackgroundComboBox(backgroundComboBox, inputMaterials);
            };

            var processButton = new Button
            {
                Text = "Extract Materials",
                Dock = DockStyle.Bottom,
                Height = 35,
                BackColor = Color.FromArgb(100, 180, 100), // Green for action
                ForeColor = Color.White,
                Font = new Font("Arial", 9, FontStyle.Bold)
            };
            processButton.Click += (s, e) => Execute();

            // Add controls to panel
            panel.Controls.Add(materialSelectionList);
            panel.Controls.Add(refreshButton);
            panel.Controls.Add(backgroundComboBox);
            panel.Controls.Add(backgroundLabel);
            panel.Controls.Add(descriptionLabel);
            panel.Controls.Add(titleLabel);
            panel.Controls.Add(processButton);

            return panel;
        }

        private void UpdateSelectedMaterials()
        {
            selectedMaterials.Clear();
            foreach (Material material in materialSelectionList.CheckedItems)
            {
                selectedMaterials.Add(material);
            }

            Logger.Log($"[ExtractMaterialsNode] Selected {selectedMaterials.Count} materials for extraction");
        }

        private void RefreshMaterialsList(List<Material> materials)
        {
            materialSelectionList.Items.Clear();

            if (materials != null)
            {
                foreach (var material in materials)
                {
                    // Add all non-exterior materials to the list by default
                    if (!material.IsExterior)
                    {
                        materialSelectionList.Items.Add(material, false);
                    }
                }
            }
        }

        private void UpdateBackgroundComboBox(ComboBox comboBox, List<Material> materials)
        {
            comboBox.Items.Clear();

            if (materials != null && materials.Count > 0)
            {
                foreach (var material in materials)
                {
                    comboBox.Items.Add(material);
                    if (material.IsExterior)
                    {
                        comboBox.SelectedItem = material;
                        defaultBackgroundID = material.ID;
                    }
                }

                if (comboBox.SelectedIndex == -1 && comboBox.Items.Count > 0)
                {
                    comboBox.SelectedIndex = 0;
                    defaultBackgroundID = ((Material)comboBox.SelectedItem).ID;
                }
            }
        }

        // Method to get data from input pins
        private void GetInputData()
        {
            // Clear existing data
            inputLabelData = null;
            inputMaterials = null;

            // Get connected nodes
            var connectedNodes = GetConnectedInputNodes();

            // Process each connected node based on pin name
            foreach (var connection in connectedNodes)
            {
                if (connection.Key == "Labels")
                {
                    var node = connection.Value;

                    // Get label data from the connected node through reflection
                    var labelDataProperty = node.GetType().GetProperty("LabelData");
                    if (labelDataProperty != null)
                    {
                        inputLabelData = labelDataProperty.GetValue(node) as ILabelVolumeData;
                    }
                }
                else if (connection.Key == "Materials")
                {
                    var node = connection.Value;

                    // Get materials from the connected node through reflection
                    var materialsProperty = node.GetType().GetProperty("Materials");
                    if (materialsProperty != null)
                    {
                        inputMaterials = materialsProperty.GetValue(node) as List<Material>;
                    }
                }
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
                // Fetch data from input pins
                GetInputData();

                if (inputLabelData == null)
                {
                    MessageBox.Show("No label dataset available. Please connect a node providing label data.",
                        "Execution Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (inputMaterials == null || inputMaterials.Count == 0)
                {
                    MessageBox.Show("No materials available. Please connect a node providing material data.",
                        "Execution Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (selectedMaterials.Count == 0)
                {
                    MessageBox.Show("Please select at least one material to extract.",
                        "No Materials Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Set up a progress form
                using (var progress = new ProgressFormWithProgress("Extracting materials..."))
                {
                    progress.Show();

                    try
                    {
                        // Create a map of material IDs: old ID → new ID
                        MaterialMappingResult mappingResult = await Task.Run(() => CreateMaterialMapping());

                        // Create a new label volume with only the selected materials
                        outputLabelData = await Task.Run(() =>
                            ExtractMaterialsFromVolume(inputLabelData, mappingResult.MaterialIdMapping, progress));

                        // Store the new materials list
                        outputMaterials = mappingResult.NewMaterials;

                        MessageBox.Show("Materials extracted successfully!",
                            "Extraction Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);

                        Logger.Log("[ExtractMaterialsNode] Materials extraction completed successfully");
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to extract materials: {ex.Message}",
                            "Extraction Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Logger.Log($"[ExtractMaterialsNode] Error extracting materials: {ex.Message}");
                    }
                    finally
                    {
                        progress.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error preparing to extract materials: {ex.Message}",
                    "Execution Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[ExtractMaterialsNode] Error in Execute: {ex.Message}");
            }
        }

        // Add property to expose extracted data to other nodes
        public ILabelVolumeData LabelData => outputLabelData;
        public List<Material> Materials => outputMaterials;

        private class MaterialMappingResult
        {
            public byte[] MaterialIdMapping { get; set; }
            public List<Material> NewMaterials { get; set; }
        }

        private MaterialMappingResult CreateMaterialMapping()
        {
            // Create a mapping from old material IDs to new material IDs
            byte[] mapping = new byte[256]; // Assuming material IDs are bytes (0-255)

            // By default, map everything to background
            for (int i = 0; i < mapping.Length; i++)
            {
                mapping[i] = defaultBackgroundID;
            }

            // Create a new list of materials containing only the selected ones plus background
            List<Material> newMaterials = new List<Material>();

            // Always include the background (exterior) material
            Material backgroundMaterial = inputMaterials.FirstOrDefault(m => m.ID == defaultBackgroundID);
            if (backgroundMaterial != null)
            {
                newMaterials.Add(backgroundMaterial);
            }
            else
            {
                // If background material not found, create a default one
                backgroundMaterial = new Material("Exterior", Color.Black, 0, 0, defaultBackgroundID) { IsExterior = true };
                newMaterials.Add(backgroundMaterial);
            }

            // Add selected materials to the new list
            byte newId = 1; // Start from 1 as 0 is typically reserved for Exterior
            foreach (var material in selectedMaterials)
            {
                // Skip if it's the background material
                if (material.ID == defaultBackgroundID)
                    continue;

                // Create a new material with a new ID
                Material newMaterial = new Material(
                    material.Name,
                    material.Color,
                    material.Min,
                    material.Max,
                    newId
                );

                // Map the old ID to the new ID
                mapping[material.ID] = newId;

                // Add to the new materials list
                newMaterials.Add(newMaterial);

                // Increment for the next material
                newId++;
            }

            return new MaterialMappingResult
            {
                MaterialIdMapping = mapping,
                NewMaterials = newMaterials
            };
        }

        private ChunkedLabelVolume ExtractMaterialsFromVolume(ILabelVolumeData source, byte[] mapping, IProgress<int> progress)
        {
            // Get dimensions from the source volume
            int width = source.Width;
            int height = source.Height;
            int depth = source.Depth;

            // Create a new label volume with the same dimensions
            // Using the constructor for ChunkedLabelVolume
            ChunkedLabelVolume result = new ChunkedLabelVolume(
                width,
                height,
                depth,
                64, // Default chunk dimension (typical chunk size)
                false, // Not using memory mapping
                null // No file path needed for in-memory mode
            );

            // Process all slices
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        // Get the original material ID
                        byte originalId = source[x, y, z];

                        // Map to the new ID (using the mapping array)
                        byte newId = mapping[originalId];

                        // Set the new ID in the result volume
                        result[x, y, z] = newId;
                    }
                }

                // Report progress
                progress?.Report((z + 1) * 100 / depth);
            }

            return result;
        }
    }
}