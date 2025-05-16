using CTS.NodeEditor;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using static MaterialDensityLibrary;

namespace CTS.Modules.NodeEditor.Nodes
{
    public class DensityNode : BaseNode, IMaterialDensityProvider
    {
        // Density parameters
        public double Density { get; set; } = 1000.0; // Default density (water: 1000 kg/m³)
        public string SelectedMaterialName { get; set; } = "Water";
        public Material SelectedMaterial { get; set; } = null;

        // Connected volume properties
        private IGrayscaleVolumeData connectedVolumeData = null;
        public double PixelSize { get; set; } = 0.01; // Default: 0.01mm (10 microns)

        // UI Controls
        private ComboBox comboMaterials;
        private NumericUpDown numDensity;
        private NumericUpDown numPixelSize;
        private Button btnDensitySettings;
        private Label lblConnectedVolume;
        private Label lblCalculatedMass;
        private Button btnSelectRegion;
        private Panel panel; // Reference to main panel for updating labels

        // List of calibration points if using grayscale calibration
        private List<CalibrationPoint> calibrationPoints = new List<CalibrationPoint>();
        private bool usingCalibration = false;

        public DensityNode(Point position) : base(position)
        {
            Color = Color.FromArgb(255, 180, 100); // Orange theme for material nodes

            // Create default material
            SelectedMaterial = new Material(
                SelectedMaterialName,
                Color.LightBlue,
                0, // min
                255, // max
                1, // id
                Density // density
            );
        }

        protected override void SetupPins()
        {
            // Input pins
            AddInputPin("Volume", Color.LightBlue);

            // Output pins
            AddOutputPin("Material", Color.Yellow);  // For density/material info
        }

