//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Drawing;
using System.Windows.Forms;
using Krypton.Toolkit;

namespace CTS.Modules.Triaxial_Simulation
{
    public class TriaxialCalibrationDialog : KryptonForm
    {
        private TriaxialSimulationForm simulationForm;
        private KryptonNumericUpDown numYoungModulus;
        private KryptonNumericUpDown numPoissonRatio;
        private KryptonNumericUpDown numYieldStrength;
        private KryptonNumericUpDown numBrittleStrength;
        private KryptonNumericUpDown numCohesion;
        private KryptonNumericUpDown numFrictionAngle;

        public CalibrationParameters CalibrationParameters { get; private set; }

        public TriaxialCalibrationDialog(TriaxialSimulationForm form)
        {
            this.simulationForm = form;
            this.CalibrationParameters = new CalibrationParameters();
            InitializeComponent();
            LoadCurrentValues();
        }

        private void InitializeComponent()
        {
            // Basic form setup
            this.Text = "Triaxial Calibration";
            this.Size = new Size(500, 450);
            this.MinimizeBox = false;
            this.MaximizeBox = false;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;

            // Create a panel for all controls
            KryptonPanel mainPanel = new KryptonPanel
            {
                Dock = DockStyle.Fill
            };
            this.Controls.Add(mainPanel);

            // Add title label
            KryptonLabel titleLabel = new KryptonLabel
            {
                Text = "Triaxial Test Calibration",
                Location = new Point(20, 20),
                Font = new Font("Segoe UI", 14, FontStyle.Bold),
                StateCommon = { ShortText = { Color1 = Color.FromArgb(50, 50, 120) } }
            };
            mainPanel.Controls.Add(titleLabel);

            // Current test results section
            KryptonGroupBox resultsGroup = new KryptonGroupBox
            {
                Text = "Current Test Results",
                Location = new Point(20, 60),
                Size = new Size(440, 120)
            };
            mainPanel.Controls.Add(resultsGroup);

            // Add result labels
            KryptonLabel lblShearStress = new KryptonLabel
            {
                Text = $"Failure Shear Stress: {GetFailureShearStress():F2} MPa",
                Location = new Point(20, 30),
                AutoSize = true
            };
            resultsGroup.Panel.Controls.Add(lblShearStress);

            KryptonLabel lblSigma1 = new KryptonLabel
            {
                Text = $"Failure Sigma 1: {GetFailureSigma1():F2} MPa",
                Location = new Point(20, 55),
                AutoSize = true
            };
            resultsGroup.Panel.Controls.Add(lblSigma1);

            KryptonLabel lblSigma3 = new KryptonLabel
            {
                Text = $"Failure Sigma 3: {GetFailureSigma3():F2} MPa",
                Location = new Point(20, 80),
                AutoSize = true
            };
            resultsGroup.Panel.Controls.Add(lblSigma3);

            KryptonLabel lblStrain = new KryptonLabel
            {
                Text = $"Failure Strain: {GetFailureStrain() * 100:F2} %",
                Location = new Point(250, 30),
                AutoSize = true
            };
            resultsGroup.Panel.Controls.Add(lblStrain);

            // Calibration parameters section
            KryptonGroupBox paramsGroup = new KryptonGroupBox
            {
                Text = "Material Parameters",
                Location = new Point(20, 190),
                Size = new Size(440, 180)
            };
            mainPanel.Controls.Add(paramsGroup);

            // Young's modulus input
            KryptonLabel lblYoungModulus = new KryptonLabel
            {
                Text = "Young's Modulus (MPa):",
                Location = new Point(20, 30),
                AutoSize = true
            };
            paramsGroup.Panel.Controls.Add(lblYoungModulus);

            numYoungModulus = new KryptonNumericUpDown
            {
                Location = new Point(170, 28),
                Width = 100,
                Minimum = 1000,
                Maximum = 100000,
                DecimalPlaces = 0
            };
            paramsGroup.Panel.Controls.Add(numYoungModulus);

            // Poisson's ratio input
            KryptonLabel lblPoissonRatio = new KryptonLabel
            {
                Text = "Poisson's Ratio:",
                Location = new Point(20, 60),
                AutoSize = true
            };
            paramsGroup.Panel.Controls.Add(lblPoissonRatio);

            numPoissonRatio = new KryptonNumericUpDown
            {
                Location = new Point(170, 58),
                Width = 100,
                Minimum = 0.01m,
                Maximum = 0.49m,
                Value = 0.3m,
                DecimalPlaces = 2,
                Increment = 0.01m
            };
            paramsGroup.Panel.Controls.Add(numPoissonRatio);

            // Yield strength input
            KryptonLabel lblYieldStrength = new KryptonLabel
            {
                Text = "Yield Strength (MPa):",
                Location = new Point(290, 30),
                AutoSize = true
            };
            paramsGroup.Panel.Controls.Add(lblYieldStrength);

            numYieldStrength = new KryptonNumericUpDown
            {
                Location = new Point(290, 55),
                Width = 100,
                Minimum = 10,
                Maximum = 5000,
                DecimalPlaces = 0
            };
            paramsGroup.Panel.Controls.Add(numYieldStrength);

            // Brittle strength input
            KryptonLabel lblBrittleStrength = new KryptonLabel
            {
                Text = "Brittle Strength (MPa):",
                Location = new Point(290, 80),
                AutoSize = true
            };
            paramsGroup.Panel.Controls.Add(lblBrittleStrength);

            numBrittleStrength = new KryptonNumericUpDown
            {
                Location = new Point(290, 105),
                Width = 100,
                Minimum = 10,
                Maximum = 5000,
                DecimalPlaces = 0
            };
            paramsGroup.Panel.Controls.Add(numBrittleStrength);

            // Cohesion input
            KryptonLabel lblCohesion = new KryptonLabel
            {
                Text = "Cohesion (MPa):",
                Location = new Point(20, 90),
                AutoSize = true
            };
            paramsGroup.Panel.Controls.Add(lblCohesion);

            numCohesion = new KryptonNumericUpDown
            {
                Location = new Point(170, 88),
                Width = 100,
                Minimum = 0,
                Maximum = 1000,
                DecimalPlaces = 1
            };
            paramsGroup.Panel.Controls.Add(numCohesion);

            // Friction angle input
            KryptonLabel lblFrictionAngle = new KryptonLabel
            {
                Text = "Friction Angle (°):",
                Location = new Point(20, 120),
                AutoSize = true
            };
            paramsGroup.Panel.Controls.Add(lblFrictionAngle);

            numFrictionAngle = new KryptonNumericUpDown
            {
                Location = new Point(170, 118),
                Width = 100,
                Minimum = 0,
                Maximum = 90,
                DecimalPlaces = 1
            };
            paramsGroup.Panel.Controls.Add(numFrictionAngle);

            // Button panel
            KryptonPanel buttonPanel = new KryptonPanel
            {
                Location = new Point(20, 380),
                Size = new Size(440, 35)
            };
            mainPanel.Controls.Add(buttonPanel);

            // OK button
            KryptonButton btnOk = new KryptonButton
            {
                Text = "Apply Calibration",
                Location = new Point(230, 0),
                Width = 120,
                Height = 35
            };
            btnOk.Click += BtnOk_Click;
            buttonPanel.Controls.Add(btnOk);

            // Cancel button
            KryptonButton btnCancel = new KryptonButton
            {
                Text = "Cancel",
                Location = new Point(360, 0),
                Width = 80,
                Height = 35
            };
            btnCancel.Click += BtnCancel_Click;
            buttonPanel.Controls.Add(btnCancel);
        }

