using System;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using Krypton.Toolkit;
using System.Drawing.Drawing2D;

namespace CTSegmenter
{
    public partial class AcousticSimulationForm
    {
        // Additional UI elements
        private ToolStripButton btnNewSimulation;
        private ToolStripButton btnSaveSimulation;
        private ToolStripButton btnOpenSimulation;
        private ToolStripButton btnExportData;

        // List to store time series data for export
        private List<double> pWaveTimeSeriesData = new List<double>();
        private List<double> sWaveTimeSeriesData = new List<double>();

        // Initialize extended toolbar - call this from the end of the constructor
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            InitializeExtendedToolbar();

            // Subscribe to simulation events to capture time series data
            if (cpuSimulator != null)
            {
                cpuSimulator.ProgressUpdated += CaptureTimeSeriesData;
            }
            if (gpuSimulator != null)
            {
                gpuSimulator.ProgressUpdated += CaptureTimeSeriesData;
            }
        }

        private void CaptureTimeSeriesData(object sender, AcousticSimulationProgressEventArgs e)
        {
            // Extract velocity at receiver location for time series data
            if (e.PWaveField != null && e.SWaveField != null)
            {
                // Extract data at the receiver point
                if (rx >= 0 && rx < e.PWaveField.GetLength(0) &&
                    ry >= 0 && ry < e.PWaveField.GetLength(1) &&
                    rz >= 0 && rz < e.PWaveField.GetLength(2))
                {
                    // Save P-wave value (from vx component)
                    pWaveTimeSeriesData.Add(e.PWaveField[rx, ry, rz]);

                    // Save S-wave value (from vy component)
                    sWaveTimeSeriesData.Add(e.SWaveField[rx, ry, rz]);
                }
            }
        }

        private void InitializeExtendedToolbar()
        {
            // Make the toolbar bigger
            toolStrip.ImageScalingSize = new Size(32, 32);
            toolStrip.Height = 40;

            // Create buttons
            btnNewSimulation = new ToolStripButton("New Simulation");
            btnSaveSimulation = new ToolStripButton("Save Simulation");
            btnOpenSimulation = new ToolStripButton("Open Simulation");
            btnExportData = new ToolStripButton("Export Data");

            // Create visualizer button
            ToolStripButton btnVisualizer = new ToolStripButton("Open Visualizer");

            // Set display style for all buttons
            btnNewSimulation.DisplayStyle = ToolStripItemDisplayStyle.Image;
            btnSaveSimulation.DisplayStyle = ToolStripItemDisplayStyle.Image;
            btnOpenSimulation.DisplayStyle = ToolStripItemDisplayStyle.Image;
            btnExportData.DisplayStyle = ToolStripItemDisplayStyle.Image;
            btnVisualizer.DisplayStyle = ToolStripItemDisplayStyle.Image;

            // Create and assign icons
            btnNewSimulation.Image = CreateNewSimulationIcon();
            btnSaveSimulation.Image = CreateSaveSimulationIcon();
            btnOpenSimulation.Image = CreateOpenSimulationIcon();
            btnExportData.Image = CreateExportDataIcon();
            btnVisualizer.Image = CreateVisualizerIcon();

            // Add tooltips
            btnNewSimulation.ToolTipText = "New Simulation (Reset)";
            btnSaveSimulation.ToolTipText = "Save Simulation State";
            btnOpenSimulation.ToolTipText = "Open Saved Simulation";
            btnExportData.ToolTipText = "Export Results to CSV/XLS";
            btnVisualizer.ToolTipText = "Open Visualizer";

            // Hook up event handlers
            btnNewSimulation.Click += BtnNewSimulation_Click;
            btnSaveSimulation.Click += BtnSaveSimulation_Click;
            btnOpenSimulation.Click += BtnOpenSimulation_Click;
            btnExportData.Click += BtnExportData_Click;
            btnVisualizer.Click += (s, e) => OpenVisualizer();

            // Enable the visualizer button only if we have simulation results
            btnVisualizer.Enabled = (simulationResults != null);

            // Clear existing buttons and add our new ones
            toolStrip.Items.Clear();
            toolStrip.Items.Add(btnNewSimulation);
            toolStrip.Items.Add(btnOpenSimulation);
            toolStrip.Items.Add(btnSaveSimulation);
            toolStrip.Items.Add(btnExportData);
            toolStrip.Items.Add(new ToolStripSeparator()); // Add a separator
            toolStrip.Items.Add(btnVisualizer);
        }


