using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Krypton.Toolkit;

namespace CTS
{
    public partial class CalibrationDialog : KryptonForm
    {
        private readonly CalibrationManager calibrationManager;
        private readonly AcousticSimulationForm simulationForm;
        private double simulatedVp;
        private double simulatedVs;
        private double simulatedVpVsRatio;
        private KryptonListBox calibrationPointsList;
        private KryptonLabel lblCalibrationSummary;
        private KryptonTextBox txtNote;
        private KryptonNumericUpDown numKnownVpVs;

        public CalibrationDialog(CalibrationManager manager, AcousticSimulationForm form,
                                 double vp, double vs, double vpVsRatio)
        {
            try
            {
                // Ensure all parameters are valid
                this.calibrationManager = manager ?? throw new ArgumentNullException(nameof(manager));
                this.simulationForm = form ?? throw new ArgumentNullException(nameof(form));
                this.simulatedVp = vp;
                this.simulatedVs = vs;
                this.simulatedVpVsRatio = vpVsRatio;

                InitializeComponent();

                // Verify essential controls were created
                if (calibrationPointsList == null || numKnownVpVs == null || txtNote == null)
                {
                    MessageBox.Show("Error initializing dialog controls.",
                        "Initialization Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    PopulateExistingCalibrationPoints();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error initializing calibration dialog: {ex.Message}",
                    "Initialization Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void InitializeComponent()
        {
            try
            {
                // Form settings
                this.Text = "Acoustic Simulator Calibration";
                this.Size = new Size(600, 700);
                this.StartPosition = FormStartPosition.CenterParent;
                this.BackColor = Color.FromArgb(45, 45, 48); // Dark background
                this.Icon=Properties.Resources.favicon; // Set the icon from resources

                // Create main layout panel
                KryptonPanel mainPanel = new KryptonPanel();
                mainPanel.Dock = DockStyle.Fill;
                mainPanel.StateCommon.Color1 = Color.FromArgb(45, 45, 48);
                mainPanel.StateCommon.Color2 = Color.FromArgb(45, 45, 48);

                // Create two main sections: existing calibration points (top) and add new point (bottom)
                TableLayoutPanel layout = new TableLayoutPanel();
                layout.Dock = DockStyle.Fill;
                layout.RowCount = 2;
                layout.ColumnCount = 1;
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60F));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40F));
                mainPanel.Controls.Add(layout);

                // === Existing Calibration Points Section ===
                KryptonGroupBox existingPointsGroup = new KryptonGroupBox();
                existingPointsGroup.Dock = DockStyle.Fill;
                existingPointsGroup.Text = "Existing Calibration Points";
                existingPointsGroup.StateCommon.Content.ShortText.Color1 = Color.White;
                layout.Controls.Add(existingPointsGroup, 0, 0);

                // List to display existing calibration points
                calibrationPointsList = new KryptonListBox();
                calibrationPointsList.Name = "calibrationPointsList";
                calibrationPointsList.Dock = DockStyle.Fill;
                calibrationPointsList.StateCommon.Back.Color1 = Color.FromArgb(30, 30, 30);
                calibrationPointsList.StateCommon.Item.Content.ShortText.Color1 = Color.White;
                existingPointsGroup.Panel.Controls.Add(calibrationPointsList);

                // Button panel for calibration set actions
                KryptonPanel calibrationActionPanel = new KryptonPanel();
                calibrationActionPanel.Dock = DockStyle.Bottom;
                calibrationActionPanel.Height = 40;
                calibrationActionPanel.StateCommon.Color1 = Color.FromArgb(45, 45, 48);
                existingPointsGroup.Panel.Controls.Add(calibrationActionPanel);

                // Add buttons for calibration dataset actions
                KryptonButton btnRemovePoint = new KryptonButton();
                btnRemovePoint.Text = "Remove Point";
                btnRemovePoint.Location = new Point(10, 5);
                btnRemovePoint.Width = 120;
                btnRemovePoint.Click += BtnRemovePoint_Click;
                calibrationActionPanel.Controls.Add(btnRemovePoint);

                KryptonButton btnSaveCalibration = new KryptonButton();
                btnSaveCalibration.Text = "Save Calibration";
                btnSaveCalibration.Location = new Point(140, 5);
                btnSaveCalibration.Width = 120;
                btnSaveCalibration.Click += BtnSaveCalibration_Click;
                calibrationActionPanel.Controls.Add(btnSaveCalibration);

                KryptonButton btnLoadCalibration = new KryptonButton();
                btnLoadCalibration.Text = "Load Calibration";
                btnLoadCalibration.Location = new Point(270, 5);
                btnLoadCalibration.Width = 120;
                btnLoadCalibration.Click += BtnLoadCalibration_Click;
                calibrationActionPanel.Controls.Add(btnLoadCalibration);

                KryptonButton btnApplyCalibration = new KryptonButton();
                btnApplyCalibration.Text = "Apply Calibration";
                btnApplyCalibration.Location = new Point(400, 5);
                btnApplyCalibration.Width = 120;
                btnApplyCalibration.Click += BtnApplyCalibration_Click;
                calibrationActionPanel.Controls.Add(btnApplyCalibration);

                // Calibration summary label
                lblCalibrationSummary = new KryptonLabel();
                lblCalibrationSummary.Name = "lblCalibrationSummary";
                lblCalibrationSummary.Dock = DockStyle.Bottom;
                lblCalibrationSummary.Height = 60;
                lblCalibrationSummary.StateCommon.ShortText.Color1 = Color.LightGreen;
                lblCalibrationSummary.StateCommon.ShortText.Font = new Font("Segoe UI", 9, FontStyle.Regular);
                lblCalibrationSummary.Text = "No calibration data available. Add at least 2 calibration points.";
                existingPointsGroup.Panel.Controls.Add(lblCalibrationSummary);

                // === Add New Calibration Point Section ===
                KryptonGroupBox addPointGroup = new KryptonGroupBox();
                addPointGroup.Dock = DockStyle.Fill;
                addPointGroup.Text = "Add New Calibration Point";
                addPointGroup.StateCommon.Content.ShortText.Color1 = Color.White;
                layout.Controls.Add(addPointGroup, 0, 1);

                // Panel for inputs to add new calibration point
                KryptonPanel addPointPanel = new KryptonPanel();
                addPointPanel.Dock = DockStyle.Fill;
                addPointPanel.StateCommon.Color1 = Color.FromArgb(45, 45, 48);
                addPointGroup.Panel.Controls.Add(addPointPanel);

                // Current material info
                KryptonLabel lblCurrentMaterial = new KryptonLabel();
                lblCurrentMaterial.Text = "Current Material:";
                lblCurrentMaterial.Location = new Point(10, 20);
                lblCurrentMaterial.StateCommon.ShortText.Color1 = Color.White;
                addPointPanel.Controls.Add(lblCurrentMaterial);

                KryptonLabel lblMaterialValue = new KryptonLabel();
                lblMaterialValue.Name = "lblMaterialValue";
                lblMaterialValue.Location = new Point(150, 20);
                lblMaterialValue.StateCommon.ShortText.Color1 = Color.LightYellow;
                lblMaterialValue.Text = simulationForm.SelectedMaterial?.Name ?? "None";
                addPointPanel.Controls.Add(lblMaterialValue);

                // Current density info
                KryptonLabel lblCurrentDensity = new KryptonLabel();
                lblCurrentDensity.Text = "Current Density:";
                lblCurrentDensity.Location = new Point(10, 50);
                lblCurrentDensity.StateCommon.ShortText.Color1 = Color.White;
                addPointPanel.Controls.Add(lblCurrentDensity);

                KryptonLabel lblDensityValue = new KryptonLabel();
                lblDensityValue.Name = "lblDensityValue";
                lblDensityValue.Location = new Point(150, 50);
                lblDensityValue.StateCommon.ShortText.Color1 = Color.White;
                lblDensityValue.Text = $"{simulationForm.SelectedMaterial?.Density ?? 0:F1} kg/m³";
                addPointPanel.Controls.Add(lblDensityValue);

                // Simulated Vp/Vs ratio
                KryptonLabel lblSimulatedVpVs = new KryptonLabel();
                lblSimulatedVpVs.Text = "Simulated Vp/Vs:";
                lblSimulatedVpVs.Location = new Point(10, 80);
                lblSimulatedVpVs.StateCommon.ShortText.Color1 = Color.White;
                addPointPanel.Controls.Add(lblSimulatedVpVs);

                KryptonLabel lblVpVsValue = new KryptonLabel();
                lblVpVsValue.Name = "lblVpVsValue";
                lblVpVsValue.Location = new Point(150, 80);
                lblVpVsValue.StateCommon.ShortText.Color1 = Color.White;
                lblVpVsValue.Text = $"{simulatedVpVsRatio:F3}";
                addPointPanel.Controls.Add(lblVpVsValue);

                // Material elastic properties
                double youngModulus = 0;
                double poissonRatio = 0;

                // Safely get values
                try
                {
                    youngModulus = (double)simulationForm.GetYoungsModulus();
                    poissonRatio = (double)simulationForm.GetPoissonRatio();
                }
                catch (Exception)
                {
                    // Default values if exception
                    youngModulus = 0;
                    poissonRatio = 0;
                }

                KryptonLabel lblYoungsModulus = new KryptonLabel();
                lblYoungsModulus.Text = "Young's Modulus:";
                lblYoungsModulus.Location = new Point(10, 110);
                lblYoungsModulus.StateCommon.ShortText.Color1 = Color.White;
                addPointPanel.Controls.Add(lblYoungsModulus);

                KryptonLabel lblYoungsValue = new KryptonLabel();
                lblYoungsValue.Name = "lblYoungsValue";
                lblYoungsValue.Location = new Point(150, 110);
                lblYoungsValue.StateCommon.ShortText.Color1 = Color.White;
                lblYoungsValue.Text = $"{youngModulus:F2} MPa";
                addPointPanel.Controls.Add(lblYoungsValue);

                KryptonLabel lblPoisson = new KryptonLabel();
                lblPoisson.Text = "Poisson's Ratio:";
                lblPoisson.Location = new Point(10, 140);
                lblPoisson.StateCommon.ShortText.Color1 = Color.White;
                addPointPanel.Controls.Add(lblPoisson);

                KryptonLabel lblPoissonValue = new KryptonLabel();
                lblPoissonValue.Name = "lblPoissonValue";
                lblPoissonValue.Location = new Point(150, 140);
                lblPoissonValue.StateCommon.ShortText.Color1 = Color.White;
                lblPoissonValue.Text = $"{poissonRatio:F4}";
                addPointPanel.Controls.Add(lblPoissonValue);

                // Known Vp/Vs ratio input
                KryptonLabel lblKnownVpVs = new KryptonLabel();
                lblKnownVpVs.Text = "Known Vp/Vs Ratio:";
                lblKnownVpVs.Location = new Point(300, 50);
                lblKnownVpVs.StateCommon.ShortText.Color1 = Color.White;
                addPointPanel.Controls.Add(lblKnownVpVs);

                numKnownVpVs = new KryptonNumericUpDown();
                numKnownVpVs.Name = "numKnownVpVs";
                numKnownVpVs.Location = new Point(450, 50);
                numKnownVpVs.Width = 100;
                numKnownVpVs.DecimalPlaces = 3;
                numKnownVpVs.Minimum = 0.0m;  // Allow zero
                numKnownVpVs.Maximum = 4.0m;
                // Make sure the value is valid
                numKnownVpVs.Value = Math.Max(0.0m, Math.Min(4.0m, (decimal)simulatedVpVsRatio));
                numKnownVpVs.Increment = 0.001m;
                addPointPanel.Controls.Add(numKnownVpVs);

                // Calibration note
                KryptonLabel lblNote = new KryptonLabel();
                lblNote.Text = "Note:";
                lblNote.Location = new Point(300, 80);
                lblNote.StateCommon.ShortText.Color1 = Color.White;
                addPointPanel.Controls.Add(lblNote);

                txtNote = new KryptonTextBox();
                txtNote.Name = "txtNote";
                txtNote.Location = new Point(450, 80);
                txtNote.Width = 120;
                txtNote.Height = 60;
                addPointPanel.Controls.Add(txtNote);

                // Add Calibration button
                KryptonButton btnAddCalibration = new KryptonButton();
                btnAddCalibration.Text = "Add Calibration Point";
                btnAddCalibration.Location = new Point(350, 140);
                btnAddCalibration.Width = 200;
                btnAddCalibration.Click += BtnAddCalibration_Click;
                addPointPanel.Controls.Add(btnAddCalibration);

                // Form close button
                KryptonButton btnClose = new KryptonButton();
                btnClose.Text = "Close";
                btnClose.Dock = DockStyle.Bottom;
                btnClose.Height = 30;
                btnClose.DialogResult = DialogResult.OK;
                this.Controls.Add(btnClose);
                this.Controls.Add(mainPanel);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error initializing controls: {ex.Message}",
                    "Control Initialization Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Use discrete methods for event handling instead of lambdas
        private void BtnRemovePoint_Click(object sender, EventArgs e)
        {
            try
            {
                if (calibrationPointsList == null || calibrationManager == null ||
                    calibrationManager.CurrentCalibration == null)
                    return;

                if (calibrationPointsList.SelectedItem is CalibrationPoint point)
                {
                    calibrationManager.CurrentCalibration.RemoveCalibrationPoint(point);
                    PopulateExistingCalibrationPoints();
                    UpdateCalibrationSummary();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error removing calibration point: {ex.Message}",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnSaveCalibration_Click(object sender, EventArgs e)
        {
            try
            {
                SaveCalibration();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving calibration: {ex.Message}",
                    "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnLoadCalibration_Click(object sender, EventArgs e)
        {
            try
            {
                LoadCalibration();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading calibration: {ex.Message}",
                    "Load Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnApplyCalibration_Click(object sender, EventArgs e)
        {
            try
            {
                if (calibrationManager == null)
                    return;

                calibrationManager.ApplyCalibrationToCurrentSimulation();
                MessageBox.Show("Calibration applied to current material.",
                    "Calibration Applied", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error applying calibration: {ex.Message}",
                    "Application Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnAddCalibration_Click(object sender, EventArgs e)
        {
            try
            {
                if (numKnownVpVs == null || calibrationManager == null)
                    return;

                double knownVpVs = (double)numKnownVpVs.Value;

                // Create and add the calibration point
                calibrationManager.AddCurrentSimulationAsCalibrationPoint(knownVpVs, simulatedVp, simulatedVs);

                // Add notes if provided
                if (txtNote != null && !string.IsNullOrEmpty(txtNote.Text) &&
                    calibrationManager.CurrentCalibration != null &&
                    calibrationManager.CurrentCalibration.CalibrationPoints != null &&
                    calibrationManager.CurrentCalibration.CalibrationPoints.Count > 0)
                {
                    var points = calibrationManager.CurrentCalibration.CalibrationPoints;
                    points[points.Count - 1].Notes = txtNote.Text;
                }

                // Update the UI
                PopulateExistingCalibrationPoints();
                UpdateCalibrationSummary();

                MessageBox.Show("Calibration point added successfully.",
                    "Calibration Added", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error adding calibration point: {ex.Message}",
                    "Add Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void PopulateExistingCalibrationPoints()
        {
            try
            {
                // Ensure list exists and do null checks
                if (calibrationPointsList == null || calibrationManager == null ||
                    calibrationManager.CurrentCalibration == null ||
                    calibrationManager.CurrentCalibration.CalibrationPoints == null)
                    return;

                calibrationPointsList.Items.Clear();

                foreach (var point in calibrationManager.CurrentCalibration.CalibrationPoints)
                {
                    if (point != null)
                        calibrationPointsList.Items.Add(point);
                }

                UpdateCalibrationSummary();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error populating points: {ex.Message}");
                // Don't show message box as it would be annoying
            }
        }

        private void UpdateCalibrationSummary()
        {
            try
            {
                // Null check on the label and calibration manager
                if (lblCalibrationSummary == null || calibrationManager == null ||
                    calibrationManager.CurrentCalibration == null)
                    return;

                if (calibrationManager.CurrentCalibration.CalibrationPoints == null ||
                    calibrationManager.CurrentCalibration.CalibrationPoints.Count < 2)
                {
                    lblCalibrationSummary.Text = "Not enough calibration points. Add at least 2 points for calibration.";
                    lblCalibrationSummary.StateCommon.ShortText.Color1 = Color.Yellow;
                }
                else
                {
                    string summary = calibrationManager.CurrentCalibration.GetCalibrationSummary();
                    if (!string.IsNullOrEmpty(summary))
                    {
                        lblCalibrationSummary.Text = summary;
                        lblCalibrationSummary.StateCommon.ShortText.Color1 = Color.LightGreen;
                    }
                    else
                    {
                        lblCalibrationSummary.Text = "Calibration data available but summary could not be generated.";
                        lblCalibrationSummary.StateCommon.ShortText.Color1 = Color.Yellow;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating summary: {ex.Message}");
                // Don't show message box as it would be annoying
            }
        }

        private void SaveCalibration()
        {
            try
            {
                if (calibrationManager == null || calibrationManager.CurrentCalibration == null ||
                    calibrationManager.CurrentCalibration.CalibrationPoints == null ||
                    calibrationManager.CurrentCalibration.CalibrationPoints.Count == 0)
                {
                    MessageBox.Show("No calibration points to save.",
                        "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                using (SaveFileDialog saveDialog = new SaveFileDialog())
                {
                    saveDialog.Filter = "Calibration Files (*.calib)|*.calib";
                    saveDialog.DefaultExt = "calib";
                    saveDialog.Title = "Save Acoustic Simulator Calibration";

                    if (saveDialog.ShowDialog() == DialogResult.OK)
                    {
                        if (calibrationManager.SaveCalibration(saveDialog.FileName))
                        {
                            MessageBox.Show("Calibration saved successfully.",
                                "Save Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving calibration: {ex.Message}",
                    "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LoadCalibration()
        {
            try
            {
                if (calibrationManager == null)
                    return;

                using (OpenFileDialog openDialog = new OpenFileDialog())
                {
                    openDialog.Filter = "Calibration Files (*.calib)|*.calib";
                    openDialog.DefaultExt = "calib";
                    openDialog.Title = "Load Acoustic Simulator Calibration";

                    if (openDialog.ShowDialog() == DialogResult.OK)
                    {
                        if (calibrationManager.LoadCalibration(openDialog.FileName))
                        {
                            MessageBox.Show("Calibration loaded successfully.",
                                "Load Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            PopulateExistingCalibrationPoints();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading calibration: {ex.Message}",
                    "Load Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}