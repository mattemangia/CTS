using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CTSegmenter.Modules.Acoustic_Simulation;
using Timer = System.Windows.Forms.Timer;

namespace CTSegmenter
{
    /// <summary>
    /// Real-time visualization window for acoustic simulation results
    /// Compatible with both CPU (AcousticSimulator) and GPU (AcousticSimulatorGPU) simulators
    /// </summary>
    public partial class AcousticSimulationVisualizer : Form
    {
        #region Fields
        // Simulation data
        private readonly object _dataLock = new object();
        private readonly List<SimulationFrame> _frames = new List<SimulationFrame>();
        private readonly int _updateInterval = 5; // Update every 5 steps
        private int _currentStep;
        private int _currentFrameIndex;
        private bool _simulationCompleted;
        private double _pWaveVelocity;
        private double _sWaveVelocity;
        private Color[] _colormap;

        // Transmitter and receiver positions
        private readonly int _tx, _ty, _tz;
        private readonly int _rx, _ry, _rz;
        private readonly int _width, _height, _depth;
        private readonly float _pixelSize;

        // UI components
        private TrackBar _timelineTrackBar;
        private Label _timeStepLabel;
        private Button _exportButton;
        private Button _exportAnimationButton;
        private Button _playPauseButton;
        private Panel _mainPanel;
        private Panel[] _subPanels;
        private PictureBox[] _pictureBoxes;
        private ToolTip _toolTip;
        private Timer _playbackTimer;
        private Timer _uiUpdateTimer;
        private bool _isPlaying;
        private readonly int _playbackInterval = 50; // ms between frames during playback

        // Icons for buttons
        private Bitmap _playIcon;
        private Bitmap _pauseIcon;
        private Bitmap _exportIcon;
        private Bitmap _animationIcon;

        // Panel dragging/zooming state
        private bool _isDragging;
        private Point _lastMousePosition;
        private readonly float[] _zoomFactors = new float[4] { 1.0f, 1.0f, 1.0f, 1.0f };
        private readonly PointF[] _panOffsets = new PointF[4] { new PointF(0, 0), new PointF(0, 0), new PointF(0, 0), new PointF(0, 0) };
        private int _selectedPanelIndex = -1;

        // Thread-safe bitmap access
        private Bitmap[] _panelBitmaps = new Bitmap[4];
        private Bitmap[] _displayBitmaps = new Bitmap[4];
        #endregion

        #region Data Structures
        /// <summary>
        /// Represents a single frame of simulation data
        /// </summary>
        private class SimulationFrame
        {
            public int TimeStep { get; set; }
            public float[] PWaveTimeSeries { get; set; }
            public float[] SWaveTimeSeries { get; set; }
            public float[,] VelocityTomography { get; set; }
            public float[,] WavefieldCrossSection { get; set; }
        }
        #endregion

        #region Constructor
        public AcousticSimulationVisualizer(int width, int height, int depth, float pixelSize,
                                   int tx, int ty, int tz, int rx, int ry, int rz)
        {
            // Store simulation parameters
            _width = width;
            _height = height;
            _depth = depth;
            _pixelSize = pixelSize;
            _tx = tx;
            _ty = ty;
            _tz = tz;
            _rx = rx;
            _ry = ry;
            _rz = rz;

            // Set up form properties
            this.Text = "Acoustic Simulation Visualizer";
            this.Size = new Size(1200, 800);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(30, 30, 30);
            this.ForeColor = Color.LightGray;
            this.FormClosing += SimulationVisualizer_FormClosing;
            this.KeyPreview = true;
            this.KeyDown += SimulationVisualizer_KeyDown;
            for (int i = 0; i < 4; i++)
            {
                _zoomFactors[i] = 0.8f;  // Start slightly zoomed out
            }
            
            // Initialize the color map (jet-like)
            InitializeColormap();

            // Initialize UI components
            InitializeComponents();

            // Create custom button icons
            CreateIcons();

            // Show the form
            this.Show();
            this.BeginInvoke((MethodInvoker)delegate
            {
                _currentFrameIndex = 0;
                UpdateVisualization();
            });
            // Start UI update timer
            _uiUpdateTimer.Start();
        }
        #endregion

        #region CPU/GPU Simulator Handlers
        /// <summary>
        /// Connects to a CPU simulator
        /// </summary>
        public void ConnectToCpuSimulator(AcousticSimulator simulator)
        {
            // Subscribe to events
            simulator.ProgressUpdated += Simulator_ProgressUpdated;
            simulator.SimulationCompleted += Simulator_SimulationCompleted;
        }

        /// <summary>
        /// Connects to a GPU simulator
        /// </summary>
        public void ConnectToGpuSimulator(AcousticSimulatorGPUWrapper simulator)
        {
            // Subscribe to events
            simulator.ProgressUpdated += Simulator_ProgressUpdated;
            simulator.SimulationCompleted += Simulator_SimulationCompleted;
        }