        // Icon creation methods
        private Image CreateNewSimulationIcon()
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // Draw a document with a plus sign
                using (Pen pen = new Pen(Color.White, 2))
                {
                    // Document outline
                    g.DrawRectangle(pen, 6, 4, 20, 24);

                    // Folded corner
                    g.DrawLine(pen, 19, 4, 19, 10);
                    g.DrawLine(pen, 19, 10, 26, 10);

                    // Plus sign
                    g.DrawLine(pen, 12, 16, 20, 16);
                    g.DrawLine(pen, 16, 12, 16, 20);
                }
            }
            return bmp;
        }

        private Image CreateSaveSimulationIcon()
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // Draw a floppy disk icon
                using (Pen pen = new Pen(Color.White, 2))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    // Main square
                    g.DrawRectangle(pen, 5, 5, 22, 22);

                    // Disk label
                    g.FillRectangle(brush, 8, 8, 16, 7);

                    // Write hole
                    g.DrawRectangle(pen, 20, 9, 3, 3);

                    // Bottom part
                    g.DrawLine(pen, 9, 15, 23, 15);
                    g.DrawLine(pen, 9, 20, 23, 20);
                }
            }
            return bmp;
        }

        private Image CreateOpenSimulationIcon()
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // Draw a folder with an arrow
                using (Pen pen = new Pen(Color.White, 2))
                {
                    // Folder
                    g.DrawLine(pen, 5, 9, 10, 9);  // Top small edge
                    g.DrawLine(pen, 10, 9, 13, 6); // Folder tab slant
                    g.DrawLine(pen, 13, 6, 24, 6); // Folder tab top
                    g.DrawLine(pen, 24, 6, 24, 9); // Folder tab right side
                    g.DrawLine(pen, 5, 9, 5, 24);  // Folder left side
                    g.DrawLine(pen, 5, 24, 27, 24); // Folder bottom
                    g.DrawLine(pen, 27, 24, 27, 9); // Folder right side
                    g.DrawLine(pen, 24, 9, 27, 9);  // Connect tab to folder

                    // Arrow pointing into the folder
                    g.DrawLine(pen, 16, 18, 22, 18); // Horizontal part
                    g.DrawLine(pen, 19, 15, 22, 18); // Upper diagonal
                    g.DrawLine(pen, 19, 21, 22, 18); // Lower diagonal
                }
            }
            return bmp;
        }

        private Image CreateExportDataIcon()
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // Draw a document with CSV/XLS letters and an arrow
                using (Pen pen = new Pen(Color.White, 2))
                using (Font font = new Font("Arial", 7, FontStyle.Bold))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    // Document
                    g.DrawRectangle(pen, 7, 4, 18, 22);

                    // Text on document
                    g.DrawString("CSV", font, textBrush, 9, 10);
                    g.DrawString("XLS", font, textBrush, 9, 18);

                    // Export arrow
                    g.DrawLine(pen, 16, 26, 22, 26); // Horizontal part
                    g.DrawLine(pen, 22, 26, 22, 23); // Vertical part
                    g.DrawLine(pen, 20, 25, 24, 29); // Upper diagonal
                    g.DrawLine(pen, 24, 29, 28, 25); // Lower diagonal
                }
            }
            return bmp;
        }

        // Button click handlers
        private void BtnNewSimulation_Click(object sender, EventArgs e)
        {
            // Ask for confirmation before discarding data
            DialogResult result = MessageBox.Show(
                "Are you sure you want to create a new simulation? Any unsaved data will be lost.",
                "Confirm Reset",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                // Reset the simulation state
                ResetSimulation();
            }
        }

        private void ResetSimulation()
        {
            // Stop any running simulation
            if (simulationRunning)
            {
                if (usingGpuSimulator)
                    gpuSimulator?.CancelSimulation();
                else
                    cpuSimulator?.CancelSimulation();

                simulationRunning = false;
            }

            // Clear time series data
            pWaveTimeSeriesData.Clear();
            sWaveTimeSeriesData.Clear();

            // Clean up simulation objects
            if (cpuSimulator != null)
            {
                cpuSimulator.Dispose();
                cpuSimulator = null;
            }

            if (gpuSimulator != null)
            {
                gpuSimulator.Dispose();
                gpuSimulator = null;
            }

            // Reset UI state
            if (simulationProgressForm != null && !simulationProgressForm.IsDisposed)
            {
                simulationProgressForm.Close();
            }

            // Reset volume data
            InitializeHomogeneousDensityVolume();

            // Reset coordinates
            tx = 0;
            ty = 0;
            tz = 0;
            rx = 0;
            ry = 0;
            rz = 0;

            // Clear path points
            _pathPoints = null;

            // Reset all form controls
            if (tabControl != null)
            {
                tabControl.SelectedTab = tabVolume; // Switch to first tab
            }

            // Reset numeric controls to default values
            ResetControlValues();

            // Clear simulation results
            simulationResults = null;

            // Refresh visuals
            if (pictureBoxVolume != null)
                pictureBoxVolume.Invalidate();
            if (pictureBoxSimulation != null)
                pictureBoxSimulation.Invalidate();

            // Notify user
            MessageBox.Show("Simulation has been reset successfully.", "Reset Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ResetControlValues()
        {
            // Reset simulation controls to default values
            if (numConfiningPressure != null) numConfiningPressure.Value = 1.0m;
            if (numTensileStrength != null) numTensileStrength.Value = 10.0m;
            if (numFailureAngle != null) numFailureAngle.Value = 30.0m;
            if (numCohesion != null) numCohesion.Value = 5.0m;
            if (numEnergy != null) numEnergy.Value = 1.0m;
            if (numFrequency != null) numFrequency.Value = 100.0m;
            if (numAmplitude != null) numAmplitude.Value = 100;
            if (numTimeSteps != null) numTimeSteps.Value = 100;
            if (numYoungsModulus != null) numYoungsModulus.Value = 50000.0m;
            if (numPoissonRatio != null) numPoissonRatio.Value = 0.25m;

            // Reset checkboxes
            if (chkElasticModel != null) chkElasticModel.Checked = true;
            if (chkPlasticModel != null) chkPlasticModel.Checked = false;
            if (chkBrittleModel != null) chkBrittleModel.Checked = false;
            if (chkRunOnGpu != null) chkRunOnGpu.Checked = false;

            // Reset combo boxes
            if (comboMaterialLibrary != null && comboMaterialLibrary.Items.Count > 0)
                comboMaterialLibrary.SelectedIndex = 0;
            if (comboAxis != null && comboAxis.Items.Count > 0)
                comboAxis.SelectedIndex = 0;
            if (comboWaveType != null && comboWaveType.Items.Count > 0)
                comboWaveType.SelectedIndex = 0;
        }

        private void BtnSaveSimulation_Click(object sender, EventArgs e)
        {
            try
            {
                // Create a SaveFileDialog to select where to save the simulation state
                using (SaveFileDialog saveDialog = new SaveFileDialog())
                {
                    saveDialog.Filter = "Acoustic Simulation Files (*.acsim)|*.acsim";
                    saveDialog.Title = "Save Simulation State";
                    saveDialog.DefaultExt = "acsim";

                    if (saveDialog.ShowDialog() == DialogResult.OK)
                    {
                        // Create a serializable state object to save
                        SimulationState state = CaptureSimulationState();

                        // Serialize and save the state
                        using (FileStream fs = new FileStream(saveDialog.FileName, FileMode.Create))
                        {
                            BinaryFormatter formatter = new BinaryFormatter();
                            formatter.Serialize(fs, state);
                        }

                        MessageBox.Show($"Simulation state saved successfully to:\n{saveDialog.FileName}",
                            "Save Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving simulation state: {ex.Message}",
                    "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[SaveSimulation] Error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        [Serializable]
        private class SimulationState
        {
            // Form state
            public byte SelectedMaterialID { get; set; }
            public double BaseDensity { get; set; }
            public bool HasDensityVariation { get; set; }
            public float[,,] DensityVolume { get; set; }

            // Coordinate state
            public int TX { get; set; }
            public int TY { get; set; }
            public int TZ { get; set; }
            public int RX { get; set; }
            public int RY { get; set; }
            public int RZ { get; set; }

            // Simulation parameters
            public string SelectedAxis { get; set; }
            public string SelectedWaveType { get; set; }
            public double ConfiningPressure { get; set; }
            public double TensileStrength { get; set; }
            public double FailureAngle { get; set; }
            public double Cohesion { get; set; }
            public double Energy { get; set; }
            public double Frequency { get; set; }
            public int Amplitude { get; set; }
            public int TimeSteps { get; set; }
            public bool UseElasticModel { get; set; }
            public bool UsePlasticModel { get; set; }
            public bool UseBrittleModel { get; set; }
            public bool UseGPU { get; set; }
            public double YoungsModulus { get; set; }
            public double PoissonRatio { get; set; }

            // Results (if available)
            public bool HasResults { get; set; }
            public double PWaveVelocity { get; set; }
            public double SWaveVelocity { get; set; }
            public double VpVsRatio { get; set; }
            public int PWaveTravelTime { get; set; }
            public int SWaveTravelTime { get; set; }

            // Path points
            public List<Point3DSerializable> PathPoints { get; set; }

            // Time series data
            public List<double> PWaveTimeSeries { get; set; }
            public List<double> SWaveTimeSeries { get; set; }

            [Serializable]
            public class Point3DSerializable
            {
                public float X { get; set; }
                public float Y { get; set; }
                public float Z { get; set; }

                public Point3DSerializable(float x, float y, float z)
                {
                    X = x;
                    Y = y;
                    Z = z;
                }
            }
        }

        private SimulationState CaptureSimulationState()
        {
            SimulationState state = new SimulationState
            {
                // Form state
                SelectedMaterialID = selectedMaterialID,
                BaseDensity = baseDensity,
                HasDensityVariation = hasDensityVariation,
                DensityVolume = densityVolume,

                // Coordinate state
                TX = tx,
                TY = ty,
                TZ = tz,
                RX = rx,
                RY = ry,
                RZ = rz,

                // Simulation parameters
                SelectedAxis = comboAxis.SelectedItem?.ToString(),
                SelectedWaveType = comboWaveType.SelectedItem?.ToString(),
                ConfiningPressure = (double)numConfiningPressure.Value,
                TensileStrength = (double)numTensileStrength.Value,
                FailureAngle = (double)numFailureAngle.Value,
                Cohesion = (double)numCohesion.Value,
                Energy = (double)numEnergy.Value,
                Frequency = (double)numFrequency.Value,
                Amplitude = (int)numAmplitude.Value,
                TimeSteps = (int)numTimeSteps.Value,
                UseElasticModel = chkElasticModel.Checked,
                UsePlasticModel = chkPlasticModel.Checked,
                UseBrittleModel = chkBrittleModel.Checked,
                UseGPU = chkRunOnGpu.Checked,
                YoungsModulus = (double)numYoungsModulus.Value,
                PoissonRatio = (double)numPoissonRatio.Value,

                // Results (if available)
                HasResults = simulationResults != null,

                // Time series data
                PWaveTimeSeries = new List<double>(pWaveTimeSeriesData),
                SWaveTimeSeries = new List<double>(sWaveTimeSeriesData),

                PathPoints = new List<SimulationState.Point3DSerializable>()
            };

            // Add results if available
            if (simulationResults != null)
            {
                state.PWaveVelocity = simulationResults.PWaveVelocity;
                state.SWaveVelocity = simulationResults.SWaveVelocity;
                state.VpVsRatio = simulationResults.VpVsRatio;
                state.PWaveTravelTime = simulationResults.PWaveTravelTime;
                state.SWaveTravelTime = simulationResults.SWaveTravelTime;
            }

            // Convert path points if available
            if (_pathPoints != null)
            {
                foreach (var point in _pathPoints)
                {
                    state.PathPoints.Add(new SimulationState.Point3DSerializable(point.X, point.Y, point.Z));
                }
            }

            return state;
        }

        private void BtnOpenSimulation_Click(object sender, EventArgs e)
        {
            try
            {
                // Create an OpenFileDialog to select a simulation file
                using (OpenFileDialog openDialog = new OpenFileDialog())
                {
                    openDialog.Filter = "Acoustic Simulation Files (*.acsim)|*.acsim";
                    openDialog.Title = "Open Simulation State";

                    if (openDialog.ShowDialog() == DialogResult.OK)
                    {
                        // Ask for confirmation if there's a simulation in progress
                        if (simulationRunning)
                        {
                            DialogResult result = MessageBox.Show(
                                "A simulation is currently running. Loading a saved state will abort the current simulation. Continue?",
                                "Confirm Load",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Warning);

                            if (result != DialogResult.Yes)
                                return;

                            // Cancel the current simulation
                            if (usingGpuSimulator)
                                gpuSimulator?.CancelSimulation();
                            else
                                cpuSimulator?.CancelSimulation();

                            simulationRunning = false;
                        }

                        // Deserialize the state
                        SimulationState state;
                        using (FileStream fs = new FileStream(openDialog.FileName, FileMode.Open))
                        {
                            BinaryFormatter formatter = new BinaryFormatter();
                            state = (SimulationState)formatter.Deserialize(fs);
                        }

                        // Apply the loaded state
                        RestoreSimulationState(state);

                        MessageBox.Show("Simulation state loaded successfully.",
                            "Load Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading simulation state: {ex.Message}",
                    "Load Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[OpenSimulation] Error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void RestoreSimulationState(SimulationState state)
        {
            // Restore form state
            selectedMaterialID = state.SelectedMaterialID;
            baseDensity = state.BaseDensity;
            hasDensityVariation = state.HasDensityVariation;
            densityVolume = state.DensityVolume;

            // Restore coordinate state
            tx = state.TX;
            ty = state.TY;
            tz = state.TZ;
            rx = state.RX;
            ry = state.RY;
            rz = state.RZ;

            // Restore simulation parameters
            if (state.SelectedAxis != null && comboAxis.Items.Contains(state.SelectedAxis))
                comboAxis.SelectedItem = state.SelectedAxis;

            if (state.SelectedWaveType != null && comboWaveType.Items.Contains(state.SelectedWaveType))
                comboWaveType.SelectedItem = state.SelectedWaveType;

            numConfiningPressure.Value = (decimal)state.ConfiningPressure;
            numTensileStrength.Value = (decimal)state.TensileStrength;
            numFailureAngle.Value = (decimal)state.FailureAngle;
            numCohesion.Value = (decimal)state.Cohesion;
            numEnergy.Value = (decimal)state.Energy;
            numFrequency.Value = (decimal)state.Frequency;
            numAmplitude.Value = state.Amplitude;
            numTimeSteps.Value = state.TimeSteps;
            chkElasticModel.Checked = state.UseElasticModel;
            chkPlasticModel.Checked = state.UsePlasticModel;
            chkBrittleModel.Checked = state.UseBrittleModel;
            chkRunOnGpu.Checked = state.UseGPU;
            numYoungsModulus.Value = (decimal)state.YoungsModulus;
            numPoissonRatio.Value = (decimal)state.PoissonRatio;

            // Restore time series data
            pWaveTimeSeriesData.Clear();
            sWaveTimeSeriesData.Clear();

            if (state.PWaveTimeSeries != null)
                pWaveTimeSeriesData.AddRange(state.PWaveTimeSeries);

            if (state.SWaveTimeSeries != null)
                sWaveTimeSeriesData.AddRange(state.SWaveTimeSeries);

            // Restore results if available
            if (state.HasResults)
            {
                simulationResults = new SimulationResults
                {
                    PWaveVelocity = state.PWaveVelocity,
                    SWaveVelocity = state.SWaveVelocity,
                    VpVsRatio = state.VpVsRatio,
                    PWaveTravelTime = state.PWaveTravelTime,
                    SWaveTravelTime = state.SWaveTravelTime
                };
            }

            // Restore path points if available
            if (state.PathPoints != null && state.PathPoints.Count > 0)
            {
                _pathPoints = new List<Point3D>();
                foreach (var point in state.PathPoints)
                {
                    _pathPoints.Add(new Point3D(point.X, point.Y, point.Z));
                }
            }
            if (state.HasResults)
            {
                foreach (ToolStripItem item in toolStrip.Items)
                {
                    if (item.ToolTipText == "Open Visualizer")
                    {
                        item.Enabled = true;
                        break;
                    }
                }
            }
            // Select the appropriate material in the combo box
            SelectMaterialByID(state.SelectedMaterialID);

            // Update UI and rendering
            UpdateMaterialInfo();
            InitializeVolumesAndRenderers();

            // Refresh visuals
            if (pictureBoxVolume != null)
                pictureBoxVolume.Invalidate();
            if (pictureBoxSimulation != null)
                pictureBoxSimulation.Invalidate();
        }

        private void SelectMaterialByID(byte materialID)
        {
            if (comboMaterials != null)
            {
                for (int i = 0; i < comboMaterials.Items.Count; i++)
                {
                    if (comboMaterials.Items[i] is Material material && material.ID == materialID)
                    {
                        comboMaterials.SelectedIndex = i;
                        return;
                    }
                }
            }
        }
        private float[,,] GenerateWaveFieldForVisualization(bool isPWave)
        {
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();

            float[,,] field = new float[width, height, depth];

            // Calculate wave path from TX to RX
            double dx = rx - tx;
            double dy = ry - ty;
            double dz = rz - tz;
            double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);

            // We'll create a simple wave pattern along the path
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        // Only populate material voxels
                        if (mainForm.volumeLabels[x, y, z] != selectedMaterialID)
                            continue;

                        // Calculate distance from voxel to line from TX to RX
                        double t = ((x - tx) * dx + (y - ty) * dy + (z - tz) * dz) / (distance * distance);
                        t = Math.Max(0, Math.Min(1, t)); // Clamp to [0,1]

                        double px = tx + t * dx;
                        double py = ty + t * dy;
                        double pz = tz + t * dz;

                        double distToLine = Math.Sqrt(
                            (x - px) * (x - px) +
                            (y - py) * (y - py) +
                            (z - pz) * (z - pz));

                        // Create wave pattern based on distance to line
                        if (distToLine < 10) // Within reasonable distance of TX-RX line
                        {
                            // Separate patterns for P and S waves
                            if (isPWave)
                            {
                                // For P-wave, create pattern along the path
                                double phase = t * 10; // 10 wavelengths along the path
                                field[x, y, z] = (float)(Math.Sin(phase * 2 * Math.PI) * Math.Exp(-distToLine / 3) * 0.1);
                            }
                            else
                            {
                                // For S-wave, create a different pattern
                                double phase = t * 6; // 6 wavelengths (longer than P-wave)
                                field[x, y, z] = (float)(Math.Sin(phase * 2 * Math.PI) * Math.Exp(-distToLine / 3) * 0.08);
                            }
                        }
                    }
                }
            }

            return field;
        }
        private void InitializeVolumesAndRenderers()
        {
            // Initialize volume renderer
            volumeRenderer = new VolumeRenderer(
                densityVolume,
                mainForm.GetWidth(),
                mainForm.GetHeight(),
                mainForm.GetDepth(),
                mainForm.GetPixelSize(),
                selectedMaterialID,
                useFullVolumeRendering
            );
            volumeRenderer.SetTransformation(rotationX, rotationY, zoom, pan);

            // Initialize simulation renderer
            simulationRenderer = new VolumeRenderer(
                densityVolume,
                mainForm.GetWidth(),
                mainForm.GetHeight(),
                mainForm.GetDepth(),
                mainForm.GetPixelSize(),
                selectedMaterialID,
                useFullVolumeRendering
            );
            simulationRenderer.SetTransformation(simRotationX, simRotationY, simZoom, simPan);
        }

        private void BtnExportData_Click(object sender, EventArgs e)
        {
            // Show export options dialog
            ExportOptionsForm exportOptions = new ExportOptionsForm();
            if (exportOptions.ShowDialog() == DialogResult.OK)
            {
                bool exportAllSteps = exportOptions.ExportAllSteps;
                string exportFormat = exportOptions.ExportFormat; // "CSV" or "XLS"

                try
                {
                    using (SaveFileDialog saveDialog = new SaveFileDialog())
                    {
                        saveDialog.Filter = exportFormat == "CSV"
                            ? "CSV Files (*.csv)|*.csv"
                            : "Excel Files (*.xlsx)|*.xlsx";
                        saveDialog.Title = "Export Simulation Results";
                        saveDialog.DefaultExt = exportFormat.ToLower();

                        if (saveDialog.ShowDialog() == DialogResult.OK)
                        {
                            // Show progress dialog
                            using (CancellableProgressForm progressDialog = new CancellableProgressForm("Exporting Data"))
                            {
                                progressDialog.Show(this);

                                // Do the export in a background thread
                                Task.Run(() =>
                                {
                                    try
                                    {
                                        ExportSimulationData(saveDialog.FileName, exportFormat, exportAllSteps, progressDialog);

                                        this.BeginInvoke(new Action(() =>
                                        {
                                            progressDialog.Close();
                                            MessageBox.Show($"Data exported successfully to:\n{saveDialog.FileName}",
                                                "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                                        }));
                                    }
                                    catch (Exception ex)
                                    {
                                        this.BeginInvoke(new Action(() =>
                                        {
                                            progressDialog.Close();
                                            MessageBox.Show($"Error exporting data: {ex.Message}",
                                                "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                            Logger.Log($"[ExportData] Error: {ex.Message}\n{ex.StackTrace}");
                                        }));
                                    }
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error preparing data export: {ex.Message}",
                        "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Logger.Log($"[ExportData] Error: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        private void ExportSimulationData(string filePath, string format, bool exportAllSteps, CancellableProgressForm progressDialog)
        {
            // Check if we have results to export
            if (simulationResults == null)
            {
                throw new InvalidOperationException("No simulation results available for export.");
            }

            // Generate the data to export
            List<string[]> dataRows = new List<string[]>();

            // Add header row
            string[] header = new string[]
            {
                "Parameter", "Value", "Units"
            };
            dataRows.Add(header);

            // Add simulation parameters
            dataRows.Add(new string[] { "Simulation Type", GetSimulationTypeString(), "" });
            dataRows.Add(new string[] { "Material ID", selectedMaterialID.ToString(), "" });
            dataRows.Add(new string[] { "Material Density", baseDensity.ToString("F2"), "kg/m³" });
            dataRows.Add(new string[] { "TX Position", $"({tx}, {ty}, {tz})", "voxels" });
            dataRows.Add(new string[] { "RX Position", $"({rx}, {ry}, {rz})", "voxels" });
            dataRows.Add(new string[] { "Distance", CalculateDistance().ToString("F3"), "mm" });
            dataRows.Add(new string[] { "Young's Modulus", numYoungsModulus.Value.ToString("F1"), "MPa" });
            dataRows.Add(new string[] { "Poisson Ratio", numPoissonRatio.Value.ToString("F3"), "" });
            dataRows.Add(new string[] { "Confining Pressure", numConfiningPressure.Value.ToString("F3"), "MPa" });
            dataRows.Add(new string[] { "Tensile Strength", numTensileStrength.Value.ToString("F3"), "MPa" });
            dataRows.Add(new string[] { "Wave Frequency", numFrequency.Value.ToString("F1"), "kHz" });

            // Add a blank row
            dataRows.Add(new string[] { "", "", "" });

            // Add results
            dataRows.Add(new string[] { "Results", "", "" });
            dataRows.Add(new string[] { "P-Wave Velocity", simulationResults.PWaveVelocity.ToString("F2"), "m/s" });
            dataRows.Add(new string[] { "S-Wave Velocity", simulationResults.SWaveVelocity.ToString("F2"), "m/s" });
            dataRows.Add(new string[] { "Vp/Vs Ratio", simulationResults.VpVsRatio.ToString("F3"), "" });
            dataRows.Add(new string[] { "P-Wave Travel Time", simulationResults.PWaveTravelTime.ToString(), "steps" });
            dataRows.Add(new string[] { "S-Wave Travel Time", simulationResults.SWaveTravelTime.ToString(), "steps" });

            // If exporting all steps and we have time series data
            if (exportAllSteps && pWaveTimeSeriesData.Count > 0 && sWaveTimeSeriesData.Count > 0)
            {
                // Add a blank row
                dataRows.Add(new string[] { "", "", "" });

                // Add time series header
                dataRows.Add(new string[] { "Time Step", "P-Wave Amplitude", "S-Wave Amplitude" });

                // Use the time series data we've captured during simulation
                int stepCount = Math.Max(pWaveTimeSeriesData.Count, sWaveTimeSeriesData.Count);
                for (int i = 0; i < stepCount; i++)
                {
                    string pValue = i < pWaveTimeSeriesData.Count ? pWaveTimeSeriesData[i].ToString("E6") : "0";
                    string sValue = i < sWaveTimeSeriesData.Count ? sWaveTimeSeriesData[i].ToString("E6") : "0";

                    dataRows.Add(new string[]
                    {
                        i.ToString(),
                        pValue,
                        sValue
                    });
                }
            }

            // Now export the data based on format
            if (format == "CSV")
            {
                ExportToCsv(filePath, dataRows, progressDialog);
            }
            else
            {
                ExportToExcel(filePath, dataRows, progressDialog);
            }
        }

        private void ExportToCsv(string filePath, List<string[]> dataRows, CancellableProgressForm progressDialog)
        {
            using (StreamWriter writer = new StreamWriter(filePath))
            {
                int totalRows = dataRows.Count;
                for (int i = 0; i < totalRows; i++)
                {
                    // Update progress
                    if (i % 10 == 0)
                    {
                        int progressValue = (i * 100) / totalRows;
                        progressDialog.BeginInvoke(new Action(() =>
                        {
                            progressDialog.UpdateProgress(progressValue);
                        }));
                    }

                    // Write CSV row
                    writer.WriteLine(string.Join(",", dataRows[i].Select(field => $"\"{field}\"")));
                }
            }
        }

        private void ExportToExcel(string filePath, List<string[]> dataRows, CancellableProgressForm progressDialog)
        {
            // For Excel export, we would typically use a library like EPPlus
            // But for simplicity, we'll just use CSV format with Excel extension

            // Create a CSV file with Excel extension
            ExportToCsv(filePath, dataRows, progressDialog);

            progressDialog.BeginInvoke(new Action(() =>
            {
                progressDialog.UpdateMessage("File saved in CSV format with Excel extension. For full Excel functionality, please open in Excel and save as .xlsx format.");
            }));
        }

        private string GetSimulationTypeString()
        {
            List<string> models = new List<string>();
            if (chkElasticModel.Checked) models.Add("Elastic");
            if (chkPlasticModel.Checked) models.Add("Plastic");
            if (chkBrittleModel.Checked) models.Add("Brittle");

            return string.Join("+", models);
        }

        private double CalculateDistance()
        {
            double dx = rx - tx;
            double dy = ry - ty;
            double dz = rz - tz;
            double distanceVoxels = Math.Sqrt(dx * dx + dy * dy + dz * dz);

            // Convert to mm
            return distanceVoxels * mainForm.GetPixelSize() * 1000;
        }
    }

    // Export options dialog
    public class ExportOptionsForm : KryptonForm
    {
        private KryptonCheckBox chkExportAllSteps;
        private KryptonRadioButton radCsv;
        private KryptonRadioButton radExcel;
        private KryptonButton btnOk;
        private KryptonButton btnCancel;

        public bool ExportAllSteps { get; private set; }
        public string ExportFormat { get; private set; }

        public ExportOptionsForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "Export Options";
            this.Size = new Size(350, 200);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = Color.FromArgb(45, 45, 48);

            // Create controls
            KryptonPanel panel = new KryptonPanel();
            panel.Dock = DockStyle.Fill;
            panel.StateCommon.Color1 = Color.FromArgb(45, 45, 48);
            panel.StateCommon.Color2 = Color.FromArgb(45, 45, 48);

            KryptonLabel lblTitle = new KryptonLabel();
            lblTitle.Text = "Select Export Options";
            lblTitle.StateCommon.ShortText.Color1 = Color.White;
            lblTitle.StateCommon.ShortText.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            lblTitle.Location = new Point(20, 20);
            lblTitle.Size = new Size(300, 25);

            KryptonGroupBox grpFormat = new KryptonGroupBox();
            grpFormat.Text = "Export Format";
            grpFormat.Location = new Point(20, 50);
            grpFormat.Size = new Size(140, 80);
            grpFormat.StateCommon.Content.ShortText.Color1 = Color.White;

            radCsv = new KryptonRadioButton();
            radCsv.Text = "CSV";
            radCsv.Checked = true;
            radCsv.Location = new Point(20, 25);
            radCsv.StateCommon.ShortText.Color1 = Color.White;

            radExcel = new KryptonRadioButton();
            radExcel.Text = "Excel";
            radExcel.Location = new Point(20, 50);
            radExcel.StateCommon.ShortText.Color1 = Color.White;

            grpFormat.Panel.Controls.Add(radCsv);
            grpFormat.Panel.Controls.Add(radExcel);

            chkExportAllSteps = new KryptonCheckBox();
            chkExportAllSteps.Text = "Export all time steps";
            chkExportAllSteps.Location = new Point(180, 70);
            chkExportAllSteps.StateCommon.ShortText.Color1 = Color.White;

            btnOk = new KryptonButton();
            btnOk.Text = "OK";
            btnOk.Location = new Point(170, 140);
            btnOk.Size = new Size(70, 25);
            btnOk.Click += BtnOk_Click;

            btnCancel = new KryptonButton();
            btnCancel.Text = "Cancel";
            btnCancel.Location = new Point(250, 140);
            btnCancel.Size = new Size(70, 25);
            btnCancel.Click += BtnCancel_Click;

            // Add controls to panel
            panel.Controls.Add(lblTitle);
            panel.Controls.Add(grpFormat);
            panel.Controls.Add(chkExportAllSteps);
            panel.Controls.Add(btnOk);
            panel.Controls.Add(btnCancel);

            // Add panel to form
            this.Controls.Add(panel);
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            // Set properties based on user selection
            ExportAllSteps = chkExportAllSteps.Checked;
            ExportFormat = radCsv.Checked ? "CSV" : "XLS";

            // Close with OK result
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private void BtnCancel_Click(object sender, EventArgs e)
        {
            // Close with Cancel result
            this.DialogResult = DialogResult.Cancel;
            this.Close();
        }
    }
}
