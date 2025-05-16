using CTS.NodeEditor;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CTS.Modules.NodeEditor.Nodes
{
    public class LabelNode : BaseNode
    {
        private ListBox materialsListBox;
        private Label materialsCountLabel;

        // Internal data that will be managed by this node
        private ILabelVolumeData labelData;
        private List<Material> materials = new List<Material>();
        private bool initialized = false;

        public ILabelVolumeData LabelData => labelData;
        public List<Material> Materials => materials;

        public LabelNode(Point position) : base(position)
        {
            Color = Color.FromArgb(255, 180, 100); // Orange theme for materials

            // Initialize with a default Exterior material
            materials.Add(new Material("Exterior", Color.Transparent, 0, 0, 0) { IsExterior = true });
        }

        protected override void SetupPins()
        {
            AddInputPin("VolumeData", Color.LightBlue);
            AddInputPin("ExistingLabels", Color.LightCoral);
            AddOutputPin("Labels", Color.LightCoral);
            AddOutputPin("Materials", Color.Orange);
        }

        public override Control CreatePropertyPanel()
        {
            var panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48),
                AutoScroll = true
            };

            var titleLabel = new Label
            {
                Text = "Label Dataset",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White,
                Font = new Font("Arial", 10, FontStyle.Bold)
            };

            materialsCountLabel = new Label
            {
                Text = $"Materials: {materials.Count}",
                Dock = DockStyle.Top,
                Height = 25,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            var materialsLabel = new Label
            {
                Text = "Materials List:",
                Dock = DockStyle.Top,
                Height = 25,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White
            };

            materialsListBox = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                DrawMode = DrawMode.OwnerDrawFixed,
                SelectionMode = SelectionMode.One,
                Height = 200
            };

            materialsListBox.DrawItem += MaterialsListBox_DrawItem;

            // Material management buttons panel
            var buttonPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 35,
                BackColor = Color.FromArgb(45, 45, 48)
            };

            // Update button
            var updateButton = new Button
            {
                Text = "Update",
                Width = 70,
                Height = 30,
                Location = new Point(5, 0),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            updateButton.Click += (s, e) => {
                GetInputData();
                RefreshMaterialsList();
            };

            // Rename button
            var renameButton = new Button
            {
                Text = "Rename",
                Width = 70,
                Height = 30,
                Location = new Point(80, 0),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            renameButton.Click += (s, e) => RenameMaterial();

            // Add button
            var addButton = new Button
            {
                Text = "Add",
                Width = 70,
                Height = 30,
                Location = new Point(155, 0),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            addButton.Click += (s, e) => AddNewMaterial();

            // Remove button
            var removeButton = new Button
            {
                Text = "Remove",
                Width = 70,
                Height = 30,
                Location = new Point(230, 0),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            removeButton.Click += (s, e) => RemoveMaterial();

            // Add buttons to panel
            buttonPanel.Controls.Add(updateButton);
            buttonPanel.Controls.Add(renameButton);
            buttonPanel.Controls.Add(addButton);
            buttonPanel.Controls.Add(removeButton);

            // Add controls to main panel
            panel.Controls.Add(materialsListBox);
            panel.Controls.Add(buttonPanel);
            panel.Controls.Add(materialsLabel);
            panel.Controls.Add(materialsCountLabel);
            panel.Controls.Add(titleLabel);

            RefreshMaterialsList();

            return panel;
        }

        private void MaterialsListBox_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || materials == null || e.Index >= materials.Count)
                return;

            e.DrawBackground();
            Material mat = materials[e.Index];

            // Draw the item background with the material color
            if ((e.State & DrawItemState.Selected) == DrawItemState.Selected)
                e.Graphics.FillRectangle(Brushes.LightBlue, e.Bounds);
            else
            {
                using (SolidBrush b = new SolidBrush(mat.Color))
                    e.Graphics.FillRectangle(b, e.Bounds);
            }

            // Choose text color based on material color brightness
            Color textColor = mat.IsExterior ? Color.Red : (mat.Color.GetBrightness() < 0.4f ? Color.White : Color.Black);

            // Draw the material name and info
            using (SolidBrush textBrush = new SolidBrush(textColor))
                e.Graphics.DrawString($"{mat.Name} [ID: {mat.ID}, Range: {mat.Min}-{mat.Max}]",
                    e.Font, textBrush, e.Bounds);

            e.DrawFocusRectangle();
        }

        private void RefreshMaterialsList()
        {
            materialsListBox.Items.Clear();

            if (materials != null)
            {
                for (int i = 0; i < materials.Count; i++)
                {
                    materialsListBox.Items.Add(materials[i]);
                }

                materialsCountLabel.Text = $"Materials: {materials.Count}";
            }
            else
            {
                materialsCountLabel.Text = "Materials: 0";
            }
        }

        private void RenameMaterial()
        {
            if (materialsListBox.SelectedIndex < 0 || materialsListBox.SelectedIndex >= materials.Count)
            {
                MessageBox.Show("Please select a material to rename.",
                    "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Material selectedMaterial = materials[materialsListBox.SelectedIndex];

            // Don't allow renaming the exterior material
            if (selectedMaterial.IsExterior)
            {
                MessageBox.Show("The Exterior material cannot be renamed.",
                    "Cannot Rename", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Show input dialog for new name
            using (var inputForm = new Form())
            {
                inputForm.Width = 300;
                inputForm.Height = 150;
                inputForm.Text = "Rename Material";
                inputForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                inputForm.StartPosition = FormStartPosition.CenterParent;
                inputForm.MaximizeBox = false;
                inputForm.MinimizeBox = false;

                var nameLabel = new Label
                {
                    Text = "New Material Name:",
                    Left = 10,
                    Top = 20,
                    Width = 120
                };

                var nameTextBox = new TextBox
                {
                    Text = selectedMaterial.Name,
                    Left = 130,
                    Top = 20,
                    Width = 150
                };

                var okButton = new Button
                {
                    Text = "OK",
                    Left = 130,
                    Top = 70,
                    Width = 70,
                    DialogResult = DialogResult.OK
                };

                var cancelButton = new Button
                {
                    Text = "Cancel",
                    Left = 210,
                    Top = 70,
                    Width = 70,
                    DialogResult = DialogResult.Cancel
                };

                inputForm.Controls.Add(nameLabel);
                inputForm.Controls.Add(nameTextBox);
                inputForm.Controls.Add(okButton);
                inputForm.Controls.Add(cancelButton);

                inputForm.AcceptButton = okButton;
                inputForm.CancelButton = cancelButton;

                // Show dialog and process result
                if (inputForm.ShowDialog() == DialogResult.OK)
                {
                    string newName = nameTextBox.Text.Trim();

                    if (string.IsNullOrEmpty(newName))
                    {
                        MessageBox.Show("Material name cannot be empty.",
                            "Invalid Name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    // Update the material name
                    selectedMaterial.Name = newName;

                    // Refresh the list to show the updated name
                    RefreshMaterialsList();
                }
            }
        }

        private void AddNewMaterial()
        {
            // Show dialog to configure new material
            using (var inputForm = new Form())
            {
                inputForm.Width = 350;
                inputForm.Height = 220;
                inputForm.Text = "Add New Material";
                inputForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                inputForm.StartPosition = FormStartPosition.CenterParent;
                inputForm.MaximizeBox = false;
                inputForm.MinimizeBox = false;

                var nameLabel = new Label
                {
                    Text = "Material Name:",
                    Left = 10,
                    Top = 20,
                    Width = 100
                };

                var nameTextBox = new TextBox
                {
                    Text = "New Material",
                    Left = 110,
                    Top = 20,
                    Width = 220
                };

                var colorLabel = new Label
                {
                    Text = "Color:",
                    Left = 10,
                    Top = 50,
                    Width = 100
                };

                Panel colorPreview = new Panel
                {
                    Left = 110,
                    Top = 50,
                    Width = 30,
                    Height = 20,
                    BackColor = Color.Blue
                };

                var selectColorButton = new Button
                {
                    Text = "Select Color",
                    Left = 150,
                    Top = 50,
                    Width = 100
                };

                // Min and max for grayscale range
                var minLabel = new Label
                {
                    Text = "Min Value:",
                    Left = 10,
                    Top = 80,
                    Width = 100
                };

                var minUpDown = new NumericUpDown
                {
                    Left = 110,
                    Top = 80,
                    Width = 60,
                    Minimum = 0,
                    Maximum = 254,
                    Value = 0
                };

                var maxLabel = new Label
                {
                    Text = "Max Value:",
                    Left = 180,
                    Top = 80,
                    Width = 100
                };

                var maxUpDown = new NumericUpDown
                {
                    Left = 270,
                    Top = 80,
                    Width = 60,
                    Minimum = 1,
                    Maximum = 255,
                    Value = 255
                };

                // Make sure min <= max
                minUpDown.ValueChanged += (s, e) => {
                    if (minUpDown.Value > maxUpDown.Value)
                        maxUpDown.Value = minUpDown.Value;
                };

                maxUpDown.ValueChanged += (s, e) => {
                    if (maxUpDown.Value < minUpDown.Value)
                        minUpDown.Value = maxUpDown.Value;
                };

                selectColorButton.Click += (s, e) => {
                    using (var colorDialog = new ColorDialog())
                    {
                        colorDialog.Color = colorPreview.BackColor;
                        if (colorDialog.ShowDialog() == DialogResult.OK)
                        {
                            colorPreview.BackColor = colorDialog.Color;
                        }
                    }
                };

                var okButton = new Button
                {
                    Text = "Add Material",
                    Left = 190,
                    Top = 140,
                    Width = 110,
                    Height = 30,
                    DialogResult = DialogResult.OK
                };

                var cancelButton = new Button
                {
                    Text = "Cancel",
                    Left = 60,
                    Top = 140,
                    Width = 110,
                    Height = 30,
                    DialogResult = DialogResult.Cancel
                };

                inputForm.Controls.Add(nameLabel);
                inputForm.Controls.Add(nameTextBox);
                inputForm.Controls.Add(colorLabel);
                inputForm.Controls.Add(colorPreview);
                inputForm.Controls.Add(selectColorButton);
                inputForm.Controls.Add(minLabel);
                inputForm.Controls.Add(minUpDown);
                inputForm.Controls.Add(maxLabel);
                inputForm.Controls.Add(maxUpDown);
                inputForm.Controls.Add(okButton);
                inputForm.Controls.Add(cancelButton);

                inputForm.AcceptButton = okButton;
                inputForm.CancelButton = cancelButton;

                if (inputForm.ShowDialog() == DialogResult.OK)
                {
                    string name = nameTextBox.Text.Trim();
                    if (string.IsNullOrEmpty(name))
                    {
                        MessageBox.Show("Material name cannot be empty.",
                            "Invalid Name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    // Create a new material with the next available ID
                    byte nextID = GetNextMaterialID();
                    Material newMaterial = new Material(
                        name,
                        colorPreview.BackColor,
                        (byte)minUpDown.Value,
                        (byte)maxUpDown.Value,
                        nextID
                    );

                    // Add to the materials list
                    materials.Add(newMaterial);
                    RefreshMaterialsList();
                }
            }
        }

        private void RemoveMaterial()
        {
            if (materialsListBox.SelectedIndex < 0 || materialsListBox.SelectedIndex >= materials.Count)
            {
                MessageBox.Show("Please select a material to remove.",
                    "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Material selectedMaterial = materials[materialsListBox.SelectedIndex];

            // Don't allow removing the exterior material
            if (selectedMaterial.IsExterior)
            {
                MessageBox.Show("The Exterior material cannot be removed.",
                    "Cannot Remove", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Confirm deletion
            if (MessageBox.Show($"Are you sure you want to remove material '{selectedMaterial.Name}'?",
                "Confirm Removal", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                // If we have label data, update it to remove references to this material
                if (labelData != null)
                {
                    RemoveMaterialFromLabelData(selectedMaterial.ID);
                }

                // Remove from list
                materials.Remove(selectedMaterial);
                RefreshMaterialsList();
            }
        }

        private void GetInputData()
        {
            // Get connected nodes
            var connectedNodes = GetConnectedInputNodes();

            // Process each connected node based on pin name
            foreach (var connection in connectedNodes)
            {
                if (connection.Key == "VolumeData")
                {
                    var node = connection.Value;
                    // Process volume data if needed
                }
                else if (connection.Key == "ExistingLabels")
                {
                    var node = connection.Value;

                    // Get label data from the connected node through reflection
                    var labelDataProperty = node.GetType().GetProperty("LabelData");
                    if (labelDataProperty != null)
                    {
                        var inputLabelData = labelDataProperty.GetValue(node) as ILabelVolumeData;

                        if (inputLabelData != null)
                        {
                            labelData = inputLabelData;

                            // Also try to get materials
                            var materialsProperty = node.GetType().GetProperty("Materials");
                            if (materialsProperty != null)
                            {
                                var inputMaterials = materialsProperty.GetValue(node) as List<Material>;
                                if (inputMaterials != null && inputMaterials.Count > 0)
                                {
                                    // Replace our materials with the input materials
                                    materials = new List<Material>(inputMaterials);
                                    initialized = true;
                                }
                            }
                        }
                    }
                }
            }
        }

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

        private byte GetNextMaterialID()
        {
            byte nextID = 1; // Start from 1 since 0 is typically reserved for Exterior

            // Find the highest ID and increment by 1
            foreach (var material in materials)
            {
                if (material.ID >= nextID)
                {
                    nextID = (byte)(material.ID + 1);
                }
            }

            return nextID;
        }

        private void RemoveMaterialFromLabelData(byte materialID)
        {
            // Only proceed if we have valid label data
            if (labelData == null)
                return;

            try
            {
                // Get dimensions
                int width = labelData.Width;
                int height = labelData.Height;
                int depth = labelData.Depth;

                // Process all voxels
                for (int z = 0; z < depth; z++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            // If this voxel has the material ID we're removing,
                            // set it to 0 (exterior/background)
                            if (labelData[x, y, z] == materialID)
                            {
                                labelData[x, y, z] = 0;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[LabelNode] Error removing material from label data: {ex.Message}");
                MessageBox.Show($"Error removing material from label data: {ex.Message}",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public override void Execute()
        {
            // Check if we need to initialize
            if (!initialized)
            {
                GetInputData();
                RefreshMaterialsList();
            }

            // No further processing required - this node primarily passes data
            Logger.Log("[LabelNode] Executed. Label data and materials are available for output.");
        }
    }
}