        private void LoadCurrentValues()
        {
            // Load current values from simulation form
            numYoungModulus.Value = (decimal)simulationForm.GetYoungsModulus();
            numPoissonRatio.Value = (decimal)simulationForm.GetPoissonRatio();

            // Set reasonable defaults for other parameters if needed
            if (numYieldStrength.Value < 10m)
                numYieldStrength.Value = (decimal)(simulationForm.GetYoungsModulus() * 0.05f);

            if (numBrittleStrength.Value < 10m)
                numBrittleStrength.Value = (decimal)(simulationForm.GetYoungsModulus() * 0.08f);

            // Initialize Mohr-Coulomb parameters
            if (numCohesion.Value < 1m)
                numCohesion.Value = (decimal)(simulationForm.GetYoungsModulus() * 0.01f);

            if (numFrictionAngle.Value < 1m)
                numFrictionAngle.Value = 30.0m;
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            // Save calibration parameters
            CalibrationParameters.YoungModulus = (float)numYoungModulus.Value;
            CalibrationParameters.PoissonRatio = (float)numPoissonRatio.Value;
            CalibrationParameters.YieldStrength = (float)numYieldStrength.Value;
            CalibrationParameters.BrittleStrength = (float)numBrittleStrength.Value;
            CalibrationParameters.Cohesion = (float)numCohesion.Value;
            CalibrationParameters.FrictionAngle = (float)numFrictionAngle.Value;

            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private void BtnCancel_Click(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.Cancel;
            this.Close();
        }

        // Methods to access simulation form data
        private float GetFailureShearStress()
        {
            return simulationForm.GetFailureShearStress();
        }

        private float GetFailureSigma1()
        {
            return simulationForm.GetFailureSigma1();
        }

        private float GetFailureSigma3()
        {
            return simulationForm.GetFailureSigma3();
        }

        private float GetFailureStrain()
        {
            return simulationForm.GetFailureStrain();
        }
    }
}