        /// <summary>
        /// Handles progress events from either simulator type
        /// </summary>
        private void Simulator_ProgressUpdated(object sender, AcousticSimulationProgressEventArgs e)
        {
            _currentStep = e.TimeStep;

            // Update every _updateInterval steps
            if (_currentStep % _updateInterval != 0)
                return;

            try
            {
                // Get wave field data from the appropriate simulator
                if (sender is AcousticSimulator cpuSimulator)
                {
                    ProcessCpuData(cpuSimulator, e);
                }
                else if (sender is AcousticSimulatorGPU gpuSimulator)
                {
                    ProcessGpuData(gpuSimulator, e);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SimulationVisualizer] Error processing data: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles simulation completion from either simulator type
        /// </summary>
        private void Simulator_SimulationCompleted(object sender, AcousticSimulationCompleteEventArgs e)
        {
            _simulationCompleted = true;
            _pWaveVelocity = e.PWaveVelocity;
            _sWaveVelocity = e.SWaveVelocity;

            // Update UI on the UI thread
            this.BeginInvoke((MethodInvoker)delegate
            {
                _timelineTrackBar.Maximum = _frames.Count - 1;
                this.Text = $"Acoustic Simulation Results - P-Wave: {_pWaveVelocity:F2} m/s, S-Wave: {_sWaveVelocity:F2} m/s";
            });
        }

        /// <summary>
        /// Process data from CPU simulator
        /// </summary>
        private void ProcessCpuData(AcousticSimulator simulator, AcousticSimulationProgressEventArgs e)
        {
            // Get wave field data from CPU simulator
            var (vx, vy, vz) = simulator.GetWaveFieldSnapshot();

            // Create a new frame
            var frame = CreateFrameFromCpuData(vx, vy, vz);

            // Add to frames collection thread-safely
            lock (_dataLock)
            {
                _frames.Add(frame);
                _currentFrameIndex = _frames.Count - 1;
            }

            // Update UI
            UpdateVisualization();
        }

        /// <summary>
        /// Process data from GPU simulator
        /// </summary>
        private void ProcessGpuData(AcousticSimulatorGPU simulator, AcousticSimulationProgressEventArgs e)
        {
            // Get wave field data from GPU simulator
            double[] vx = simulator.GetVelocityX();
            double[] vy = simulator.GetVelocityY();
            double[] vz = simulator.GetVelocityZ();

            // Create a new frame
            var frame = CreateFrameFromGpuData(vx, vy, vz);

            // Add to frames collection thread-safely
            lock (_dataLock)
            {
                _frames.Add(frame);
                _currentFrameIndex = _frames.Count - 1;
            }

            // Update UI
            UpdateVisualization();
        }

        /// <summary>
        /// Creates a simulation frame from CPU data (3D arrays)
        /// </summary>
        private SimulationFrame CreateFrameFromCpuData(double[,,] vx, double[,,] vy, double[,,] vz)
        {
            // Create time series
            float[] pWaveSeries = ExtractPWaveTimeSeries(_frames.Count, _rx, _ry, _rz, vx);
            float[] sWaveSeries = ExtractSWaveTimeSeries(_frames.Count, _rx, _ry, _rz, vy, vz);

            // Create tomography data
            float[,] velocityTomo = ComputeVelocityTomography(_tx, _ty, _tz, _rx, _ry, _rz, vx, vy, vz);

            // Create cross-section
            float[,] crossSection = ExtractCrossSection(vx, vy, vz);

            return new SimulationFrame
            {
                TimeStep = _currentStep,
                PWaveTimeSeries = pWaveSeries,
                SWaveTimeSeries = sWaveSeries,
                VelocityTomography = velocityTomo,
                WavefieldCrossSection = crossSection
            };
        }

        /// <summary>
        /// Creates a simulation frame from GPU data (flat arrays)
        /// </summary>
        private SimulationFrame CreateFrameFromGpuData(double[] vx, double[] vy, double[] vz)
        {
            // Create time series from flat arrays
            float[] pWaveSeries = ExtractPWaveTimeSeriesFlat(_frames.Count, _rx, _ry, _rz, vx, _width, _depth);
            float[] sWaveSeries = ExtractSWaveTimeSeriesFlat(_frames.Count, _rx, _ry, _rz, vy, vz, _width, _depth);

            // Create tomography data
            float[,] velocityTomo = ComputeVelocityTomographyFlat(_tx, _ty, _tz, _rx, _ry, _rz, vx, vy, vz, _width, _height, _depth);

            // Create cross-section
            float[,] crossSection = ExtractCrossSectionFlat(vx, vy, vz, _width, _height, _depth);

            return new SimulationFrame
            {
                TimeStep = _currentStep,
                PWaveTimeSeries = pWaveSeries,
                SWaveTimeSeries = sWaveSeries,
                VelocityTomography = velocityTomo,
                WavefieldCrossSection = crossSection
            };
        }
        #endregion

        #region Data Processing
        /// <summary>
        /// Initialize a colormap (jet-like)
        /// </summary>
        private void InitializeColormap()
        {
            _colormap = new Color[256];
            for (int i = 0; i < 256; i++)
            {
                double value = i / 255.0;
                int r, g, b;

                if (value < 0.125)
                {
                    r = 0;
                    g = 0;
                    b = (int)(255 * (value / 0.125));
                }
                else if (value < 0.375)
                {
                    r = 0;
                    g = (int)(255 * ((value - 0.125) / 0.25));
                    b = 255;
                }
                else if (value < 0.625)
                {
                    r = (int)(255 * ((value - 0.375) / 0.25));
                    g = 255;
                    b = (int)(255 * (1.0 - ((value - 0.375) / 0.25)));
                }
                else if (value < 0.875)
                {
                    r = 255;
                    g = (int)(255 * (1.0 - ((value - 0.625) / 0.25)));
                    b = 0;
                }
                else
                {
                    r = (int)(255 * (1.0 - ((value - 0.875) / 0.125)));
                    g = 0;
                    b = 0;
                }

                _colormap[i] = Color.FromArgb(r, g, b);
            }
        }

        /// <summary>
        /// Extract P-wave time series at receiver
        /// </summary>
        private float[] ExtractPWaveTimeSeries(int frameCount, int rx, int ry, int rz, double[,,] vx)
        {
            float[] series = new float[frameCount + 1];

            // Add the current value to the time series
            if (frameCount > 0)
            {
                // Copy previous values
                for (int i = 0; i < frameCount; i++)
                {
                    series[i] = i < _frames.Count ? _frames[i].PWaveTimeSeries[i] : 0;
                }
            }

            // Get current value and amplify for better visibility
            double value = vx[rx, ry, rz];

            // Ensure we have a meaningful value to display
            if (Math.Abs(value) < 1e-10) value = 0;

            // Amplify the signal for better visibility
            value *= 10.0;  // Adjust amplification factor as needed

            series[frameCount] = (float)value;

            return series;
        }

        /// <summary>
        /// Extract S-wave time series at receiver from 3D arrays
        /// </summary>
        private float[] ExtractSWaveTimeSeries(int frameCount, int rx, int ry, int rz, double[,,] vy, double[,,] vz)
        {
            float[] series = new float[frameCount + 1];

            // Add the current value to the time series
            if (frameCount > 0)
            {
                // Copy previous values
                for (int i = 0; i < frameCount; i++)
                {
                    series[i] = i < _frames.Count ? _frames[i].SWaveTimeSeries[i] : 0;
                }
            }

            // Compute magnitude of S-wave (combination of y and z components)
            double magnitude = Math.Sqrt(vy[rx, ry, rz] * vy[rx, ry, rz] + vz[rx, ry, rz] * vz[rx, ry, rz]);

            // Ensure we have a meaningful value to display
            if (magnitude < 1e-10) magnitude = 0;

            // Amplify the signal for better visibility
            magnitude *= 10.0;  // Adjust amplification factor as needed

            series[frameCount] = (float)magnitude;

            return series;
        }

        /// <summary>
        /// Extract P-wave time series at receiver from flat arrays
        /// </summary>
        private float[] ExtractPWaveTimeSeriesFlat(int frameCount, int rx, int ry, int rz, double[] vx, int width, int depth)
        {
            float[] series = new float[frameCount + 1];

            // Add the current value to the time series
            if (frameCount > 0)
            {
                // Copy previous values
                for (int i = 0; i < frameCount; i++)
                {
                    series[i] = _frames[i].PWaveTimeSeries[i];
                }
            }

            // Calculate flat index
            int index = (rz * width * _height) + (ry * width) + rx;

            // Add the current value at the receiver position
            series[frameCount] = (float)vx[index];

            return series;
        }

        /// <summary>
        /// Extract S-wave time series at receiver from flat arrays
        /// </summary>
        private float[] ExtractSWaveTimeSeriesFlat(int frameCount, int rx, int ry, int rz, double[] vy, double[] vz, int width, int depth)
        {
            float[] series = new float[frameCount + 1];

            // Add the current value to the time series
            if (frameCount > 0)
            {
                // Copy previous values
                for (int i = 0; i < frameCount; i++)
                {
                    series[i] = _frames[i].SWaveTimeSeries[i];
                }
            }

            // Calculate flat index
            int index = (rz * width * _height) + (ry * width) + rx;

            // Compute magnitude of S-wave (combination of y and z components)
            double magnitude = Math.Sqrt(vy[index] * vy[index] + vz[index] * vz[index]);
            series[frameCount] = (float)magnitude;

            return series;
        }

        /// <summary>
        /// Compute velocity tomography along the path from TX to RX from 3D arrays
        /// </summary>
        private float[,] ComputeVelocityTomography(int tx, int ty, int tz, int rx, int ry, int rz,
                                         double[,,] vx, double[,,] vy, double[,,] vz)
        {
            // Determine the plane to use based on largest dimension of the path
            int dx = Math.Abs(rx - tx);
            int dy = Math.Abs(ry - ty);
            int dz = Math.Abs(rz - tz);

            int width, height;
            float[,] tomography;

            // Use a scaling factor to ensure transducers are properly spaced
            float displayScale = 1.0f;

            if (dx >= dy && dx >= dz)
            {
                // YZ plane
                width = _height;
                height = _depth;
                tomography = new float[width, height];

                int x = (tx + rx) / 2; // Use middle of path

                for (int y = 0; y < width; y++)
                {
                    for (int z = 0; z < height; z++)
                    {
                        // Calculate velocity magnitude
                        double vMag = Math.Sqrt(vx[x, y, z] * vx[x, y, z] +
                                              vy[x, y, z] * vy[x, y, z] +
                                              vz[x, y, z] * vz[x, y, z]);
                        // Apply a threshold for better visualization
                        if (vMag < 1e-6) vMag = 0;
                        tomography[y, z] = (float)vMag;
                    }
                }
            }
            else if (dy >= dx && dy >= dz)
            {
                // XZ plane
                width = _width;
                height = _depth;
                tomography = new float[width, height];

                int y = (ty + ry) / 2; // Use middle of path

                for (int x = 0; x < width; x++)
                {
                    for (int z = 0; z < height; z++)
                    {
                        // Calculate velocity magnitude
                        double vMag = Math.Sqrt(vx[x, y, z] * vx[x, y, z] +
                                              vy[x, y, z] * vy[x, y, z] +
                                              vz[x, y, z] * vz[x, y, z]);
                        // Apply a threshold for better visualization
                        if (vMag < 1e-6) vMag = 0;
                        tomography[x, z] = (float)vMag;
                    }
                }
            }
            else
            {
                // XY plane
                width = _width;
                height = _height;
                tomography = new float[width, height];

                int z = (tz + rz) / 2; // Use middle of path

                for (int x = 0; x < width; x++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        // Calculate velocity magnitude
                        double vMag = Math.Sqrt(vx[x, y, z] * vx[x, y, z] +
                                              vy[x, y, z] * vy[x, y, z] +
                                              vz[x, y, z] * vz[x, y, z]);
                        // Apply a threshold for better visualization
                        if (vMag < 1e-6) vMag = 0;
                        tomography[x, y] = (float)vMag;
                    }
                }
            }

            return tomography;
        }
        /// <summary>
        /// Compute velocity tomography along the path from TX to RX from flat arrays
        /// </summary>
        private float[,] ComputeVelocityTomographyFlat(int tx, int ty, int tz, int rx, int ry, int rz,
                                                  double[] vx, double[] vy, double[] vz,
                                                  int width, int height, int depth)
        {
            // Determine the plane to use based on largest dimension of the path
            int dx = Math.Abs(rx - tx);
            int dy = Math.Abs(ry - ty);
            int dz = Math.Abs(rz - tz);

            int planeWidth, planeHeight;
            float[,] tomography;

            if (dx >= dy && dx >= dz)
            {
                // YZ plane
                planeWidth = height;
                planeHeight = depth;
                tomography = new float[planeWidth, planeHeight];

                int x = (tx + rx) / 2; // Use middle of path

                for (int y = 0; y < planeWidth; y++)
                {
                    for (int z = 0; z < planeHeight; z++)
                    {
                        int index = (z * width * height) + (y * width) + x;

                        // Calculate velocity magnitude
                        double vMag = Math.Sqrt(vx[index] * vx[index] +
                                              vy[index] * vy[index] +
                                              vz[index] * vz[index]);
                        tomography[y, z] = (float)vMag;
                    }
                }
            }
            else if (dy >= dx && dy >= dz)
            {
                // XZ plane
                planeWidth = width;
                planeHeight = depth;
                tomography = new float[planeWidth, planeHeight];

                int y = (ty + ry) / 2; // Use middle of path

                for (int x = 0; x < planeWidth; x++)
                {
                    for (int z = 0; z < planeHeight; z++)
                    {
                        int index = (z * width * height) + (y * width) + x;

                        // Calculate velocity magnitude
                        double vMag = Math.Sqrt(vx[index] * vx[index] +
                                              vy[index] * vy[index] +
                                              vz[index] * vz[index]);
                        tomography[x, z] = (float)vMag;
                    }
                }
            }
            else
            {
                // XY plane
                planeWidth = width;
                planeHeight = height;
                tomography = new float[planeWidth, planeHeight];

                int z = (tz + rz) / 2; // Use middle of path

                for (int x = 0; x < planeWidth; x++)
                {
                    for (int y = 0; y < planeHeight; y++)
                    {
                        int index = (z * width * height) + (y * width) + x;

                        // Calculate velocity magnitude
                        double vMag = Math.Sqrt(vx[index] * vx[index] +
                                              vy[index] * vy[index] +
                                              vz[index] * vz[index]);
                        tomography[x, y] = (float)vMag;
                    }
                }
            }

            return tomography;
        }

        /// <summary>
        /// Extract a cross-section of the wave field from 3D arrays
        /// </summary>
        private float[,] ExtractCrossSection(double[,,] vx, double[,,] vy, double[,,] vz)
        {
            // Use the plane that contains both TX and RX
            int dx = Math.Abs(_rx - _tx);
            int dy = Math.Abs(_ry - _ty);
            int dz = Math.Abs(_rz - _tz);

            float[,] crossSection;

            if (dz <= dx && dz <= dy)
            {
                // Use XY plane at middle Z
                int z = (_tz + _rz) / 2;
                crossSection = new float[_width, _height];

                for (int x = 0; x < _width; x++)
                {
                    for (int y = 0; y < _height; y++)
                    {
                        double magnitude = Math.Sqrt(
                            vx[x, y, z] * vx[x, y, z] +
                            vy[x, y, z] * vy[x, y, z] +
                            vz[x, y, z] * vz[x, y, z]);

                        crossSection[x, y] = (float)magnitude;
                    }
                }
            }
            else if (dy <= dx && dy <= dz)
            {
                // Use XZ plane at middle Y
                int y = (_ty + _ry) / 2;
                crossSection = new float[_width, _depth];

                for (int x = 0; x < _width; x++)
                {
                    for (int z = 0; z < _depth; z++)
                    {
                        double magnitude = Math.Sqrt(
                            vx[x, y, z] * vx[x, y, z] +
                            vy[x, y, z] * vy[x, y, z] +
                            vz[x, y, z] * vz[x, y, z]);

                        crossSection[x, z] = (float)magnitude;
                    }
                }
            }
            else
            {
                // Use YZ plane at middle X
                int x = (_tx + _rx) / 2;
                crossSection = new float[_height, _depth];

                for (int y = 0; y < _height; y++)
                {
                    for (int z = 0; z < _depth; z++)
                    {
                        double magnitude = Math.Sqrt(
                            vx[x, y, z] * vx[x, y, z] +
                            vy[x, y, z] * vy[x, y, z] +
                            vz[x, y, z] * vz[x, y, z]);

                        crossSection[y, z] = (float)magnitude;
                    }
                }
            }

            return crossSection;
        }

        /// <summary>
        /// Extract a cross-section of the wave field from flat arrays
        /// </summary>
        private float[,] ExtractCrossSectionFlat(double[] vx, double[] vy, double[] vz, int width, int height, int depth)
        {
            // Use the plane that contains both TX and RX
            int dx = Math.Abs(_rx - _tx);
            int dy = Math.Abs(_ry - _ty);
            int dz = Math.Abs(_rz - _tz);

            float[,] crossSection;

            if (dz <= dx && dz <= dy)
            {
                // Use XY plane at middle Z
                int z = (_tz + _rz) / 2;
                crossSection = new float[width, height];

                for (int x = 0; x < width; x++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        int index = (z * width * height) + (y * width) + x;
                        double magnitude = Math.Sqrt(
                            vx[index] * vx[index] +
                            vy[index] * vy[index] +
                            vz[index] * vz[index]);

                        crossSection[x, y] = (float)magnitude;
                    }
                }
            }
            else if (dy <= dx && dy <= dz)
            {
                // Use XZ plane at middle Y
                int y = (_ty + _ry) / 2;
                crossSection = new float[width, depth];

                for (int x = 0; x < width; x++)
                {
                    for (int z = 0; z < depth; z++)
                    {
                        int index = (z * width * height) + (y * width) + x;
                        double magnitude = Math.Sqrt(
                            vx[index] * vx[index] +
                            vy[index] * vy[index] +
                            vz[index] * vz[index]);

                        crossSection[x, z] = (float)magnitude;
                    }
                }
            }
            else
            {
                // Use YZ plane at middle X
                int x = (_tx + _rx) / 2;
                crossSection = new float[height, depth];

                for (int y = 0; y < height; y++)
                {
                    for (int z = 0; z < depth; z++)
                    {
                        int index = (z * width * height) + (y * width) + x;
                        double magnitude = Math.Sqrt(
                            vx[index] * vx[index] +
                            vy[index] * vy[index] +
                            vz[index] * vz[index]);

                        crossSection[y, z] = (float)magnitude;
                    }
                }
            }

            return crossSection;
        }
        #endregion

