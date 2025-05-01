using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using Krypton.Toolkit;

namespace CTSegmenter.Modules.Triaxial_Simulation
{
    internal class TriaxialSimulationForm : KryptonForm
    {
        #region Fields
        private readonly MainForm mainForm;
        private ILabelVolumeData volumeLabels;
        private IGrayscaleVolumeData volumeData;
        private float[,,] densityVolume;
        private int width, height, depth;

        // Either/or simulator
        private TriaxialSimulator cpuSim;
        private TriaxialSimulatorGPU gpuSim;

        private volatile bool simulationRunning;
        private volatile bool simulationPaused;

        // UI
        private TableLayoutPanel mainLayout;
        private Panel parametersPanel, resultsPanel;
        private KryptonComboBox cmbMaterial, cmbStressAxis;
        private KryptonNumericUpDown nudConfiningP, nudInitialP, nudFinalP, nudSteps;
        private KryptonNumericUpDown nudE, nudNu, nudTensile, nudFriction, nudCohesion;
        private KryptonCheckBox chkElastic, chkPlastic, chkBrittle, chkUseGPU;
        private KryptonButton btnRun, btnPause, btnCancel, btnContinue;
        private KryptonProgressBar progressBar;
        private KryptonLabel lblStatus;
        private Chart chart;

        // CancellationTokenSource for safe cancellation
        private CancellationTokenSource cts;
        #endregion

        public TriaxialSimulationForm(MainForm mainForm)
        {
            Logger.Log("[TriaxialSimulationForm] ctor");
            this.mainForm = mainForm;
            Text = "Triaxial Simulation";
            Size = new Size(900, 700);
            StartPosition = FormStartPosition.CenterParent;
            FormClosing += OnClosing;

            BuildUI();
            LoadMaterials();
            ProbeGpu();
        }

