using CTS;
using Krypton.Toolkit;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using static MaterialDensityLibrary;

namespace CTS
{
    public class DensitySettingsForm : KryptonForm
    {
        private IMaterialDensityProvider parentForm;
        private MainForm mainForm;
        private double totalVolume; // in m³
        private double currentDensity; // in kg/m³

        // UI Controls
        private KryptonRadioButton rbMass;
        private KryptonRadioButton rbDensity;
        private KryptonRadioButton rbCalibration;

        // Mass input controls
        private KryptonNumericUpDown numMass;
        private KryptonLabel lblCalculatedDensity;

        // Direct density input controls
        private KryptonNumericUpDown numDensity;

        // Calibration controls
        private KryptonDataGridView gridCalibration;
        private KryptonButton btnAddCalibration;
        private KryptonButton btnRemoveCalibration;
        private KryptonButton btnBoxSelect;
        private ComboBox comboMaterials;

        // OK/Cancel buttons
        private KryptonButton btnOK;
        private KryptonButton btnCancel;

        // Calibration data
        private List<CalibrationPoint> calibrationPoints = new List<CalibrationPoint>();
        private bool isSelectionMode = false;

        public DensitySettingsForm(IMaterialDensityProvider parent, MainForm mainForm)
        {
            this.parentForm = parent;
            this.mainForm = mainForm;
            this.Text = "Density Settings";
            this.Size = new Size(600, 800);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ControlBox = true; // Ensure window control buttons are visible

            // Calculate total volume
            totalVolume = parentForm.CalculateTotalVolume();

            // Initialize currentDensity with a safe default value if material density is 0
            currentDensity = parentForm.SelectedMaterial?.Density ?? 0;
            if (currentDensity <= 0)
            {
                currentDensity = 1.0; // Default to 1.0 kg/m³ if density is 0 or negative
            }

            InitializeControls();
        }