        #region UI Components
        /// <summary>
        /// Initialize UI components
        /// </summary>
        private void InitializeComponents()
        {
            // Main panel for visualizations
            _mainPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(20, 20, 20)
            };
            this.Controls.Add(_mainPanel);

            // Create sub-panels for each visualization
            _subPanels = new Panel[4];
            _pictureBoxes = new PictureBox[4];

            // Panel layout: 2x2 grid
            int panelWidth = _mainPanel.Width / 2;
            int panelHeight = _mainPanel.Height / 2;

            for (int i = 0; i < 4; i++)
            {
                int row = i / 2;
                int col = i % 2;

                _subPanels[i] = new Panel
                {
                    Left = col * panelWidth,
                    Top = row * panelHeight,
                    Width = panelWidth,
                    Height = panelHeight,
                    BackColor = Color.Black,
                    BorderStyle = BorderStyle.FixedSingle
                };

                _pictureBoxes[i] = new PictureBox
                {
                    Dock = DockStyle.Fill,
                    SizeMode = PictureBoxSizeMode.StretchImage,
                    BackColor = Color.Black
                };

                // Add mouse handlers for zoom/pan
                int index = i; // Capture for lambda
                _pictureBoxes[i].MouseDown += (s, e) => PictureBox_MouseDown(s, e, index);
                _pictureBoxes[i].MouseMove += (s, e) => PictureBox_MouseMove(s, e, index);
                _pictureBoxes[i].MouseUp += (s, e) => PictureBox_MouseUp(s, e);
                _pictureBoxes[i].MouseWheel += (s, e) => PictureBox_MouseWheel(s, e, index);

                // Initialize panel bitmaps
                _panelBitmaps[i] = new Bitmap(400, 300);
                _displayBitmaps[i] = new Bitmap(400, 300);
                _pictureBoxes[i].Image = _displayBitmaps[i];

                // Add labels to each panel
                string[] titles = { "P-Wave Time Series", "S-Wave Time Series",
                                   "Velocity Tomography", "Wavefield Cross-Section" };

                Label titleLabel = new Label
                {
                    Text = titles[i],
                    ForeColor = Color.White,
                    BackColor = Color.FromArgb(50, 50, 50),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Dock = DockStyle.Top,
                    Height = 24
                };

                _subPanels[i].Controls.Add(_pictureBoxes[i]);
                _subPanels[i].Controls.Add(titleLabel);
                _mainPanel.Controls.Add(_subPanels[i]);
            }

