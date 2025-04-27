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
        private double _vpVsRatio;
        private Color[] _colormap;

        // Transmitter and receiver positions
        private readonly int _tx, _ty, _tz;
        private readonly int _rx, _ry, _rz;
        private readonly int _width, _height, _depth;
        private readonly float _pixelSize;
        private readonly float _pixelSizeMm; // For display

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
        private readonly float[] _zoomFactors = new float[6] { 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f };
        private readonly PointF[] _panOffsets = new PointF[6] {
            new PointF(0, 0), new PointF(0, 0), new PointF(0, 0),
            new PointF(0, 0), new PointF(0, 0), new PointF(0, 0)
        };
        private int _selectedPanelIndex = -1;

        // Thread-safe bitmap access
        private Bitmap[] _panelBitmaps = new Bitmap[6];
        private Bitmap[] _displayBitmaps = new Bitmap[6];

        // Signal amplification factors - Increased dramatically to address flat time series
        private const float SIGNAL_AMPLIFICATION = 50000.0f; // Amplification for time series
        private const float WAVE_VISUALIZATION_AMPLIFICATION = 200.0f; // For 3D visualization

        // Additional simulation data for stats
        private int _pWaveTravelTime = 0;
        private int _sWaveTravelTime = 0;
        private int _totalTimeSteps = 0;
        private string _simulationStatus = "Initializing";
        private List<Point3D> _pathPoints;
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
            public float PWaveValue { get; set; }
            public float SWaveValue { get; set; }
            
            public float PWavePathProgress { get; set; } = 0.0f;
            public float SWavePathProgress { get; set; } = 0.0f;
            public float[] PWaveSpatialSeries { get; set; }
            public float[] SWaveSpatialSeries { get; set; }

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
            _pixelSizeMm = pixelSize * 1000f; // Convert m to mm for display

            // Store transducer positions directly from parameters
            _tx = tx;
            _ty = ty;
            _tz = tz;
            _rx = rx;
            _ry = ry;
            _rz = rz;

            Logger.Log($"[SimulationVisualizer] TX position: ({_tx}, {_ty}, {_tz})");
            Logger.Log($"[SimulationVisualizer] RX position: ({_rx}, {_ry}, {_rz})");
            Logger.Log($"[SimulationVisualizer] Volume dimensions: {_width}x{_height}x{_depth}");
            Logger.Log($"[SimulationVisualizer] Pixel size: {_pixelSize}m ({_pixelSizeMm}mm)");
            int numSamples = Math.Max(Math.Max(_width, _height), _depth);
            _pathPoints = GetPointsAlongPath(_tx, _ty, _tz, _rx, _ry, _rz, numSamples);
            // Setup form
            this.Text = "Acoustic Simulation Visualizer";
            this.Size = new Size(1200, 850);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(20, 20, 20);
            this.ForeColor = Color.LightGray;
            this.FormClosing += SimulationVisualizer_FormClosing;
            this.KeyPreview = true;
            this.KeyDown += SimulationVisualizer_KeyDown;

            // Initialize the color map (jet-like)
            InitializeColormap();

            // Initialize UI components
            InitializeComponents();

            // Create custom button icons
            CreateIcons();

            // Show the form
            this.Show();

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
            Logger.Log("[SimulationVisualizer] Connected to CPU simulator");
        }

        /// <summary>
        /// Connects to a GPU simulator
        /// </summary>
        public void ConnectToGpuSimulator(AcousticSimulatorGPUWrapper simulator)
        {
            // Subscribe to events
            simulator.ProgressUpdated += Simulator_ProgressUpdated;
            simulator.SimulationCompleted += Simulator_SimulationCompleted;
            Logger.Log("[SimulationVisualizer] Connected to GPU simulator");
        }
        /// <summary>
        /// Converts a double array to a float array
        /// </summary>
        private float[,,] ConvertToFloatArray(double[,,] array)
        {
            int width = array.GetLength(0);
            int height = array.GetLength(1);
            int depth = array.GetLength(2);

            float[,,] result = new float[width, height, depth];

            for (int z = 0; z < depth; z++)
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        result[x, y, z] = (float)array[x, y, z];

            return result;
        }
        private void Simulator_ProgressUpdated(object sender, AcousticSimulationProgressEventArgs e)
        {
            // Only update every 10 steps
            const int UPDATE_INTERVAL = 10;
            if (e.TimeStep % UPDATE_INTERVAL != 0)
                return;

            try
            {
                // 1) grab the raw 3D fields
                var snapshot = sender is AcousticSimulator cpuSim
                             ? cpuSim.GetWaveFieldSnapshot()
                             : ((AcousticSimulatorGPUWrapper)sender).GetWaveFieldSnapshot();
                double[,,] vx = snapshot.vx;
                double[,,] vy = snapshot.vy;
                double[,,] vz = snapshot.vz;

                // 2) sample the receiver time series (P and S)
                const float TIME_SERIES_GAIN = 50000f;
                float recvP = (float)(vx[_rx, _ry, _rz] * TIME_SERIES_GAIN);
                float recvS = (float)(vy[_rx, _ry, _rz] * TIME_SERIES_GAIN);

                // Log TX and RX values as requested
                Logger.Log($"[SimulationVisualizer] P-wave at TX: {vx[_tx, _ty, _tz]:E6}, RX: {vx[_rx, _ry, _rz]:E6}");
                Logger.Log($"[SimulationVisualizer] S-wave at TX: {vy[_tx, _ty, _tz]:E6}, RX: {vy[_rx, _ry, _rz]:E6}");

                // 3) compute tomography + cross-section for *every* update
                var tomo = ComputeVelocityTomography(_tx, _ty, _tz, _rx, _ry, _rz, vx, vy, vz);
                var xsec = ExtractCrossSection(_tx, _ty, _tz, _rx, _ry, _rz, vx, vy, vz);

                // Log tomography data as requested
                Logger.Log($"[SimulationVisualizer] Tomography size: {tomo.GetLength(0)}x{tomo.GetLength(1)}");

                // 4) build the new frame
                var frame = new SimulationFrame
                {
                    TimeStep = e.TimeStep,
                    PWaveValue = recvP,
                    SWaveValue = recvS,
                    PWavePathProgress = (float)CalculateProgressAlongPath(_tx, _ty, _tz, _rx, _ry, _rz, _rx, _ry, _rz),
                    SWavePathProgress = (float)CalculateProgressAlongPath(_tx, _ty, _tz, _rx, _ry, _rz, _rx, _ry, _rz),
                    VelocityTomography = tomo,
                    WavefieldCrossSection = xsec
                };

                // 5) append to your _frames list and extend the time-series arrays
                lock (_dataLock)
                {
                    int n = _frames.Count;
                    frame.PWaveTimeSeries = new float[n + 1];
                    frame.SWaveTimeSeries = new float[n + 1];

                    for (int i = 0; i < n; i++)
                    {
                        frame.PWaveTimeSeries[i] = _frames[i].PWaveValue;
                        frame.SWaveTimeSeries[i] = _frames[i].SWaveValue;
                    }

                    frame.PWaveTimeSeries[n] = recvP;
                    frame.SWaveTimeSeries[n] = recvS;

                    // Log waveform values for time series as requested
                    Logger.Log($"[SimulationVisualizer] Waveform values - P: {recvP:E6}, S: {recvS:E6}");

                    // Calculate and log vp/vs ratio as requested
                    if (Math.Abs(recvS) > 1e-10)
                    {
                        double vpVsRatio = Math.Abs(recvP) / Math.Abs(recvS);
                        Logger.Log($"[SimulationVisualizer] vp/vs ratio: {vpVsRatio:F3}");
                    }
                    else
                    {
                        Logger.Log("[SimulationVisualizer] vp/vs ratio: N/A (S-wave too small)");
                    }

                    _frames.Add(frame);
                    _currentFrameIndex = n;
                    _currentStep = e.TimeStep;
                    _simulationStatus = e.StatusText ?? "Simulating";
                }

                // 6) invoke the UI redraw on the main thread
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke((Action)UpdateVisualization);
            }
            catch (Exception ex)
            {
                Logger.Log($"[SimulationVisualizer] ProgressUpdated error: {ex.Message}");
            }
        }

        /// <summary>
        /// Auto-scaled version of tomography computation
        /// </summary>
        private float[,] ComputeVelocityTomographyAutoScaled(int tx, int ty, int tz, int rx, int ry, int rz,
                                         double[,,] vx, double[,,] vy, double[,,] vz)
        {
            // Same method as before, but with adaptive scaling
            int dx = Math.Abs(rx - tx);
            int dy = Math.Abs(ry - ty);
            int dz = Math.Abs(rz - tz);

            float[,] tomography;

            // First find the maximum value to determine scaling
            double maxVal = 0;
            for (int z = 0; z < _depth; z++)
                for (int y = 0; y < _height; y++)
                    for (int x = 0; x < _width; x++)
                    {
                        double vMag = Math.Sqrt(vx[x, y, z] * vx[x, y, z] +
                                              vy[x, y, z] * vy[x, y, z] +
                                              vz[x, y, z] * vz[x, y, z]);
                        maxVal = Math.Max(maxVal, vMag);
                    }

            // Calculate adaptive scaling factor
            double scaleFactor = (maxVal > 1e-15) ? 1.0 / maxVal : 1000.0;

            // Now create the tomography with appropriate scaling
            if (dx >= dy && dx >= dz)
            {
                // Wave travels along X axis - show YZ plane
                int planeWidth = _height;
                int planeHeight = _depth;
                tomography = new float[planeWidth, planeHeight];
                int xPos = (tx + rx) / 2;
                if (xPos < 0 || xPos >= vx.GetLength(0)) xPos = Math.Min(vx.GetLength(0) - 1, Math.Max(0, xPos));

                for (int y = 0; y < planeWidth; y++)
                {
                    for (int z = 0; z < planeHeight; z++)
                    {
                        if (y < _height && z < _depth && xPos < _width)
                        {
                            double vMag = Math.Sqrt(vx[xPos, y, z] * vx[xPos, y, z] +
                                                  vy[xPos, y, z] * vy[xPos, y, z] +
                                                  vz[xPos, y, z] * vz[xPos, y, z]);
                            tomography[y, z] = (float)(vMag * scaleFactor);
                        }
                    }
                }
            }
            else if (dy >= dx && dy >= dz)
            {
                // Wave travels along Y axis - show XZ plane
                int planeWidth = _width;
                int planeHeight = _depth;
                tomography = new float[planeWidth, planeHeight];
                int yPos = (ty + ry) / 2;
                if (yPos < 0 || yPos >= vy.GetLength(1)) yPos = Math.Min(vy.GetLength(1) - 1, Math.Max(0, yPos));

                for (int x = 0; x < planeWidth; x++)
                {
                    for (int z = 0; z < planeHeight; z++)
                    {
                        if (x < _width && z < _depth && yPos < _height)
                        {
                            double vMag = Math.Sqrt(vx[x, yPos, z] * vx[x, yPos, z] +
                                                  vy[x, yPos, z] * vy[x, yPos, z] +
                                                  vz[x, yPos, z] * vz[x, yPos, z]);
                            tomography[x, z] = (float)(vMag * scaleFactor);
                        }
                    }
                }
            }
            else
            {
                // Wave travels along Z axis - show XY plane
                int planeWidth = _width;
                int planeHeight = _height;
                tomography = new float[planeWidth, planeHeight];
                int zPos = (tz + rz) / 2;
                if (zPos < 0 || zPos >= vz.GetLength(2)) zPos = Math.Min(vz.GetLength(2) - 1, Math.Max(0, zPos));

                for (int x = 0; x < planeWidth; x++)
                {
                    for (int y = 0; y < planeHeight; y++)
                    {
                        if (x < _width && y < _height && zPos < _depth)
                        {
                            double vMag = Math.Sqrt(vx[x, y, zPos] * vx[x, y, zPos] +
                                                  vy[x, y, zPos] * vy[x, y, zPos] +
                                                  vz[x, y, zPos] * vz[x, y, zPos]);
                            tomography[x, y] = (float)(vMag * scaleFactor);
                        }
                    }
                }
            }

            return tomography;
        }

        /// <summary>
        /// Auto-scaled version of cross-section extraction
        /// </summary>
        private float[,] ExtractCrossSectionAutoScaled(double[,,] vx, double[,,] vy, double[,,] vz)
        {
            int dx = Math.Abs(_rx - _tx);
            int dy = Math.Abs(_ry - _ty);
            int dz = Math.Abs(_rz - _tz);

            float[,] crossSection;

            // Find max velocity for scaling
            double maxVelocity = 0;
            for (int z = 0; z < vx.GetLength(2); z++)
                for (int y = 0; y < vx.GetLength(1); y++)
                    for (int x = 0; x < vx.GetLength(0); x++)
                    {
                        double mag = Math.Sqrt(
                            vx[x, y, z] * vx[x, y, z] +
                            vy[x, y, z] * vy[x, y, z] +
                            vz[x, y, z] * vz[x, y, z]);
                        maxVelocity = Math.Max(maxVelocity, mag);
                    }

            // Calculate adaptive scaling
            double scaleFactor = (maxVelocity > 1e-15) ? 1.0 / maxVelocity : 1000.0;

            Logger.Log($"[ExtractCrossSection] Max velocity: {maxVelocity:E6}, Scale factor: {scaleFactor:F3}");

            if (dz <= dx && dz <= dy)
            {
                // Use XY plane at middle Z
                int z = (_tz + _rz) / 2;
                crossSection = new float[_width, _height];

                for (int x = 0; x < _width; x++)
                {
                    for (int y = 0; y < _height; y++)
                    {
                        if (z >= 0 && z < vx.GetLength(2))
                        {
                            double magnitude = Math.Sqrt(
                                vx[x, y, z] * vx[x, y, z] +
                                vy[x, y, z] * vy[x, y, z] +
                                vz[x, y, z] * vz[x, y, z]);

                            crossSection[x, y] = (float)(magnitude * scaleFactor);
                        }
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
                        if (y >= 0 && y < vx.GetLength(1))
                        {
                            double magnitude = Math.Sqrt(
                                vx[x, y, z] * vx[x, y, z] +
                                vy[x, y, z] * vy[x, y, z] +
                                vz[x, y, z] * vz[x, y, z]);

                            crossSection[x, z] = (float)(magnitude * scaleFactor);
                        }
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
                        if (x >= 0 && x < vx.GetLength(0))
                        {
                            double magnitude = Math.Sqrt(
                                vx[x, y, z] * vx[x, y, z] +
                                vy[x, y, z] * vy[x, y, z] +
                                vz[x, y, z] * vz[x, y, z]);

                            crossSection[y, z] = (float)(magnitude * scaleFactor);
                        }
                    }
                }
            }

            return crossSection;
        }
        /// <summary>
        /// Calculate the distance from a point to a line defined by two points (TX-RX)
        /// </summary>
        private double DistanceToLine(int x1, int y1, int z1, int x2, int y2, int z2, int px, int py, int pz)
        {
            // Vector from line point 1 to line point 2
            double dx = x2 - x1;
            double dy = y2 - y1;
            double dz = z2 - z1;

            // Vector from line point 1 to the point
            double vx = px - x1;
            double vy = py - y1;
            double vz = pz - z1;

            // Line length squared
            double lineLength2 = dx * dx + dy * dy + dz * dz;

            if (lineLength2 < 1e-10) // Very short line, just use point-to-point distance
                return Math.Sqrt(vx * vx + vy * vy + vz * vz);

            // Calculate projection ratio
            double t = (vx * dx + vy * dy + vz * dz) / lineLength2;

            // Clamp t to be between 0 and 1 (i.e., point on line segment)
            t = Math.Max(0, Math.Min(1, t));

            // Closest point on the line
            double nearestX = x1 + t * dx;
            double nearestY = y1 + t * dy;
            double nearestZ = z1 + t * dz;

            // Distance from point to nearest point on line
            return Math.Sqrt(
                (px - nearestX) * (px - nearestX) +
                (py - nearestY) * (py - nearestY) +
                (pz - nearestZ) * (pz - nearestZ)
            );
        }
        private List<Point3D> GetPointsAlongPath(int x1, int y1, int z1, int x2, int y2, int z2, int numPoints)
        {
            List<Point3D> points = new List<Point3D>();

            for (int i = 0; i < numPoints; i++)
            {
                float t = i / (float)(numPoints - 1);
                int x = (int)Math.Round(x1 + (x2 - x1) * t);
                int y = (int)Math.Round(y1 + (y2 - y1) * t);
                int z = (int)Math.Round(z1 + (z2 - z1) * t);
                points.Add(new Point3D(x, y, z));
            }

            return points;
        }

        private double CalculateProgressAlongPath(int x1, int y1, int z1, int x2, int y2, int z2, int px, int py, int pz)
        {
            // Calculate distances
            double fullLength = Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2) + Math.Pow(z2 - z1, 2));
            if (fullLength < 1e-6) return 0.0; // Avoid division by zero

            // Vector from start to point
            double dx = px - x1;
            double dy = py - y1;
            double dz = pz - z1;

            // Project onto the path vector
            double pathDx = x2 - x1;
            double pathDy = y2 - y1;
            double pathDz = z2 - z1;

            // Dot product divided by path length gives projection distance
            double dotProduct = dx * pathDx + dy * pathDy + dz * pathDz;
            double projectionDistance = dotProduct / fullLength;

            // Progress is projection distance divided by full path length
            double progress = projectionDistance / fullLength;

            // Clamp to 0-1 range
            return Math.Max(0, Math.Min(1, progress));
        }


        private class Point3D
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }

            public Point3D(int x, int y, int z)
            {
                X = x;
                Y = y;
                Z = z;
            }
        }
        private double[,,] ConvertToDoubleArray(float[,,] array)
        {
            int width = array.GetLength(0);
            int height = array.GetLength(1);
            int depth = array.GetLength(2);

            double[,,] result = new double[width, height, depth];

            for (int z = 0; z < depth; z++)
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        result[x, y, z] = array[x, y, z];

            return result;
        }

        /// <summary>
        /// Handles simulation completion from either simulator type
        /// </summary>
        private void Simulator_SimulationCompleted(object sender, AcousticSimulationCompleteEventArgs e)
        {
            _simulationCompleted = true;
            _pWaveVelocity = e.PWaveVelocity;
            _sWaveVelocity = e.SWaveVelocity;
            _vpVsRatio = e.VpVsRatio;
            _pWaveTravelTime = e.PWaveTravelTime;
            _sWaveTravelTime = e.SWaveTravelTime;
            _totalTimeSteps = e.TotalTimeSteps;
            _simulationStatus = "Completed";

            // Update UI on the UI thread
            this.BeginInvoke((MethodInvoker)delegate
            {
                _timelineTrackBar.Maximum = _frames.Count - 1;
                this.Text = $"Acoustic Simulation Results - P-Wave: {_pWaveVelocity:F2} m/s, S-Wave: {_sWaveVelocity:F2} m/s";
                UpdateVisualization(); // Refresh to show completion status
            });

            Logger.Log($"[SimulationVisualizer] Simulation completed: P-Wave={_pWaveVelocity:F2}m/s, S-Wave={_sWaveVelocity:F2}m/s");
        }
        #endregion

        #region Data Processing
        /// <summary>
        /// Initialize a colormap (jet-like)
        /// </summary>
        private void InitializeColormap()
        {
            _colormap = new Color[256];

            // Use a true jet colormap (blue→cyan→green→yellow→red)
            for (int i = 0; i < 256; i++)
            {
                double position = i / 255.0;

                // Calculate RGB values based on position in the colormap
                int r, g, b;

                if (position < 0.125)
                {
                    // Dark blue to blue
                    r = 0;
                    g = 0;
                    b = (int)(255 * (0.5 + position / 0.125 * 0.5));
                }
                else if (position < 0.375)
                {
                    // Blue to cyan
                    r = 0;
                    g = (int)(255 * ((position - 0.125) / 0.25));
                    b = 255;
                }
                else if (position < 0.625)
                {
                    // Cyan to yellow
                    r = (int)(255 * ((position - 0.375) / 0.25));
                    g = 255;
                    b = (int)(255 * (1.0 - ((position - 0.375) / 0.25)));
                }
                else if (position < 0.875)
                {
                    // Yellow to red
                    r = 255;
                    g = (int)(255 * (1.0 - ((position - 0.625) / 0.25)));
                    b = 0;
                }
                else
                {
                    // Red to dark red
                    r = (int)(255 * (1.0 - 0.5 * ((position - 0.875) / 0.125)));
                    g = 0;
                    b = 0;
                }

                // Ensure RGB values are in valid range
                r = Math.Max(0, Math.Min(255, r));
                g = Math.Max(0, Math.Min(255, g));
                b = Math.Max(0, Math.Min(255, b));

                _colormap[i] = Color.FromArgb(r, g, b);
            }

            // Verify colormap at key positions
            Logger.Log($"[ColorMap] First color: R={_colormap[0].R}, G={_colormap[0].G}, B={_colormap[0].B}");
            Logger.Log($"[ColorMap] Mid color: R={_colormap[128].R}, G={_colormap[128].G}, B={_colormap[128].B}");
            Logger.Log($"[ColorMap] Last color: R={_colormap[255].R}, G={_colormap[255].G}, B={_colormap[255].B}");
        }

        /// <summary>
        /// Compute velocity tomography along the path from TX to RX from 3D arrays
        /// </summary>
        private float[,] ComputeVelocityTomography(
    int tx, int ty, int tz,
    int rx, int ry, int rz,
    double[,,] vx, double[,,] vy, double[,,] vz)
        {
            // Determine the primary direction of wave propagation
            int dx = Math.Abs(rx - tx);
            int dy = Math.Abs(ry - ty);
            int dz = Math.Abs(rz - tz);

            float[,] tomography;

            // Create a maximum intensity projection along the wave path
            if (dx >= dy && dx >= dz) // X is primary axis - project to YZ plane
            {
                tomography = new float[_height, _depth];

                // Initialize with zeros
                for (int y = 0; y < _height; y++)
                    for (int z = 0; z < _depth; z++)
                        tomography[y, z] = 0f;

                // Take maximum intensity projection along X axis (from TX to RX)
                int startX = Math.Min(tx, rx);
                int endX = Math.Max(tx, rx);

                Logger.Log($"[ComputeVelocityTomography] Creating YZ tomography projection from X={startX} to X={endX}");

                for (int x = startX; x <= endX; x++)
                {
                    if (x < 0 || x >= _width) continue;

                    for (int y = 0; y < _height; y++)
                    {
                        for (int z = 0; z < _depth; z++)
                        {
                            // Calculate wave magnitude at this point
                            double magnitude = Math.Sqrt(
                                vx[x, y, z] * vx[x, y, z] +
                                vy[x, y, z] * vy[x, y, z] +
                                vz[x, y, z] * vz[x, y, z]);

                            // Keep maximum value at each YZ position
                            tomography[y, z] = Math.Max(tomography[y, z], (float)magnitude);
                        }
                    }
                }
            }
            else if (dy >= dx && dy >= dz) // Y is primary axis - project to XZ plane
            {
                tomography = new float[_width, _depth];

                // Initialize with zeros
                for (int x = 0; x < _width; x++)
                    for (int z = 0; z < _depth; z++)
                        tomography[x, z] = 0f;

                // Take maximum intensity projection along Y axis (from TX to RY)
                int startY = Math.Min(ty, ry);
                int endY = Math.Max(ty, ry);

                Logger.Log($"[ComputeVelocityTomography] Creating XZ tomography projection from Y={startY} to Y={endY}");

                for (int y = startY; y <= endY; y++)
                {
                    if (y < 0 || y >= _height) continue;

                    for (int x = 0; x < _width; x++)
                    {
                        for (int z = 0; z < _depth; z++)
                        {
                            // Calculate wave magnitude at this point
                            double magnitude = Math.Sqrt(
                                vx[x, y, z] * vx[x, y, z] +
                                vy[x, y, z] * vy[x, y, z] +
                                vz[x, y, z] * vz[x, y, z]);

                            // Keep maximum value at each XZ position
                            tomography[x, z] = Math.Max(tomography[x, z], (float)magnitude);
                        }
                    }
                }
            }
            else // Z is primary axis - project to XY plane
            {
                tomography = new float[_width, _height];

                // Initialize with zeros
                for (int x = 0; x < _width; x++)
                    for (int y = 0; y < _height; y++)
                        tomography[x, y] = 0f;

                // Take maximum intensity projection along Z axis (from TX to RZ)
                int startZ = Math.Min(tz, rz);
                int endZ = Math.Max(tz, rz);

                Logger.Log($"[ComputeVelocityTomography] Creating XY tomography projection from Z={startZ} to Z={endZ}");

                for (int z = startZ; z <= endZ; z++)
                {
                    if (z < 0 || z >= _depth) continue;

                    for (int x = 0; x < _width; x++)
                    {
                        for (int y = 0; y < _height; y++)
                        {
                            // Calculate wave magnitude at this point
                            double magnitude = Math.Sqrt(
                                vx[x, y, z] * vx[x, y, z] +
                                vy[x, y, z] * vy[x, y, z] +
                                vz[x, y, z] * vz[x, y, z]);

                            // Keep maximum value at each XY position
                            tomography[x, y] = Math.Max(tomography[x, y], (float)magnitude);
                        }
                    }
                }
            }

            // Find min/max values for normalization
            float minVal = float.MaxValue;
            float maxVal = float.MinValue;

            int w = tomography.GetLength(0);
            int h = tomography.GetLength(1);

            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    if (tomography[i, j] < minVal) minVal = tomography[i, j];
                    if (tomography[i, j] > maxVal) maxVal = tomography[i, j];
                }
            }

            // Guard against tiny range
            if (Math.Abs(maxVal - minVal) < 1e-6f)
            {
                minVal = 0f;
                maxVal = 1e-6f;
            }

            // Normalize to 0-1 range
            float range = maxVal - minVal;
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    tomography[i, j] = (tomography[i, j] - minVal) / range;
                }
            }

            Logger.Log($"[ComputeVelocityTomography] Tomography size: {w}x{h}, Min: {minVal:E6}, Max: {maxVal:E6}");
            return tomography;
        }

        /// <summary>
        /// Extract a cross-section of the wave field from 3D arrays with improved amplification
        /// </summary>
        private float[,] ExtractCrossSection(
    int tx, int ty, int tz,
    int rx, int ry, int rz,
    double[,,] vx, double[,,] vy, double[,,] vz)
        {
            // Determine the primary direction of wave propagation
            int dx = Math.Abs(rx - tx);
            int dy = Math.Abs(ry - ty);
            int dz = Math.Abs(rz - tz);

            float[,] crossSection;

            // Extract cross-section perpendicular to the primary direction
            if (dx >= dy && dx >= dz) // X is primary axis - extract YZ plane
            {
                crossSection = new float[_height, _depth];

                // Use the midpoint between TX and RX for the cross-section
                int x = (tx + rx) / 2;
                x = Math.Max(0, Math.Min(x, _width - 1)); // Ensure within bounds

                Logger.Log($"[ExtractCrossSection] Extracting YZ plane at X={x} (perpendicular to wave)");

                // Sample the YZ plane at the specified X
                for (int y = 0; y < _height; y++)
                {
                    for (int z = 0; z < _depth; z++)
                    {
                        // Calculate wave magnitude at this point
                        double magnitude = Math.Sqrt(
                            vx[x, y, z] * vx[x, y, z] +
                            vy[x, y, z] * vy[x, y, z] +
                            vz[x, y, z] * vz[x, y, z]);

                        crossSection[y, z] = (float)magnitude;
                    }
                }
            }
            else if (dy >= dx && dy >= dz) // Y is primary axis - extract XZ plane
            {
                crossSection = new float[_width, _depth];

                // Use the midpoint between TY and RY for the cross-section
                int y = (ty + ry) / 2;
                y = Math.Max(0, Math.Min(y, _height - 1)); // Ensure within bounds

                Logger.Log($"[ExtractCrossSection] Extracting XZ plane at Y={y} (perpendicular to wave)");

                // Sample the XZ plane at the specified Y
                for (int x = 0; x < _width; x++)
                {
                    for (int z = 0; z < _depth; z++)
                    {
                        // Calculate wave magnitude at this point
                        double magnitude = Math.Sqrt(
                            vx[x, y, z] * vx[x, y, z] +
                            vy[x, y, z] * vy[x, y, z] +
                            vz[x, y, z] * vz[x, y, z]);

                        crossSection[x, z] = (float)magnitude;
                    }
                }
            }
            else // Z is primary axis - extract XY plane
            {
                crossSection = new float[_width, _height];

                // Use the midpoint between TZ and RZ for the cross-section
                int z = (tz + rz) / 2;
                z = Math.Max(0, Math.Min(z, _depth - 1)); // Ensure within bounds

                Logger.Log($"[ExtractCrossSection] Extracting XY plane at Z={z} (perpendicular to wave)");

                // Sample the XY plane at the specified Z
                for (int x = 0; x < _width; x++)
                {
                    for (int y = 0; y < _height; y++)
                    {
                        // Calculate wave magnitude at this point
                        double magnitude = Math.Sqrt(
                            vx[x, y, z] * vx[x, y, z] +
                            vy[x, y, z] * vy[x, y, z] +
                            vz[x, y, z] * vz[x, y, z]);

                        crossSection[x, y] = (float)magnitude;
                    }
                }
            }

            // Find min/max values for normalization
            float minVal = float.MaxValue;
            float maxVal = float.MinValue;

            int w = crossSection.GetLength(0);
            int h = crossSection.GetLength(1);

            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    if (crossSection[i, j] < minVal) minVal = crossSection[i, j];
                    if (crossSection[i, j] > maxVal) maxVal = crossSection[i, j];
                }
            }

            // Guard against tiny range
            if (Math.Abs(maxVal - minVal) < 1e-6f)
            {
                minVal = 0f;
                maxVal = 1e-6f;
            }

            // Normalize to 0-1 range
            float range = maxVal - minVal;
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    crossSection[i, j] = (crossSection[i, j] - minVal) / range;
                }
            }

            Logger.Log($"[ExtractCrossSection] Cross-section size: {w}x{h}, Min: {minVal:E6}, Max: {maxVal:E6}");
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
            this.TopMost = true;
            this.BringToFront();

            // Create sub-panels for each visualization (now 6 panels)
            _subPanels = new Panel[6];
            _pictureBoxes = new PictureBox[6];

            // Panel layout: 2 rows of 3 panels
            int rowCount = 2;
            int colCount = 3;
            int panelWidth = _mainPanel.Width / colCount;
            int panelHeight = _mainPanel.Height / rowCount;

            for (int i = 0; i < 6; i++)
            {
                int row = i / colCount;
                int col = i % colCount;

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
                string[] titles = {
                    "P-Wave Time Series",
                    "S-Wave Time Series",
                    "Velocity Tomography",
                    "Wavefield Cross-Section",
                    "Combined P/S Wave Visualization",
                    "Simulation Information"
                };

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

                    // Update combined P/S wave visualization panel
                    using (Graphics g = Graphics.FromImage(_panelBitmaps[4]))
                    {
                        DrawCombinedWaveVisualization(g, frame);
                    }

                    // Update information panel
                    using (Graphics g = Graphics.FromImage(_panelBitmaps[5]))
                    {
                        DrawInformationPanel(g);
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
                        for (int i = 0; i < 6; i++)
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
                    Logger.Log($"[SimulationVisualizer] Stack trace: {ex.StackTrace}");
                }
            }
        }
        /// <summary>
        /// Draw time series data on the specified graphics context with robust bounds checking
        /// </summary>
        private void DrawTimeSeries(Graphics g, float[] series, int panelIndex)
        {
            if (series == null || series.Length == 0)
                return;

            int width = _panelBitmaps[panelIndex].Width;
            int height = _panelBitmaps[panelIndex].Height;

            // Clear background
            g.Clear(Color.Black);

            // Draw white rectangle border
            using (Pen borderPen = new Pen(Color.White, 1))
            {
                g.DrawRectangle(borderPen, 0, 0, width - 1, height - 1);
            }

            // Find min/max values for scaling with better error handling
            float minVal = float.MaxValue;
            float maxVal = float.MinValue;
            bool hasNonZeroData = false;

            foreach (float val in series)
            {
                if (!float.IsNaN(val) && !float.IsInfinity(val))
                {
                    minVal = Math.Min(minVal, val);
                    maxVal = Math.Max(maxVal, val);
                    if (Math.Abs(val) > 1e-10) // More sensitive threshold
                        hasNonZeroData = true;
                }
            }

            // If we don't have meaningful data, use default range
            if (!hasNonZeroData || Math.Abs(maxVal - minVal) < 1e-6)
            {
                // Use smaller default range to make small signals more visible
                minVal = -0.1f;
                maxVal = 0.1f;
            }
            else
            {
                // Ensure symmetrical range for waveform display
                float absMax = Math.Max(Math.Abs(minVal), Math.Abs(maxVal));
                minVal = -absMax;
                maxVal = absMax;

                // Add 10% padding
                float padding = (maxVal - minVal) * 0.1f;
                minVal -= padding;
                maxVal += padding;
            }

            // Calculate scaling factors - prevent division by zero
            float xScale = (series.Length > 1) ? (float)width / (series.Length - 1) : width;
            float yScale = (maxVal != minVal) ? height / (maxVal - minVal) : height;
            float centerY = height / 2;

            // Draw path indicator at the top
            int pathHeight = 20;
            Rectangle pathRect = new Rectangle(10, 10, width - 20, pathHeight);

            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(40, 40, 40)))
            {
                g.FillRectangle(bgBrush, pathRect);
            }

            using (Pen pathPen = new Pen(Color.Gray, 1))
            {
                g.DrawRectangle(pathPen, pathRect);
            }

            // Draw TX and RX markers
            using (SolidBrush txBrush = new SolidBrush(Color.Yellow))
            using (SolidBrush rxBrush = new SolidBrush(Color.Cyan))
            {
                g.FillEllipse(txBrush, pathRect.X - 3, pathRect.Y + pathHeight / 2 - 3, 6, 6);
                g.FillEllipse(rxBrush, pathRect.Right - 3, pathRect.Y + pathHeight / 2 - 3, 6, 6);

                // Draw TX/RX labels
                using (Font smallFont = new Font("Arial", 7))
                {
                    g.DrawString("TX", smallFont, txBrush, pathRect.X - 4, pathRect.Y + pathHeight + 2);
                    g.DrawString("RX", smallFont, rxBrush, pathRect.Right - 8, pathRect.Y + pathHeight + 2);
                }
            }

            // Draw current wave position indicator on path
            lock (_dataLock)
            {
                if (_frames.Count > 0 && _currentFrameIndex >= 0 && _currentFrameIndex < _frames.Count)
                {
                    float progress = panelIndex == 0 ?
                        _frames[_currentFrameIndex].PWavePathProgress :
                        _frames[_currentFrameIndex].SWavePathProgress;

                    // Clamp to valid range
                    progress = Math.Max(0.0f, Math.Min(1.0f, progress));

                    int markerX = pathRect.X + (int)(progress * pathRect.Width);

                    using (SolidBrush waveBrush = new SolidBrush(
                        panelIndex == 0 ? Color.DeepSkyBlue : Color.Crimson))
                    {
                        g.FillRectangle(waveBrush, markerX - 2, pathRect.Y, 4, pathHeight);

                        // Draw wave path
                        using (Pen wavePen = new Pen(waveBrush.Color, 2) { DashStyle = DashStyle.Dot })
                        {
                            g.DrawLine(wavePen,
                                pathRect.X, pathRect.Y + pathHeight / 2,
                                markerX, pathRect.Y + pathHeight / 2);
                        }

                        // Draw path progress percentage
                        using (Font progressFont = new Font("Arial", 7))
                        {
                            string progressText = $"{progress:P0}";
                            g.DrawString(progressText, progressFont, waveBrush, markerX - 10, pathRect.Bottom + 2);
                        }
                    }
                }
            }

            // Main time series area
            int graphTop = pathRect.Bottom + 20;
            int graphHeight = height - graphTop - 10;

            // Draw zero line
            g.DrawLine(new Pen(Color.FromArgb(80, 80, 80), 1), 0, graphTop + graphHeight / 2, width, graphTop + graphHeight / 2);

            // Draw grid lines
            using (Pen gridPen = new Pen(Color.FromArgb(40, 40, 40)))
            {
                // Vertical grid lines
                int verticalCount = Math.Min(series.Length, 20);
                if (verticalCount < 2) verticalCount = 2;

                float verticalSpacing = width / (float)verticalCount;

                for (int i = 0; i <= verticalCount; i++)
                {
                    float x = i * verticalSpacing;
                    g.DrawLine(gridPen, x, graphTop, x, graphTop + graphHeight);
                }

                // Horizontal grid lines
                for (int i = 1; i <= 4; i++)
                {
                    float y = graphTop + graphHeight / 2 + i * (graphHeight / 10);
                    g.DrawLine(gridPen, 0, y, width, y);

                    y = graphTop + graphHeight / 2 - i * (graphHeight / 10);
                    g.DrawLine(gridPen, 0, y, width, y);
                }
            }

            // Create points for the line with careful bounds checking
            if (series.Length > 1)
            {
                int numPoints = Math.Min(series.Length, 1000);
                PointF[] points = new PointF[numPoints];

                for (int i = 0; i < numPoints; i++)
                {
                    float x = i * width / (float)(numPoints - 1);

                    // Calculate source index with bounds checking
                    float sourceIdx = i * (series.Length - 1) / (float)(numPoints - 1);
                    int idxLow = (int)Math.Floor(sourceIdx);
                    int idxHigh = (int)Math.Ceiling(sourceIdx);

                    // Ensure indices are within bounds
                    idxLow = Math.Max(0, Math.Min(idxLow, series.Length - 1));
                    idxHigh = Math.Max(0, Math.Min(idxHigh, series.Length - 1));

                    // Get interpolation fraction
                    float fraction = sourceIdx - idxLow;

                    // Interpolate for smoother display
                    float val;
                    if (idxLow != idxHigh)
                        val = series[idxLow] * (1 - fraction) + series[idxHigh] * fraction;
                    else
                        val = series[idxLow];

                    // Protect against NaN or infinity
                    if (float.IsNaN(val) || float.IsInfinity(val))
                        val = 0;

                    // Map to y coordinate - IMPORTANT: Using center as zero point
                    float y = graphTop + graphHeight / 2 - (val - ((maxVal + minVal) / 2)) * graphHeight / (maxVal - minVal);

                    // Clamp to visible area
                    y = Math.Max(graphTop, Math.Min(graphTop + graphHeight, y));

                    points[i] = new PointF(x, y);
                }

                // Draw filled area
                using (GraphicsPath path = new GraphicsPath())
                {
                    // Create path for filled area
                    path.AddLine(points[0].X, graphTop + graphHeight / 2, points[0].X, points[0].Y);
                    for (int i = 1; i < points.Length; i++)
                        path.AddLine(points[i - 1], points[i]);

                    path.AddLine(points[points.Length - 1].X, points[points.Length - 1].Y,
                                 points[points.Length - 1].X, graphTop + graphHeight / 2);
                    path.CloseFigure();

                    // Fill with gradient
                    using (LinearGradientBrush brush = new LinearGradientBrush(
                        new Rectangle(0, graphTop, width, graphHeight),
                        panelIndex == 0 ? Color.FromArgb(100, 0, 150, 255) : Color.FromArgb(100, 255, 50, 50),
                        Color.FromArgb(10, 0, 0, 0),
                        LinearGradientMode.Vertical))
                    {
                        g.FillPath(brush, path);
                    }
                }

                // Draw line with appropriate color
                using (Pen linePen = new Pen(
                    panelIndex == 0 ? Color.DeepSkyBlue : Color.Crimson, 2f))
                {
                    g.DrawLines(linePen, points);
                }
            }
            else if (series.Length == 1)
            {
                // Special case for single-point series
                float centerX = width / 2;
                float val = series[0];
                if (float.IsNaN(val) || float.IsInfinity(val)) val = 0;

                float y = graphTop + graphHeight / 2 - (val - ((maxVal + minVal) / 2)) * graphHeight / (maxVal - minVal);

                // Clamp to visible area
                y = Math.Max(graphTop, Math.Min(graphTop + graphHeight, y));

                // Draw a single point
                using (SolidBrush pointBrush = new SolidBrush(
                    panelIndex == 0 ? Color.DeepSkyBlue : Color.Crimson))
                {
                    g.FillEllipse(pointBrush, centerX - 3, y - 3, 6, 6);
                }
            }

            // Draw title
            using (Font titleFont = new Font("Arial", 12, FontStyle.Bold))
            using (Brush titleBrush = new SolidBrush(
                panelIndex == 0 ? Color.DeepSkyBlue : Color.Crimson))
            {
                string title = panelIndex == 0 ? "P-Wave" : "S-Wave";
                SizeF titleSize = g.MeasureString(title, titleFont);
                g.DrawString(title, titleFont, titleBrush,
                            (width - titleSize.Width) / 2, 50);
            }

            // Draw amplitude labels
            using (Font labelFont = new Font("Arial", 8))
            using (Brush labelBrush = new SolidBrush(Color.LightGray))
            {
                g.DrawString(maxVal.ToString("0.000"), labelFont, labelBrush, 5, graphTop + 5);
                g.DrawString("0.000", labelFont, labelBrush, 5, graphTop + graphHeight / 2 - 15);
                g.DrawString(minVal.ToString("0.000"), labelFont, labelBrush, 5, graphTop + graphHeight - 20);
            }

            // Draw arrival marker if simulation is completed
            if (_simulationCompleted && series.Length > 0)
            {
                int arrivalStep = panelIndex == 0 ? _pWaveTravelTime : _sWaveTravelTime;

                if (arrivalStep > 0)
                {
                    // Calculate x position safely
                    float arrivalPos = arrivalStep / (float)Math.Max(1, series.Length) * width;
                    arrivalPos = Math.Min(arrivalPos, width - 10);

                    using (Pen arrivalPen = new Pen(Color.Yellow, 2))
                    {
                        g.DrawLine(arrivalPen, arrivalPos, graphTop, arrivalPos, graphTop + graphHeight);

                        using (Font font = new Font("Arial", 8))
                        using (SolidBrush brush = new SolidBrush(Color.Yellow))
                        {
                            g.DrawString("Arrival", font, brush, arrivalPos - 15, graphTop + 5);
                        }
                    }
                }
            }
        }
        private void DrawColorbar(Graphics g, Rectangle rect)
        {
            // Create a bitmap for the colorbar
            Bitmap colorbar = new Bitmap(rect.Width, rect.Height);

            // Draw the gradient
            for (int y = 0; y < rect.Height; y++)
            {
                // Map y position to colormap index (invert so max is at top)
                int index = 255 - (int)((float)y / rect.Height * 256);

                // Clamp to valid range
                index = Math.Max(0, Math.Min(255, index));

                // Draw a horizontal line of this color
                for (int x = 0; x < rect.Width; x++)
                {
                    colorbar.SetPixel(x, y, _colormap[index]);
                }
            }

            // Draw the colorbar on the graphics context
            g.DrawImage(colorbar, rect);

            // Draw a border around the colorbar
            g.DrawRectangle(Pens.White, rect);

            // Clean up
            colorbar.Dispose();
        }

        /// <summary>
        /// Draw heatmap data on the specified graphics context
        /// </summary>
        private void DrawHeatmap(Graphics g, float[,] data, int panelIndex)
        {
            // Get panel dimensions
            int panelW = _panelBitmaps[panelIndex].Width;
            int panelH = _panelBitmaps[panelIndex].Height;

            // Clear background and draw border
            g.Clear(Color.Black);
            using (var border = new Pen(Color.White, 1))
            {
                g.DrawRectangle(border, 0, 0, panelW - 1, panelH - 1);
            }

            // Handle null data or empty array
            if (data == null || data.Length == 0)
            {
                using (var font = new Font("Arial", 10, FontStyle.Bold))
                using (var brush = new SolidBrush(Color.White))
                {
                    string msg = "No Data";
                    SizeF sz = g.MeasureString(msg, font);
                    g.DrawString(msg, font, brush,
                                 (panelW - sz.Width) / 2,
                                 (panelH - sz.Height) / 2);
                }
                return;
            }

            // Get data dimensions
            int dataW = data.GetLength(0);
            int dataH = data.GetLength(1);

            // Compute min and max data values
            float minVal = float.MaxValue;
            float maxVal = float.MinValue;
            for (int y = 0; y < dataH; y++)
            {
                for (int x = 0; x < dataW; x++)
                {
                    float v = data[x, y];
                    if (float.IsNaN(v) || float.IsInfinity(v)) continue;
                    if (v < minVal) minVal = v;
                    if (v > maxVal) maxVal = v;
                }
            }

            // Ensure we have a valid range
            if (Math.Abs(maxVal - minVal) < 1e-6f)
            {
                float avg = (minVal + maxVal) * 0.5f;
                minVal = avg - 0.01f;
                maxVal = avg + 0.01f;
            }
            float range = maxVal - minVal;

            // Reserve space for colorbar
            int colorbarWidth = 20;
            int colorbarMargin = 10;
            int imageWidth = panelW - colorbarWidth - colorbarMargin * 2;

            // Draw the heatmap
            for (int py = 0; py < panelH; py++)
            {
                int dataY = (int)(py * (float)dataH / panelH);
                if (dataY >= dataH) dataY = dataH - 1;

                for (int px = 0; px < imageWidth; px++)
                {
                    int dataX = (int)(px * (float)dataW / imageWidth);
                    if (dataX >= dataW) dataX = dataW - 1;

                    float v = data[dataX, dataY];
                    if (float.IsNaN(v) || float.IsInfinity(v)) v = minVal;

                    float norm = (v - minVal) / range;
                    int cIdx = (int)(norm * 255);
                    cIdx = Math.Max(0, Math.Min(255, cIdx));

                    using (var b = new SolidBrush(_colormap[cIdx]))
                    {
                        g.FillRectangle(b, px, py, 1, 1);
                    }
                }
            }

            // Draw colorbar
            Rectangle colorbarRect = new Rectangle(panelW - colorbarWidth - colorbarMargin,
                                                  colorbarMargin,
                                                  colorbarWidth,
                                                  panelH - colorbarMargin * 2);
            DrawColorbar(g, colorbarRect);

            // Add min/max value labels to colorbar
            using (var font = new Font("Arial", 8))
            using (var brush = new SolidBrush(Color.White))
            {
                // Min value at bottom
                string minText = FormatHeatmapValue(minVal);
                g.DrawString(minText, font, brush,
                            colorbarRect.Right + 2,
                            colorbarRect.Bottom - 15);

                // Max value at top
                string maxText = FormatHeatmapValue(maxVal);
                g.DrawString(maxText, font, brush,
                            colorbarRect.Right + 2,
                            colorbarRect.Top);

                // Add title based on panel type
                string title = "";
                if (panelIndex == 2) title = "Velocity Tomography";
                else if (panelIndex == 3) title = "Wavefield Cross-Section";

                if (!string.IsNullOrEmpty(title))
                {
                    g.DrawString(title, new Font("Arial", 9, FontStyle.Bold), brush,
                                10, 10);
                }
            }
        }

        private string FormatHeatmapValue(float value)
        {
            // Format based on magnitude
            if (Math.Abs(value) < 0.0001f)
                return value.ToString("0.00E+0");
            else if (Math.Abs(value) < 1)
                return value.ToString("0.000");
            else if (Math.Abs(value) < 1000)
                return value.ToString("0.0");
            else
                return value.ToString("0.00E+0");
        }

        /// <summary>
        /// Draw the combined time series with parallel P and S waves next to a transmitter drawing
        /// </summary>
        private void DrawCombinedWaveVisualization(Graphics g, SimulationFrame frame)
        {
            if (_pathPoints == null || _pathPoints.Count == 0 || frame == null)
            {
                // Clear background and show waiting message
                g.Clear(Color.Black);
                using (var font = new Font("Arial", 12, FontStyle.Italic))
                using (var brush = new SolidBrush(Color.White))
                {
                    g.DrawString("Waiting for data...", font, brush, 10, 10);
                }
                return;
            }

            // Clear background and draw border
            g.Clear(Color.Black);
            g.DrawRectangle(Pens.White, 0, 0, _panelBitmaps[4].Width - 1, _panelBitmaps[4].Height - 1);

            // Draw TX/RX illustration in the top half
            int halfHeight = _panelBitmaps[4].Height / 2;
            DrawTxRxIllustration(g, 10, 10, _panelBitmaps[4].Width - 20, halfHeight - 20);

            // Draw P/S time series in the bottom half
            if (frame.PWaveTimeSeries != null && frame.SWaveTimeSeries != null)
            {
                DrawParallelWaves(g, 10, halfHeight,
                                 _panelBitmaps[4].Width - 20, halfHeight - 10,
                                 frame.PWaveTimeSeries, frame.SWaveTimeSeries);
            }
        }

        /// <summary>
        /// Draw TX-RX illustration showing wave propagation
        /// </summary>
        private void DrawTxRxIllustration(Graphics g, int x, int y, int width, int height)
        {
            // Drawing area
            int margin = 20;
            int drawWidth = width - 2 * margin;
            int drawHeight = height - 2 * margin;

            // Calculate TX and RX positions
            int txX = x + margin + drawWidth / 5;
            int rxX = x + margin + drawWidth * 4 / 5;
            int centerY = y + height / 2;

            // Draw TX icon (circle)
            using (SolidBrush txBrush = new SolidBrush(Color.Yellow))
            using (Pen txPen = new Pen(Color.White, 2))
            {
                g.FillEllipse(txBrush, txX - 10, centerY - 10, 20, 20);
                g.DrawEllipse(txPen, txX - 10, centerY - 10, 20, 20);

                // TX label
                using (Font font = new Font("Arial", 9, FontStyle.Bold))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    g.DrawString("TX", font, textBrush, txX - 8, centerY - 6);
                }
            }

            // Draw RX icon (square)
            using (SolidBrush rxBrush = new SolidBrush(Color.LightGreen))
            using (Pen rxPen = new Pen(Color.White, 2))
            {
                g.FillRectangle(rxBrush, rxX - 10, centerY - 10, 20, 20);
                g.DrawRectangle(rxPen, rxX - 10, centerY - 10, 20, 20);

                // RX label
                using (Font font = new Font("Arial", 9, FontStyle.Bold))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    g.DrawString("RX", font, textBrush, rxX - 8, centerY - 6);
                }
            }

            // Draw line connecting TX and RX
            using (Pen linePen = new Pen(Color.Gray, 1))
            {
                linePen.DashStyle = DashStyle.Dot;
                g.DrawLine(linePen, txX, centerY, rxX, centerY);
            }

            // Calculate wave progress based on current frame
            float pWaveProgress = 0;
            float sWaveProgress = 0;
            lock (_dataLock)
            {
                if (_frames.Count > 0 && _currentFrameIndex < _frames.Count)
                {
                    // Calculate progress based on frame index and total frames
                    if (_simulationCompleted && _pWaveTravelTime > 0)
                    {
                        // After completion, use real travel times for accurate visualization
                        int step = _frames[_currentFrameIndex].TimeStep;
                        pWaveProgress = Math.Min(1.0f, (float)step / _pWaveTravelTime);
                        sWaveProgress = Math.Min(1.0f, (float)step / _sWaveTravelTime);
                    }
                    else
                    {
                        // During simulation, estimate based on frame index
                        pWaveProgress = Math.Min(1.0f, (float)(_currentFrameIndex + 1) / _frames.Count * 2.5f);
                        sWaveProgress = Math.Min(1.0f, (float)(_currentFrameIndex + 1) / _frames.Count * 1.5f);
                    }
                }
            }

            // Draw P-wave propagation
            if (pWaveProgress > 0)
            {
                int waveDistance = rxX - txX;
                int pWaveX = txX + (int)(waveDistance * pWaveProgress);

                // P wave front
                using (Pen wavePen = new Pen(Color.DeepSkyBlue, 3))
                {
                    wavePen.DashStyle = DashStyle.Dash;
                    g.DrawLine(wavePen, pWaveX, centerY - 30, pWaveX, centerY + 30);
                }

                // P wave label
                using (Font font = new Font("Arial", 8, FontStyle.Bold))
                using (SolidBrush textBrush = new SolidBrush(Color.DeepSkyBlue))
                {
                    g.DrawString("P", font, textBrush, pWaveX - 4, centerY - 40);
                }
            }

            // Draw S-wave propagation
            if (sWaveProgress > 0)
            {
                int waveDistance = rxX - txX;
                int sWaveX = txX + (int)(waveDistance * sWaveProgress);

                // S wave front
                using (Pen wavePen = new Pen(Color.Crimson, 3))
                {
                    wavePen.DashStyle = DashStyle.Dash;
                    g.DrawLine(wavePen, sWaveX, centerY - 20, sWaveX, centerY + 20);
                }

                // S wave label
                using (Font font = new Font("Arial", 8, FontStyle.Bold))
                using (SolidBrush textBrush = new SolidBrush(Color.Crimson))
                {
                    g.DrawString("S", font, textBrush, sWaveX - 4, centerY + 25);
                }
            }

            // Draw distance information
            float distanceM = CalculateDistance(_tx, _ty, _tz, _rx, _ry, _rz) * _pixelSize;
            string distanceString = $"Distance: {distanceM:F3} m";

            using (Font font = new Font("Arial", 8))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                g.DrawString(distanceString, font, textBrush, x + margin, y + height - margin - 15);
            }
        }


        /// <summary>
        /// Draw parallel P and S waves with safer bounds checking
        /// </summary>
        private void DrawParallelWaves(Graphics g, int x, int y, int width, int height, float[] pSeries, float[] sSeries)
        {
            // Check if we have valid data
            if (pSeries == null || sSeries == null || pSeries.Length == 0 || sSeries.Length == 0)
            {
                using (Font font = new Font("Arial", 10))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    g.DrawString("Waiting for data...", font, brush, x + 10, y + height / 2);
                }
                return;
            }

            // Find min/max values for both series with safer bounds checking
            float minP = float.MaxValue, maxP = float.MinValue;
            float minS = float.MaxValue, maxS = float.MinValue;

            foreach (float val in pSeries)
            {
                if (!float.IsNaN(val) && !float.IsInfinity(val))
                {
                    minP = Math.Min(minP, val);
                    maxP = Math.Max(maxP, val);
                }
            }

            foreach (float val in sSeries)
            {
                if (!float.IsNaN(val) && !float.IsInfinity(val))
                {
                    minS = Math.Min(minS, val);
                    maxS = Math.Max(maxS, val);
                }
            }

            // If we don't have meaningful data, use default range
            if (Math.Abs(maxP - minP) < 1e-6)
            {
                minP = -0.1f;
                maxP = 0.1f;
            }
            else
            {
                // Ensure symmetrical range for P waveform display
                float absMaxP = Math.Max(Math.Abs(minP), Math.Abs(maxP));
                minP = -absMaxP;
                maxP = absMaxP;
            }

            if (Math.Abs(maxS - minS) < 1e-6)
            {
                minS = -0.1f;
                maxS = 0.1f;
            }
            else
            {
                // Ensure symmetrical range for S waveform display
                float absMaxS = Math.Max(Math.Abs(minS), Math.Abs(maxS));
                minS = -absMaxS;
                maxS = absMaxS;
            }

            // Add padding
            float paddingP = (maxP - minP) * 0.1f;
            minP -= paddingP;
            maxP += paddingP;

            float paddingS = (maxS - minS) * 0.1f;
            minS -= paddingS;
            maxS += paddingS;

            // Split the display area for P and S waves
            int pAreaHeight = height / 2 - 5;
            int sAreaHeight = height / 2 - 5;
            int pAreaY = y + 5;
            int sAreaY = y + height / 2 + 5;

            // Define drawing areas with white borders
            Rectangle pArea = new Rectangle(x + 5, pAreaY, width - 10, pAreaHeight - 10);
            Rectangle sArea = new Rectangle(x + 5, sAreaY, width - 10, sAreaHeight - 10);

            // Draw white rectangles to delimit graph areas
            using (Pen borderPen = new Pen(Color.White, 1))
            {
                g.DrawRectangle(borderPen, pArea);
                g.DrawRectangle(borderPen, sArea);
            }

            // Draw path visualization above each graph area
            int pathHeight = 15;
            Rectangle pPathRect = new Rectangle(pArea.X, pAreaY - pathHeight - 5, pArea.Width, pathHeight);
            Rectangle sPathRect = new Rectangle(sArea.X, sAreaY - pathHeight - 5, sArea.Width, pathHeight);

            // Draw path backgrounds
            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(40, 40, 40)))
            {
                g.FillRectangle(bgBrush, pPathRect);
                g.FillRectangle(bgBrush, sPathRect);
            }

            // Draw path borders
            using (Pen pathPen = new Pen(Color.Gray, 1))
            {
                g.DrawRectangle(pathPen, pPathRect);
                g.DrawRectangle(pathPen, sPathRect);
            }

            // Draw TX and RX markers on both paths
            using (SolidBrush txBrush = new SolidBrush(Color.Yellow))
            using (SolidBrush rxBrush = new SolidBrush(Color.Cyan))
            {
                // P-wave path
                g.FillEllipse(txBrush, pPathRect.X - 3, pPathRect.Y + pathHeight / 2 - 3, 6, 6);
                g.FillEllipse(rxBrush, pPathRect.Right - 3, pPathRect.Y + pathHeight / 2 - 3, 6, 6);

                // S-wave path
                g.FillEllipse(txBrush, sPathRect.X - 3, sPathRect.Y + pathHeight / 2 - 3, 6, 6);
                g.FillEllipse(rxBrush, sPathRect.Right - 3, sPathRect.Y + pathHeight / 2 - 3, 6, 6);
            }

            // Draw current wave position markers on paths - check if frame properties exist
            lock (_dataLock)
            {
                if (_frames.Count > 0 && _currentFrameIndex >= 0 && _currentFrameIndex < _frames.Count)
                {
                    // Check if progress properties exist on the frame object (backward compatibility)
                    float pProgress = 0.0f;
                    float sProgress = 0.0f;

                    // We need to check if these properties exist before using them
                    // as they might be missing if code changes weren't fully applied
                    try
                    {
                        // Try to get progress from frame or use simple estimate
                        pProgress = _frames[_currentFrameIndex].PWavePathProgress;
                        sProgress = _frames[_currentFrameIndex].SWavePathProgress;
                    }
                    catch
                    {
                        // Fallback to a simple estimate based on frame index
                        float simpleProgress = Math.Min(1.0f, (float)(_currentFrameIndex + 1) / _frames.Count);
                        pProgress = simpleProgress * 1.5f; // P-waves travel faster
                        if (pProgress > 1.0f) pProgress = 1.0f;

                        sProgress = simpleProgress * 0.8f; // S-waves travel slower
                        if (sProgress > 1.0f) sProgress = 1.0f;
                    }

                    int pMarkerX = pPathRect.X + (int)(pProgress * pPathRect.Width);
                    int sMarkerX = sPathRect.X + (int)(sProgress * sPathRect.Width);

                    using (SolidBrush pWaveBrush = new SolidBrush(Color.DeepSkyBlue))
                    using (SolidBrush sWaveBrush = new SolidBrush(Color.Crimson))
                    {
                        // Draw wave front markers
                        g.FillRectangle(pWaveBrush, pMarkerX - 2, pPathRect.Y, 4, pathHeight);
                        g.FillRectangle(sWaveBrush, sMarkerX - 2, sPathRect.Y, 4, pathHeight);

                        // Draw wave paths
                        using (Pen pPathPen = new Pen(Color.DeepSkyBlue, 2) { DashStyle = DashStyle.Dot })
                        using (Pen sPathPen = new Pen(Color.Crimson, 2) { DashStyle = DashStyle.Dot })
                        {
                            // Draw from TX to current position
                            g.DrawLine(pPathPen,
                                pPathRect.X, pPathRect.Y + pathHeight / 2,
                                pMarkerX, pPathRect.Y + pathHeight / 2);

                            g.DrawLine(sPathPen,
                                sPathRect.X, sPathRect.Y + pathHeight / 2,
                                sMarkerX, sPathRect.Y + pathHeight / 2);
                        }

                        // Draw progress percentages
                        using (Font progressFont = new Font("Arial", 7))
                        {
                            string pProgressText = $"{pProgress:P0}";
                            string sProgressText = $"{sProgress:P0}";

                            g.DrawString(pProgressText, progressFont, pWaveBrush,
                                pMarkerX - 10, pPathRect.Y);

                            g.DrawString(sProgressText, progressFont, sWaveBrush,
                                sMarkerX - 10, sPathRect.Y);
                        }
                    }
                }
            }

            // P-wave area title
            using (Font titleFont = new Font("Arial", 9, FontStyle.Bold))
            using (SolidBrush pBrush = new SolidBrush(Color.DeepSkyBlue))
            {
                g.DrawString("P-Wave", titleFont, pBrush, pArea.X + 5, pAreaY - 15);
            }

            // S-wave area title
            using (Font titleFont = new Font("Arial", 9, FontStyle.Bold))
            using (SolidBrush sBrush = new SolidBrush(Color.Crimson))
            {
                g.DrawString("S-Wave", titleFont, sBrush, sArea.X + 5, sAreaY - 15);
            }

            // Draw center lines
            using (Pen centerLinePen = new Pen(Color.Gray, 1))
            {
                centerLinePen.DashStyle = DashStyle.Dot;
                int pCenterY = pArea.Y + pArea.Height / 2;
                int sCenterY = sArea.Y + sArea.Height / 2;

                g.DrawLine(centerLinePen, pArea.X, pCenterY, pArea.X + pArea.Width, pCenterY);
                g.DrawLine(centerLinePen, sArea.X, sCenterY, sArea.X + sArea.Width, sCenterY);
            }

            // Draw P-wave time series with careful bounds checking
            if (pSeries.Length > 1)
            {
                int pLength = Math.Min(pSeries.Length, 1000); // Limit to prevent excessive points
                PointF[] pPoints = new PointF[pLength];

                for (int i = 0; i < pLength; i++)
                {
                    float t = i * pArea.Width / (float)(pLength - 1);

                    // Calculate source index more safely
                    float sourceIdx = i * (pSeries.Length - 1) / (float)(pLength - 1);
                    int idxLow = (int)Math.Floor(sourceIdx);
                    int idxHigh = (int)Math.Ceiling(sourceIdx);

                    // Ensure we're within array bounds
                    idxLow = Math.Max(0, Math.Min(idxLow, pSeries.Length - 1));
                    idxHigh = Math.Max(0, Math.Min(idxHigh, pSeries.Length - 1));

                    // Get interpolation fraction
                    float fraction = sourceIdx - idxLow;

                    // Interpolate for smoother display (with bounds checking)
                    float value;
                    if (idxLow != idxHigh)
                        value = pSeries[idxLow] * (1 - fraction) + pSeries[idxHigh] * fraction;
                    else
                        value = pSeries[idxLow];

                    // Normalize to fit in graph area
                    float normalizedVal = (value - minP) / (maxP - minP);
                    // Clamp to prevent drawing outside the area
                    normalizedVal = Math.Max(0, Math.Min(1, normalizedVal));

                    float yPos = pArea.Y + pArea.Height - normalizedVal * pArea.Height;

                    pPoints[i] = new PointF(pArea.X + t, yPos);
                }

                using (Pen pWavePen = new Pen(Color.DeepSkyBlue, 1.5f))
                {
                    g.DrawLines(pWavePen, pPoints);
                }
            }

            // Draw S-wave time series with careful bounds checking
            if (sSeries.Length > 1)
            {
                int sLength = Math.Min(sSeries.Length, 1000); // Limit to prevent excessive points
                PointF[] sPoints = new PointF[sLength];

                for (int i = 0; i < sLength; i++)
                {
                    float t = i * sArea.Width / (float)(sLength - 1);

                    // Calculate source index more safely
                    float sourceIdx = i * (sSeries.Length - 1) / (float)(sLength - 1);
                    int idxLow = (int)Math.Floor(sourceIdx);
                    int idxHigh = (int)Math.Ceiling(sourceIdx);

                    // Ensure we're within array bounds
                    idxLow = Math.Max(0, Math.Min(idxLow, sSeries.Length - 1));
                    idxHigh = Math.Max(0, Math.Min(idxHigh, sSeries.Length - 1));

                    // Get interpolation fraction
                    float fraction = sourceIdx - idxLow;

                    // Interpolate for smoother display (with bounds checking)
                    float value;
                    if (idxLow != idxHigh)
                        value = sSeries[idxLow] * (1 - fraction) + sSeries[idxHigh] * fraction;
                    else
                        value = sSeries[idxLow];

                    // Normalize to fit in graph area
                    float normalizedVal = (value - minS) / (maxS - minS);
                    // Clamp to prevent drawing outside the area
                    normalizedVal = Math.Max(0, Math.Min(1, normalizedVal));

                    float yPos = sArea.Y + sArea.Height - normalizedVal * sArea.Height;

                    sPoints[i] = new PointF(sArea.X + t, yPos);
                }

                using (Pen sWavePen = new Pen(Color.Crimson, 1.5f))
                {
                    g.DrawLines(sWavePen, sPoints);
                }
            }

            // Draw Time and markers if we have travel times
            if (_simulationCompleted && _pWaveTravelTime > 0 && _sWaveTravelTime > 0)
            {
                // P-wave arrival marker - with bounds checking
                if (pSeries.Length > 0) // Avoid division by zero
                {
                    float pArrivalX = pArea.X + (float)Math.Min(_pWaveTravelTime, pSeries.Length) / pSeries.Length * pArea.Width;
                    using (Pen arrivalPen = new Pen(Color.Yellow, 2))
                    {
                        g.DrawLine(arrivalPen, pArrivalX, pArea.Y, pArrivalX, pArea.Y + pArea.Height);

                        using (Font font = new Font("Arial", 8))
                        using (SolidBrush brush = new SolidBrush(Color.Yellow))
                        {
                            g.DrawString("Arrival", font, brush, pArrivalX - 15, pArea.Y + 2);
                        }
                    }
                }

                // S-wave arrival marker - with bounds checking
                if (sSeries.Length > 0) // Avoid division by zero
                {
                    float sArrivalX = sArea.X + (float)Math.Min(_sWaveTravelTime, sSeries.Length) / sSeries.Length * sArea.Width;
                    using (Pen arrivalPen = new Pen(Color.Yellow, 2))
                    {
                        g.DrawLine(arrivalPen, sArrivalX, sArea.Y, sArrivalX, sArea.Y + sArea.Height);

                        using (Font font = new Font("Arial", 8))
                        using (SolidBrush brush = new SolidBrush(Color.Yellow))
                        {
                            g.DrawString("Arrival", font, brush, sArrivalX - 15, sArea.Y + 2);
                        }
                    }
                }
            }

            // Draw amplitude scales
            using (Font labelFont = new Font("Arial", 7))
            using (SolidBrush labelBrush = new SolidBrush(Color.LightGray))
            {
                // P-wave scales
                g.DrawString(maxP.ToString("0.000"), labelFont, labelBrush, pArea.X + 2, pArea.Y + 2);
                g.DrawString("0", labelFont, labelBrush, pArea.X + 2, pArea.Y + pArea.Height / 2);
                g.DrawString(minP.ToString("0.000"), labelFont, labelBrush, pArea.X + 2, pArea.Y + pArea.Height - 10);

                // S-wave scales
                g.DrawString(maxS.ToString("0.000"), labelFont, labelBrush, sArea.X + 2, sArea.Y + 2);
                g.DrawString("0", labelFont, labelBrush, sArea.X + 2, sArea.Y + sArea.Height / 2);
                g.DrawString(minS.ToString("0.000"), labelFont, labelBrush, sArea.X + 2, sArea.Y + sArea.Height - 10);
            }
        }

        /// <summary>
        /// Draw the information panel with simulation details
        /// </summary>
        private void DrawInformationPanel(Graphics g)
        {
            int width = _panelBitmaps[5].Width;
            int height = _panelBitmaps[5].Height;

            // Clear background
            g.Clear(Color.Black);

            // Draw white rectangle border
            using (Pen borderPen = new Pen(Color.White, 1))
            {
                g.DrawRectangle(borderPen, 0, 0, width - 1, height - 1);
            }

            // Set up fonts
            Font titleFont = new Font("Arial", 12, FontStyle.Bold);
            Font subtitleFont = new Font("Arial", 10, FontStyle.Bold);
            Font regularFont = new Font("Arial", 9);

            // Set up brushes
            SolidBrush whiteBrush = new SolidBrush(Color.White);
            SolidBrush lightBlueBrush = new SolidBrush(Color.LightBlue);
            SolidBrush lightGreenBrush = new SolidBrush(Color.LightGreen);
            SolidBrush orangeBrush = new SolidBrush(Color.Orange);

            try
            {
                // Draw title
                g.DrawString("Simulation Information", titleFont, lightBlueBrush, 10, 10);

                // Draw status
                string statusText = $"Status: {_simulationStatus}";
                g.DrawString(statusText, subtitleFont, (_simulationCompleted ? lightGreenBrush : orangeBrush), 10, 35);

                // Current step info
                string stepInfo = $"Current Step: {_currentStep}";
                g.DrawString(stepInfo, regularFont, whiteBrush, 10, 60);

                // Distance information
                float distanceM = CalculateDistance(_tx, _ty, _tz, _rx, _ry, _rz) * _pixelSize;
                string distanceString = $"Distance: {distanceM:F3} m ({distanceM * 1000:F1} mm)";
                g.DrawString(distanceString, regularFont, whiteBrush, 10, 80);

                // Grid information
                string gridString = $"Grid Size: {_width}×{_height}×{_depth} pixels";
                string pixelSizeString = $"Pixel Size: {_pixelSize:E3} m ({_pixelSizeMm:F3} mm)";
                g.DrawString(gridString, regularFont, whiteBrush, 10, 100);
                g.DrawString(pixelSizeString, regularFont, whiteBrush, 10, 120);

                // Transducer positions
                string txString = $"Transmitter: ({_tx}, {_ty}, {_tz})";
                string rxString = $"Receiver: ({_rx}, {_ry}, {_rz})";
                g.DrawString(txString, regularFont, whiteBrush, 10, 140);
                g.DrawString(rxString, regularFont, whiteBrush, 10, 160);

                if (_simulationCompleted)
                {
                    // Draw wave velocity results
                    g.DrawString("Results:", subtitleFont, lightGreenBrush, 10, 190);

                    g.DrawString($"P-Wave Velocity: {_pWaveVelocity:F2} m/s", regularFont, whiteBrush, 20, 210);
                    g.DrawString($"S-Wave Velocity: {_sWaveVelocity:F2} m/s", regularFont, whiteBrush, 20, 230);
                    g.DrawString($"Vp/Vs Ratio: {_vpVsRatio:F3}", regularFont, whiteBrush, 20, 250);

                    g.DrawString($"P-Wave Travel Time: {_pWaveTravelTime} steps", regularFont, whiteBrush, 20, 270);
                    g.DrawString($"S-Wave Travel Time: {_sWaveTravelTime} steps", regularFont, whiteBrush, 20, 290);
                    g.DrawString($"Total Time Steps: {_totalTimeSteps}", regularFont, whiteBrush, 20, 310);
                }
                else
                {
                    // Drawing instructions
                    g.DrawString("Visualization Controls:", subtitleFont, lightBlueBrush, 10, 190);
                    g.DrawString("• Drag to pan any panel", regularFont, whiteBrush, 20, 210);
                    g.DrawString("• Mouse wheel to zoom in/out", regularFont, whiteBrush, 20, 230);
                    g.DrawString("• Press ESC to reset view", regularFont, whiteBrush, 20, 250);
                    g.DrawString("• Press left/right arrow to step through frames", regularFont, whiteBrush, 20, 270);
                    g.DrawString("• Press space to play/pause animation", regularFont, whiteBrush, 20, 290);
                }

                // Signal quality indicator
                float signalQuality = CalculateSignalQuality();
                string signalQualityText = $"Signal Quality: {GetSignalQualityDescription(signalQuality)}";
                Color qualityColor = GetSignalQualityColor(signalQuality);

                using (SolidBrush qualityBrush = new SolidBrush(qualityColor))
                {
                    g.DrawString(signalQualityText, regularFont, qualityBrush, 10, height - 40);

                    // Draw a quality bar
                    int barWidth = width - 40;
                    int barHeight = 15;
                    Rectangle barRect = new Rectangle(20, height - 25, barWidth, barHeight);

                    using (LinearGradientBrush gradientBrush = new LinearGradientBrush(
                        barRect, Color.Red, Color.Green, LinearGradientMode.Horizontal))
                    {
                        g.FillRectangle(gradientBrush, barRect);
                    }

                    // Draw a marker at the current quality level
                    int markerX = 20 + (int)(signalQuality * barWidth);
                    Point[] markerPoints = {
                        new Point(markerX, height - 25 - 5),
                        new Point(markerX - 5, height - 25),
                        new Point(markerX + 5, height - 25)
                    };

                    g.FillPolygon(new SolidBrush(Color.White), markerPoints);
                }
            }
            finally
            {
                // Clean up resources
                titleFont.Dispose();
                subtitleFont.Dispose();
                regularFont.Dispose();
                whiteBrush.Dispose();
                lightBlueBrush.Dispose();
                lightGreenBrush.Dispose();
                orangeBrush.Dispose();
            }
        }

        /// <summary>
        /// Calculate the 3D distance between TX and RX
        /// </summary>
        private float CalculateDistance(int x1, int y1, int z1, int x2, int y2, int z2)
        {
            double dx = x2 - x1;
            double dy = y2 - y1;
            double dz = z2 - z1;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>
        /// Calculate signal quality from current frames (0.0-1.0)
        /// </summary>
        private float CalculateSignalQuality()
        {
            if (_frames.Count == 0) return 0.0f;

            // Find maximum signal values in P and S series
            float maxP = 0f;
            float maxS = 0f;

            foreach (var frame in _frames)
            {
                maxP = Math.Max(maxP, Math.Abs(frame.PWaveValue));
                maxS = Math.Max(maxS, Math.Abs(frame.SWaveValue));
            }

            // Normalize to get quality value from 0-1
            float signalStrength = (maxP + maxS) / 2.0f;

            // Extremely high or low values indicate issues
            float normalizedQuality;
            if (signalStrength < 0.1f)
                normalizedQuality = signalStrength * 10f; // Scale up weak signals
            else if (signalStrength > 100f)
                normalizedQuality = 1.0f - (1.0f / signalStrength); // Scale down very strong signals
            else
                normalizedQuality = 0.5f + (float)Math.Min(0.5, Math.Log10(signalStrength) / 4.0);

            return Math.Max(0f, Math.Min(1f, normalizedQuality));
        }

        /// <summary>
        /// Get a text description of signal quality
        /// </summary>
        private string GetSignalQualityDescription(float quality)
        {
            if (quality < 0.2f) return "Poor";
            if (quality < 0.4f) return "Fair";
            if (quality < 0.6f) return "Good";
            if (quality < 0.8f) return "Very Good";
            return "Excellent";
        }

        /// <summary>
        /// Get color representing signal quality
        /// </summary>
        private Color GetSignalQualityColor(float quality)
        {
            if (quality < 0.2f) return Color.Red;
            if (quality < 0.4f) return Color.Orange;
            if (quality < 0.6f) return Color.Yellow;
            if (quality < 0.8f) return Color.GreenYellow;
            return Color.LimeGreen;
        }

        private string FormatColorbarValue(float value)
        {
            // Handle special cases
            if (float.IsNaN(value) || float.IsInfinity(value))
                return "N/A";

            // Check magnitude to determine formatting
            double absVal = Math.Abs(value);

            if (absVal == 0)
                return "0.000";
            else if (absVal < 0.001)
                return value.ToString("0.000e0");
            else if (absVal < 1000)
                return value.ToString("0.000");
            else if (absVal < 1000000)
                return (value / 1000).ToString("0.00") + "k";
            else
                return (value / 1000000).ToString("0.00") + "M";
        }

        private void DrawTransducersOnTomography(Graphics g, int width, int height)
        {
            // Determine which plane we're showing based on wave direction
            int dx = Math.Abs(_rx - _tx);
            int dy = Math.Abs(_ry - _ty);
            int dz = Math.Abs(_rz - _tz);

            PointF txPoint = new PointF();
            PointF rxPoint = new PointF();
            bool validPoints = false;

            if (dx >= dy && dx >= dz)
            {
                // YZ plane (X direction)
                float txScreenY = (float)_ty / _height * height;
                float txScreenZ = (float)_tz / _depth * height;
                float rxScreenY = (float)_ry / _height * height;
                float rxScreenZ = (float)_rz / _depth * height;

                txPoint = new PointF(10, txScreenY); // Place TX at left edge
                rxPoint = new PointF(width - 10, rxScreenY); // Place RX at right edge
                validPoints = true;
            }
            else if (dy >= dx && dy >= dz)
            {
                // XZ plane (Y direction)
                float txScreenX = (float)_tx / _width * width;
                float txScreenZ = (float)_tz / _depth * height;
                float rxScreenX = (float)_rx / _width * width;
                float rxScreenZ = (float)_rz / _depth * height;

                txPoint = new PointF(txScreenX, 10); // Place TX at top edge
                rxPoint = new PointF(rxScreenX, height - 10); // Place RX at bottom edge
                validPoints = true;
            }
            else
            {
                // XY plane (Z direction)
                float txScreenX = (float)_tx / _width * width;
                float txScreenY = (float)_ty / _height * height;
                float rxScreenX = (float)_rx / _width * width;
                float rxScreenY = (float)_ry / _height * height;

                txPoint = new PointF(txScreenX, txScreenY);
                rxPoint = new PointF(rxScreenX, rxScreenY);
                validPoints = true;
            }

            if (validPoints)
            {
                // Draw line connecting TX and RX
                using (Pen linePen = new Pen(Color.White, 1.5f))
                {
                    linePen.DashStyle = DashStyle.Dash;
                    g.DrawLine(linePen, txPoint, rxPoint);
                }

                // Draw TX marker
                const int markerSize = 8;
                g.FillEllipse(Brushes.Yellow,
                             txPoint.X - markerSize / 2, txPoint.Y - markerSize / 2,
                             markerSize, markerSize);
                g.DrawEllipse(Pens.White,
                             txPoint.X - markerSize / 2, txPoint.Y - markerSize / 2,
                             markerSize, markerSize);

                // Draw RX marker
                g.FillEllipse(Brushes.Cyan,
                             rxPoint.X - markerSize / 2, rxPoint.Y - markerSize / 2,
                             markerSize, markerSize);
                g.DrawEllipse(Pens.White,
                             rxPoint.X - markerSize / 2, rxPoint.Y - markerSize / 2,
                             markerSize, markerSize);

                // Draw labels
                using (Font font = new Font("Arial", 8, FontStyle.Bold))
                {
                    g.DrawString("TX", font, Brushes.Yellow,
                                txPoint.X + markerSize / 2 + 2, txPoint.Y - markerSize / 2 - 2);
                    g.DrawString("RX", font, Brushes.Cyan,
                                rxPoint.X + markerSize / 2 + 2, rxPoint.Y - markerSize / 2 - 2);
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
            int rowCount = 2;
            int colCount = 3;
            int panelWidth = _mainPanel.Width / colCount;
            int panelHeight = _mainPanel.Height / rowCount;

            for (int i = 0; i < 6; i++)
            {
                int row = i / colCount;
                int col = i % colCount;

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
                int rowCount = 2;
                int colCount = 3;
                int panelWidth = _mainPanel.Width / colCount;
                int panelHeight = _mainPanel.Height / rowCount;

                for (int i = 0; i < 6; i++)
                {
                    int row = i / colCount;
                    int col = i % colCount;

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
            for (int i = 0; i < 6; i++)
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
                for (int i = 0; i < 6; i++)
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
                int rowCount = 2;
                int colCount = 3;
                int panelWidth = width / colCount;
                int panelHeight = height / rowCount;

                for (int i = 0; i < 6; i++)
                {
                    int row = i / colCount;
                    int col = i % colCount;

                    int x = col * panelWidth;
                    int y = row * panelHeight;

                    // Draw panel content
                    g.DrawImage(_panelBitmaps[i], x, y, panelWidth, panelHeight);

                    // Draw panel title
                    string[] titles = {
                        "P-Wave Time Series", "S-Wave Time Series",
                        "Velocity Tomography", "Wavefield Cross-Section",
                        "Combined P/S Wave Visualization", "Simulation Information"
                    };

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
                        progress.UpdateProgress((i + 1) * 100 / frameCount);
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

        private void LogAmplitudeInformation(double[,,] vx, double[,,] vy, double[,,] vz)
        {
            double maxVx = 0, maxVy = 0, maxVz = 0;
            double sumVx = 0, sumVy = 0, sumVz = 0;
            int nonZeroVx = 0, nonZeroVy = 0, nonZeroVz = 0;
            double threshold = 1e-10; // Very small threshold to count non-zero values

            // Sample the arrays to find statistical information
            int stride = Math.Max(1, Math.Min(Math.Min(vx.GetLength(0), vx.GetLength(1)), vx.GetLength(2)) / 20);

            for (int z = 0; z < vx.GetLength(2); z += stride)
                for (int y = 0; y < vx.GetLength(1); y += stride)
                    for (int x = 0; x < vx.GetLength(0); x += stride)
                    {
                        double absVx = Math.Abs(vx[x, y, z]);
                        double absVy = Math.Abs(vy[x, y, z]);
                        double absVz = Math.Abs(vz[x, y, z]);

                        maxVx = Math.Max(maxVx, absVx);
                        maxVy = Math.Max(maxVy, absVy);
                        maxVz = Math.Max(maxVz, absVz);

                        if (absVx > threshold) { sumVx += absVx; nonZeroVx++; }
                        if (absVy > threshold) { sumVy += absVy; nonZeroVy++; }
                        if (absVz > threshold) { sumVz += absVz; nonZeroVz++; }
                    }

            double avgVx = nonZeroVx > 0 ? sumVx / nonZeroVx : 0;
            double avgVy = nonZeroVy > 0 ? sumVy / nonZeroVy : 0;
            double avgVz = nonZeroVz > 0 ? sumVz / nonZeroVz : 0;

            Logger.Log($"[Visualizer] Wave field analysis:");
            Logger.Log($"  Max values: Vx={maxVx:E4}, Vy={maxVy:E4}, Vz={maxVz:E4}");
            Logger.Log($"  Avg non-zero: Vx={avgVx:E4}, Vy={avgVy:E4}, Vz={avgVz:E4}");
            Logger.Log($"  Non-zero count: Vx={nonZeroVx}, Vy={nonZeroVy}, Vz={nonZeroVz}");
            Logger.Log($"  Amplification: {SIGNAL_AMPLIFICATION}");
        }
        #endregion
    }
}
