using System;
using System.Drawing;
using System.Windows.Forms;
using Krypton.Toolkit;
using System.Collections.Generic;
using System.Linq;

namespace CTS
{
    public partial class TriaxialSimulationForm
    {
        // Remove the KryptonTabControl and instead use a direct panel approach
        private Panel calibrationPanel;
        private KryptonButton btnCalibrate;

        private void InitializeCalibrationComponents()
        {
            // Create a calibration panel instead of tabs
            calibrationPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(42, 42, 42),
                Visible = false // Initially hidden
            };

            // Add to appropriate container
            if (renderPanel != null)
            {
                renderPanel.Controls.Add(calibrationPanel);
                calibrationPanel.BringToFront(); // Make sure it's on top when visible
            }

            // Create calibration button
            btnCalibrate = new KryptonButton
            {
                Text = "Calibrate",
                Location = new Point(10, 1050),
                Width = 310,
                Height = 30,
                StateCommon = {
                    Back = { Color1 = Color.FromArgb(100, 100, 160) },
                    Content = { ShortText = { Color1 = Color.White } }
                }
            };
            btnCalibrate.Click += BtnCalibrate_Click;

            // Add button to control panel
            // Find the controls content panel to add the button
            Panel controlsContent = null;
            foreach (Control control in this.Controls)
            {
                if (control is TableLayoutPanel mainLayout)
                {
                    foreach (Control panelControl in mainLayout.Controls)
                    {
                        if (panelControl is Panel panel)
                        {
                            foreach (Control c in panel.Controls)
                            {
                                if (c is Panel scrollablePanel && scrollablePanel.AutoScroll)
                                {
                                    foreach (Control sc in scrollablePanel.Controls)
                                    {
                                        if (sc is Panel content && content.Height > 800)
                                        {
                                            controlsContent = content;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (controlsContent != null)
            {
                controlsContent.Controls.Add(btnCalibrate);
            }
        }

        private void BtnCalibrate_Click(object sender, EventArgs e)
        {
            OpenCalibrationDialog();
        }

        private void OpenCalibrationDialog()
        {
            using (var dialog = new Modules.Triaxial_Simulation.TriaxialCalibrationDialog(this))
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    // Handle calibration results
                    // Update the simulation based on calibration parameters
                    UpdateSimulationFromCalibration(dialog.CalibrationParameters);
                }
            }
        }

        private void UpdateSimulationFromCalibration(CTS.Modules.Triaxial_Simulation.CalibrationParameters parameters)
        {
            // Apply calibration parameters to the simulation
            if (parameters != null)
            {
                youngModulus = parameters.YoungModulus;
                poissonRatio = parameters.PoissonRatio;
                yieldStrength = parameters.YieldStrength;
                brittleStrength = parameters.BrittleStrength;

                // Update cohesion and friction angle if available
                if (parameters.Cohesion > 0)
                    cohesion = parameters.Cohesion;

                if (parameters.FrictionAngle > 0)
                    frictionAngle = parameters.FrictionAngle;

                // Update UI controls to reflect changes
                if (numYoungModulus != null && !numYoungModulus.IsDisposed)
                    numYoungModulus.Value = (decimal)youngModulus;

                if (numPoissonRatio != null && !numPoissonRatio.IsDisposed)
                    numPoissonRatio.Value = (decimal)poissonRatio;

                if (numYieldStrength != null && !numYieldStrength.IsDisposed)
                    numYieldStrength.Value = (decimal)yieldStrength;

                if (numBrittleStrength != null && !numBrittleStrength.IsDisposed)
                    numBrittleStrength.Value = (decimal)brittleStrength;

                if (numCohesion != null && !numCohesion.IsDisposed)
                    numCohesion.Value = (decimal)cohesion;

                if (numFrictionAngle != null && !numFrictionAngle.IsDisposed)
                    numFrictionAngle.Value = (decimal)frictionAngle;

                // Force redraw of visualization
                glControl.Invalidate();

                // Update Mohr-Coulomb graph if available
                if (mohrCoulombGraph != null && !mohrCoulombGraph.IsDisposed)
                    mohrCoulombGraph.Invalidate();
            }
        }

        // Call this in the InitializeComponent method to set up calibration UI
        private void SetupCalibrationUI()
        {
            InitializeCalibrationComponents();
        }
    }

    // Helper class for calibration parameters
    public class CalibrationParameters
    {
        public float YoungModulus { get; set; }
        public float PoissonRatio { get; set; }
        public float YieldStrength { get; set; }
        public float BrittleStrength { get; set; }
        public float Cohesion { get; set; }
        public float FrictionAngle { get; set; }
    }
}