            // Add controls panel at the bottom
            Panel controlsPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 60,
                BackColor = Color.FromArgb(40, 40, 40)
            };
            this.Controls.Add(controlsPanel);

            // Add timeline trackbar
            _timelineTrackBar = new TrackBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                TickFrequency = 10,
                Width = 600,
                Height = 45,
                Left = 80,
                Top = 10,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.White
            };
            _timelineTrackBar.ValueChanged += TimelineTrackBar_ValueChanged;
            controlsPanel.Controls.Add(_timelineTrackBar);

            // Add time step label
            _timeStepLabel = new Label
            {
                Text = "Step: 0",
                ForeColor = Color.White,
                Left = 10,
                Top = 20,
                Width = 70,
                Height = 20
            };
            controlsPanel.Controls.Add(_timeStepLabel);

            // Add export button
            _exportButton = new Button
            {
                Width = 40,
                Height = 40,
                Left = 690,
                Top = 10,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            _exportButton.Click += ExportButton_Click;
            controlsPanel.Controls.Add(_exportButton);

            // Add export animation button
            _exportAnimationButton = new Button
            {
                Width = 40,
                Height = 40,
                Left = 740,
                Top = 10,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            _exportAnimationButton.Click += ExportAnimationButton_Click;
            controlsPanel.Controls.Add(_exportAnimationButton);

            // Add play/pause button
            _playPauseButton = new Button
            {
                Width = 40,
                Height = 40,
                Left = 790,
                Top = 10,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            _playPauseButton.Click += PlayPauseButton_Click;
            controlsPanel.Controls.Add(_playPauseButton);

            // Create tooltips
            _toolTip = new ToolTip();
            _toolTip.SetToolTip(_exportButton, "Export current view");
            _toolTip.SetToolTip(_exportAnimationButton, "Export animation");
            _toolTip.SetToolTip(_playPauseButton, "Play/Pause animation");

            // Create playback timer
            _playbackTimer = new Timer
            {
                Interval = _playbackInterval,
                Enabled = false
            };
            _playbackTimer.Tick += PlaybackTimer_Tick;

            // Create UI update timer
            _uiUpdateTimer = new Timer
            {
                Interval = 100, // 100ms update interval
                Enabled = false
            };
            _uiUpdateTimer.Tick += UiUpdateTimer_Tick;

            // Resize handler
            this.Resize += SimulationVisualizer_Resize;
        }

        /// <summary>
        /// Create custom icons for buttons
        /// </summary>
        private void CreateIcons()
        {
            // Create play icon
            _playIcon = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(_playIcon))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;

                Point[] trianglePoints = new Point[] {
                    new Point(8, 8),
                    new Point(8, 24),
                    new Point(24, 16)
                };

                g.FillPolygon(Brushes.LightGreen, trianglePoints);
            }

            // Create pause icon
            _pauseIcon = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(_pauseIcon))
            {
                g.Clear(Color.Transparent);
                g.FillRectangle(Brushes.LightBlue, 8, 8, 6, 16);
                g.FillRectangle(Brushes.LightBlue, 18, 8, 6, 16);
            }

            // Create export icon
            _exportIcon = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(_exportIcon))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;

                // Draw document
                g.FillRectangle(Brushes.White, 8, 4, 16, 20);
                g.DrawRectangle(new Pen(Color.Gray), 8, 4, 16, 20);

                // Draw arrow
                Point[] arrowPoints = new Point[] {
                    new Point(16, 16),
                    new Point(16, 28),
                    new Point(10, 22),
                    new Point(16, 28),
                    new Point(22, 22)
                };

                g.DrawLines(new Pen(Color.Green, 2), arrowPoints);
            }

            // Create animation export icon
            _animationIcon = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(_animationIcon))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;

                // Draw film strip
                g.FillRectangle(Brushes.DarkGray, 6, 6, 20, 20);
                g.FillRectangle(Brushes.Black, 8, 8, 16, 16);

                // Draw sprocket holes
                g.FillRectangle(Brushes.White, 6, 6, 3, 3);
                g.FillRectangle(Brushes.White, 6, 23, 3, 3);
                g.FillRectangle(Brushes.White, 23, 6, 3, 3);
                g.FillRectangle(Brushes.White, 23, 23, 3, 3);

                // Draw arrow
                Point[] arrowPoints = new Point[] {
                    new Point(16, 12),
                    new Point(16, 20),
                    new Point(20, 16)
                };

                g.FillPolygon(Brushes.Yellow, arrowPoints);
            }

            // Set button images
            _exportButton.Image = _exportIcon;
            _exportAnimationButton.Image = _animationIcon;
            _playPauseButton.Image = _playIcon;
        }

        /// <summary>
        /// Update the visualization with the current frame data
        /// </summary>
        private void UpdateVisualization()
        {
            lock (_dataLock)
            {
                // Check if we have frames
                if (_frames.Count == 0 || _currentFrameIndex < 0 || _currentFrameIndex >= _frames.Count)
                    return;

                SimulationFrame frame = _frames[_currentFrameIndex];

                try
                {
                    // Update time series panel (P-wave)
                    using (Graphics g = Graphics.FromImage(_panelBitmaps[0]))
                    {
                        DrawTimeSeries(g, frame.PWaveTimeSeries, 0);
                    }

                    // Update time series panel (S-wave)
                    using (Graphics g = Graphics.FromImage(_panelBitmaps[1]))
                    {
                        DrawTimeSeries(g, frame.SWaveTimeSeries, 1);
                    }

                    // Update velocity tomography panel
                    using (Graphics g = Graphics.FromImage(_panelBitmaps[2]))
                    {
                        DrawHeatmap(g, frame.VelocityTomography, 2);
                    }

                    // Update cross-section panel
                    using (Graphics g = Graphics.FromImage(_panelBitmaps[3]))
                    {
                        DrawHeatmap(g, frame.WavefieldCrossSection, 3);
                    }

                    // Update the UI on the main thread
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        // Update time step label
                        _timeStepLabel.Text = $"Step: {frame.TimeStep}";

                        // If this is a new maximum, update the trackbar
                        if (_frames.Count > _timelineTrackBar.Maximum + 1)
                        {
                            _timelineTrackBar.Maximum = _frames.Count - 1;
                        }

                        // Only update trackbar if not being dragged
                        if (!_timelineTrackBar.Capture)
                        {
                            _timelineTrackBar.Value = _currentFrameIndex;
                        }

                        // Copy bitmaps to display bitmaps and refresh pictureboxes
                        for (int i = 0; i < 4; i++)
                        {
                            if (_displayBitmaps[i] != null && _displayBitmaps[i] != _panelBitmaps[i])
                            {
                                _displayBitmaps[i].Dispose();
                            }

                            _displayBitmaps[i] = (Bitmap)_panelBitmaps[i].Clone();
                            _pictureBoxes[i].Image = _displayBitmaps[i];
                            _pictureBoxes[i].Refresh();
                        }
                    });
                }
                catch (Exception ex)
                {
                    Logger.Log($"[SimulationVisualizer] Error updating visualization: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Draw time series data on the specified graphics context
        /// </summary>
        private void DrawTimeSeries(Graphics g, float[] series, int panelIndex)
        {
            if (series == null || series.Length == 0)
                return;

            int width = _panelBitmaps[panelIndex].Width;
            int height = _panelBitmaps[panelIndex].Height;

            // Clear background
            g.Clear(Color.Black);

            // Find min/max values for scaling
            float minVal = float.MaxValue;
            float maxVal = float.MinValue;
            bool hasNonZeroData = false;

            foreach (float val in series)
            {
                if (Math.Abs(val) > 1e-6) hasNonZeroData = true;
                minVal = Math.Min(minVal, val);
                maxVal = Math.Max(maxVal, val);
            }

            // If we don't have meaningful data yet, use default range
            if (!hasNonZeroData || Math.Abs(maxVal - minVal) < 1e-6)
            {
                minVal = -1.0f;
                maxVal = 1.0f;
            }

            // Ensure minimum range for visibility
            float range = maxVal - minVal;
            if (range < 0.1f)
            {
                float mid = (maxVal + minVal) / 2;
                minVal = mid - 0.05f;
                maxVal = mid + 0.05f;
            }

            // Calculate scaling factors
            float xScale = (float)width / (series.Length > 1 ? series.Length - 1 : 1);
            float yScale = height / (maxVal - minVal);

            // Draw zero line
            if (minVal < 0 && maxVal > 0)
            {
                float zeroY = height - (0 - minVal) * yScale;
                g.DrawLine(new Pen(Color.FromArgb(60, 60, 60), 1), 0, zeroY, width, zeroY);
            }

            // Draw grid lines
            using (Pen gridPen = new Pen(Color.FromArgb(30, 30, 30)))
            {
                // Vertical grid lines
                int numVerticals = 10;
                for (int i = 1; i < numVerticals; i++)
                {
                    float x = (float)i * width / numVerticals;
                    g.DrawLine(gridPen, x, 0, x, height);
                }

                // Horizontal grid lines
                int numHorizontals = 8;
                for (int i = 1; i < numHorizontals; i++)
                {
                    float y = (float)i * height / numHorizontals;
                    g.DrawLine(gridPen, 0, y, width, y);
                }
            }

            // Draw the time series
            Point[] points = new Point[series.Length];

            for (int i = 0; i < series.Length; i++)
            {
                float x = i * xScale;
                float y = height - (series[i] - minVal) * yScale;

                // Clamp y to prevent drawing outside bounds
                y = Math.Max(0, Math.Min(height, y));

                points[i] = new Point((int)x, (int)y);
            }

            // Draw filled area below the line with more vibrant colors
            Point[] areaPoints = new Point[points.Length + 2];
            Array.Copy(points, 0, areaPoints, 0, points.Length);
            areaPoints[points.Length] = new Point(points[points.Length - 1].X, height);
            areaPoints[points.Length + 1] = new Point(points[0].X, height);

            using (Brush areaBrush = panelIndex == 0 ?
                   new LinearGradientBrush(new Point(0, 0), new Point(0, height),
                                          Color.FromArgb(120, 0, 180, 255), Color.FromArgb(30, 0, 0, 80)) :
                   new LinearGradientBrush(new Point(0, 0), new Point(0, height),
                                          Color.FromArgb(120, 255, 100, 100), Color.FromArgb(30, 80, 0, 0)))
            {
                g.FillPolygon(areaBrush, areaPoints);
            }

            // Draw the line with thicker, more visible pen
            using (Pen linePen = new Pen(panelIndex == 0 ? Color.DeepSkyBlue : Color.Crimson, 2f))
            {
                if (points.Length > 1)
                {
                    g.DrawLines(linePen, points);
                }
            }

            // Draw points on the line for better visibility
            using (Brush pointBrush = new SolidBrush(panelIndex == 0 ? Color.LightCyan : Color.LightPink))
            {
                // Only draw points if we have fewer than 100 to avoid cluttering
                if (points.Length < 100)
                {
                    foreach (var point in points)
                    {
                        g.FillEllipse(pointBrush, point.X - 2, point.Y - 2, 4, 4);
                    }
                }
            }

            // Draw coordinate values
            using (Font font = new Font("Arial", 8))
            using (Brush textBrush = new SolidBrush(Color.LightGray))
            {
                // Min/max values
                g.DrawString($"{minVal:F3}", font, textBrush, 5, height - 15);
                g.DrawString($"{maxVal:F3}", font, textBrush, 5, 5);

                // Panel title with more visibility
                string title = panelIndex == 0 ? "P-Wave" : "S-Wave";
                using (Font titleFont = new Font("Arial", 10, FontStyle.Bold))
                using (Brush titleBrush = new SolidBrush(panelIndex == 0 ? Color.DeepSkyBlue : Color.Crimson))
                {
                    g.DrawString(title, titleFont, titleBrush, width / 2 - 30, 5);
                }
            }
        }

        /// <summary>
        /// Draw heatmap data on the specified graphics context
        /// </summary>
        private void DrawHeatmap(Graphics g, float[,] data, int panelIndex)
        {
            if (data == null)
                return;

            int width = _panelBitmaps[panelIndex].Width;
            int height = _panelBitmaps[panelIndex].Height;

            // Clear background
            g.Clear(Color.Black);

            int dataWidth = data.GetLength(0);
            int dataHeight = data.GetLength(1);

            // Find min/max values for scaling
            float minVal = float.MaxValue;
            float maxVal = float.MinValue;

            for (int y = 0; y < dataHeight; y++)
            {
                for (int x = 0; x < dataWidth; x++)
                {
                    minVal = Math.Min(minVal, data[x, y]);
                    maxVal = Math.Max(maxVal, data[x, y]);
                }
            }

            // Ensure we have a valid range
            if (Math.Abs(maxVal - minVal) < 1e-6)
            {
                maxVal = minVal + 1.0f;
            }

            // Create a direct bitmap for faster pixel manipulation
            Bitmap heatmap = new Bitmap(width, height);

            // Calculate pixel size
            float cellWidth = (float)width / dataWidth;
            float cellHeight = (float)height / dataHeight;

            // Draw each pixel
            BitmapData bmpData = heatmap.LockBits(new Rectangle(0, 0, width, height),
                                                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            try
            {
                unsafe
                {
                    byte* ptr = (byte*)bmpData.Scan0;

                    for (int y = 0; y < height; y++)
                    {
                        int dataY = (int)(y / cellHeight);
                        if (dataY >= dataHeight) dataY = dataHeight - 1;

                        for (int x = 0; x < width; x++)
                        {
                            int dataX = (int)(x / cellWidth);
                            if (dataX >= dataWidth) dataX = dataWidth - 1;

                            float value = data[dataX, dataY];
                            float normalized = (value - minVal) / (maxVal - minVal);
                            int colorIndex = (int)(normalized * 255);
                            if (colorIndex < 0) colorIndex = 0;
                            if (colorIndex > 255) colorIndex = 255;

                            Color color = _colormap[colorIndex];

                            int index = y * bmpData.Stride + x * 4;
                            ptr[index] = color.B;
                            ptr[index + 1] = color.G;
                            ptr[index + 2] = color.R;
                            ptr[index + 3] = 255; // Alpha
                        }
                    }
                }
            }
            finally
            {
                heatmap.UnlockBits(bmpData);
            }

            // Draw the heatmap
            g.DrawImage(heatmap, 0, 0, width, height);
            heatmap.Dispose();

            // Draw TX and RX points
            if (panelIndex == 2) // Velocity tomography
            {
                // Draw source and receiver positions if they're in this plane
                float txX = 0, txY = 0, rxX = 0, rxY = 0;
                bool showTx = false, showRx = false;

                // Determine which plane we're showing and map TX/RX positions
                // This depends on how we constructed the tomography
                int dx = Math.Abs(_rx - _tx);
                int dy = Math.Abs(_ry - _ty);
                int dz = Math.Abs(_rz - _tz);

                if (dx >= dy && dx >= dz)
                {
                    // YZ plane - check if TX/RX are near this plane
                    showTx = true;
                    showRx = true;
                    txX = _ty * cellWidth;
                    txY = _tz * cellHeight;
                    rxX = _ry * cellWidth;
                    rxY = _rz * cellHeight;
                }
                else if (dy >= dx && dy >= dz)
                {
                    // XZ plane - check if TX/RX are near this plane
                    showTx = true;
                    showRx = true;
                    txX = _tx * cellWidth;
                    txY = _tz * cellHeight;
                    rxX = _rx * cellWidth;
                    rxY = _rz * cellHeight;
                }
                else
                {
                    // XY plane - check if TX/RX are near this plane
                    showTx = true;
                    showRx = true;
                    txX = _tx * cellWidth;
                    txY = _ty * cellHeight;
                    rxX = _rx * cellWidth;
                    rxY = _ry * cellHeight;
                }

                // Draw lines from TX to RX
                if (showTx && showRx)
                {
                    using (Pen pen = new Pen(Color.White, 1))
                    {
                        pen.DashStyle = DashStyle.Dash;
                        g.DrawLine(pen, txX, txY, rxX, rxY);
                    }
                }

                // Draw TX marker (source)
                if (showTx)
                {
                    const int markerSize = 6;
                    g.FillEllipse(Brushes.Yellow, txX - markerSize / 2, txY - markerSize / 2, markerSize, markerSize);
                    g.DrawEllipse(Pens.White, txX - markerSize / 2, txY - markerSize / 2, markerSize, markerSize);

                    using (Font font = new Font("Arial", 8))
                    using (Brush textBrush = new SolidBrush(Color.Yellow))
                    {
                        g.DrawString("TX", font, textBrush, txX + 8, txY - 8);
                    }
                }

                // Draw RX marker (receiver)
                if (showRx)
                {
                    const int markerSize = 6;
                    g.FillEllipse(Brushes.Cyan, rxX - markerSize / 2, rxY - markerSize / 2, markerSize, markerSize);
                    g.DrawEllipse(Pens.White, rxX - markerSize / 2, rxY - markerSize / 2, markerSize, markerSize);

                    using (Font font = new Font("Arial", 8))
                    using (Brush textBrush = new SolidBrush(Color.Cyan))
                    {
                        g.DrawString("RX", font, textBrush, rxX + 8, rxY - 8);
                    }
                }
            }
            else if (panelIndex == 3) // Wavefield cross-section
            {
                // Draw a colorbar to indicate magnitude scale
                int barWidth = 15;
                int barHeight = 150;
                int barX = width - barWidth - 10;
                int barY = 20;

                // Draw colorbar
                for (int y = 0; y < barHeight; y++)
                {
                    float normalized = 1.0f - (float)y / barHeight;
                    int colorIndex = (int)(normalized * 255);
                    if (colorIndex < 0) colorIndex = 0;
                    if (colorIndex > 255) colorIndex = 255;

                    using (Brush brush = new SolidBrush(_colormap[colorIndex]))
                    {
                        g.FillRectangle(brush, barX, barY + y, barWidth, 1);
                    }
                }

                // Draw colorbar border
                g.DrawRectangle(Pens.Gray, barX, barY, barWidth, barHeight);

                // Draw min/max labels
                using (Font font = new Font("Arial", 8))
                using (Brush textBrush = new SolidBrush(Color.White))
                {
                    g.DrawString($"{maxVal:F3}", font, textBrush, barX - 35, barY - 5);
                    g.DrawString($"{minVal:F3}", font, textBrush, barX - 35, barY + barHeight + 5);
                }
            }
        }
        #endregion

        #region Event Handlers
        /// <summary>
        /// Handle timeline trackbar value changed
        /// </summary>
        private void TimelineTrackBar_ValueChanged(object sender, EventArgs e)
        {
            lock (_dataLock)
            {
                // Update current frame index
                int newIndex = _timelineTrackBar.Value;
                if (newIndex >= 0 && newIndex < _frames.Count)
                {
                    _currentFrameIndex = newIndex;
                    UpdateVisualization();
                }
            }
        }

        /// <summary>
        /// Handle export button click
        /// </summary>
        private void ExportButton_Click(object sender, EventArgs e)
        {
            // Create combined image of all panels
            Bitmap combinedImage = CombinePanelsForExport();

            // Show save dialog
            SaveFileDialog dialog = new SaveFileDialog
            {
                Filter = "PNG Image|*.png|JPEG Image|*.jpg|Bitmap Image|*.bmp",
                Title = "Export Visualization"
            };

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    // Save based on selected format
                    string ext = Path.GetExtension(dialog.FileName).ToLower();
                    ImageFormat format;

                    switch (ext)
                    {
                        case ".jpg":
                            format = ImageFormat.Jpeg;
                            break;
                        case ".bmp":
                            format = ImageFormat.Bmp;
                            break;
                        default:
                            format = ImageFormat.Png;
                            break;
                    }

                    combinedImage.Save(dialog.FileName, format);
                    MessageBox.Show("Visualization exported successfully!", "Export Complete",
                                  MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error exporting visualization: {ex.Message}", "Export Error",
                                  MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    combinedImage.Dispose();
                }
            }
        }

        /// <summary>
        /// Handle export animation button click
        /// </summary>
        private void ExportAnimationButton_Click(object sender, EventArgs e)
        {
            // Show folder selection dialog
            FolderBrowserDialog dialog = new FolderBrowserDialog
            {
                Description = "Select folder to save animation frames"
            };

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    string folderPath = dialog.SelectedPath;

                    // Show progress dialog
                    using (ProgressForm progress = new ProgressForm("Exporting Animation"))
                    {
                        progress.Show(this);

                        // Export frames in a background thread
                        Task.Run(() =>
                        {
                            try
                            {
                                ExportAnimationFrames(folderPath, progress);

                                this.BeginInvoke((MethodInvoker)delegate
                                {
                                    MessageBox.Show($"Animation exported successfully!\nFrames saved to: {folderPath}",
                                                  "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                                });
                            }
                            catch (Exception ex)
                            {
                                this.BeginInvoke((MethodInvoker)delegate
                                {
                                    MessageBox.Show($"Error exporting animation: {ex.Message}",
                                                  "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                });
                            }
                            finally
                            {
                                this.BeginInvoke((MethodInvoker)delegate
                                {
                                    progress.Close();
                                });
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error preparing animation export: {ex.Message}",
                                  "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        /// <summary>
        /// Handle play/pause button click
        /// </summary>
        private void PlayPauseButton_Click(object sender, EventArgs e)
        {
            _isPlaying = !_isPlaying;

            if (_isPlaying)
            {
                // Start playback
                _playPauseButton.Image = _pauseIcon;
                _toolTip.SetToolTip(_playPauseButton, "Pause animation");
                _playbackTimer.Start();
            }
            else
            {
                // Pause playback
                _playPauseButton.Image = _playIcon;
                _toolTip.SetToolTip(_playPauseButton, "Play animation");
                _playbackTimer.Stop();
            }
        }

        /// <summary>
        /// Handle playback timer tick
        /// </summary>
        private void PlaybackTimer_Tick(object sender, EventArgs e)
        {
            lock (_dataLock)
            {
                // Advance to next frame
                if (_currentFrameIndex < _frames.Count - 1)
                {
                    _currentFrameIndex++;
                }
                else
                {
                    // Loop back to beginning
                    _currentFrameIndex = 0;
                }

                // Update trackbar and visualization
                _timelineTrackBar.Value = _currentFrameIndex;
                UpdateVisualization();
            }
        }

        /// <summary>
        /// Handle UI update timer tick
        /// </summary>
        private void UiUpdateTimer_Tick(object sender, EventArgs e)
        {
            // Update panel layouts in case of resize
            int panelWidth = _mainPanel.Width / 2;
            int panelHeight = _mainPanel.Height / 2;

            for (int i = 0; i < 4; i++)
            {
                int row = i / 2;
                int col = i % 2;

                _subPanels[i].Left = col * panelWidth;
                _subPanels[i].Top = row * panelHeight;
                _subPanels[i].Width = panelWidth;
                _subPanels[i].Height = panelHeight;
            }
        }

        /// <summary>
        /// Handle form resize
        /// </summary>
        private void SimulationVisualizer_Resize(object sender, EventArgs e)
        {
            if (WindowState != FormWindowState.Minimized)
            {
                // Resize panels
                int panelWidth = _mainPanel.Width / 2;
                int panelHeight = _mainPanel.Height / 2;

                for (int i = 0; i < 4; i++)
                {
                    int row = i / 2;
                    int col = i % 2;

                    _subPanels[i].Left = col * panelWidth;
                    _subPanels[i].Top = row * panelHeight;
                    _subPanels[i].Width = panelWidth;
                    _subPanels[i].Height = panelHeight;

                    // Resize panel bitmaps
                    if (_panelBitmaps[i] != null)
                    {
                        _panelBitmaps[i].Dispose();
                    }

                    _panelBitmaps[i] = new Bitmap(Math.Max(1, _pictureBoxes[i].Width),
                                               Math.Max(1, _pictureBoxes[i].Height));

                    if (_displayBitmaps[i] != null)
                    {
                        _displayBitmaps[i].Dispose();
                    }

                    _displayBitmaps[i] = new Bitmap(Math.Max(1, _pictureBoxes[i].Width),
                                                 Math.Max(1, _pictureBoxes[i].Height));
                    _pictureBoxes[i].Image = _displayBitmaps[i];
                }

                // Update visualization with resized bitmaps
                UpdateVisualization();
            }
        }

        /// <summary>
        /// Handle form closing
        /// </summary>
        private void SimulationVisualizer_FormClosing(object sender, FormClosingEventArgs e)
        {
            // Clean up resources
            _playbackTimer.Stop();
            _uiUpdateTimer.Stop();

            // Dispose of bitmaps
            for (int i = 0; i < 4; i++)
            {
                if (_panelBitmaps[i] != null)
                {
                    _panelBitmaps[i].Dispose();
                    _panelBitmaps[i] = null;
                }

                if (_displayBitmaps[i] != null)
                {
                    _displayBitmaps[i].Dispose();
                    _displayBitmaps[i] = null;
                }
            }

            // Dispose of icons
            if (_playIcon != null) _playIcon.Dispose();
            if (_pauseIcon != null) _pauseIcon.Dispose();
            if (_exportIcon != null) _exportIcon.Dispose();
            if (_animationIcon != null) _animationIcon.Dispose();
        }

        /// <summary>
        /// Handle key down
        /// </summary>
        private void SimulationVisualizer_KeyDown(object sender, KeyEventArgs e)
        {
            // Allow keyboard navigation through frames
            if (e.KeyCode == Keys.Left)
            {
                // Previous frame
                if (_currentFrameIndex > 0)
                {
                    _currentFrameIndex--;
                    _timelineTrackBar.Value = _currentFrameIndex;
                }
            }
            else if (e.KeyCode == Keys.Right)
            {
                // Next frame
                if (_currentFrameIndex < _frames.Count - 1)
                {
                    _currentFrameIndex++;
                    _timelineTrackBar.Value = _currentFrameIndex;
                }
            }
            else if (e.KeyCode == Keys.Space)
            {
                // Toggle play/pause
                PlayPauseButton_Click(sender, e);
            }
            else if (e.KeyCode == Keys.Escape)
            {
                // Reset zoom and pan for all panels
                for (int i = 0; i < 4; i++)
                {
                    _zoomFactors[i] = 1.0f;
                    _panOffsets[i] = new PointF(0, 0);
                }

                UpdateVisualization();
            }
        }

        /// <summary>
        /// Handle mouse down on picture box
        /// </summary>
        private void PictureBox_MouseDown(object sender, MouseEventArgs e, int panelIndex)
        {
            if (e.Button == MouseButtons.Left)
            {
                _isDragging = true;
                _lastMousePosition = e.Location;
                _selectedPanelIndex = panelIndex;

                ((PictureBox)sender).Cursor = Cursors.Hand;
            }
        }

        /// <summary>
        /// Handle mouse move on picture box
        /// </summary>
        private void PictureBox_MouseMove(object sender, MouseEventArgs e, int panelIndex)
        {
            if (_isDragging && _selectedPanelIndex == panelIndex)
            {
                // Calculate delta
                int dx = e.X - _lastMousePosition.X;
                int dy = e.Y - _lastMousePosition.Y;

                // Apply pan offset
                _panOffsets[panelIndex].X += dx / _zoomFactors[panelIndex];
                _panOffsets[panelIndex].Y += dy / _zoomFactors[panelIndex];

                // Update last position
                _lastMousePosition = e.Location;

                // Redraw the image with the new pan offset
                DrawPannedZoomedImage(panelIndex);
            }
        }

        /// <summary>
        /// Handle mouse up on picture box
        /// </summary>
        private void PictureBox_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _isDragging = false;
                _selectedPanelIndex = -1;

                ((PictureBox)sender).Cursor = Cursors.Default;
            }
        }

        /// <summary>
        /// Handle mouse wheel on picture box
        /// </summary>
        private void PictureBox_MouseWheel(object sender, MouseEventArgs e, int panelIndex)
        {
            // Calculate zoom factor change
            float zoomDelta = e.Delta > 0 ? 1.1f : 0.9f;

            // Apply zoom centered on mouse position
            float mouseX = e.X;
            float mouseY = e.Y;

            PictureBox pb = (PictureBox)sender;

            // Convert mouse position to image coordinates
            float imageX = mouseX / _zoomFactors[panelIndex] - _panOffsets[panelIndex].X;
            float imageY = mouseY / _zoomFactors[panelIndex] - _panOffsets[panelIndex].Y;

            // Apply zoom factor
            _zoomFactors[panelIndex] *= zoomDelta;

            // Clamp zoom factor
            _zoomFactors[panelIndex] = Math.Max(0.1f, Math.Min(10.0f, _zoomFactors[panelIndex]));

            // Recalculate pan offset to zoom at mouse position
            _panOffsets[panelIndex].X = mouseX / _zoomFactors[panelIndex] - imageX;
            _panOffsets[panelIndex].Y = mouseY / _zoomFactors[panelIndex] - imageY;

            // Redraw image with the new zoom/pan
            DrawPannedZoomedImage(panelIndex);
        }
        #endregion

        #region Helper Methods
        /// <summary>
        /// Draw a panned and zoomed image for the specified panel
        /// </summary>
        private void DrawPannedZoomedImage(int panelIndex)
        {
            if (_panelBitmaps[panelIndex] == null || _displayBitmaps[panelIndex] == null)
                return;

            // Clear display bitmap
            using (Graphics g = Graphics.FromImage(_displayBitmaps[panelIndex]))
            {
                g.Clear(Color.Black);

                // Set up transform for pan and zoom
                Matrix transform = new Matrix();
                transform.Translate(_panOffsets[panelIndex].X, _panOffsets[panelIndex].Y);
                transform.Scale(_zoomFactors[panelIndex], _zoomFactors[panelIndex]);
                g.Transform = transform;

                // Draw the panel bitmap with the transform applied
                g.DrawImage(_panelBitmaps[panelIndex], 0, 0);
            }

            // Update picture box
            _pictureBoxes[panelIndex].Refresh();
        }

        /// <summary>
        /// Combine all panels into a single image for export
        /// </summary>
        private Bitmap CombinePanelsForExport()
        {
            // Calculate combined size
            int width = _mainPanel.Width;
            int height = _mainPanel.Height;

            // Create combined bitmap
            Bitmap combined = new Bitmap(width, height);

            using (Graphics g = Graphics.FromImage(combined))
            {
                g.Clear(Color.Black);

                // Draw each panel
                for (int i = 0; i < 4; i++)
                {
                    int row = i / 2;
                    int col = i % 2;

                    int x = col * (width / 2);
                    int y = row * (height / 2);

                    // Draw panel content
                    g.DrawImage(_panelBitmaps[i], x, y, width / 2, height / 2);

                    // Draw panel title
                    string[] titles = { "P-Wave Time Series", "S-Wave Time Series",
                                      "Velocity Tomography", "Wavefield Cross-Section" };

                    using (Font font = new Font("Arial", 10, FontStyle.Bold))
                    using (Brush brush = new SolidBrush(Color.White))
                    using (Brush bgBrush = new SolidBrush(Color.FromArgb(128, 0, 0, 0)))
                    {
                        SizeF textSize = g.MeasureString(titles[i], font);
                        g.FillRectangle(bgBrush, x + 5, y + 5, textSize.Width + 10, textSize.Height + 5);
                        g.DrawString(titles[i], font, brush, x + 10, y + 7);
                    }
                }

                // Draw step info
                if (_frames.Count > 0 && _currentFrameIndex >= 0 && _currentFrameIndex < _frames.Count)
                {
                    int step = _frames[_currentFrameIndex].TimeStep;

                    using (Font font = new Font("Arial", 12, FontStyle.Bold))
                    using (Brush brush = new SolidBrush(Color.White))
                    using (Brush bgBrush = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                    {
                        string info = $"Step: {step}";
                        if (_simulationCompleted)
                        {
                            info += $"   P-Wave: {_pWaveVelocity:F2} m/s   S-Wave: {_sWaveVelocity:F2} m/s";
                        }

                        SizeF textSize = g.MeasureString(info, font);
                        g.FillRectangle(bgBrush, (width - textSize.Width) / 2 - 10, height - textSize.Height - 15,
                                      textSize.Width + 20, textSize.Height + 10);
                        g.DrawString(info, font, brush, (width - textSize.Width) / 2, height - textSize.Height - 10);
                    }
                }
            }

            return combined;
        }

        /// <summary>
        /// Export animation frames to the specified folder
        /// </summary>
        private void ExportAnimationFrames(string folderPath, ProgressForm progress)
        {
            int frameCount = _frames.Count;
            int currentIndex = _currentFrameIndex; // Remember current position

            try
            {
                // Export each frame
                for (int i = 0; i < frameCount; i++)
                {
                    lock (_dataLock)
                    {
                        // Update to this frame
                        _currentFrameIndex = i;
                        UpdateVisualization();

                        // Create combined image
                        Bitmap combined = CombinePanelsForExport();

                        // Save frame
                        string filename = Path.Combine(folderPath, $"frame_{i:D5}.png");
                        combined.Save(filename, ImageFormat.Png);
                        combined.Dispose();
                    }

                    // Update progress
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        progress.UpdateProgress(i + 1);
                    });
                }

                // Create a README.txt file with instructions
                string readmePath = Path.Combine(folderPath, "README.txt");
                using (StreamWriter writer = new StreamWriter(readmePath))
                {
                    writer.WriteLine("Animation Frames Exported from Acoustic Simulation Visualizer");
                    writer.WriteLine("-------------------------------------------------------");
                    writer.WriteLine($"Total Frames: {frameCount}");
                    writer.WriteLine($"Date: {DateTime.Now}");
                    writer.WriteLine();
                    writer.WriteLine("To create a video from these frames, you can use:");
                    writer.WriteLine("1. FFmpeg command: ffmpeg -framerate 10 -i frame_%05d.png -c:v libx264 -pix_fmt yuv420p simulation.mp4");
                    writer.WriteLine("2. Or import the sequence into a video editing program");
                }
            }
            finally
            {
                // Restore original frame
                lock (_dataLock)
                {
                    _currentFrameIndex = currentIndex;
                    UpdateVisualization();
                }
            }
        }
        #endregion
    }

}