        private void InitializeControls()
        {
            // Panel to contain all controls
            KryptonPanel mainPanel = new KryptonPanel();
            mainPanel.Dock = DockStyle.Fill;

            // Calculate from Mass option
            rbMass = new KryptonRadioButton
            {
                Text = "Calculate from Mass",
                Location = new Point(20, 20),
                Checked = true
            };

            // Mass Input group box
            KryptonGroupBox massGroup = new KryptonGroupBox
            {
                Text = "Mass Input",
                Location = new Point(40, 45),
                Size = new Size(520, 110)
            };

            // Mass input controls
            KryptonLabel lblMass = new KryptonLabel
            {
                Text = "Sample Mass (g):",
                Location = new Point(20, 25)
            };

            numMass = new KryptonNumericUpDown
            {
                Location = new Point(150, 20),
                Width = 100,
                Minimum = 0.001M,
                Maximum = 10000M,
                DecimalPlaces = 3,
                Value = 1.0M,
                Increment = 0.1M
            };
            numMass.ValueChanged += NumMass_ValueChanged;

            KryptonLabel lblVolume = new KryptonLabel
            {
                Text = $"Total Volume: {totalVolume * 1e6:F3} cm³",
                Location = new Point(300, 25)
            };

            lblCalculatedDensity = new KryptonLabel
            {
                Text = $"Calculated Density: {(double)numMass.Value / (totalVolume * 1000):F2} g/cm³",
                Location = new Point(20, 55)
            };

            massGroup.Panel.Controls.Add(lblMass);
            massGroup.Panel.Controls.Add(numMass);
            massGroup.Panel.Controls.Add(lblVolume);
            massGroup.Panel.Controls.Add(lblCalculatedDensity);

            // Direct Density option
            rbDensity = new KryptonRadioButton
            {
                Text = "Directly Set Density",
                Location = new Point(20, 165)
            };

            // Direct Density Input group box
            KryptonGroupBox densityGroup = new KryptonGroupBox
            {
                Text = "Direct Density Input",
                Location = new Point(40, 185),
                Size = new Size(520, 70)
            };

            // Direct density controls
            KryptonLabel lblDirectDensity = new KryptonLabel
            {
                Text = "Density (kg/m³):",
                Location = new Point(20, 15)
            };

            numDensity = new KryptonNumericUpDown
            {
                Location = new Point(150, 15),
                Width = 100,
                Minimum = 0.1M,
                Maximum = 20000M,
                DecimalPlaces = 1,
                Value = (decimal)Math.Max(0.1, currentDensity),
                Increment = 10M
            };

            densityGroup.Panel.Controls.Add(lblDirectDensity);
            densityGroup.Panel.Controls.Add(numDensity);

            // Grayscale Calibration option
            rbCalibration = new KryptonRadioButton
            {
                Text = "Grayscale Calibration",
                Location = new Point(20, 260)
            };

            // Grayscale Calibration group box
            KryptonGroupBox calibrationGroup = new KryptonGroupBox
            {
                Text = "Grayscale Calibration",
                Location = new Point(40, 285),
                Size = new Size(520, 180)
            };

            // Calibration grid
            gridCalibration = new KryptonDataGridView
            {
                Location = new Point(20, 20),
                Size = new Size(480, 100),
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };

            gridCalibration.Columns.Add("Region", "Region");
            gridCalibration.Columns.Add("Material", "Material");
            gridCalibration.Columns.Add("Density", "Density (kg/m³)");
            gridCalibration.Columns.Add("AvgGrayValue", "Avg. Gray Value");
            gridCalibration.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

            // Calibration buttons
            btnAddCalibration = new KryptonButton
            {
                Text = "Add Region",
                Location = new Point(20, 130),
                Width = 100,
                Values = { Image = CreateAddIcon(16) }
            };
            btnAddCalibration.Click += BtnAddCalibration_Click;

            btnRemoveCalibration = new KryptonButton
            {
                Text = "Remove",
                Location = new Point(130, 130),
                Width = 80,
                Values = { Image = CreateRemoveIcon(16) }
            };
            btnRemoveCalibration.Click += BtnRemoveCalibration_Click;

            btnBoxSelect = new KryptonButton
            {
                Text = "Select Region",
                Location = new Point(220, 130),
                Width = 120,
                Values = { Image = CreateSelectIcon(16) }
            };
            btnBoxSelect.Click += BtnBoxSelect_Click;

            comboMaterials = new ComboBox
            {
                Location = new Point(350, 130),
                Width = 150,
                FlatStyle= FlatStyle.Flat,
                BackColor = Color.DarkGray,
                ForeColor= Color.LightCyan,
                DropDownStyle = ComboBoxStyle.DropDownList,
                DropDownWidth = 250, // Make dropdown wider to show full material names
                DropDownHeight = 200, // Ensure enough height for the dropdown
                DisplayMember = "Name",
                ValueMember = "Density" // Add ValueMember property
            };

            // Fill combobox with materials
            comboMaterials.DataSource = null; // Clear any existing binding
            comboMaterials.Items.Clear();     // Clear any existing items
            List<MaterialDensity> materialsList = new List<MaterialDensity>(MaterialDensityLibrary.Materials);
            comboMaterials.DataSource = materialsList;

            if (comboMaterials.Items.Count > 0)
                comboMaterials.SelectedIndex = 0;

            // Add controls to calibration group
            calibrationGroup.Panel.Controls.Add(gridCalibration);
            calibrationGroup.Panel.Controls.Add(btnAddCalibration);
            calibrationGroup.Panel.Controls.Add(btnRemoveCalibration);
            calibrationGroup.Panel.Controls.Add(btnBoxSelect);
            calibrationGroup.Panel.Controls.Add(comboMaterials);

            // OK/Cancel buttons - placed at the bottom
            btnOK = new KryptonButton
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(400, 480),
                Width = 75,
                Values = { Image = CreateOkIcon(16) }
            };
            btnOK.Click += BtnOK_Click;

            btnCancel = new KryptonButton
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(485, 480),
                Width = 75,
                Values = { Image = CreateCancelIcon(16) }
            };