        public override Control CreatePropertyPanel()
        {
            panel = new Panel
            {
                BackColor = Color.FromArgb(45, 45, 48),
                AutoScroll = true
            };

            // Title
            var titleLabel = new Label
            {
                Text = "Material Density",
                Font = new Font("Arial", 12, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(10, 10),
                ForeColor = Color.White
            };
            panel.Controls.Add(titleLabel);

            int currentY = 40;

            // Connected volume information
            lblConnectedVolume = new Label
            {
                Text = "No volume connected",
                AutoSize = true,
                Location = new Point(10, currentY),
                ForeColor = Color.LightGray
            };
            panel.Controls.Add(lblConnectedVolume);
            currentY += 30;

            // Pixel size input
            var pixelSizeLabel = new Label
            {
                Text = "Pixel Size (mm):",
                AutoSize = true,
                Location = new Point(10, currentY),
                ForeColor = Color.White
            };
            panel.Controls.Add(pixelSizeLabel);
            currentY += 25;

            numPixelSize = new NumericUpDown
            {
                Location = new Point(10, currentY),
                Width = 120,
                Minimum = 0.001m,
                Maximum = 10m,
                DecimalPlaces = 4,
                Value = (decimal)PixelSize,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            numPixelSize.ValueChanged += (s, e) =>
            {
                PixelSize = (double)numPixelSize.Value;
                UpdateCalculatedMass();
            };
            panel.Controls.Add(numPixelSize);
            currentY += 30;

            // Material selection
            var materialLabel = new Label
            {
                Text = "Material:",
                AutoSize = true,
                Location = new Point(10, currentY),
                ForeColor = Color.White
            };
            panel.Controls.Add(materialLabel);
            currentY += 25;

            comboMaterials = new ComboBox
            {
                Location = new Point(10, currentY),
                Width = 200,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                DropDownStyle = ComboBoxStyle.DropDownList,
                DisplayMember = "Name",
                ValueMember = "Density"
            };

            // Fill material dropdown with options from MaterialDensityLibrary
            PopulateMaterialsDropdown();

            comboMaterials.SelectedIndexChanged += (s, e) =>
            {
                if (comboMaterials.SelectedItem is MaterialDensity material)
                {
                    Density = material.Density;
                    SelectedMaterialName = material.Name;
                    numDensity.Value = (decimal)material.Density;

                    // Update the material
                    UpdateSelectedMaterial();

                    // Mark that we're not using calibration anymore
                    usingCalibration = false;
                    UpdateCalculatedMass();
                }
            };
            panel.Controls.Add(comboMaterials);
            currentY += 30;

            // Density input
            var densityLabel = new Label
            {
                Text = "Density (kg/m³):",
                AutoSize = true,
                Location = new Point(10, currentY),
                ForeColor = Color.White
            };
            panel.Controls.Add(densityLabel);
            currentY += 25;

            numDensity = new NumericUpDown
            {
                Location = new Point(10, currentY),
                Width = 120,
                Minimum = 0.1m,
                Maximum = 25000m,
                DecimalPlaces = 1,
                Value = (decimal)Density,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            numDensity.ValueChanged += (s, e) =>
            {
                Density = (double)numDensity.Value;

                // Update material
                UpdateSelectedMaterial();

                // Update mass calculation
                UpdateCalculatedMass();

                // Find and select matching material if any
                SelectMatchingMaterial(Density);
            };
            panel.Controls.Add(numDensity);
            currentY += 35;

            // Calculated mass display
            lblCalculatedMass = new Label
            {
                Text = "Calculated Mass: N/A (no volume connected)",
                AutoSize = true,
                Location = new Point(10, currentY),
                ForeColor = Color.White
            };
            panel.Controls.Add(lblCalculatedMass);
            currentY += 35;

            // Select Region button (for calibration)
            btnSelectRegion = new Button
            {
                Text = "Select Calibration Region",
                Location = new Point(10, currentY),
                Width = 200,
                Height = 30,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                Enabled = connectedVolumeData != null
            };
            btnSelectRegion.Click += BtnSelectRegion_Click;
            panel.Controls.Add(btnSelectRegion);
            currentY += 40;

            // Advanced settings button
            btnDensitySettings = new Button
            {
                Text = "Advanced Density Settings...",
                Location = new Point(10, currentY),
                Width = 200,
                Height = 30,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                Enabled = false // Disabled until volume is connected
            };
            btnDensitySettings.Click += BtnDensitySettings_Click;
            panel.Controls.Add(btnDensitySettings);
            currentY += 50;

            // Calibration status
            var calibrationLabel = new Label
            {
                Text = usingCalibration ?
                    $"Using grayscale calibration ({calibrationPoints.Count} points)" :
                    "Not using grayscale calibration",
                AutoSize = true,
                Location = new Point(10, currentY),
                ForeColor = usingCalibration ? Color.LightGreen : Color.LightGray,
                Font = new Font("Arial", 8)
            };
            panel.Controls.Add(calibrationLabel);

            return panel;
        }

        private void PopulateMaterialsDropdown()
        {
            comboMaterials.Items.Clear();
            foreach (var material in MaterialDensityLibrary.Materials)
            {
                comboMaterials.Items.Add(material);
                if (material.Name == SelectedMaterialName)
                {
                    comboMaterials.SelectedItem = material;
                }
            }

            // If no match found, select first item
            if (comboMaterials.SelectedIndex < 0 && comboMaterials.Items.Count > 0)
            {
                comboMaterials.SelectedIndex = 0;
            }
        }

        private void SelectMatchingMaterial(double density)
        {
            // First try for an exact match
            for (int i = 0; i < comboMaterials.Items.Count; i++)
            {
                if (comboMaterials.Items[i] is MaterialDensity material)
                {
                    if (Math.Abs(material.Density - density) < 0.1)
                    {
                        comboMaterials.SelectedIndex = i;
                        SelectedMaterialName = material.Name;
                        return;
                    }
                }
            }

            // If no exact match, show "Custom" 
            SelectedMaterialName = "Custom";
            comboMaterials.Text = "Custom";
        }

        private void UpdateSelectedMaterial()
        {
            if (SelectedMaterial != null)
            {
                SelectedMaterial.Density = Density;
            }
            else
            {
                // Create a new material if needed
                SelectedMaterial = new Material(
                    SelectedMaterialName,
                    Color.LightBlue,
                    0, // min
                    255, // max
                    1, // id
                    Density // density
                );
            }
        }

        private void UpdateCalculatedMass()
        {
            if (connectedVolumeData != null)
            {
                double volume = CalculateTotalVolume(); // m³
                double mass = volume * Density;  // kg

                lblCalculatedMass.Text = $"Calculated Mass: {mass:F3} kg";
            }
            else
            {
                lblCalculatedMass.Text = "Calculated Mass: N/A (no volume connected)";
            }
        }

        private void BtnSelectRegion_Click(object sender, EventArgs e)
        {
            if (connectedVolumeData == null)
            {
                MessageBox.Show("Please connect a volume first.", "No Volume Connected",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                // Create a temporary MainForm with our connected volume for the calibration preview
                var tempMainForm = new MainForm(new string[0]);
                tempMainForm.volumeData = connectedVolumeData;

                // Open the density calibration preview form
                using (var previewForm = new DensityCalibrationPreviewForm(tempMainForm))
                {
                    if (previewForm.ShowDialog() == DialogResult.OK)
                    {
                        // Process the selected region
                        Rectangle region = previewForm.SelectedRegion;
                        double avgGrayValue = previewForm.AverageGrayValue;

                        // Get selected material from dropdown
                        if (comboMaterials.SelectedItem is MaterialDensity material)
                        {
                            var point = new CalibrationPoint
                            {
                                Region = $"Region {calibrationPoints.Count + 1} [{region.X},{region.Y},{region.Width},{region.Height}]",
                                Material = material.Name,
                                Density = material.Density,
                                AvgGrayValue = avgGrayValue
                            };

                            // Add to calibration points
                            calibrationPoints.Add(point);

                            // If we have at least 2 points, apply the calibration
                            if (calibrationPoints.Count >= 2)
                            {
                                ApplyDensityCalibration(calibrationPoints);

                                // Update the UI to show we're using calibration
                                var calibrationLabel = panel.Controls.OfType<Label>().LastOrDefault();
                                if (calibrationLabel != null)
                                {
                                    calibrationLabel.Text = $"Using grayscale calibration ({calibrationPoints.Count} points)";
                                    calibrationLabel.ForeColor = Color.LightGreen;
                                }
                            }

                            MessageBox.Show($"Added calibration point for {material.Name} with gray value {avgGrayValue:F1}.",
                                "Calibration Point Added", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error selecting region: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnDensitySettings_Click(object sender, EventArgs e)
        {
            if (connectedVolumeData == null)
            {
                MessageBox.Show("Please connect a volume first.", "No Volume Connected",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                // Create a temporary MainForm with our connected volume for the density settings
                var tempMainForm = new MainForm(new string[0]);
                tempMainForm.volumeData = connectedVolumeData;

                // Set the pixel size in the temporary MainForm
                var pixelSizeField = tempMainForm.GetType().GetField("pixelSize",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);

                if (pixelSizeField != null)
                {
                    pixelSizeField.SetValue(tempMainForm, PixelSize);
                }

                // Open density settings form
                using (var densityForm = new DensitySettingsForm(this, tempMainForm))
                {
                    if (densityForm.ShowDialog() == DialogResult.OK)
                    {
                        // Update UI to reflect changes
                        if (!usingCalibration)
                        {
                            numDensity.Value = (decimal)Density;
                            SelectMatchingMaterial(Density);
                        }

                        // Update calculated mass
                        UpdateCalculatedMass();

                        // Update the calibration status label
                        var calibrationLabel = panel.Controls.OfType<Label>().LastOrDefault();
                        if (calibrationLabel != null)
                        {
                            calibrationLabel.Text = usingCalibration ?
                                $"Using grayscale calibration ({calibrationPoints.Count} points)" :
                                "Not using grayscale calibration";
                            calibrationLabel.ForeColor = usingCalibration ? Color.LightGreen : Color.LightGray;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening density settings: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public override void Execute()
        {
            try
            {
                // Get data from connected input nodes
                var inputNodes = GetConnectedInputNodes();
                foreach (var node in inputNodes)
                {
                    // Check for volume data connections
                    if (node.Key == "Volume")
                    {
                        // Get the connected volume data
                        var volumeNode = node.Value;
                        if (volumeNode is LoadDatasetNode loadNode)
                        {
                            // Access the actual volume data from the LoadDatasetNode
                            connectedVolumeData = GetVolumeDataFromNode(loadNode);

                            // Get the pixel size from the load node
                            PixelSize = loadNode.PixelSize;
                            numPixelSize.Value = (decimal)PixelSize;

                            // Update volume info in UI
                            if (connectedVolumeData != null)
                            {
                                lblConnectedVolume.Text = $"Connected Volume: {connectedVolumeData.Width}×{connectedVolumeData.Height}×{connectedVolumeData.Depth}";
                                btnSelectRegion.Enabled = true;
                                btnDensitySettings.Enabled = true;

                                // Update calculated mass
                                UpdateCalculatedMass();
                            }
                        }
                    }
                }

                // Update the selected material
                UpdateSelectedMaterial();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in DensityNode.Execute: {ex.Message}");
            }
        }

        // Helper methods to access connected nodes
        private Dictionary<string, BaseNode> GetConnectedInputNodes()
        {
            var result = new Dictionary<string, BaseNode>();

            // Get the node editor instance
            var nodeEditor = FindNodeEditorForm();
            if (nodeEditor == null) return result;

            // Get connections list
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

        private IGrayscaleVolumeData GetVolumeDataFromNode(LoadDatasetNode node)
        {
            // Access the volume data from the LoadDatasetNode
            // For now, use mainForm's volume data for demonstration
            var mainForm = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
            if (mainForm != null)
            {
                return mainForm.volumeData;
            }

            return null;
        }

        private Control FindNodeEditorForm()
        {
            var mainForm = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
            if (mainForm == null) return null;

            return FindControlOfType(mainForm, "NodeEditorForm");
        }

        private Control FindControlOfType(Control parent, string typeName)
        {
            if (parent.GetType().Name == typeName)
                return parent;

            foreach (Control child in parent.Controls)
            {
                var result = FindControlOfType(child, typeName);
                if (result != null)
                    return result;
            }

            return null;
        }

        #region IMaterialDensityProvider implementation

        public double CalculateTotalVolume()
        {
            if (connectedVolumeData == null) return 0;

            // Calculate volume in m³
            double pixelSizeM = PixelSize / 1000.0; // mm to m
            return connectedVolumeData.Width * connectedVolumeData.Height *
                   connectedVolumeData.Depth * Math.Pow(pixelSizeM, 3);
        }

        public void SetMaterialDensity(double density)
        {
            Density = density;

            // Update the selected material
            UpdateSelectedMaterial();

            // Set not using calibration
            usingCalibration = false;
            calibrationPoints.Clear();
        }

        public void ApplyDensityCalibration(List<CalibrationPoint> points)
        {
            if (points == null || points.Count < 2)
            {
                throw new ArgumentException("At least 2 calibration points are required");
            }

            // Store calibration points
            calibrationPoints = new List<CalibrationPoint>(points);
            usingCalibration = true;

            // Calculate density based on calibration
            var convertedPoints = points.Select(p => new MaterialDensityLibrary.CalibrationPoint
            {
                Region = p.Region,
                Material = p.Material,
                Density = p.Density,
                AvgGrayValue = p.AvgGrayValue
            }).ToList();

            var model = MaterialDensityLibrary.CalculateLinearDensityModel(convertedPoints);
            Density = model.slope * points.Average(p => p.AvgGrayValue) + model.intercept;

            // Update the selected material
            UpdateSelectedMaterial();
        }

        #endregion
    }
}