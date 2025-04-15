using System;
using System.Windows.Forms;
using System.Drawing;

namespace CTSegmenter
{
    public class PoreConnectivityDialog : Form
    {
        // Public properties to access the settings
        public double MaxThroatLengthFactor { get;  set; } = 3.0;
        public double MinOverlapFactor { get;  set; } = 0.1;
        public bool EnforceFlowPath { get;  set; } = true;

        // UI controls
        private NumericUpDown maxThroatLengthFactorNumeric;
        private NumericUpDown minOverlapFactorNumeric;
        private CheckBox enforceFlowPathCheckBox;

        public PoreConnectivityDialog()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            // Form settings - INCREASED SIZE
            this.Text = "Pore Connectivity Settings";
            this.Size = new Size(515, 290); // Increased from 400x220
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = Color.FromArgb(240, 240, 240);
            this.Font = new Font("Segoe UI", 9F);

            // Create controls with explanatory labels
            Label titleLabel = new Label
            {
                Text = "Petrophysical Connectivity Controls",
                Location = new Point(20, 15),
                Size = new Size(350, 25), // Increased height
                Font = new Font("Segoe UI", 11, FontStyle.Bold) // Increased font size
            };
            this.Controls.Add(titleLabel);

            // Maximum throat length factor - ADJUSTED POSITIONS AND SIZES
            Label maxThroatLengthLabel = new Label
            {
                Text = "Max Throat Length Factor:",
                Location = new Point(20, 60), // More vertical space
                Size = new Size(180, 23),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.DarkRed // Make main labels more visible
            };
            this.Controls.Add(maxThroatLengthLabel);

            maxThroatLengthFactorNumeric = new NumericUpDown
            {
                Location = new Point(230, 60),
                Size = new Size(70, 23), // Wider control
                Minimum = 1.0m,
                Maximum = 10.0m,
                Value = 3.0m,
                DecimalPlaces = 1,
                Increment = 0.1m
            };
            this.Controls.Add(maxThroatLengthFactorNumeric);

            Label maxThroatHelpLabel = new Label
            {
                Text = "Maximum throat length as multiple of average pore radius",
                Location = new Point(315, 60),
                Size = new Size(170, 40), // More width and height for help text
                Font = new Font("Segoe UI", 8),
                ForeColor = Color.DarkGray
            };
            this.Controls.Add(maxThroatHelpLabel);

            // Minimum overlap factor - ADJUSTED POSITIONS
            Label minOverlapLabel = new Label
            {
                Text = "Min. Overlap Factor:",
                Location = new Point(20, 110), // More vertical space
                Size = new Size(180, 23),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.DarkRed // Make main labels more visible
            };
            this.Controls.Add(minOverlapLabel);

            minOverlapFactorNumeric = new NumericUpDown
            {
                Location = new Point(230, 110),
                Size = new Size(70, 23), // Wider control
                Minimum = 0.0m,
                Maximum = 1.0m,
                Value = 0.1m,
                DecimalPlaces = 2,
                Increment = 0.05m
            };
            this.Controls.Add(minOverlapFactorNumeric);

            Label minOverlapHelpLabel = new Label
            {
                Text = "Minimum pore overlap required for connection",
                Location = new Point(315, 110),
                Size = new Size(170, 40), // More width and height
                Font = new Font("Segoe UI", 8),
                ForeColor = Color.DarkGray
            };
            this.Controls.Add(minOverlapHelpLabel);

            // Enforce flow path option - ADJUSTED POSITIONS
            enforceFlowPathCheckBox = new CheckBox
            {
                Text = "Enforce Flow Path Connectivity",
                Location = new Point(20, 160), // More vertical space
                Size = new Size(210, 20),
                Checked = true
            };
            this.Controls.Add(enforceFlowPathCheckBox);

            // Help text for enforce flow path
            Label enforceFlowPathHelpLabel = new Label
            {
                Text = "Ensures at least one connected path through the sample",
                Location = new Point(230, 160),
                Size = new Size(255, 20), // More width
                Font = new Font("Segoe UI", 8),
                ForeColor = Color.DarkGray
            };
            this.Controls.Add(enforceFlowPathHelpLabel);

            // OK and Cancel buttons - MOVED DOWN
            Button okButton = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(310, 200), // Moved down
                Size = new Size(80, 25) // Slightly larger
            };
            okButton.Click += OkButton_Click;
            this.Controls.Add(okButton);

            Button cancelButton = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(400, 200), // Moved down
                Size = new Size(80, 25) // Slightly larger
            };
            this.Controls.Add(cancelButton);

            this.AcceptButton = okButton;
            this.CancelButton = cancelButton;
        }


        private void OkButton_Click(object sender, EventArgs e)
        {
            // Store the selected values in public properties
            MaxThroatLengthFactor = (double)maxThroatLengthFactorNumeric.Value;
            MinOverlapFactor = (double)minOverlapFactorNumeric.Value;
            EnforceFlowPath = enforceFlowPathCheckBox.Checked;
        }
    }
}