using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CTS
{
    public class PermeabilitySimulationDialog : Form
    {
        // UI controls
        private ComboBox axisComboBox;
        private NumericUpDown viscosityNumeric;
        private NumericUpDown inputPressureNumeric;
        private NumericUpDown outputPressureNumeric;
        private Button okButton;
        private Button cancelButton;

        // Calculation method controls
        private GroupBox calcMethodGroupBox;
        private CheckBox darcyCheckBox;
        private CheckBox latticeBoltzmannCheckBox;
        private CheckBox navierStokesCheckBox;

        // Tortuosity controls
        private Label tortuosityLabel;
        private NumericUpDown tortuosityNumeric;

        // Public properties to access the selected values
        public PermeabilitySimulator.FlowAxis SelectedAxis { get; private set; }
        public double Viscosity { get; private set; }
        public double InputPressure { get; private set; }
        public double OutputPressure { get; private set; }
        public double Tortuosity { get; private set; }

        // Calculation method flags
        public bool UseDarcyMethod { get; private set; }
        public bool UseLatticeBoltzmannMethod { get; private set; }
        public bool UseNavierStokesMethod { get; private set; }

        public PermeabilitySimulationDialog()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            // Form settings
            this.Text = "Permeability Simulation Parameters";
            this.Size = new Size(600, 450); // Increased size significantly
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = Color.FromArgb(240, 240, 240);
            this.Font = new Font("Segoe UI", 9F);

            // Create controls
            Label axisLabel = new Label
            {
                Text = "Flow Axis:",
                Location = new Point(20, 20),
                Size = new Size(150, 23),
                TextAlign = ContentAlignment.MiddleRight
            };

            axisComboBox = new ComboBox
            {
                Location = new Point(180, 20),
                Size = new Size(370, 23),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            axisComboBox.Items.AddRange(new object[] { "X-Axis", "Y-Axis", "Z-Axis" });
            axisComboBox.SelectedIndex = 2; // Default to Z-axis

            Label viscosityLabel = new Label
            {
                Text = "Fluid Viscosity (Pa·s):",
                Location = new Point(20, 60),
                Size = new Size(150, 23),
                TextAlign = ContentAlignment.MiddleRight
            };

            viscosityNumeric = new NumericUpDown
            {
                Location = new Point(180, 60),
                Size = new Size(370, 23),
                DecimalPlaces = 5,
                Minimum = 0.00001m,
                Maximum = 1000m,
                Value = 0.001m,  // Default to water (1 cP or 0.001 Pa·s)
                Increment = 0.0001m
            };

            Label inputPressureLabel = new Label
            {
                Text = "Input Pressure (Pa):",
                Location = new Point(20, 100),
                Size = new Size(150, 23),
                TextAlign = ContentAlignment.MiddleRight
            };

            inputPressureNumeric = new NumericUpDown
            {
                Location = new Point(180, 100),
                Size = new Size(370, 23),
                DecimalPlaces = 2,
                Minimum = 0m,
                Maximum = 1000000m,
                Value = 10000m,  // Default: 10,000 Pa (10 kPa)
                Increment = 1000m
            };

            Label outputPressureLabel = new Label
            {
                Text = "Output Pressure (Pa):",
                Location = new Point(20, 140),
                Size = new Size(150, 23),
                TextAlign = ContentAlignment.MiddleRight
            };

            outputPressureNumeric = new NumericUpDown
            {
                Location = new Point(180, 140),
                Size = new Size(370, 23),
                DecimalPlaces = 2,
                Minimum = 0m,
                Maximum = 1000000m,
                Value = 1000m,  // Default: 1000 Pa
                Increment = 1000m
            };

            // Tortuosity control
            tortuosityLabel = new Label
            {
                Text = "Tortuosity Factor:",
                Location = new Point(20, 180),
                Size = new Size(150, 23),
                TextAlign = ContentAlignment.MiddleRight
            };

            tortuosityNumeric = new NumericUpDown
            {
                Location = new Point(180, 180),
                Size = new Size(370, 23),
                DecimalPlaces = 2,
                Minimum = 1.0m,
                Maximum = 10.0m,
                Value = 1.5m,  // Default tortuosity value
                Increment = 0.1m
            };

            // Calculation Method Group Box
            calcMethodGroupBox = new GroupBox
            {
                Text = "Permeability Calculation Methods",
                Location = new Point(20, 220),
                Size = new Size(530, 120),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };

            // Darcy checkbox
            darcyCheckBox = new CheckBox
            {
                Text = "Darcy's Law (k = (Q * μ * L) / (A * ΔP))",
                Location = new Point(20, 30),
                Size = new Size(490, 24),
                Checked = true,
                Font = new Font("Segoe UI", 9F)
            };

            // Lattice Boltzmann checkbox
            latticeBoltzmannCheckBox = new CheckBox
            {
                Text = "Lattice Boltzmann Method (computational fluid dynamics approach)",
                Location = new Point(20, 55),
                Size = new Size(490, 24),
                Checked = false,
                Font = new Font("Segoe UI", 9F)
            };

            // Navier-Stokes checkbox
            navierStokesCheckBox = new CheckBox
            {
                Text = "Navier-Stokes Method (advanced fluid dynamics with inertial effects)",
                Location = new Point(20, 80),
                Size = new Size(490, 24),
                Checked = false,
                Font = new Font("Segoe UI", 9F)
            };

            // Add checkboxes to the groupbox
            calcMethodGroupBox.Controls.Add(darcyCheckBox);
            calcMethodGroupBox.Controls.Add(latticeBoltzmannCheckBox);
            calcMethodGroupBox.Controls.Add(navierStokesCheckBox);

            // Button positioning
            okButton = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(330, 370),
                Size = new Size(100, 30)
            };
            okButton.Click += OkButton_Click;

            cancelButton = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(450, 370),
                Size = new Size(100, 30)
            };

            // Add controls to form
            this.Controls.Add(axisLabel);
            this.Controls.Add(axisComboBox);
            this.Controls.Add(viscosityLabel);
            this.Controls.Add(viscosityNumeric);
            this.Controls.Add(inputPressureLabel);
            this.Controls.Add(inputPressureNumeric);
            this.Controls.Add(outputPressureLabel);
            this.Controls.Add(outputPressureNumeric);
            this.Controls.Add(tortuosityLabel);
            this.Controls.Add(tortuosityNumeric);
            this.Controls.Add(calcMethodGroupBox);
            this.Controls.Add(okButton);
            this.Controls.Add(cancelButton);

            this.AcceptButton = okButton;
            this.CancelButton = cancelButton;

            // Event handler for checkbox validation
            darcyCheckBox.CheckedChanged += CalculationMethod_CheckedChanged;
            latticeBoltzmannCheckBox.CheckedChanged += CalculationMethod_CheckedChanged;
            navierStokesCheckBox.CheckedChanged += CalculationMethod_CheckedChanged;
        }

        private void CalculationMethod_CheckedChanged(object sender, EventArgs e)
        {
            // Ensure at least one calculation method is selected
            if (!darcyCheckBox.Checked && !latticeBoltzmannCheckBox.Checked && !navierStokesCheckBox.Checked)
            {
                // If the last checked box is being unchecked, prevent it
                ((CheckBox)sender).Checked = true;
                MessageBox.Show("At least one calculation method must be selected.",
                    "Invalid Selection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        public void SetInitialTortuosity(double tortuosityValue)
        {
            // Ensure the value is within the allowed range for the NumericUpDown control
            decimal value = (decimal)Math.Max(
                Math.Min(tortuosityValue, (double)tortuosityNumeric.Maximum),
                (double)tortuosityNumeric.Minimum);

            // Apply the value to the control
            tortuosityNumeric.Value = value;

            // Ensure a reasonable default if the value is too small
            if (value < 1.0m)
            {
                tortuosityNumeric.Value = 1.0m;
                Logger.Log($"[PermeabilitySimulationDialog] Applied minimum tortuosity value (1.0) instead of {tortuosityValue:F2}");
            }
        }
        private void OkButton_Click(object sender, EventArgs e)
        {
            // Validate input (ensure input pressure > output pressure)
            if (inputPressureNumeric.Value <= outputPressureNumeric.Value)
            {
                MessageBox.Show("Input pressure must be greater than output pressure.",
                    "Invalid Parameters", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                // Keep dialog open
                this.DialogResult = DialogResult.None;
                return;
            }

            // Ensure at least one calculation method is selected
            if (!darcyCheckBox.Checked && !latticeBoltzmannCheckBox.Checked && !navierStokesCheckBox.Checked)
            {
                MessageBox.Show("Please select at least one calculation method.",
                    "Invalid Parameters", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                // Keep dialog open
                this.DialogResult = DialogResult.None;
                return;
            }

            // Store the selected values
            SelectedAxis = (PermeabilitySimulator.FlowAxis)axisComboBox.SelectedIndex;
            Viscosity = (double)viscosityNumeric.Value;
            InputPressure = (double)inputPressureNumeric.Value;
            OutputPressure = (double)outputPressureNumeric.Value;
            Tortuosity = (double)tortuosityNumeric.Value;

            // Store calculation method choices
            UseDarcyMethod = darcyCheckBox.Checked;
            UseLatticeBoltzmannMethod = latticeBoltzmannCheckBox.Checked;
            UseNavierStokesMethod = navierStokesCheckBox.Checked;
        }
    }
}