            // Add controls to the main panel
            mainPanel.Controls.Add(rbMass);
            mainPanel.Controls.Add(massGroup);
            mainPanel.Controls.Add(rbDensity);
            mainPanel.Controls.Add(densityGroup);
            mainPanel.Controls.Add(rbCalibration);
            mainPanel.Controls.Add(calibrationGroup);
            mainPanel.Controls.Add(btnOK);
            mainPanel.Controls.Add(btnCancel);

            // Add the main panel to the form
            this.Controls.Add(mainPanel);

            // Set form size to accommodate all controls
            this.Size = new Size(600, 580);

            // Wire up radio button events
            rbMass.CheckedChanged += RadioButton_CheckedChanged;
            rbDensity.CheckedChanged += RadioButton_CheckedChanged;
            rbCalibration.CheckedChanged += RadioButton_CheckedChanged;

            // Initial state
            SetControlStates();
        }

        // Helper methods to create icons
        private Image CreateAddIcon(int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (Pen pen = new Pen(Color.Green, 2))
                {
                    // Draw a plus sign
                    g.DrawLine(pen, size / 2, 2, size / 2, size - 2);
                    g.DrawLine(pen, 2, size / 2, size - 2, size / 2);
                }
            }
            return bmp;
        }

        private Image CreateRemoveIcon(int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (Pen pen = new Pen(Color.Red, 2))
                {
                    // Draw an X
                    g.DrawLine(pen, 2, 2, size - 2, size - 2);
                    g.DrawLine(pen, size - 2, 2, 2, size - 2);
                }
            }
            return bmp;
        }

        private Image CreateSelectIcon(int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);

                // Draw a selection rectangle
                Rectangle rect = new Rectangle(2, 2, size - 4, size - 4);
                using (Pen pen = new Pen(Color.DodgerBlue, 1))
                {
                    g.DrawRectangle(pen, rect);

                    // Draw diagonal lines to indicate selection
                    g.DrawLine(pen, rect.Left, rect.Top, rect.Left + 3, rect.Top);
                    g.DrawLine(pen, rect.Left, rect.Top, rect.Left, rect.Top + 3);

                    g.DrawLine(pen, rect.Right, rect.Top, rect.Right - 3, rect.Top);
                    g.DrawLine(pen, rect.Right, rect.Top, rect.Right, rect.Top + 3);

                    g.DrawLine(pen, rect.Left, rect.Bottom, rect.Left + 3, rect.Bottom);
                    g.DrawLine(pen, rect.Left, rect.Bottom, rect.Left, rect.Bottom - 3);

                    g.DrawLine(pen, rect.Right, rect.Bottom, rect.Right - 3, rect.Bottom);
                    g.DrawLine(pen, rect.Right, rect.Bottom, rect.Right, rect.Bottom - 3);
                }
            }
            return bmp;
        }

        private Image CreateOkIcon(int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (Pen pen = new Pen(Color.Green, 2))
                {
                    // Draw a checkmark
                    Point[] checkmark = new Point[]
                    {
                        new Point(size / 4, size / 2),
                        new Point(size / 2, size * 3 / 4),
                        new Point(size * 3 / 4, size / 4)
                    };
                    g.DrawLines(pen, checkmark);
                }
            }
            return bmp;
        }

        private Image CreateCancelIcon(int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (Pen pen = new Pen(Color.Red, 2))
                {
                    // Draw an X
                    g.DrawLine(pen, 4, 4, size - 4, size - 4);
                    g.DrawLine(pen, size - 4, 4, 4, size - 4);
                }
            }
            return bmp;
        }

        private void RadioButton_CheckedChanged(object sender, EventArgs e)
        {
            SetControlStates();
        }

        private void SetControlStates()
        {
            // Enable/disable controls based on selected option
            foreach (Control control in Controls[0].Controls)
            {
                if (control is KryptonGroupBox groupBox)
                {
                    if ((groupBox.Text == "Mass Input" && rbMass.Checked) ||
                        (groupBox.Text == "Direct Density Input" && rbDensity.Checked) ||
                        (groupBox.Text == "Grayscale Calibration" && rbCalibration.Checked))
                    {
                        // Enable the group
                        foreach (Control c in groupBox.Panel.Controls)
                        {
                            c.Enabled = true;
                        }
                    }
                    else
                    {
                        // Disable the group
                        foreach (Control c in groupBox.Panel.Controls)
                        {
                            c.Enabled = false;
                        }
                    }
                }
            }
        }

        private void NumMass_ValueChanged(object sender, EventArgs e)
        {
            // Update calculated density label
            double mass = (double)numMass.Value;
            double density = mass / (totalVolume * 1000); // g/cm³
            lblCalculatedDensity.Text = $"Calculated Density: {density:F2} g/cm³";
        }

        private void BtnAddCalibration_Click(object sender, EventArgs e)
        {
            // Add a calibration point manually (without selection)
            if (comboMaterials.SelectedItem is MaterialDensity material)
            {
                CalibrationPoint point = new CalibrationPoint
                {
                    Region = $"Region {calibrationPoints.Count + 1}",
                    Material = material.Name,
                    Density = (float)material.Density,
                    AvgGrayValue = 128 // Default value
                };

                calibrationPoints.Add(point);
                AddCalibrationToGrid(point);
            }
        }

        private void BtnRemoveCalibration_Click(object sender, EventArgs e)
        {
            // Remove selected calibration point
            if (gridCalibration.SelectedRows.Count > 0)
            {
                int index = gridCalibration.SelectedRows[0].Index;
                if (index >= 0 && index < calibrationPoints.Count)
                {
                    calibrationPoints.RemoveAt(index);
                    gridCalibration.Rows.RemoveAt(index);
                }
            }
        }

        private void BtnBoxSelect_Click(object sender, EventArgs e)
        {
            if (!isSelectionMode)
            {
                try
                {
                    using (DensityCalibrationPreviewForm previewForm = new DensityCalibrationPreviewForm(mainForm))
                    {
                        if (previewForm.ShowDialog() == DialogResult.OK)
                        {
                            // Process the selected region
                            Rectangle region = previewForm.SelectedRegion;
                            double avgGrayValue = previewForm.AverageGrayValue;

                            if (comboMaterials.SelectedItem is MaterialDensity material)
                            {
                                CalibrationPoint point = new CalibrationPoint
                                {
                                    Region = $"Region {calibrationPoints.Count + 1} [{region.X},{region.Y},{region.Width},{region.Height}]",
                                    Material = material.Name,
                                    Density = (float)material.Density,
                                    AvgGrayValue = avgGrayValue
                                };

                                calibrationPoints.Add(point);
                                AddCalibrationToGrid(point);
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
        }

        private void AddCalibrationToGrid(CalibrationPoint point)
        {
            int rowIndex = gridCalibration.Rows.Add();
            var row = gridCalibration.Rows[rowIndex];

            row.Cells["Region"].Value = point.Region;
            row.Cells["Material"].Value = point.Material;
            row.Cells["Density"].Value = point.Density;
            row.Cells["AvgGrayValue"].Value = point.AvgGrayValue.ToString("F1");
        }

        private void BtnOK_Click(object sender, EventArgs e)
        {
            try
            {
                if (rbMass.Checked)
                {
                    // Calculate density from mass (kg/m³)
                    double mass = (double)numMass.Value / 1000; // convert g to kg
                    double density = mass / totalVolume;
                    parentForm.SetMaterialDensity(density);
                }
                else if (rbDensity.Checked)
                {
                    // Apply direct density value
                    double density = (double)numDensity.Value;
                    parentForm.SetMaterialDensity(density);
                }
                else if (rbCalibration.Checked)
                {
                    // Apply calibration-based density calculation
                    if (calibrationPoints.Count >= 2)
                    {
                        parentForm.ApplyDensityCalibration(calibrationPoints);
                    }
                    else
                    {
                        MessageBox.Show("At least 2 calibration points are needed for grayscale calibration.",
                            "Insufficient Calibration Points", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        DialogResult = DialogResult.None;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error setting density: {ex.Message}", "Error",
                               MessageBoxButtons.OK, MessageBoxIcon.Error);
                DialogResult = DialogResult.None;
            }
        }
    }
}