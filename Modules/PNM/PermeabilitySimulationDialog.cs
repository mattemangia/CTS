using System;
using System.Drawing;
using System.Windows.Forms;

namespace CTSegmenter
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

        // Public properties to access the selected values
        public PermeabilitySimulator.FlowAxis SelectedAxis { get; private set; }

        public double Viscosity { get; private set; }
        public double InputPressure { get; private set; }
        public double OutputPressure { get; private set; }

        public PermeabilitySimulationDialog()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            // Form settings
            this.Text = "Permeability Simulation Parameters";
            this.Size = new Size(450, 250); // Increased width for better visibility
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
                Size = new Size(150, 23), // Increased width for label
                TextAlign = ContentAlignment.MiddleRight
            };

            axisComboBox = new ComboBox
            {
                Location = new Point(180, 20), // Adjusted x position
                Size = new Size(240, 23), // Increased width
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            axisComboBox.Items.AddRange(new object[] { "X-Axis", "Y-Axis", "Z-Axis" });
            axisComboBox.SelectedIndex = 2; // Default to Z-axis

            Label viscosityLabel = new Label
            {
                Text = "Fluid Viscosity (Pa·s):",
                Location = new Point(20, 60),
                Size = new Size(150, 23), // Increased width for label
                TextAlign = ContentAlignment.MiddleRight
            };

            viscosityNumeric = new NumericUpDown
            {
                Location = new Point(180, 60), // Adjusted x position
                Size = new Size(240, 23), // Increased width
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
                Size = new Size(150, 23), // Increased width for label
                TextAlign = ContentAlignment.MiddleRight
            };

            inputPressureNumeric = new NumericUpDown
            {
                Location = new Point(180, 100), // Adjusted x position
                Size = new Size(240, 23), // Increased width
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
                Size = new Size(150, 23), // Increased width for label
                TextAlign = ContentAlignment.MiddleRight
            };

            outputPressureNumeric = new NumericUpDown
            {
                Location = new Point(180, 140), // Adjusted x position
                Size = new Size(240, 23), // Increased width
                DecimalPlaces = 2,
                Minimum = 0m,
                Maximum = 1000000m,
                Value = 1000m,  // Default: 0 Pa
                Increment = 1000m
            };

            okButton = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(180, 180), // Adjusted x position
                Size = new Size(100, 30)
            };
            okButton.Click += OkButton_Click;

            cancelButton = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(320, 180), // Adjusted x position
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
            this.Controls.Add(okButton);
            this.Controls.Add(cancelButton);

            this.AcceptButton = okButton;
            this.CancelButton = cancelButton;
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

            // Store the selected values
            SelectedAxis = (PermeabilitySimulator.FlowAxis)axisComboBox.SelectedIndex;
            Viscosity = (double)viscosityNumeric.Value;
            InputPressure = (double)inputPressureNumeric.Value;
            OutputPressure = (double)outputPressureNumeric.Value;
        }
    }
}