using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
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
        private KryptonRadioButton rbVpVsRatio;
        private KryptonRadioButton rbSeparateValues;
        private KryptonNumericUpDown numKnownVp;
        private KryptonNumericUpDown numKnownVs;
        double youngModulus = 0;
        double poissonRatio = 0.25;
        private KryptonNumericUpDown numConfiningPressure;
        private KryptonPanel pnlInputMethod;
        private KryptonLabel lblVpVsValue;
        private KryptonButton btnExportPlot;
        private KryptonLabel lblPoissonValue;

        public CalibrationDialog(CalibrationManager manager, AcousticSimulationForm form,
                         double vp, double vs, double vpVsRatio)
        {
            try
            {
                // Ensure all parameters are valid first
                this.calibrationManager = manager ?? throw new ArgumentNullException(nameof(manager));
                this.simulationForm = form ?? throw new ArgumentNullException(nameof(form));

                // Now safely get values from simulationForm
                youngModulus = (double)simulationForm.GetYoungsModulus();
                poissonRatio = (double)simulationForm.GetPoissonRatio();

                // Ensure we have a valid Poisson's ratio
                if (poissonRatio <= 0 || poissonRatio >= 0.5)
                {
                    poissonRatio = 0.25; // Use default if invalid
                }

                // Ensure we have reasonable values for wave velocities
                this.simulatedVp = Math.Max(0, vp);
                this.simulatedVs = Math.Max(0, vs);

                // Calculate VpVs ratio directly if necessary
                if (vpVsRatio <= 0 && this.simulatedVs > 0)
                {
                    this.simulatedVpVsRatio = this.simulatedVp / this.simulatedVs;
                    Logger.Log($"[CalibrationDialog] Recalculated Vp/Vs ratio: {this.simulatedVpVsRatio:F3}");
                }
                else
                {
                    this.simulatedVpVsRatio = vpVsRatio;
                }

                // Validate the final value isn't still zero
                if (this.simulatedVpVsRatio <= 0)
                {
                    Logger.Log("[CalibrationDialog] Warning: VpVs ratio is still <= 0, setting default");
                    this.simulatedVpVsRatio = 1.732; // Default sqrt(3)
                }

                InitializeComponent();

                // Double-check that the values are set in UI after initialization
                if (lblVpVsValue != null)
                {
                    lblVpVsValue.Text = $"{this.simulatedVpVsRatio:F3}";
                    Logger.Log($"[CalibrationDialog] Set Vp/Vs display to: {this.simulatedVpVsRatio:F3}");
                }

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
                this.MinimumSize = new Size(800, 600);
                this.Size = new Size(1000, 900);
                this.StartPosition = FormStartPosition.CenterParent;
                this.BackColor = Color.FromArgb(45, 45, 48);
                this.Icon = Properties.Resources.favicon;

                // Create main panel with padding
                KryptonPanel mainPanel = new KryptonPanel();
                mainPanel.Dock = DockStyle.Fill;
                mainPanel.Padding = new Padding(10);
                mainPanel.StateCommon.Color1 = Color.FromArgb(45, 45, 48);
                mainPanel.StateCommon.Color2 = Color.FromArgb(45, 45, 48);
                this.Controls.Add(mainPanel);

                // Create split container - DO NOT SET SPLITTER DISTANCE HERE
                SplitContainer splitContainer = new SplitContainer();
                splitContainer.Dock = DockStyle.Fill;
                splitContainer.Orientation = Orientation.Horizontal;
                splitContainer.Panel1MinSize = 100;  // Small minimum
                splitContainer.Panel2MinSize = 100;  // Small minimum
                splitContainer.BackColor = Color.FromArgb(45, 45, 48);
                // IMPORTANT: Do not set SplitterDistance here
                mainPanel.Controls.Add(splitContainer);

                // === Top Panel - Existing Calibration Points ===
                KryptonGroupBox existingPointsGroup = new KryptonGroupBox();
                existingPointsGroup.Dock = DockStyle.Fill;
                existingPointsGroup.Text = "Existing Calibration Points";
                existingPointsGroup.StateCommon.Content.ShortText.Color1 = Color.White;
                splitContainer.Panel1.Controls.Add(existingPointsGroup);

                // Create a panel for the list and summary
                KryptonPanel listPanel = new KryptonPanel();
                listPanel.Dock = DockStyle.Fill;
                existingPointsGroup.Panel.Controls.Add(listPanel);

                // List to display existing calibration points
                calibrationPointsList = new KryptonListBox();
                calibrationPointsList.Name = "calibrationPointsList";
                calibrationPointsList.Dock = DockStyle.Fill;
                calibrationPointsList.StateCommon.Back.Color1 = Color.FromArgb(30, 30, 30);
                calibrationPointsList.StateCommon.Item.Content.ShortText.Color1 = Color.White;
                listPanel.Controls.Add(calibrationPointsList);

                // Calibration summary panel with scrollable text
                KryptonPanel summaryPanel = new KryptonPanel();
                summaryPanel.Dock = DockStyle.Bottom;
                summaryPanel.Height = 100;
                summaryPanel.AutoScroll = true;
                listPanel.Controls.Add(summaryPanel);

                lblCalibrationSummary = new KryptonLabel();
                lblCalibrationSummary.Name = "lblCalibrationSummary";
                lblCalibrationSummary.Location = new Point(5, 5);
                lblCalibrationSummary.AutoSize = true;
                lblCalibrationSummary.MaximumSize = new Size(0, 0);
                lblCalibrationSummary.StateCommon.ShortText.Color1 = Color.LightGreen;
                lblCalibrationSummary.StateCommon.ShortText.Font = new Font("Segoe UI", 9, FontStyle.Regular);
                lblCalibrationSummary.Text = "No calibration data available. Add at least 2 calibration points.";
                summaryPanel.Controls.Add(lblCalibrationSummary);

                // Button panel for calibration set actions
                KryptonPanel calibrationActionPanel = new KryptonPanel();
                calibrationActionPanel.Dock = DockStyle.Bottom;
                calibrationActionPanel.Height = 50;
                calibrationActionPanel.StateCommon.Color1 = Color.FromArgb(45, 45, 48);
                existingPointsGroup.Panel.Controls.Add(calibrationActionPanel);

                // Flow layout panel for buttons
                FlowLayoutPanel buttonFlow = new FlowLayoutPanel();
                buttonFlow.Dock = DockStyle.Fill;
                buttonFlow.FlowDirection = FlowDirection.LeftToRight;
                buttonFlow.Padding = new Padding(5);
                calibrationActionPanel.Controls.Add(buttonFlow);

                // Add buttons
                KryptonButton btnRemovePoint = new KryptonButton();
                btnRemovePoint.Text = "Remove Point";
                btnRemovePoint.AutoSize = true;
                btnRemovePoint.Click += BtnRemovePoint_Click;
                buttonFlow.Controls.Add(btnRemovePoint);

                KryptonButton btnSaveCalibration = new KryptonButton();
                btnSaveCalibration.Text = "Save Calibration";
                btnSaveCalibration.AutoSize = true;
                btnSaveCalibration.Click += BtnSaveCalibration_Click;
                buttonFlow.Controls.Add(btnSaveCalibration);

                KryptonButton btnLoadCalibration = new KryptonButton();
                btnLoadCalibration.Text = "Load Calibration";
                btnLoadCalibration.AutoSize = true;
                btnLoadCalibration.Click += BtnLoadCalibration_Click;
                buttonFlow.Controls.Add(btnLoadCalibration);

                KryptonButton btnApplyCalibration = new KryptonButton();
                btnApplyCalibration.Text = "Apply Calibration";
                btnApplyCalibration.AutoSize = true;
                btnApplyCalibration.Click += BtnApplyCalibration_Click;
                buttonFlow.Controls.Add(btnApplyCalibration);

                btnExportPlot = new KryptonButton();
                btnExportPlot.Text = "Export Plot";
                btnExportPlot.AutoSize = true;
                btnExportPlot.Click += BtnExportPlot_Click;
                buttonFlow.Controls.Add(btnExportPlot);

                // === Bottom Panel - Add New Calibration Point ===
                KryptonGroupBox addPointGroup = new KryptonGroupBox();
                addPointGroup.Dock = DockStyle.Fill;
                addPointGroup.Text = "Add New Calibration Point";
                addPointGroup.StateCommon.Content.ShortText.Color1 = Color.White;
                splitContainer.Panel2.Controls.Add(addPointGroup);

                // Scrollable panel for content
                KryptonPanel addPointPanel = new KryptonPanel();
                addPointPanel.Dock = DockStyle.Fill;
                addPointPanel.AutoScroll = true;
                addPointPanel.StateCommon.Color1 = Color.FromArgb(45, 45, 48);
                addPointGroup.Panel.Controls.Add(addPointPanel);

                // Create inner panel for fixed layout
                KryptonPanel innerPanel = new KryptonPanel();
                innerPanel.Width = 750;
                innerPanel.Height = 400;
                innerPanel.Location = new Point(10, 10);
                innerPanel.StateCommon.Color1 = Color.FromArgb(45, 45, 48);
                addPointPanel.Controls.Add(innerPanel);

                // Current material info
                int yPosition = 10;
                KryptonLabel lblCurrentMaterial = new KryptonLabel();
                lblCurrentMaterial.Text = "Current Material:";
                lblCurrentMaterial.Location = new Point(10, yPosition);
                lblCurrentMaterial.StateCommon.ShortText.Color1 = Color.White;
                innerPanel.Controls.Add(lblCurrentMaterial);

                KryptonLabel lblMaterialValue = new KryptonLabel();
                lblMaterialValue.Name = "lblMaterialValue";
                lblMaterialValue.Location = new Point(150, yPosition);
                lblMaterialValue.StateCommon.ShortText.Color1 = Color.LightYellow;
                lblMaterialValue.Text = simulationForm.SelectedMaterial?.Name ?? "None";
                innerPanel.Controls.Add(lblMaterialValue);

                // Current density info
                yPosition += 30;
                KryptonLabel lblCurrentDensity = new KryptonLabel();
                lblCurrentDensity.Text = "Current Density:";
                lblCurrentDensity.Location = new Point(10, yPosition);
                lblCurrentDensity.StateCommon.ShortText.Color1 = Color.White;
                innerPanel.Controls.Add(lblCurrentDensity);

                KryptonLabel lblDensityValue = new KryptonLabel();
                lblDensityValue.Name = "lblDensityValue";
                lblDensityValue.Location = new Point(150, yPosition);
                lblDensityValue.StateCommon.ShortText.Color1 = Color.White;
                lblDensityValue.Text = $"{simulationForm.SelectedMaterial?.Density ?? 0:F1} kg/m³";
                innerPanel.Controls.Add(lblDensityValue);

                // Simulated Vp/Vs ratio
                yPosition += 30;
                KryptonLabel lblSimulatedVpVs = new KryptonLabel();
                lblSimulatedVpVs.Text = "Simulated Vp/Vs:";
                lblSimulatedVpVs.Location = new Point(10, yPosition);
                lblSimulatedVpVs.StateCommon.ShortText.Color1 = Color.White;
                innerPanel.Controls.Add(lblSimulatedVpVs);

                lblVpVsValue = new KryptonLabel();
                lblVpVsValue.Name = "lblVpVsValue";
                lblVpVsValue.Location = new Point(150, yPosition);
                lblVpVsValue.StateCommon.ShortText.Color1 = Color.LightGreen;
                lblVpVsValue.Text = simulatedVpVsRatio > 0 ? $"{simulatedVpVsRatio:F3}" : "N/A";
                innerPanel.Controls.Add(lblVpVsValue);

                // Material elastic properties
                yPosition += 30;
                KryptonLabel lblYoungsModulus = new KryptonLabel();
                lblYoungsModulus.Text = "Young's Modulus:";
                lblYoungsModulus.Location = new Point(10, yPosition);
                lblYoungsModulus.StateCommon.ShortText.Color1 = Color.White;
                innerPanel.Controls.Add(lblYoungsModulus);

                KryptonLabel lblYoungsValue = new KryptonLabel();
                lblYoungsValue.Name = "lblYoungsValue";
                lblYoungsValue.Location = new Point(150, yPosition);
                lblYoungsValue.StateCommon.ShortText.Color1 = Color.White;
                lblYoungsValue.Text = $"{youngModulus:F2} MPa";
                innerPanel.Controls.Add(lblYoungsValue);

                yPosition += 30;
                KryptonLabel lblPoisson = new KryptonLabel();
                lblPoisson.Text = "Poisson's Ratio:";
                lblPoisson.Location = new Point(10, yPosition);
                lblPoisson.StateCommon.ShortText.Color1 = Color.White;
                innerPanel.Controls.Add(lblPoisson);

                lblPoissonValue = new KryptonLabel();
                lblPoissonValue.Name = "lblPoissonValue";
                lblPoissonValue.Location = new Point(150, yPosition);
                lblPoissonValue.StateCommon.ShortText.Color1 = Color.White;
                lblPoissonValue.Text = $"{poissonRatio:F4}";
                innerPanel.Controls.Add(lblPoissonValue);

                // Input method selection panel
                pnlInputMethod = new KryptonPanel();
                pnlInputMethod.Location = new Point(350, 10);
                pnlInputMethod.Width = 380;
                pnlInputMethod.Height = 250;
                pnlInputMethod.StateCommon.Color1 = Color.FromArgb(45, 45, 48);
                pnlInputMethod.StateCommon.Color2 = Color.FromArgb(45, 45, 48);
                innerPanel.Controls.Add(pnlInputMethod);

                // Header for input method
                KryptonLabel lblInputMethod = new KryptonLabel();
                lblInputMethod.Text = "Calibration Input Method:";
                lblInputMethod.Location = new Point(10, 5);
                lblInputMethod.StateCommon.ShortText.Color1 = Color.LightGray;
                lblInputMethod.StateCommon.ShortText.Font = new Font("Segoe UI", 10, FontStyle.Bold);
                pnlInputMethod.Controls.Add(lblInputMethod);

                // Radio buttons for input type
                rbVpVsRatio = new KryptonRadioButton();
                rbVpVsRatio.Text = "Enter Vp/Vs Ratio";
                rbVpVsRatio.Location = new Point(10, 30);
                rbVpVsRatio.Width = 200;
                rbVpVsRatio.Checked = true;
                rbVpVsRatio.CheckedChanged += RbInputType_CheckedChanged;
                pnlInputMethod.Controls.Add(rbVpVsRatio);

                rbSeparateValues = new KryptonRadioButton();
                rbSeparateValues.Text = "Enter Vp and Vs separately";
                rbSeparateValues.Location = new Point(10, 55);
                rbSeparateValues.Width = 200;
                rbSeparateValues.CheckedChanged += RbInputType_CheckedChanged;
                pnlInputMethod.Controls.Add(rbSeparateValues);

                // Vp/Vs ratio input
                KryptonLabel lblKnownVpVs = new KryptonLabel();
                lblKnownVpVs.Text = "Known Vp/Vs Ratio:";
                lblKnownVpVs.Location = new Point(10, 85);
                lblKnownVpVs.StateCommon.ShortText.Color1 = Color.White;
                pnlInputMethod.Controls.Add(lblKnownVpVs);

                numKnownVpVs = new KryptonNumericUpDown();
                numKnownVpVs.Name = "numKnownVpVs";
                numKnownVpVs.Location = new Point(160, 85);
                numKnownVpVs.Width = 100;
                numKnownVpVs.DecimalPlaces = 3;
                numKnownVpVs.Minimum = 0.0m;
                numKnownVpVs.Maximum = 4.0m;
                numKnownVpVs.Value = Math.Max(0.0m, Math.Min(4.0m, (decimal)simulatedVpVsRatio));
                numKnownVpVs.Increment = 0.001m;
                numKnownVpVs.ValueChanged += NumKnownVpVs_ValueChanged;  // Fixed: Use correct event handler
                pnlInputMethod.Controls.Add(numKnownVpVs);

                // Separate Vp and Vs inputs
                KryptonLabel lblKnownVp = new KryptonLabel();
                lblKnownVp.Text = "Known Vp (m/s):";
                lblKnownVp.Location = new Point(10, 115);
                lblKnownVp.StateCommon.ShortText.Color1 = Color.White;
                pnlInputMethod.Controls.Add(lblKnownVp);

                numKnownVp = new KryptonNumericUpDown();
                numKnownVp.Name = "numKnownVp";
                numKnownVp.Location = new Point(160, 115);
                numKnownVp.Width = 100;
                numKnownVp.DecimalPlaces = 0;
                numKnownVp.Minimum = 0;
                numKnownVp.Maximum = 10000;
                numKnownVp.Value = 5000;
                numKnownVp.Visible = false;
                pnlInputMethod.Controls.Add(numKnownVp);

                KryptonLabel lblKnownVs = new KryptonLabel();
                lblKnownVs.Text = "Known Vs (m/s):";
                lblKnownVs.Location = new Point(10, 145);
                lblKnownVs.StateCommon.ShortText.Color1 = Color.White;
                pnlInputMethod.Controls.Add(lblKnownVs);

                numKnownVs = new KryptonNumericUpDown();
                numKnownVs.Name = "numKnownVs";
                numKnownVs.Location = new Point(160, 145);
                numKnownVs.Width = 100;
                numKnownVs.DecimalPlaces = 0;
                numKnownVs.Minimum = 0;
                numKnownVs.Maximum = 10000;
                numKnownVs.Value = 3000;
                numKnownVs.Visible = false;
                pnlInputMethod.Controls.Add(numKnownVs);

                // Confining Pressure input
                KryptonLabel lblConfining = new KryptonLabel();
                lblConfining.Text = "Confining Pressure (MPa):";
                lblConfining.Location = new Point(10, 175);
                lblConfining.StateCommon.ShortText.Color1 = Color.White;
                pnlInputMethod.Controls.Add(lblConfining);

                numConfiningPressure = new KryptonNumericUpDown();
                numConfiningPressure.Name = "numConfiningPressure";
                numConfiningPressure.Location = new Point(160, 175);
                numConfiningPressure.Width = 100;
                numConfiningPressure.DecimalPlaces = 1;
                numConfiningPressure.Minimum = 0;
                numConfiningPressure.Maximum = 1000;
                numConfiningPressure.Value = 1.0m;
                pnlInputMethod.Controls.Add(numConfiningPressure);

                // Calibration note
                yPosition = 175;
                KryptonLabel lblNote = new KryptonLabel();
                lblNote.Text = "Notes (optional):";
                lblNote.Location = new Point(10, yPosition);
                lblNote.StateCommon.ShortText.Color1 = Color.White;
                innerPanel.Controls.Add(lblNote);

                txtNote = new KryptonTextBox();
                txtNote.Name = "txtNote";
                txtNote.Location = new Point(10, yPosition + 20);
                txtNote.Width = 320;
                txtNote.Height = 60;
                txtNote.Multiline = true;
                innerPanel.Controls.Add(txtNote);

                // Add Calibration button
                KryptonButton btnAddCalibration = new KryptonButton();
                btnAddCalibration.Text = "Add Calibration Point";
                btnAddCalibration.Location = new Point(10, yPosition + 90);
                btnAddCalibration.Width = 200;
                btnAddCalibration.Height = 35;
                btnAddCalibration.Click += BtnAddCalibration_Click;
                innerPanel.Controls.Add(btnAddCalibration);

                // Form close button panel at the very bottom
                KryptonPanel bottomPanel = new KryptonPanel();
                bottomPanel.Dock = DockStyle.Bottom;
                bottomPanel.Height = 50;
                bottomPanel.StateCommon.Color1 = Color.FromArgb(45, 45, 48);
                this.Controls.Add(bottomPanel);

                KryptonButton btnClose = new KryptonButton();
                btnClose.Text = "Close";
                btnClose.Location = new Point((bottomPanel.Width - 100) / 2, 10);
                btnClose.Width = 100;
                btnClose.Height = 30;
                btnClose.DialogResult = DialogResult.OK;
                btnClose.Anchor = AnchorStyles.None;
                bottomPanel.Controls.Add(btnClose);

                // Set splitter AFTER form is shown
                this.Shown += (sender, e) => {
                    if (splitContainer.Height > 0)
                    {
                        splitContainer.SplitterDistance = splitContainer.Height / 2;
                    }
                };
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error initializing controls: {ex.Message}",
                    "Control Initialization Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void RbInputType_CheckedChanged(object sender, EventArgs e)
        {
            if (rbVpVsRatio.Checked)
            {
                numKnownVpVs.Visible = true;
                numKnownVp.Visible = false;
                numKnownVs.Visible = false;

                // Remove value changed handlers from Vp and Vs when in ratio mode
                numKnownVp.ValueChanged -= UpdateVpVsFromSeparateValues;
                numKnownVs.ValueChanged -= UpdateVpVsFromSeparateValues;
            }
            else
            {
                numKnownVpVs.Visible = false;
                numKnownVp.Visible = true;
                numKnownVs.Visible = true;

                // Add value changed handlers for Vp and Vs when in separate values mode
                numKnownVp.ValueChanged += UpdateVpVsFromSeparateValues;
                numKnownVs.ValueChanged += UpdateVpVsFromSeparateValues;
            }
        }

        // Fixed: Separate event handler for when VpVs ratio is directly changed
        private void NumKnownVpVs_ValueChanged(object sender, EventArgs e)
        {
            if (rbVpVsRatio.Checked && numKnownVpVs.Value > 0)
            {
                double vpVs = (double)numKnownVpVs.Value;
                double nu = CalibrationManager.PoissonFromVpVs(vpVs);
                lblPoissonValue.Text = nu.ToString("F4", CultureInfo.InvariantCulture);
            }
        }

        // Fixed: New event handler for when Vp or Vs values are changed
        private void UpdateVpVsFromSeparateValues(object sender, EventArgs e)
        {
            if (rbSeparateValues.Checked && numKnownVs.Value > 0)
            {
                double vpVs = (double)(numKnownVp.Value / numKnownVs.Value);
                // Update the VpVs display without triggering its ValueChanged event
                numKnownVpVs.ValueChanged -= NumKnownVpVs_ValueChanged;
                numKnownVpVs.Value = (decimal)vpVs;
                numKnownVpVs.ValueChanged += NumKnownVpVs_ValueChanged;

                double nu = CalibrationManager.PoissonFromVpVs(vpVs);
                lblPoissonValue.Text = nu.ToString("F4", CultureInfo.InvariantCulture);
            }
        }

        // Event handlers
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
                if (calibrationManager == null)
                    return;

                double confiningPressure = (double)numConfiningPressure.Value;

                if (rbVpVsRatio.Checked)
                {
                    // Using Vp/Vs ratio
                    double knownVpVs = (double)numKnownVpVs.Value;
                    calibrationManager.AddCurrentSimulationAsCalibrationPoint(
                        knownVpVs, simulatedVp, simulatedVs, confiningPressure);
                }
                else
                {
                    // Using separate Vp and Vs values
                    double knownVp = (double)numKnownVp.Value;
                    double knownVs = (double)numKnownVs.Value;
                    calibrationManager.AddCurrentSimulationAsCalibrationPoint(
                        knownVp, knownVs, simulatedVp, simulatedVs, confiningPressure);
                }

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

        private void BtnExportPlot_Click(object sender, EventArgs e)
        {
            try
            {
                if (calibrationManager == null || calibrationManager.CurrentCalibration == null ||
                    calibrationManager.CurrentCalibration.CalibrationPoints == null ||
                    calibrationManager.CurrentCalibration.CalibrationPoints.Count < 2)
                {
                    MessageBox.Show("Need at least 2 calibration points to create a plot.",
                        "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                using (SaveFileDialog saveDialog = new SaveFileDialog())
                {
                    saveDialog.Filter = "JPEG Files (*.jpg)|*.jpg|PNG Files (*.png)|*.png";
                    saveDialog.DefaultExt = "jpg";
                    saveDialog.Title = "Export Calibration Curve Plot";

                    if (saveDialog.ShowDialog() == DialogResult.OK)
                    {
                        ExportCalibrationPlot(saveDialog.FileName);
                        MessageBox.Show("Calibration plot exported successfully.",
                            "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting plot: {ex.Message}",
                    "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Additional methods
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

        private void ExportCalibrationPlot(string filename)
        {
            var points = calibrationManager.CurrentCalibration.CalibrationPoints;

            // Create chart
            using (Chart chart = new Chart())
            {
                chart.Size = new Size(800, 600);
                chart.BackColor = Color.White;

                // Create chart area
                ChartArea chartArea = new ChartArea("MainArea");
                chartArea.BackColor = Color.White;
                chartArea.AxisX.Title = "Confining Pressure (MPa)";
                chartArea.AxisY.Title = "Vp/Vs Ratio";
                chartArea.AxisX.TitleFont = new Font("Arial", 12, FontStyle.Bold);
                chartArea.AxisY.TitleFont = new Font("Arial", 12, FontStyle.Bold);
                chartArea.AxisX.LabelStyle.Font = new Font("Arial", 10);
                chartArea.AxisY.LabelStyle.Font = new Font("Arial", 10);
                chart.ChartAreas.Add(chartArea);

                // Create legend
                Legend legend = new Legend("Legend");
                legend.Font = new Font("Arial", 10);
                legend.Docking = Docking.Right;
                chart.Legends.Add(legend);

                // Add title
                Title title = new Title("Acoustic Calibration Curve", Docking.Top,
                    new Font("Arial", 16, FontStyle.Bold), Color.Black);
                chart.Titles.Add(title);

                // Add material name as subtitle
                if (simulationForm.SelectedMaterial != null)
                {
                    Title subtitle = new Title(simulationForm.SelectedMaterial.Name,
                        Docking.Top, new Font("Arial", 12), Color.DarkGray);
                    chart.Titles.Add(subtitle);
                }

                // Create series for actual points
                Series actualSeries = new Series("Actual Data");
                actualSeries.ChartType = SeriesChartType.Point;
                actualSeries.MarkerStyle = MarkerStyle.Circle;
                actualSeries.MarkerSize = 10;
                actualSeries.MarkerColor = Color.Blue;
                actualSeries.IsVisibleInLegend = true;

                // Create series for simulated points
                Series simulatedSeries = new Series("Simulated Data");
                simulatedSeries.ChartType = SeriesChartType.Point;
                simulatedSeries.MarkerStyle = MarkerStyle.Diamond;
                simulatedSeries.MarkerSize = 10;
                simulatedSeries.MarkerColor = Color.Red;
                simulatedSeries.IsVisibleInLegend = true;

                // Add data points and prepare for regression
                List<double> pressures = new List<double>();
                List<double> actualRatios = new List<double>();
                List<double> simulatedRatios = new List<double>();

                foreach (var point in points)
                {
                    // Add actual point
                    actualSeries.Points.AddXY(point.ConfiningPressureMPa, point.KnownVpVsRatio);

                    // Add simulated point
                    simulatedSeries.Points.AddXY(point.ConfiningPressureMPa, point.SimulatedVpVsRatio);

                    // Collect for regression
                    pressures.Add(point.ConfiningPressureMPa);
                    actualRatios.Add(point.KnownVpVsRatio);
                    simulatedRatios.Add(point.SimulatedVpVsRatio);
                }

                chart.Series.Add(actualSeries);
                chart.Series.Add(simulatedSeries);

                // Add regression lines if enough points
                if (points.Count >= 2)
                {
                    // Calculate regression for actual data
                    var actualRegression = CalculateLinearRegression(pressures.ToArray(), actualRatios.ToArray());
                    AddRegressionLine(chart, "Actual Trend", actualRegression, pressures.Min(), pressures.Max(),
                        Color.Blue, ChartDashStyle.Solid);

                    // Calculate regression for simulated data
                    var simulatedRegression = CalculateLinearRegression(pressures.ToArray(), simulatedRatios.ToArray());
                    AddRegressionLine(chart, "Simulated Trend", simulatedRegression, pressures.Min(), pressures.Max(),
                        Color.Red, ChartDashStyle.Dash);
                }

                // Add R² values as annotations if available
                if (points.Count >= 3)
                {
                    var actualR2 = CalculateRSquared(pressures.ToArray(), actualRatios.ToArray());
                    var simulatedR2 = CalculateRSquared(pressures.ToArray(), simulatedRatios.ToArray());

                    TextAnnotation r2Annotation = new TextAnnotation();
                    r2Annotation.Text = $"R² Actual: {actualR2:F4}\nR² Simulated: {simulatedR2:F4}";
                    r2Annotation.X = 80;
                    r2Annotation.Y = 10;
                    r2Annotation.Font = new Font("Arial", 10);
                    r2Annotation.ForeColor = Color.Black;
                    chart.Annotations.Add(r2Annotation);
                }

                // Save to file
                ImageFormat format = filename.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ?
                    ImageFormat.Png : ImageFormat.Jpeg;
                chart.SaveImage(filename, format);
            }
        }

        private (double slope, double intercept) CalculateLinearRegression(double[] x, double[] y)
        {
            int n = x.Length;
            double sumX = x.Sum();
            double sumY = y.Sum();
            double sumXY = x.Zip(y, (xi, yi) => xi * yi).Sum();
            double sumX2 = x.Select(xi => xi * xi).Sum();

            double slope = (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);
            double intercept = (sumY - slope * sumX) / n;

            return (slope, intercept);
        }

        private double CalculateRSquared(double[] x, double[] y)
        {
            var regression = CalculateLinearRegression(x, y);
            double meanY = y.Average();

            double totalSS = y.Select(yi => Math.Pow(yi - meanY, 2)).Sum();
            double residualSS = x.Zip(y, (xi, yi) =>
                Math.Pow(yi - (regression.slope * xi + regression.intercept), 2)).Sum();

            return 1 - (residualSS / totalSS);
        }

        private void AddRegressionLine(Chart chart, string name, (double slope, double intercept) regression,
            double minX, double maxX, Color color, ChartDashStyle dashStyle)
        {
            Series lineSeries = new Series(name);
            lineSeries.ChartType = SeriesChartType.Line;
            lineSeries.Color = color;
            lineSeries.BorderWidth = 2;
            lineSeries.BorderDashStyle = dashStyle;

            // Add points for the regression line
            lineSeries.Points.AddXY(minX, regression.slope * minX + regression.intercept);
            lineSeries.Points.AddXY(maxX, regression.slope * maxX + regression.intercept);

            chart.Series.Add(lineSeries);
        }
    }
}