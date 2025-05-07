using System;
using System.Linq;
using System.Windows.Forms;
using Krypton.Toolkit;

namespace CTS
{
    // Partial class to extend AcousticSimulationForm with calibration functionality
    public partial class AcousticSimulationForm
    {
        private CalibrationManager calibrationManager;
        private KryptonButton btnCalibrate;
        private KryptonCheckBox chkAutoCalibrate;
        private KryptonButton btnManageCalibration;

        /// <summary>
        /// Initialize calibration components in the form
        /// </summary>
        private void InitializeCalibrationComponents()
        {
            // Create calibration manager
            calibrationManager = new CalibrationManager(this);

            // Create the buttons and checkboxes
            btnCalibrate = new KryptonButton
            {
                Text = "Calibrate with this Result",
                Visible = false
            };
            btnCalibrate.Click += BtnCalibrate_Click;

            btnManageCalibration = new KryptonButton
            {
                Text = "Manage Calibration",
                Width = 200
            };
            btnManageCalibration.Click += BtnManageCalibration_Click;

            // Auto-calibrate checkbox - will be positioned properly in InitializeSimulationTab
            chkAutoCalibrate = new KryptonCheckBox
            {
                Text = "Auto-apply calibration",
                Width = 230,
                Checked = false,
                ToolTipValues = {
            Description = "Automatically apply calibration to new materials"
        }
            };

            // Defer the event handler attachment until the full initialization is complete
            this.Load += (s, e) => {
                if (chkAutoElasticProps != null)
                {
                    chkAutoCalibrate.CheckedChanged += (sender, args) => {
                        if (chkAutoCalibrate.Checked)
                        {
                            chkAutoElasticProps.Checked = false;
                            chkAutoElasticProps.Enabled = false;

                            // Apply calibration immediately if we have enough points
                            if (calibrationManager.CurrentCalibration.CalibrationPoints.Count >= 2)
                            {
                                ApplyCalibrationToCurrentMaterial();
                            }
                            else
                            {
                                MessageBox.Show("Not enough calibration points. Add at least 2 points using the Manage Calibration button.",
                                    "Calibration Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                chkAutoCalibrate.Checked = false;
                                chkAutoElasticProps.Enabled = true;
                            }
                        }
                        else
                        {
                            chkAutoElasticProps.Enabled = true;
                        }
                    };
                }
            };
        }

        /// <summary>
        /// Add calibration controls 
        /// </summary>
        private void InitializeCalibrationControls()
        {
            // This method should be called after the simulation results are available
            // It will add the calibration button to the results tab
            if (tabResults.Controls.Count > 0 && tabResults.Controls[0] is Panel resultsPanel)
            {
                btnCalibrate.Location = new System.Drawing.Point(20, resultsPanel.Height - 80);
                btnCalibrate.Width = 200;
                btnCalibrate.Height = 40;
                btnCalibrate.Visible = true;

                if (!resultsPanel.Controls.Contains(btnCalibrate))
                {
                    resultsPanel.Controls.Add(btnCalibrate);
                }
            }
        }

        /// <summary>
        /// Handle the Calibrate button click
        /// </summary>
        private void BtnCalibrate_Click(object sender, EventArgs e)
        {
            if (simulationResults == null)
            {
                MessageBox.Show("No simulation results available for calibration.",
                    "Calibration Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Show calibration dialog with the current simulation results
            using (CalibrationDialog dialog = new CalibrationDialog(
                calibrationManager,
                this,
                simulationResults.PWaveVelocity,
                simulationResults.SWaveVelocity,
                simulationResults.VpVsRatio))
            {
                dialog.ShowDialog(this);
            }
        }

        /// <summary>
        /// Handle the Manage Calibration button click
        /// </summary>
        private void BtnManageCalibration_Click(object sender, EventArgs e)
        {
            // Show calibration dialog with empty results (for management only)
            using (CalibrationDialog dialog = new CalibrationDialog(
                calibrationManager,
                this,
                0, 0, 0))
            {
                dialog.ShowDialog(this);
            }
        }

        /// <summary>
        /// Override the material selection changed method to apply calibration if auto-calibrate is enabled
        /// </summary>
        private void comboMaterials_SelectedIndexChanged_Extended(object sender, EventArgs e)
        {
            // Call the original method first
            comboMaterials_SelectedIndexChanged(sender, e);

            // Apply calibration if auto-calibrate is enabled
            if (chkAutoCalibrate != null && chkAutoCalibrate.Checked &&
                calibrationManager != null &&
                calibrationManager.CurrentCalibration.CalibrationPoints.Count >= 2)
            {
                ApplyCalibrationToCurrentMaterial();
            }
        }

        /// <summary>
        /// Apply calibration to the current material
        /// </summary>
        private void ApplyCalibrationToCurrentMaterial()
        {
            try
            {
                calibrationManager.ApplyCalibrationToCurrentSimulation();
                Logger.Log("[AcousticSimulationForm] Applied calibration to material: " +
                          (selectedMaterial != null ? selectedMaterial.Name : "unknown"));
            }
            catch (Exception ex)
            {
                Logger.Log("[AcousticSimulationForm] Error applying calibration: " + ex.Message);
                MessageBox.Show("Error applying calibration: " + ex.Message,
                    "Calibration Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }


        /// <summary>
        /// Get the current Young's modulus value from the UI
        /// </summary>
        public decimal GetYoungsModulus()
        {
            return numYoungsModulus != null ? numYoungsModulus.Value : 0;
        }

        /// <summary>
        /// Get the current Poisson's ratio value from the UI
        /// </summary>
        public decimal GetPoissonRatio()
        {
            return numPoissonRatio != null ? numPoissonRatio.Value : 0;
        }

        /// <summary>
        /// Set the Young's modulus value in the UI
        /// </summary>
        public void SetYoungsModulus(decimal value)
        {
            if (numYoungsModulus != null)
            {
                // Clamp within valid range
                value = Math.Max(numYoungsModulus.Minimum, Math.Min(numYoungsModulus.Maximum, value));
                numYoungsModulus.Value = value;
            }
        }

        /// <summary>
        /// Set the Poisson's ratio value in the UI
        /// </summary>
        public void SetPoissonRatio(decimal value)
        {
            if (numPoissonRatio != null)
            {
                // Clamp within valid range (0-0.5 for Poisson's ratio)
                value = Math.Max(0.0m, Math.Min(0.5m, value));
                numPoissonRatio.Value = value;
            }
        }

        /// <summary>
        /// Extended method to update the results display with calibration information
        /// </summary>
        private void UpdateResultsDisplayWithCalibration()
        {
            // First call the original method
            UpdateResultsDisplay();

            // Initialize calibration controls in the results tab
            InitializeCalibrationControls();

            // Show calibration accuracy if we have calibration data
            if (calibrationManager != null &&
                calibrationManager.CurrentCalibration.CalibrationPoints.Count >= 2 &&
                simulationResults != null)
            {
                try
                {
                    // Get the nearest calibration point by density
                    var points = calibrationManager.CurrentCalibration.CalibrationPoints;
                    var currentDensity = selectedMaterial != null ? selectedMaterial.Density : 0;

                    var closestPoint = points
                        .OrderBy(p => Math.Abs(p.MeasuredDensity - currentDensity))
                        .FirstOrDefault();

                    if (closestPoint != null && tabResults != null && tabResults.Controls.Count > 0)
                    {
                        // Calculate the expected VpVs based on calibration
                        double expectedVpVs = calibrationManager.CurrentCalibration.PredictVpVsRatio(currentDensity);

                        // Calculate error percentage
                        double error = 0;
                        if (expectedVpVs > 0)
                        {
                            error = Math.Abs(simulationResults.VpVsRatio - expectedVpVs) / expectedVpVs * 100;
                        }

                        
                        if (tabResults.Controls[0] is Panel resultsPanel)
                        {
                            // Try to find existing label or create a new one
                            KryptonLabel lblCalibrationInfo = null;
                            foreach (Control ctrl in resultsPanel.Controls)
                            {
                                if (ctrl is KryptonLabel label && label.Name == "lblCalibrationInfo")
                                {
                                    lblCalibrationInfo = label;
                                    break;
                                }
                            }

                            if (lblCalibrationInfo == null)
                            {
                                lblCalibrationInfo = new KryptonLabel
                                {
                                    Name = "lblCalibrationInfo",
                                    Width = 300,
                                    Height = 80
                                };
                                resultsPanel.Controls.Add(lblCalibrationInfo);
                            }

                            // Set position - make sure btnCalibrate is positioned first
                            if (btnCalibrate != null && btnCalibrate.Parent == resultsPanel)
                            {
                                lblCalibrationInfo.Location = new System.Drawing.Point(20, btnCalibrate.Location.Y - 100);
                            }
                            else
                            {
                                lblCalibrationInfo.Location = new System.Drawing.Point(20, resultsPanel.Height - 180);
                            }

                            // Update the label text and color
                            lblCalibrationInfo.Text =
                                $"Calibration Accuracy:\n" +
                                $"Expected Vp/Vs: {expectedVpVs:F3}\n" +
                                $"Measured Vp/Vs: {simulationResults.VpVsRatio:F3}\n" +
                                $"Error: {error:F2}%";

                            lblCalibrationInfo.StateCommon.ShortText.Color1 = error < 5 ?
                                System.Drawing.Color.LightGreen :
                                System.Drawing.Color.Yellow;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error in UpdateResultsDisplayWithCalibration: {ex.Message}");
                    // Don't show error to user, just log it
                    Logger.Log($"[AcousticSimulationForm] Error updating calibration display: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Extension point to initialize the form with calibration components
        /// </summary>
        private void InitializeComponentWithCalibration()
        {
            // Call the original initialization
            InitializeComponent();

            // Initialize calibration components
            InitializeCalibrationComponents();

            // Hook into the extended material selection change event
            comboMaterials.SelectedIndexChanged -= comboMaterials_SelectedIndexChanged;
            comboMaterials.SelectedIndexChanged += comboMaterials_SelectedIndexChanged_Extended;

            // Hook into the simulation completion to show calibration accuracy
            SimulationCompleted -= Simulator_SimulationCompleted;
            SimulationCompleted += (s, e) => {
                Simulator_SimulationCompleted(s, e);
                UpdateResultsDisplayWithCalibration();
            };
        }
    }
}