        private void BuildUI()
        {
            mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(10)
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40f));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60f));

            parametersPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10) };
            resultsPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };

            mainLayout.Controls.Add(parametersPanel, 0, 0);
            mainLayout.Controls.Add(resultsPanel, 1, 0);
            Controls.Add(mainLayout);

            BuildParameters();
            BuildResults();
        }

        private void BuildParameters()
        {
            var tl = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            int r = 0;

            void L(string t) => tl.Controls.Add(new KryptonLabel { Text = t, Dock = DockStyle.Fill }, 0, r);
            KryptonNumericUpDown N(decimal min, decimal max, decimal val, int dp)
                => new KryptonNumericUpDown
                {
                    Minimum = min,
                    Maximum = max,
                    Value = val,
                    DecimalPlaces = dp,
                    Increment = 1,
                    Dock = DockStyle.Fill
                };

            L("Material:"); cmbMaterial = new KryptonComboBox { Dock = DockStyle.Fill }; tl.Controls.Add(cmbMaterial, 1, r++);
            L("Axis:"); cmbStressAxis = new KryptonComboBox { Dock = DockStyle.Fill };
            cmbStressAxis.Items.AddRange(new[] { "X", "Y", "Z" }); cmbStressAxis.SelectedIndex = 2; tl.Controls.Add(cmbStressAxis, 1, r++);
            L("Confining P (MPa):"); nudConfiningP = N(0, 1000, 10, 2); tl.Controls.Add(nudConfiningP, 1, r++);
            L("Initial P (MPa):"); nudInitialP = N(0, 1000, 10, 2); tl.Controls.Add(nudInitialP, 1, r++);
            L("Final P (MPa):"); nudFinalP = N(0, 1000, 100, 2); tl.Controls.Add(nudFinalP, 1, r++);
            L("Steps:"); nudSteps = N(1, 1000, 20, 0); tl.Controls.Add(nudSteps, 1, r++);
            L("Models:"); var mp = new Panel { Dock = DockStyle.Fill };
            chkElastic = new KryptonCheckBox { Text = "Elastic", Checked = true };
            chkPlastic = new KryptonCheckBox { Text = "Plastic", Checked = true, Left = 70 };
            chkBrittle = new KryptonCheckBox { Text = "Brittle", Checked = true, Left = 140 };
            mp.Controls.AddRange(new[] { chkElastic, chkPlastic, chkBrittle });
            tl.Controls.Add(mp, 1, r++);
            L("E (GPa):"); nudE = N(0, 1000, 70, 1); tl.Controls.Add(nudE, 1, r++);
            L("ν:"); nudNu = N(0, 0.5m, 0.25m, 3); nudNu.Increment = 0.01m; tl.Controls.Add(nudNu, 1, r++);
            L("Tensile (MPa):"); nudTensile = N(0, 500, 10, 2); tl.Controls.Add(nudTensile, 1, r++);
            L("Friction (°):"); nudFriction = N(0, 90, 30, 1); tl.Controls.Add(nudFriction, 1, r++);
            L("Cohesion (MPa):"); nudCohesion = N(0, 100, 5, 2); tl.Controls.Add(nudCohesion, 1, r++);
            L("GPU Accel:"); chkUseGPU = new KryptonCheckBox { Text = "Use GPU", Enabled = false, Dock = DockStyle.Fill }; tl.Controls.Add(chkUseGPU, 1, r++);

            // Buttons
            var bp = new Panel { Height = 40, Dock = DockStyle.Fill };
            btnRun = new KryptonButton { Text = "Run", Left = 0, Width = 100 };
            btnPause = new KryptonButton { Text = "Pause", Left = 110, Width = 100, Enabled = false };
            btnCancel = new KryptonButton { Text = "Cancel", Left = 220, Width = 100, Enabled = false };
            btnContinue = new KryptonButton { Text = "Continue", Left = 330, Width = 100, Visible = false };
            bp.Controls.AddRange(new Control[] { btnRun, btnPause, btnCancel, btnContinue });
            tl.Controls.Add(bp, 0, r); tl.SetColumnSpan(bp, 2);

            parametersPanel.Controls.Add(tl);

            btnRun.Click += OnRun;
            btnPause.Click += OnPause;
            btnCancel.Click += OnCancel;
            btnContinue.Click += OnContinue;
        }

        private void BuildResults()
        {
            var tl = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
            tl.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
            tl.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            tl.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

            chart = new Chart { Dock = DockStyle.Fill };
            var ca = new ChartArea("CA");
            ca.AxisX.Title = "Axial Strain"; ca.AxisY.Title = "Axial Stress (MPa)";
            chart.ChartAreas.Add(ca);
            chart.Series.Add(new Series("Curve") { ChartType = SeriesChartType.Line, BorderWidth = 2 });
            tl.Controls.Add(chart, 0, 0);

            progressBar = new KryptonProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };
            tl.Controls.Add(progressBar, 0, 1);

            lblStatus = new KryptonLabel { Text = "Ready", Dock = DockStyle.Fill };
            tl.Controls.Add(lblStatus, 0, 2);

            resultsPanel.Controls.Add(tl);
        }

        private void LoadMaterials()
        {
            cmbMaterial.Items.Clear();
            foreach (var m in mainForm.Materials.Where(x => !x.IsExterior))
                cmbMaterial.Items.Add(m);
            if (cmbMaterial.Items.Count > 0) cmbMaterial.SelectedIndex = 0;
        }

        private void ProbeGpu()
        {
            Task.Run(() => {
                bool ok = TriaxialSimulatorGPU.IsGpuAvailable();
                Invoke((Action)(() =>
                {
                    chkUseGPU.Enabled = ok;
                    if (!ok) chkUseGPU.Text = "Use GPU (none)";
                    Logger.Log($"[TriaxialSimulationForm] GPU available={ok}");
                }));
            });
        }

        #region Simulation
        private void OnRun(object s, EventArgs e)
        {
            if (simulationRunning) return;

            // Create new CancellationTokenSource
            cts = new CancellationTokenSource();

            // Prepare data
            volumeLabels = mainForm.volumeLabels;
            volumeData = mainForm.volumeData;
            width = mainForm.GetWidth();
            height = mainForm.GetHeight();
            depth = mainForm.GetDepth();

            if (volumeLabels == null || volumeData == null)
            {
                MessageBox.Show("No data", "Error", MessageBoxButtons.OK);
                return;
            }

            if (densityVolume == null)
                densityVolume = CreateDensity(volumeData);

            // Read UI parameters
            var mat = (Material)cmbMaterial.SelectedItem;
            byte matID = mat.ID;
            StressAxis axis = (StressAxis)cmbStressAxis.SelectedIndex;
            double confP = (double)nudConfiningP.Value;
            double p0 = (double)nudInitialP.Value;
            double p1 = (double)nudFinalP.Value;
            int steps = (int)nudSteps.Value;
            double E = (double)nudE.Value;
            double nu = (double)nudNu.Value;
            bool ue = chkElastic.Checked, up = chkPlastic.Checked, ub = chkBrittle.Checked;
            double ts = (double)nudTensile.Value;
            double phi = (double)nudFriction.Value;
            double co = (double)nudCohesion.Value;
            bool useGpu = chkUseGPU.Checked && chkUseGPU.Enabled;

            // Reset UI state
            chart.Series["Curve"].Points.Clear();
            progressBar.Value = 0;
            lblStatus.Text = "Starting…";
            simulationRunning = true;
            simulationPaused = false;
            btnPause.Enabled = btnCancel.Enabled = true;
            btnContinue.Visible = false;

            // Cleanup any existing simulators
            cpuSim?.Dispose();
            gpuSim?.Dispose();
            cpuSim = null;
            gpuSim = null;

            if (useGpu)
            {
                Logger.Log("[TriaxialSimulationForm] Starting GPU simulation");

                // Create wrapped byte and float arrays for GPU simulator
                byte[,,] labelArray = new byte[width, height, depth];
                for (int z = 0; z < depth; z++)
                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                            labelArray[x, y, z] = volumeLabels[x, y, z];

                gpuSim = new TriaxialSimulatorGPU(
                    width, height, depth,
                    (float)mainForm.pixelSize,
                    labelArray,
                    densityVolume,
                    matID,
                    E, nu,
                    ue, up, ub,
                    ts, phi, co
                );

                gpuSim.ProgressUpdated += OnProgress;
                gpuSim.FailureDetected += OnFailure;
                gpuSim.SimulationCompleted += OnComplete;

                gpuSim.StartSimulationAsync(
                    confP, p0, p1, steps, axis, 200, cts.Token
                );
            }
            else
            {
                Logger.Log("[TriaxialSimulationForm] Starting CPU simulation");

                cpuSim = new TriaxialSimulator(
                    width, height, depth,
                    (float)mainForm.pixelSize,
                    volumeLabels,
                    densityVolume,
                    matID,
                    confP, p0, p1, steps, axis,
                    ue, up, ub,
                    ts, phi, co,
                    E, nu,
                    200
                );

                cpuSim.ProgressUpdated += OnProgress;
                cpuSim.FailureDetected += OnFailure;
                cpuSim.SimulationCompleted += OnComplete;

                cpuSim.StartSimulationAsync();
            }
        }

        private void OnProgress(object s, TriaxialSimulationProgressEventArgs e)
        {
            Logger.Log($"[TriaxialSimulationForm] Progress {e.Percent}% Step {e.Step}");

            if (InvokeRequired)
            {
                Invoke((Action)(() => OnProgress(s, e)));
                return;
            }

            // Get current strain and stress from active simulator
            double currentStrain = 0, currentStress = 0;

            if (cpuSim != null)
            {
                currentStrain = TriaxialSimulatorExtension.GetCurrentStrain(cpuSim);
                currentStress = TriaxialSimulatorExtension.GetCurrentStress(cpuSim);
            }
            else if (gpuSim != null)
            {
                currentStrain = gpuSim.CurrentStrain;
                currentStress = gpuSim.CurrentStress;
            }

            // Add point to chart
            chart.Series["Curve"].Points.AddXY(currentStrain, currentStress);

            // Update progress indicators
            progressBar.Value = e.Percent;
            lblStatus.Text = e.Status;
        }

        private void OnFailure(object s, FailureDetectedEventArgs e)
        {
            Logger.Log($"[TriaxialSimulationForm] Failure at step {e.CurrentStep}");

            if (InvokeRequired)
            {
                Invoke((Action)(() => OnFailure(s, e)));
                return;
            }

            btnPause.Enabled = false;
            btnContinue.Visible = true;
            lblStatus.Text = "Failure detected — click Continue or Cancel";
            simulationPaused = true;
        }

        private void OnComplete(object s, TriaxialSimulationCompleteEventArgs e)
        {
            Logger.Log("[TriaxialSimulationForm] Simulation complete");

            if (InvokeRequired)
            {
                Invoke((Action)(() => OnComplete(s, e)));
                return;
            }

            simulationRunning = false;
            simulationPaused = false;
            btnPause.Enabled = btnCancel.Enabled = false;
            btnContinue.Visible = false;

            // Update chart with final data if available
            if (e.AxialStrain.Length > 0 && e.AxialStress.Length > 0)
            {
                chart.Series["Curve"].Points.Clear();
                for (int i = 0; i < e.AxialStrain.Length; i++)
                {
                    chart.Series["Curve"].Points.AddXY(e.AxialStrain[i], e.AxialStress[i]);
                }

                // Mark peak stress point
                if (e.PeakStress > 0)
                {
                    int peakIndex = Array.IndexOf(e.AxialStress, e.PeakStress);
                    if (peakIndex >= 0)
                    {
                        var dataPoint = chart.Series["Curve"].Points[peakIndex];
                        dataPoint.MarkerSize = 8;
                        dataPoint.MarkerStyle = MarkerStyle.Circle;
                        dataPoint.MarkerColor = Color.Red;
                    }
                }
            }

            lblStatus.Text = e.FailureDetected
                ? $"Done - Failure at step {e.FailureStep}"
                : "Simulation completed successfully";
        }

        private void OnPause(object s, EventArgs e)
        {
            if (!simulationRunning) return;

            simulationPaused = true;
            lblStatus.Text = "Paused";

            if (cpuSim != null) cpuSim.PauseSimulation();
            if (gpuSim != null) gpuSim.PauseSimulation();

            Logger.Log("[TriaxialSimulationForm] Paused");
        }

        private void OnContinue(object s, EventArgs e)
        {
            simulationPaused = false;
            btnContinue.Visible = false;
            lblStatus.Text = "Continuing after failure";

            if (cpuSim != null) cpuSim.ContinueAfterFailure();
            if (gpuSim != null) gpuSim.ContinueAfterFailure();

            Logger.Log("[TriaxialSimulationForm] Continued after failure");
        }

        private void OnCancel(object s, EventArgs e)
        {
            if (!simulationRunning) return;

            Logger.Log("[TriaxialSimulationForm] Cancelling simulation");

            cts?.Cancel();
            simulationRunning = false;
            simulationPaused = false;

            if (cpuSim != null) cpuSim.CancelSimulation();
            if (gpuSim != null) gpuSim.CancelSimulation();

            btnPause.Enabled = btnCancel.Enabled = false;
            btnContinue.Visible = false;
            lblStatus.Text = "Cancelled";
        }
        #endregion

        private float[,,] CreateDensity(IGrayscaleVolumeData v)
        {
            var d = new float[width, height, depth];
            const float dmin = 800, dmax = 3000;

            Parallel.For(0, depth, z =>
            {
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        d[x, y, z] = dmin + (v[x, y, z] / 255f) * (dmax - dmin);
            });

            return d;
        }

        private void OnClosing(object s, FormClosingEventArgs e)
        {
            Logger.Log("[TriaxialSimulationForm] Closing");

            cts?.Cancel();

            if (cpuSim != null)
            {
                cpuSim.CancelSimulation();
                cpuSim.Dispose();
                cpuSim = null;
            }

            if (gpuSim != null)
            {
                gpuSim.CancelSimulation();
                gpuSim.Dispose();
                gpuSim = null;
            }
        }
    }
}