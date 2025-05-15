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

using Timer = System.Windows.Forms.Timer;

namespace CTS
{
    /// <summary>
    /// Real-time visualization window for acoustic simulation results
    /// Compatible with both CPU (AcousticSimulator) and GPU (AcousticSimulatorGPU) simulators
    /// </summary>
    public partial class AcousticSimulationVisualizer : Form
    {
        #region Fields

        // Panel Detachment Tracking
        private Form[] _detachedWindows;
        private bool[] _isPanelDetached;
        private Button[] _detachButtons;
        private Button _detachAllButton;
        private FrameCacheManager cacheManager;
        private bool usingCachedFrames = false;
        private CachedFrame currentCachedFrame;
        // Simulation data
        private readonly object _dataLock = new object();
        private readonly List<SimulationFrame> _frames = new List<SimulationFrame>();
        private readonly int _updateInterval = 1; // Update every 5 steps
        private int _currentStep;
        private int _currentFrameIndex;
        private bool _simulationCompleted;
        private double _pWaveVelocity;
        private double _sWaveVelocity;
        private double _vpVsRatio;
        private Color[] _colormap;
        private float _lastTomographyMin = 0;
        private float _lastTomographyMax = 1;
        private float _lastCrossSectionMin = 0;
        private float _lastCrossSectionMax = 1;

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
            public float PWaveMidpointValue { get; set; }
            public float SWaveMidpointValue { get; set; }
            public float[] PWaveMidpointSeries { get; set; }
            public float[] SWaveMidpointSeries { get; set; }

        }

        #endregion

        #region Constructor
        public AcousticSimulationVisualizer(
    int width, int height, int depth, float pixelSize,
    int tx, int ty, int tz, int rx, int ry, int rz)
        {
            this.Icon = Properties.Resources.favicon;
            // Store simulation parameters
            _width = width;
            _height = height;
            _depth = depth;
            _pixelSize = pixelSize;
            _pixelSizeMm = pixelSize * 1000f; // Convert m to mm for display
            _detachedWindows = new Form[6]; // One for each panel
            _isPanelDetached = new bool[6]; // Track detached state
            _detachButtons = new Button[6]; // Detach buttons for each panel
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

            // Setup path points
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
            Task.Delay(500).ContinueWith(_ =>
            {
                this.BeginInvoke((MethodInvoker)CheckForRecentCache);
            });
        }
        #endregion

        #region CPU/GPU Simulator Handlers
        private void CheckForRecentCache()
        {
            string cacheBaseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AcousticSimulator");

            if (Directory.Exists(cacheBaseDir))
            {
                var directories = Directory.GetDirectories(cacheBaseDir)
                                          .OrderByDescending(d => Directory.GetCreationTime(d))
                                          .Take(5);

                if (directories.Any())
                {
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        var result = MessageBox.Show("Recent cached simulations found. Would you like to load one?",
                                                   "Load Cached Simulation",
                                                   MessageBoxButtons.YesNo,
                                                   MessageBoxIcon.Question);

                        if (result == DialogResult.Yes)
                        {
                            using (var dialog = new FolderBrowserDialog())
                            {
                                dialog.Description = "Select cache directory";
                                dialog.SelectedPath = cacheBaseDir;

                                if (dialog.ShowDialog() == DialogResult.OK)
                                {
                                    LoadFromCache(dialog.SelectedPath);
                                }
                            }
                        }
                    });
                }
            }
        }

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

                // 3) Sample midpoint values explicitly
                int midX = (_tx + _rx) / 2;
                int midY = (_ty + _ry) / 2;
                int midZ = (_tz + _rz) / 2;

                float midP = (float)(vx[midX, midY, midZ] * TIME_SERIES_GAIN);
                float midS = (float)(vy[midX, midY, midZ] * TIME_SERIES_GAIN);

                // 4) Log TX, MID, and RX values (reduced frequency to avoid log spamming)
                if (e.TimeStep % 20 == 0)
                {
                    Logger.Log($"[SimulationVisualizer] P-wave at TX: {vx[_tx, _ty, _tz]:E6}, MID: {vx[midX, midY, midZ]:E6}, RX: {vx[_rx, _ry, _rz]:E6}");
                    Logger.Log($"[SimulationVisualizer] S-wave at TX: {vy[_tx, _ty, _tz]:E6}, MID: {vy[midX, midY, midZ]:E6}, RX: {vy[_rx, _ry, _rz]:E6}");
                }

                // 5) compute tomography + cross-section
                var tomo = ComputeVelocityTomography(_tx, _ty, _tz, _rx, _ry, _rz, vx, vy, vz);
                var xsec = ExtractCrossSection(_tx, _ty, _tz, _rx, _ry, _rz, vx, vy, vz);

                // 6) Sample path for the path visualization
                const int PATH_SAMPLES = 100;
                float[] pWavePath = new float[PATH_SAMPLES];
                float[] sWavePath = new float[PATH_SAMPLES];

                for (int i = 0; i < PATH_SAMPLES; i++)
                {
                    float t = i / (float)(PATH_SAMPLES - 1);

                    int x = (int)Math.Round(_tx + (_rx - _tx) * t);
                    int y = (int)Math.Round(_ty + (_ry - _ty) * t);
                    int z = (int)Math.Round(_tz + (_rz - _tz) * t);

                    x = Math.Max(0, Math.Min(x, _width - 1));
                    y = Math.Max(0, Math.Min(y, _height - 1));
                    z = Math.Max(0, Math.Min(z, _depth - 1));

                    pWavePath[i] = (float)(vx[x, y, z] * TIME_SERIES_GAIN);

                    double sMag = Math.Sqrt(
                        vy[x, y, z] * vy[x, y, z] +
                        vz[x, y, z] * vz[x, y, z]);
                    sWavePath[i] = (float)(sMag * TIME_SERIES_GAIN);
                }

                // 7) Calculate wave progress
                float pProgress = 0.0f;
                float sProgress = 0.0f;

                if (_simulationCompleted && _pWaveTravelTime > 0)
                {
                    // After completion, use real travel times for accurate visualization
                    pProgress = Math.Min(1.0f, (float)e.TimeStep / _pWaveTravelTime);
                    sProgress = Math.Min(1.0f, (float)e.TimeStep / _sWaveTravelTime);
                }
                else
                {
                    // Estimate progress during simulation using current frame index
                    int totalFrames = _frames.Count;

                    // Use reasonable guess for total expected frames
                    int estimatedTotalFrames = 500;

                    if (_totalTimeSteps > 0)
                    {
                        // If we have totalTimeSteps from the simulator, use that
                        estimatedTotalFrames = _totalTimeSteps;
                    }

                    // Calculate based on current step
                    float simpleProgress = Math.Min(1.0f, (float)e.TimeStep / estimatedTotalFrames);

                    // P-waves travel faster than S-waves
                    pProgress = Math.Min(1.0f, simpleProgress * 1.5f);
                    sProgress = Math.Min(1.0f, simpleProgress * 0.8f);
                }

                // 8) build the new frame with spatial path data
                var frame = new SimulationFrame
                {
                    TimeStep = e.TimeStep,
                    PWaveValue = recvP,
                    SWaveValue = recvS,
                    PWavePathProgress = pProgress,
                    SWavePathProgress = sProgress,
                    VelocityTomography = tomo,
                    WavefieldCrossSection = xsec,
                    PWaveSpatialSeries = pWavePath,
                    SWaveSpatialSeries = sWavePath
                };

                // 9) append to _frames list and update the time-series arrays
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

                    _frames.Add(frame);
                    _currentFrameIndex = n;
                    _currentStep = e.TimeStep;
                    _simulationStatus = e.StatusText ?? "Simulating";

                    // Update trackbar maximum immediately on the UI thread
                    if (IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            if (_frames.Count > 1 && _timelineTrackBar.Maximum < (_frames.Count - 1))
                            {
                                _timelineTrackBar.Maximum = _frames.Count - 1;
                            }
                        });
                    }
                }

                // 10) invoke the UI redraw on the main thread
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
        /// Compute velocity tomography parallel to the wave path from TX to RX
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
            float[,] rawVelocity; // To hold the non-normalized values for the colorbar

            // Apply a consistent amplification for visualization
            //const double VELOCITY_AMPLIFICATION = 50000.0;

            // For tomography, we want a plane that is PARALLEL to wave path
            if (dx >= dy && dx >= dz) // X is primary axis
            {
                // Use XY or XZ plane
                if (dy >= dz)
                {
                    // Use XY plane (fixed Z)
                    int midZ = (tz + rz) / 2;
                    midZ = Math.Max(0, Math.Min(midZ, _depth - 1));

                    tomography = new float[_width, _height];
                    rawVelocity = new float[_width, _height];

                    for (int x = 0; x < _width; x++)
                    {
                        for (int y = 0; y < _height; y++)
                        {
                            double magnitude = Math.Sqrt(
                                vx[x, y, midZ] * vx[x, y, midZ] +
                                vy[x, y, midZ] * vy[x, y, midZ] +
                                vz[x, y, midZ] * vz[x, y, midZ]);

                            // rawVelocity[x, y] = (float)(magnitude * VELOCITY_AMPLIFICATION);
                            rawVelocity[x, y] = (float)(magnitude);
                            tomography[x, y] = rawVelocity[x, y];
                        }
                    }
                }
                else
                {
                    // Use XZ plane (fixed Y)
                    int midY = (ty + ry) / 2;
                    midY = Math.Max(0, Math.Min(midY, _height - 1));

                    tomography = new float[_width, _depth];
                    rawVelocity = new float[_width, _depth];

                    for (int x = 0; x < _width; x++)
                    {
                        for (int z = 0; z < _depth; z++)
                        {
                            double magnitude = Math.Sqrt(
                                vx[x, midY, z] * vx[x, midY, z] +
                                vy[x, midY, z] * vy[x, midY, z] +
                                vz[x, midY, z] * vz[x, midY, z]);

                            //rawVelocity[x, z] = (float)(magnitude * VELOCITY_AMPLIFICATION);
                            rawVelocity[x, z] = (float)(magnitude);
                            tomography[x, z] = rawVelocity[x, z];
                        }
                    }
                }
            }
            else if (dy >= dx && dy >= dz) // Y is primary axis
            {
                // Use YZ or XY plane
                if (dx >= dz)
                {
                    // Use XY plane (fixed Z)
                    int midZ = (tz + rz) / 2;
                    midZ = Math.Max(0, Math.Min(midZ, _depth - 1));

                    tomography = new float[_width, _height];
                    rawVelocity = new float[_width, _height];

                    for (int x = 0; x < _width; x++)
                    {
                        for (int y = 0; y < _height; y++)
                        {
                            double magnitude = Math.Sqrt(
                                vx[x, y, midZ] * vx[x, y, midZ] +
                                vy[x, y, midZ] * vy[x, y, midZ] +
                                vz[x, y, midZ] * vz[x, y, midZ]);

                            //rawVelocity[x, y] = (float)(magnitude * VELOCITY_AMPLIFICATION);
                            rawVelocity[x, y] = (float)(magnitude);
                            tomography[x, y] = rawVelocity[x, y];
                        }
                    }
                }
                else
                {
                    // Use YZ plane (fixed X)
                    int midX = (tx + rx) / 2;
                    midX = Math.Max(0, Math.Min(midX, _width - 1));

                    tomography = new float[_height, _depth];
                    rawVelocity = new float[_height, _depth];

                    for (int y = 0; y < _height; y++)
                    {
                        for (int z = 0; z < _depth; z++)
                        {
                            double magnitude = Math.Sqrt(
                                vx[midX, y, z] * vx[midX, y, z] +
                                vy[midX, y, z] * vy[midX, y, z] +
                                vz[midX, y, z] * vz[midX, y, z]);

                            //rawVelocity[y, z] = (float)(magnitude * VELOCITY_AMPLIFICATION);
                            rawVelocity[y, z] = (float)(magnitude);
                            tomography[y, z] = rawVelocity[y, z];
                        }
                    }
                }
            }
            else // Z is primary axis
            {
                // Use XZ or YZ plane
                if (dx >= dy)
                {
                    // Use XZ plane (fixed Y)
                    int midY = (ty + ry) / 2;
                    midY = Math.Max(0, Math.Min(midY, _height - 1));

                    tomography = new float[_width, _depth];
                    rawVelocity = new float[_width, _depth];

                    for (int x = 0; x < _width; x++)
                    {
                        for (int z = 0; z < _depth; z++)
                        {
                            double magnitude = Math.Sqrt(
                                vx[x, midY, z] * vx[x, midY, z] +
                                vy[x, midY, z] * vy[x, midY, z] +
                                vz[x, midY, z] * vz[x, midY, z]);

                            //rawVelocity[x, z] = (float)(magnitude * VELOCITY_AMPLIFICATION);
                            rawVelocity[x, z] = (float)(magnitude);
                            tomography[x, z] = rawVelocity[x, z];
                        }
                    }
                }
                else
                {
                    // Use YZ plane (fixed X)
                    int midX = (tx + rx) / 2;
                    midX = Math.Max(0, Math.Min(midX, _width - 1));

                    tomography = new float[_height, _depth];
                    rawVelocity = new float[_height, _depth];

                    for (int y = 0; y < _height; y++)
                    {
                        for (int z = 0; z < _depth; z++)
                        {
                            double magnitude = Math.Sqrt(
                                vx[midX, y, z] * vx[midX, y, z] +
                                vy[midX, y, z] * vy[midX, y, z] +
                                vz[midX, y, z] * vz[midX, y, z]);

                            //rawVelocity[y, z] = (float)(magnitude * VELOCITY_AMPLIFICATION);
                            rawVelocity[y, z] = (float)(magnitude );
                            tomography[y, z] = rawVelocity[y, z];
                        }
                    }
                }
            }

            // Find the raw min and max values for the colorbar
            int w = tomography.GetLength(0);
            int h = tomography.GetLength(1);

            float rawMin = float.MaxValue;
            float rawMax = float.MinValue;

            // Create a sorted list of all non-zero values
            List<float> sortedValues = new List<float>();
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    float value = tomography[i, j];
                    if (value > 0 && !float.IsNaN(value) && !float.IsInfinity(value))
                    {
                        sortedValues.Add(value);
                        rawMin = Math.Min(rawMin, value);
                        rawMax = Math.Max(rawMax, value);
                    }
                }
            }

            // Sort the values
            sortedValues.Sort();

            // Use 5th and 95th percentiles to avoid outliers (if we have enough data)
            float minThreshold;
            float maxThreshold;

            if (sortedValues.Count > 20)
            {
                // Lower threshold at 5th percentile
                int lowerIdx = Math.Max(0, (int)(sortedValues.Count * 0.05));
                minThreshold = sortedValues[lowerIdx];

                // Upper threshold at 95th percentile for more dynamic range
                int upperIdx = Math.Min(sortedValues.Count - 1, (int)(sortedValues.Count * 0.95));
                maxThreshold = sortedValues[upperIdx];
            }
            else if (sortedValues.Count > 0)
            {
                minThreshold = sortedValues[0];
                maxThreshold = sortedValues[sortedValues.Count - 1];
            }
            else
            {
                minThreshold = 0.0f;
                maxThreshold = 1.0f;
            }

            // Ensure we have a valid range
            if (maxThreshold <= minThreshold)
            {
                maxThreshold = minThreshold + 1.0f;
            }

            // Save these values for the colorbar
            _lastTomographyMin = rawMin;
            _lastTomographyMax = rawMax;

            // Apply thresholds and normalize to [0-1] for visualization
            float[,] normalizedTomography = new float[w, h];
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    // Clamp to thresholds
                    float value = Math.Max(minThreshold, Math.Min(maxThreshold, tomography[i, j]));

                    // Apply logarithmic scaling for better visualization of dynamic range
                    // Only if range is large enough to warrant it
                    if (maxThreshold / minThreshold > 10 && value > 0)
                    {
                        float logMin = (float)Math.Log10(Math.Max(1e-6, minThreshold));
                        float logMax = (float)Math.Log10(maxThreshold);
                        float logVal = (float)Math.Log10(Math.Max(1e-6, value));

                        normalizedTomography[i, j] = (logVal - logMin) / (logMax - logMin);
                    }
                    else
                    {
                        // Linear mapping
                        normalizedTomography[i, j] = (value - minThreshold) / (maxThreshold - minThreshold);
                    }
                }
            }

            Logger.Log($"[ComputeVelocityTomography] Raw velocity range: {rawMin:E3} m/s to {rawMax:E3} m/s");
            Logger.Log($"[ComputeVelocityTomography] Normalized thresholds: Min={minThreshold:E3} m/s, Max={maxThreshold:E3} m/s");

            return normalizedTomography;
        }
        /// <summary>
        /// Extract a cross-section of the wave field perpendicular to the wave path
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
            const double AMPLIFICATION = 1.0;

            // For cross-section, we want a plane PERPENDICULAR to wave path
            if (dx >= dy && dx >= dz) // X is primary axis
            {
                // Take YZ plane (perpendicular to X)
                int midX = (tx + rx) / 2;
                midX = Math.Max(0, Math.Min(midX, _width - 1));

                crossSection = new float[_height, _depth];

                for (int y = 0; y < _height; y++)
                {
                    for (int z = 0; z < _depth; z++)
                    {
                        double magnitude = Math.Sqrt(
                            vx[midX, y, z] * vx[midX, y, z] +
                            vy[midX, y, z] * vy[midX, y, z] +
                            vz[midX, y, z] * vz[midX, y, z]);

                        crossSection[y, z] = (float)(magnitude * AMPLIFICATION);
                    }
                }

                Logger.Log($"[ExtractCrossSection] Created YZ cross-section at X={midX}");
            }
            else if (dy >= dx && dy >= dz) // Y is primary axis
            {
                // Take XZ plane (perpendicular to Y)
                int midY = (ty + ry) / 2;
                midY = Math.Max(0, Math.Min(midY, _height - 1));

                crossSection = new float[_width, _depth];

                for (int x = 0; x < _width; x++)
                {
                    for (int z = 0; z < _depth; z++)
                    {
                        double magnitude = Math.Sqrt(
                            vx[x, midY, z] * vx[x, midY, z] +
                            vy[x, midY, z] * vy[x, midY, z] +
                            vz[x, midY, z] * vz[x, midY, z]);

                        crossSection[x, z] = (float)(magnitude * AMPLIFICATION);
                    }
                }

                Logger.Log($"[ExtractCrossSection] Created XZ cross-section at Y={midY}");
            }
            else // Z is primary axis
            {
                // Take XY plane (perpendicular to Z)
                int midZ = (tz + rz) / 2;
                midZ = Math.Max(0, Math.Min(midZ, _depth - 1));

                crossSection = new float[_width, _height];

                for (int x = 0; x < _width; x++)
                {
                    for (int y = 0; y < _height; y++)
                    {
                        double magnitude = Math.Sqrt(
                            vx[x, y, midZ] * vx[x, y, midZ] +
                            vy[x, y, midZ] * vy[x, y, midZ] +
                            vz[x, y, midZ] * vz[x, y, midZ]);

                        crossSection[x, y] = (float)(magnitude * AMPLIFICATION);
                    }
                }

                Logger.Log($"[ExtractCrossSection] Created XY cross-section at Z={midZ}");
            }

            // Calculate percentile-based thresholds for better visualization
            int w = crossSection.GetLength(0);
            int h = crossSection.GetLength(1);

            // Create a sorted list of all non-zero values
            List<float> sortedValues = new List<float>();
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    float value = crossSection[i, j];
                    if (value > 0)
                        sortedValues.Add(value);
                }
            }

            // Sort the values
            sortedValues.Sort();

            // Use 5th and 95th percentiles to avoid outliers (if we have enough data)
            float minThreshold = 0;
            float maxThreshold;

            if (sortedValues.Count > 0)
            {
                // Lower threshold at 5th percentile
                int lowerIdx = Math.Max(0, (int)(sortedValues.Count * 0.05));
                minThreshold = sortedValues[lowerIdx];

                // Upper threshold at 95th percentile to avoid extreme outliers
                int upperIdx = Math.Min(sortedValues.Count - 1, (int)(sortedValues.Count * 0.95));
                maxThreshold = sortedValues[upperIdx];
            }
            else
            {
                maxThreshold = 1.0f;
            }

            // Ensure we have a valid range
            if (maxThreshold <= minThreshold)
            {
                maxThreshold = minThreshold + 1.0f;
            }

            // Save these values for the colorbar - THIS IS THE FIX
            _lastCrossSectionMin = minThreshold;
            _lastCrossSectionMax = maxThreshold;

            // Apply thresholds and normalize to [0-1] for visualization
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    // Clamp to thresholds
                    crossSection[i, j] = Math.Max(minThreshold, Math.Min(maxThreshold, crossSection[i, j]));

                    // Apply logarithmic scaling for better visualization of dynamic range
                    // Only if range is large enough to warrant it
                    if (maxThreshold / minThreshold > 10 && crossSection[i, j] > 0)
                    {
                        float logMin = (float)Math.Log10(Math.Max(1e-6, minThreshold));
                        float logMax = (float)Math.Log10(maxThreshold);
                        float logVal = (float)Math.Log10(Math.Max(1e-6, crossSection[i, j]));

                        crossSection[i, j] = (logVal - logMin) / (logMax - logMin);
                    }
                    else
                    {
                        // Linear mapping
                        crossSection[i, j] = (crossSection[i, j] - minThreshold) / (maxThreshold - minThreshold);
                    }
                }
            }

            Logger.Log($"[ExtractCrossSection] Adaptive thresholds: Min={minThreshold:E3} m/s, Max={maxThreshold:E3} m/s");
            return crossSection;
        }
        #endregion
        
        #region UI Components
        /// <summary>
        /// Initialize UI components
        /// </summary>
        private void InitializeComponents()
        {
            CreateMenuStrip();

            // Main panel for visualizations
            _mainPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(20, 20, 20)
            };
            this.Controls.Add(_mainPanel);

            // Remove topmost property as requested
            this.TopMost = false;

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

            // Initialize detachable panel system
            _detachedWindows = new Form[6]; // One for each panel
            _isPanelDetached = new bool[6]; // Track detached state
            _detachButtons = new Button[6]; // Detach buttons for each panel

            // Add detach buttons to each panel (except Information panel)
            for (int i = 0; i < 5; i++)  // Stop at 5 to exclude Information panel (index 5)
            {
                // Create detach button with semi-transparent background
                _detachButtons[i] = new Button
                {
                    FlatStyle = FlatStyle.Flat,
                    Size = new Size(28, 28),
                    Location = new Point(8, _subPanels[i].Height - 36),
                    BackColor = Color.FromArgb(120, 30, 30, 30), // Semi-transparent background
                    ForeColor = Color.White,
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                    Cursor = Cursors.Hand,
                    TabStop = false,
                    TabIndex = 999 // High tab index to ensure it gets focus last
                };

                // Create a detach icon for the button
                Bitmap detachIcon = new Bitmap(16, 16);
                using (Graphics g = Graphics.FromImage(detachIcon))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    // Draw a simple "pop-out" icon
                    using (Pen pen = new Pen(Color.White, 2))
                    {
                        // Arrow
                        g.DrawLine(pen, 3, 13, 10, 6);

                        // Arrow head
                        g.DrawLine(pen, 10, 6, 10, 10);
                        g.DrawLine(pen, 10, 6, 6, 6);

                        // Small square
                        g.DrawRectangle(pen, 2, 2, 12, 12);
                    }
                }

                _detachButtons[i].Image = detachIcon;
                _detachButtons[i].ImageAlign = ContentAlignment.MiddleCenter;
                _detachButtons[i].TextAlign = ContentAlignment.MiddleCenter;

                int panelIndex = i; // Capture for lambda
                _detachButtons[i].Click += (s, e) => DetachPanel(panelIndex);

                // Add the button to the panel and make sure it's on top
                _subPanels[i].Controls.Add(_detachButtons[i]);
                _detachButtons[i].BringToFront();
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

            // Add tooltips for detach buttons
            for (int i = 0; i < 5; i++)  // Only the first 5 panels have detach buttons
            {
                _toolTip.SetToolTip(_detachButtons[i], "Detach panel to separate window");
            }

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
        /// Detach a panel to its own window
        /// </summary>
        private void DetachPanel(int panelIndex)
        {
            // Don't allow detaching the information panel
            if (panelIndex == 5) return;

            if (_isPanelDetached[panelIndex])
            {
                // Already detached, bring to front
                _detachedWindows[panelIndex].BringToFront();
                return;
            }

            // Create detached window
            Form detachedWindow = new Form
            {
                Text = GetPanelTitle(panelIndex),
                Size = new Size(500, 400),
                MinimumSize = new Size(300, 200),
                StartPosition = FormStartPosition.CenterScreen,
                Icon = this.Icon,
                BackColor = Color.FromArgb(30, 30, 30)
            };

            // Create picture box for detached window
            PictureBox detachedPictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black
            };
            detachedWindow.Controls.Add(detachedPictureBox);

            // Create save button with square design and semi-transparent background
            Button saveButton = new Button
            {
                FlatStyle = FlatStyle.Flat,
                Size = new Size(32, 32),
                Location = new Point(10, 10),
                BackColor = Color.FromArgb(120, 30, 30, 30), // Semi-transparent background
                ForeColor = Color.White,
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                Cursor = Cursors.Hand
            };

            // Create a save icon for the button
            Bitmap saveIcon = new Bitmap(20, 20);
            using (Graphics g = Graphics.FromImage(saveIcon))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // Draw a simple floppy disk icon
                using (Pen pen = new Pen(Color.White, 1.5f))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    // Outer rectangle
                    g.DrawRectangle(pen, 1, 1, 18, 18);

                    // Inner rectangle (disk label)
                    g.FillRectangle(brush, 4, 4, 12, 5);

                    // Bottom part
                    g.FillRectangle(brush, 4, 11, 12, 5);

                    // Small square (hole)
                    g.DrawRectangle(pen, 13, 5, 2, 2);
                }
            }

            saveButton.Image = saveIcon;
            saveButton.ImageAlign = ContentAlignment.MiddleCenter;

            saveButton.Click += (s, e) => SaveDetachedPanel(panelIndex);
            detachedWindow.Controls.Add(saveButton);

            // Make sure the save button is on top
            saveButton.BringToFront();

            // Create a tooltip for the save button
            ToolTip saveToolTip = new ToolTip();
            saveToolTip.SetToolTip(saveButton, "Save panel image");

            // Handle window closing
            detachedWindow.FormClosing += (s, e) => ReattachPanel(panelIndex);

            // Set initial picture
            detachedPictureBox.Image = (Bitmap)_displayBitmaps[panelIndex].Clone();

            // Track detached state
            _detachedWindows[panelIndex] = detachedWindow;
            _isPanelDetached[panelIndex] = true;

            // Hide the original picture box to prevent confusion
            _pictureBoxes[panelIndex].Visible = false;

            // Create "Panel Detached" label in the original panel
            Label detachedLabel = new Label
            {
                Text = "Panel Detached",
                ForeColor = Color.White,
                BackColor = Color.FromArgb(40, 40, 40),
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
                Font = new Font("Arial", 12, FontStyle.Bold)
            };
            _subPanels[panelIndex].Controls.Add(detachedLabel);
            detachedLabel.BringToFront();

            // Show the detached window
            detachedWindow.Show();
        }
        /// <summary>
        /// Reattach a detached panel
        /// </summary>
        private void ReattachPanel(int panelIndex)
        {
            if (!_isPanelDetached[panelIndex]) return;

            // Remove "Panel Detached" label
            foreach (Control control in _subPanels[panelIndex].Controls)
            {
                if (control is Label label && label.Text == "Panel Detached")
                {
                    _subPanels[panelIndex].Controls.Remove(label);
                    label.Dispose();
                    break;
                }
            }

            // Find and clear the PictureBox in the detached window before closing the window
            if (_detachedWindows[panelIndex] != null && !_detachedWindows[panelIndex].IsDisposed)
            {
                try
                {
                    // First find the PictureBox in the detached window
                    foreach (Control control in _detachedWindows[panelIndex].Controls)
                    {
                        if (control is PictureBox pictureBox)
                        {
                            // Clear the image first to prevent the animation error
                            Bitmap oldImage = pictureBox.Image as Bitmap;
                            pictureBox.Image = null;

                            // Now it's safe to dispose the old image
                            if (oldImage != null)
                            {
                                try
                                {
                                    oldImage.Dispose();
                                }
                                catch
                                {
                                    // Ignore errors while disposing the image
                                }
                            }
                            break;
                        }
                    }

                    // Now we can safely close the window
                    _detachedWindows[panelIndex].Dispose();
                }
                catch (Exception ex)
                {
                    // Log the error but continue
                    Logger.Log($"[ReattachPanel] Error while cleaning up detached window: {ex.Message}");
                }
            }

            _detachedWindows[panelIndex] = null;
            _isPanelDetached[panelIndex] = false;

            // Make sure the picture box is visible again
            _pictureBoxes[panelIndex].Visible = true;

            // Update with current image
            try
            {
                _pictureBoxes[panelIndex].Image = _displayBitmaps[panelIndex];
            }
            catch (Exception ex)
            {
                // Log the error but continue
                Logger.Log($"[ReattachPanel] Error while updating picture box: {ex.Message}");

                // Create a new bitmap if needed
                _pictureBoxes[panelIndex].Image = new Bitmap(Math.Max(1, _pictureBoxes[panelIndex].Width),
                                                Math.Max(1, _pictureBoxes[panelIndex].Height));
            }

            // Refresh the panel
            _pictureBoxes[panelIndex].Refresh();
        }

        /// <summary>
        /// Detach all panels at once
        /// </summary>
        private void DetachAllPanels()
        {
            for (int i = 0; i < 6; i++)
            {
                if (!_isPanelDetached[i])
                {
                    DetachPanel(i);
                }
            }
        }
        
        /// <summary>
        /// Save detached panel image
        /// </summary>
        private void SaveDetachedPanel(int panelIndex)
        {
            using (SaveFileDialog dialog = new SaveFileDialog
            {
                Filter = "PNG Image|*.png|JPEG Image|*.jpg|Bitmap Image|*.bmp",
                Title = "Save Panel Image"
            })
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        // Get current panel bitmap
                        Bitmap bitmap = (Bitmap)_displayBitmaps[panelIndex].Clone();

                        // Save bitmap
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

                        bitmap.Save(dialog.FileName, format);
                        MessageBox.Show($"Panel image saved to {dialog.FileName}", "Save Complete",
                                      MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error saving image: {ex.Message}", "Save Error",
                                      MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        /// <summary>
        /// Get panel title based on index
        /// </summary>
        private string GetPanelTitle(int panelIndex)
        {
            string[] titles = {
        "P-Wave Time Series",
        "S-Wave Time Series",
        "Velocity Tomography",
        "Wavefield Cross-Section",
        "Combined P/S Wave Visualization",
        "Simulation Information"
    };

            return titles[panelIndex];
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
            Logger.Log($"[UpdateVisualization] Starting - cached: {usingCachedFrames}");

            if (usingCachedFrames)
            {
                Logger.Log($"[UpdateVisualization] Using cached visualization");
                UpdateVisualizationFromCache();
                return;
            }

            lock (_dataLock)
            {
                Logger.Log($"[UpdateVisualization] Frame count: {_frames.Count}, Current index: {_currentFrameIndex}");

                // Check if we have frames
                if (_frames.Count == 0 || _currentFrameIndex < 0 || _currentFrameIndex >= _frames.Count)
                {
                    Logger.Log($"[UpdateVisualization] Invalid frame state - aborting");
                    return;
                }

                SimulationFrame frame = _frames[_currentFrameIndex];
                Logger.Log($"[UpdateVisualization] Frame loaded: TimeStep={frame.TimeStep}");

                try
                {
                    // Update time series panel (P-wave)
                    using (Graphics g = Graphics.FromImage(_panelBitmaps[0]))
                    {
                        DrawTimeSeries(g, frame.PWaveTimeSeries, 0);
                    }
                    Logger.Log($"[UpdateVisualization] P-wave time series drawn");

                    // Update time series panel (S-wave)
                    using (Graphics g = Graphics.FromImage(_panelBitmaps[1]))
                    {
                        DrawTimeSeries(g, frame.SWaveTimeSeries, 1);
                    }
                    Logger.Log($"[UpdateVisualization] S-wave time series drawn");

                    // Update velocity tomography panel
                    using (Graphics g = Graphics.FromImage(_panelBitmaps[2]))
                    {
                        DrawHeatmap(g, frame.VelocityTomography, 2);
                    }
                    Logger.Log($"[UpdateVisualization] Velocity tomography drawn");

                    // Update cross-section panel
                    using (Graphics g = Graphics.FromImage(_panelBitmaps[3]))
                    {
                        DrawHeatmap(g, frame.WavefieldCrossSection, 3);
                    }
                    Logger.Log($"[UpdateVisualization] Cross-section drawn");

                    // Update combined P/S wave visualization panel
                    using (Graphics g = Graphics.FromImage(_panelBitmaps[4]))
                    {
                        DrawCombinedWaveVisualization(g, frame);
                    }
                    Logger.Log($"[UpdateVisualization] Combined wave visualization drawn");

                    // Update information panel
                    using (Graphics g = Graphics.FromImage(_panelBitmaps[5]))
                    {
                        DrawInformationPanel(g);
                    }
                    Logger.Log($"[UpdateVisualization] Information panel drawn");

                    // Update the UI on the main thread
                    Logger.Log($"[UpdateVisualization] Starting UI update");
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        try
                        {
                            Logger.Log($"[UpdateVisualization] Updating UI components");

                            // Update time step label
                            _timeStepLabel.Text = $"Step: {frame.TimeStep}";

                            // Update trackbar
                            if (_frames.Count > 1 && _timelineTrackBar.Maximum < (_frames.Count - 1))
                            {
                                _timelineTrackBar.Maximum = _frames.Count - 1;
                            }

                            if (!_timelineTrackBar.Capture)
                            {
                                int safeIndex = Math.Min(_currentFrameIndex, _timelineTrackBar.Maximum);
                                safeIndex = Math.Max(safeIndex, _timelineTrackBar.Minimum);

                                if (_timelineTrackBar.Value != safeIndex)
                                {
                                    _timelineTrackBar.Value = safeIndex;
                                }
                            }

                            // Copy bitmaps to display bitmaps and refresh pictureboxes
                            for (int i = 0; i < 6; i++)
                            {
                                Logger.Log($"[UpdateVisualization] Updating panel {i}");

                                if (_displayBitmaps[i] != null && _displayBitmaps[i] != _panelBitmaps[i])
                                {
                                    _displayBitmaps[i].Dispose();
                                }

                                _displayBitmaps[i] = (Bitmap)_panelBitmaps[i].Clone();

                                // Update main window pictureboxes if not detached
                                if (!_isPanelDetached[i])
                                {
                                    _pictureBoxes[i].Image = _displayBitmaps[i];
                                    _pictureBoxes[i].Refresh();
                                }

                                // Update detached windows if they exist
                                if (_isPanelDetached[i] && _detachedWindows[i] != null && !_detachedWindows[i].IsDisposed)
                                {
                                    foreach (Control control in _detachedWindows[i].Controls)
                                    {
                                        if (control is PictureBox pictureBox)
                                        {
                                            if (pictureBox.Image != null)
                                            {
                                                pictureBox.Image.Dispose();
                                            }
                                            pictureBox.Image = (Bitmap)_displayBitmaps[i].Clone();
                                            break;
                                        }
                                    }
                                }
                            }

                            Logger.Log($"[UpdateVisualization] UI update completed");
                        }
                        catch (Exception uiEx)
                        {
                            Logger.Log($"[UpdateVisualization] UI update error: {uiEx.Message}");
                            Logger.Log($"[UpdateVisualization] Stack trace: {uiEx.StackTrace}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Logger.Log($"[UpdateVisualization] Error: {ex.Message}");
                    Logger.Log($"[UpdateVisualization] Stack trace: {ex.StackTrace}");
                }
            }
        }
        private void UpdateVisualizationFromCache()
        {
            lock (_dataLock)
            {
                if (cacheManager == null || _currentFrameIndex < 0 || _currentFrameIndex >= _frames.Count)
                    return;

                try
                {
                    // Load frame from cache
                    currentCachedFrame = cacheManager.LoadFrame(_currentFrameIndex);
                    if (currentCachedFrame == null)
                    {
                        Logger.Log($"[Visualizer] Failed to load cached frame {_currentFrameIndex}");
                        return;
                    }

                    // Convert cached frame to visualization frame
                    var frame = new SimulationFrame
                    {
                        TimeStep = currentCachedFrame.TimeStep,
                        PWaveValue = currentCachedFrame.PWaveValue,
                        SWaveValue = currentCachedFrame.SWaveValue,
                        PWavePathProgress = currentCachedFrame.PWavePathProgress,
                        SWavePathProgress = currentCachedFrame.SWavePathProgress,
                        VelocityTomography = currentCachedFrame.Tomography,
                        WavefieldCrossSection = currentCachedFrame.CrossSection,
                        PWaveTimeSeries = currentCachedFrame.PWaveTimeSeries ?? new float[1],
                        SWaveTimeSeries = currentCachedFrame.SWaveTimeSeries ?? new float[1],
                        PWaveSpatialSeries = ExtractSpatialSeries(currentCachedFrame.VX, true),
                        SWaveSpatialSeries = ExtractSpatialSeries(currentCachedFrame.VY, false)
                    };

                    // Store in frames list for consistency
                    _frames[_currentFrameIndex] = frame;

                    // Update all panels
                    using (Graphics g = Graphics.FromImage(_panelBitmaps[0]))
                    {
                        DrawTimeSeries(g, frame.PWaveTimeSeries, 0);
                    }

                    using (Graphics g = Graphics.FromImage(_panelBitmaps[1]))
                    {
                        DrawTimeSeries(g, frame.SWaveTimeSeries, 1);
                    }

                    using (Graphics g = Graphics.FromImage(_panelBitmaps[2]))
                    {
                        DrawHeatmap(g, frame.VelocityTomography, 2);
                    }

                    using (Graphics g = Graphics.FromImage(_panelBitmaps[3]))
                    {
                        DrawHeatmap(g, frame.WavefieldCrossSection, 3);
                    }

                    using (Graphics g = Graphics.FromImage(_panelBitmaps[4]))
                    {
                        DrawCombinedWaveVisualization(g, frame);
                    }

                    using (Graphics g = Graphics.FromImage(_panelBitmaps[5]))
                    {
                        DrawInformationPanel(g);
                    }

                    // Update UI
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        _timeStepLabel.Text = $"Step: {frame.TimeStep}";

                        // Update trackbar if needed
                        if (!_timelineTrackBar.Capture && _timelineTrackBar.Value != _currentFrameIndex)
                        {
                            _timelineTrackBar.Value = _currentFrameIndex;
                        }

                        // Update all display bitmaps
                        for (int i = 0; i < 6; i++)
                        {
                            if (_displayBitmaps[i] != null && _displayBitmaps[i] != _panelBitmaps[i])
                            {
                                _displayBitmaps[i].Dispose();
                            }

                            _displayBitmaps[i] = (Bitmap)_panelBitmaps[i].Clone();

                            if (!_isPanelDetached[i])
                            {
                                _pictureBoxes[i].Image = _displayBitmaps[i];
                                _pictureBoxes[i].Refresh();
                            }

                            // Update detached windows
                            if (_isPanelDetached[i] && _detachedWindows[i] != null && !_detachedWindows[i].IsDisposed)
                            {
                                foreach (Control control in _detachedWindows[i].Controls)
                                {
                                    if (control is PictureBox pictureBox)
                                    {
                                        if (pictureBox.Image != null)
                                        {
                                            pictureBox.Image.Dispose();
                                        }
                                        pictureBox.Image = (Bitmap)_displayBitmaps[i].Clone();
                                        break;
                                    }
                                }
                            }
                        }
                    });
                }
                catch (Exception ex)
                {
                    Logger.Log($"[Visualizer] Error updating from cache: {ex.Message}");
                }
            }
        }
        private float[] ExtractSpatialSeries(float[,,] field, bool isPWave)
        {
            if (field == null)
                return new float[1];

            const int PATH_SAMPLES = 100;
            float[] series = new float[PATH_SAMPLES];

            for (int i = 0; i < PATH_SAMPLES; i++)
            {
                float t = i / (float)(PATH_SAMPLES - 1);

                int x = (int)Math.Round(_tx + (_rx - _tx) * t);
                int y = (int)Math.Round(_ty + (_ry - _ty) * t);
                int z = (int)Math.Round(_tz + (_rz - _tz) * t);

                x = Math.Max(0, Math.Min(x, _width - 1));
                y = Math.Max(0, Math.Min(y, _height - 1));
                z = Math.Max(0, Math.Min(z, _depth - 1));

                series[i] = field[x, y, z];
            }

            return series;
        }


        /// <summary>
        /// Draw time series data with enhanced oscillation visibility
        /// </summary>
        private void DrawTimeSeries(Graphics g, float[] series, int panelIndex)
        {
            if (series == null || series.Length <= 1)
                return;

            int width = _panelBitmaps[panelIndex].Width;
            int height = _panelBitmaps[panelIndex].Height;

            // Clear and setup
            g.Clear(Color.Black);
            g.DrawRectangle(Pens.White, 0, 0, width - 1, height - 1);

            // Draw minimal grid
            using (Pen gridPen = new Pen(Color.FromArgb(20, 20, 20)))
            {
                for (int i = 0; i < width; i += 50)
                    g.DrawLine(gridPen, i, 0, i, height);
                for (int i = 0; i < height; i += 50)
                    g.DrawLine(gridPen, 0, i, width, i);
            }

            // Find actual min/max values - use ALL data points
            float minVal = float.MaxValue;
            float maxVal = float.MinValue;

            for (int i = 0; i < series.Length; i++)
            {
                if (!float.IsNaN(series[i]) && !float.IsInfinity(series[i]))
                {
                    minVal = Math.Min(minVal, series[i]);
                    maxVal = Math.Max(maxVal, series[i]);
                }
            }

            // If no valid range, use defaults
            if (minVal >= maxVal)
            {
                minVal = -0.1f;
                maxVal = 0.1f;
            }

            // Ensure symmetrical range around zero for proper oscillation display
            float absMax = Math.Max(Math.Abs(minVal), Math.Abs(maxVal));
            minVal = -absMax;
            maxVal = absMax;

            // Ensure non-zero range and add small padding
            if (Math.Abs(maxVal - minVal) < 1e-6f)
            {
                float mid = (maxVal + minVal) / 2;
                minVal = mid - 0.1f;
                maxVal = mid + 0.1f;
            }

            // Draw value range
            using (Font font = new Font("Arial", 8))
            using (Brush brush = new SolidBrush(Color.LightGray))
            {
                g.DrawString($"{maxVal:0.00E+0} m/s", font, brush, 5, 5);
                g.DrawString($"{minVal:0.00E+0} m/s", font, brush, 5, height - 20);
            }

            // Draw zero line - exactly in the middle
            float zeroY = height / 2;
            using (Pen zeroPen = new Pen(Color.DimGray, 1))
            {
                zeroPen.DashStyle = DashStyle.Dash;
                g.DrawLine(zeroPen, 0, zeroY, width, zeroY);
            }

            // DIRECT POINT-TO-POINT RENDERING OF EVERY SAMPLE
            // This is the key to showing actual oscillations
            float xStep = (float)width / (series.Length - 1);

            using (Pen linePen = new Pen(panelIndex == 0 ? Color.DeepSkyBlue : Color.Crimson, 1.5f))
            {
                for (int i = 1; i < series.Length; i++)
                {
                    if (float.IsNaN(series[i - 1]) || float.IsInfinity(series[i - 1]) ||
                        float.IsNaN(series[i]) || float.IsInfinity(series[i]))
                        continue;

                  
                    float x1 = (i - 1) * xStep;
                    float x2 = i * xStep;

                    // Y coordinates centered around the middle of the screen
                    float y1 = height / 2 - ((series[i - 1] / absMax) * (height / 2 - 10));
                    float y2 = height / 2 - ((series[i] / absMax) * (height / 2 - 10));

                    // Draw segment
                    g.DrawLine(linePen, x1, y1, x2, y2);
                }
            }

            // Fill area under curve from zero line
            if (series.Length > 1)
            {
                // Create filled path
                using (GraphicsPath path = new GraphicsPath())
                {
                    // Start at zero line
                    path.AddLine(0, height / 2, 0, height / 2 - ((series[0] / absMax) * (height / 2 - 10)));

                    // Add all points
                    for (int i = 1; i < series.Length; i++)
                    {
                        if (float.IsNaN(series[i]) || float.IsInfinity(series[i]))
                            continue;

                        float x = i * xStep;
                        float y = height / 2 - ((series[i] / absMax) * (height / 2 - 10));
                        path.AddLine(
                            (i - 1) * xStep,
                            height / 2 - ((series[i - 1] / absMax) * (height / 2 - 10)),
                            x, y);
                    }

                    // Close to zero line
                    path.AddLine(
                        (series.Length - 1) * xStep,
                        height / 2 - ((series[series.Length - 1] / absMax) * (height / 2 - 10)),
                        (series.Length - 1) * xStep, height / 2);

                    path.CloseFigure();

                    // Fill path
                    using (SolidBrush fillBrush = new SolidBrush(
                        panelIndex == 0 ? Color.FromArgb(40, 0, 150, 255) : Color.FromArgb(40, 255, 50, 50)))
                    {
                        g.FillPath(fillBrush, path);
                    }
                }
            }

            // Draw title
            using (Font titleFont = new Font("Arial", 12, FontStyle.Bold))
            using (Brush titleBrush = new SolidBrush(
                panelIndex == 0 ? Color.DeepSkyBlue : Color.Crimson))
            {
                string title = panelIndex == 0 ? "P-Wave (Compressive)" : "S-Wave (Shear)";
                SizeF titleSize = g.MeasureString(title, titleFont);
                g.DrawString(title, titleFont, titleBrush, (width - titleSize.Width) / 2, 10);
            }

            // Amplification info
            using (Font smallFont = new Font("Arial", 8))
            using (Brush smallBrush = new SolidBrush(Color.Silver))
            {
                g.DrawString($"Amplification: {SIGNAL_AMPLIFICATION}x", smallFont, smallBrush, width - 150, height - 20);
                g.DrawString($"Samples: {series.Length}", smallFont, smallBrush, width - 150, height - 35);
            }
        }
        private void DrawColorbar(Graphics g, Rectangle rect, float minVal, float maxVal)
        {
            try
            {
                // Create a bitmap for the colorbar
                Bitmap colorbar = new Bitmap(rect.Width, rect.Height);

                // Draw the gradient
                for (int y = 0; y < rect.Height; y++)
                {
                    // Map y position to colormap index (invert so max is at top)
                    int index = 255 - (int)((float)y / rect.Height * 256);
                    index = Math.Max(0, Math.Min(255, index));

                    // Draw a horizontal line of this color
                    for (int x = 0; x < rect.Width; x++)
                    {
                        colorbar.SetPixel(x, y, _colormap[index]);
                    }
                }

                // Draw the colorbar on the graphics context
                g.DrawImage(colorbar, rect);
                g.DrawRectangle(Pens.White, rect);

                // Handle possible NaN or infinity values
                if (float.IsNaN(minVal) || float.IsInfinity(minVal)) minVal = 0;
                if (float.IsNaN(maxVal) || float.IsInfinity(maxVal)) maxVal = 1;

                // Ensure min < max
                if (minVal > maxVal)
                {
                    float temp = minVal;
                    minVal = maxVal;
                    maxVal = temp;
                }

                Font font = new Font("Arial", 8);
                SolidBrush textBrush = new SolidBrush(Color.White);
                SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(80, 0, 0, 0));

                // Create velocity-specific labels with proper units and formatting
                string maxText = FormatVelocityValue(maxVal);
                string minText = FormatVelocityValue(minVal);
                string midText = FormatVelocityValue((minVal + maxVal) / 2);

                // Draw max value at top with shadow for better visibility
                SizeF maxSize = g.MeasureString(maxText, font);
                float maxX = Math.Max(0, rect.X - maxSize.Width - 2);
                g.FillRectangle(shadowBrush, maxX, rect.Y - 2, maxSize.Width + 4, maxSize.Height + 4);
                g.DrawString(maxText, font, textBrush, maxX, rect.Y);

                // Draw mid value at middle with shadow
                SizeF midSize = g.MeasureString(midText, font);
                float midX = Math.Max(0, rect.X - midSize.Width - 2);
                float midY = rect.Y + rect.Height / 2 - midSize.Height / 2;
                g.FillRectangle(shadowBrush, midX, midY, midSize.Width + 4, midSize.Height + 4);
                g.DrawString(midText, font, textBrush, midX, midY);

                // Draw min value at bottom with shadow
                SizeF minSize = g.MeasureString(minText, font);
                float minX = Math.Max(0, rect.X - minSize.Width - 2);
                g.FillRectangle(shadowBrush, minX, rect.Bottom - minSize.Height - 2, minSize.Width + 4, minSize.Height + 4);
                g.DrawString(minText, font, textBrush, minX, rect.Bottom - minSize.Height);

                font.Dispose();
                textBrush.Dispose();
                shadowBrush.Dispose();
                colorbar.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Log($"[DrawColorbar] Error: {ex.Message}");
            }
        }
        private string FormatVelocityValue(float value)
        {
            // Format based on magnitude with m/s units
            if (float.IsNaN(value) || float.IsInfinity(value))
                return "N/A";

            if (Math.Abs(value) < 0.001f)
                return $"{value:0.00E+0} m/s";
            else if (Math.Abs(value) < 10)
                return $"{value:0.00} m/s";
            else if (Math.Abs(value) < 1000)
                return $"{value:0.0} m/s";
            else
                return $"{value:0.0} m/s"; 
        }
        public void LoadFromCache(string cacheDirectory)
        {
            try
            {
                // Create cache manager and load metadata
                cacheManager = new FrameCacheManager(cacheDirectory, _width, _height, _depth);
                cacheManager.LoadMetadata();

                usingCachedFrames = true;

                // Update frames list with cache info
                lock (_dataLock)
                {
                    _frames.Clear();
                    int frameCount = cacheManager.FrameCount;

                    // Create placeholder frames
                    for (int i = 0; i < frameCount; i++)
                    {
                        _frames.Add(new SimulationFrame { TimeStep = i });
                    }

                    _currentFrameIndex = 0;
                    _simulationCompleted = true; // Cached simulations are always complete
                }

                // Update UI
                BeginInvoke((MethodInvoker)delegate
                {
                    _timelineTrackBar.Maximum = _frames.Count - 1;
                    _timelineTrackBar.Value = 0;
                    this.Text = $"Acoustic Simulation Visualizer - Cached ({_frames.Count} frames)";
                    UpdateVisualization();
                });

                Logger.Log($"[Visualizer] Loaded {_frames.Count} frames from cache at: {cacheDirectory}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[Visualizer] Error loading cache: {ex.Message}");
                MessageBox.Show($"Error loading cached simulation: {ex.Message}",
                                "Load Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
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

            // Get the actual velocity range for this panel
            float rawMin, rawMax;
            if (panelIndex == 2) // Velocity tomography
            {
                rawMin = _lastTomographyMin;
                rawMax = _lastTomographyMax;
            }
            else if (panelIndex == 3) // Wavefield cross-section
            {
                rawMin = _lastCrossSectionMin;
                rawMax = _lastCrossSectionMax;
            }
            else
            {
                rawMin = 0;
                rawMax = 1;
            }

            // Reserve space for colorbar
            int colorbarWidth = 20;
            int imageWidth = panelW - colorbarWidth - 10;

            // Create a bitmap for the heatmap
            Bitmap heatmapBitmap = new Bitmap(imageWidth, panelH);

            // Fill the heatmap bitmap
            for (int py = 0; py < panelH; py++)
            {
                int dataY = Math.Min(dataH - 1, (int)(py * (float)dataH / panelH));

                for (int px = 0; px < imageWidth; px++)
                {
                    int dataX = Math.Min(dataW - 1, (int)(px * (float)dataW / imageWidth));

                    // Get data value with bounds checking
                    float v = 0;
                    if (dataX >= 0 && dataX < dataW && dataY >= 0 && dataY < dataH)
                    {
                        v = data[dataX, dataY];
                        if (float.IsNaN(v) || float.IsInfinity(v)) v = 0;
                    }

                    // Already normalized in processing method, so just clamp to [0,1]
                    v = Math.Max(0, Math.Min(1, v));

                    // Convert to color index
                    int colorIdx = (int)(v * 255);
                    colorIdx = Math.Max(0, Math.Min(255, colorIdx));

                    // Set pixel color
                    heatmapBitmap.SetPixel(px, py, _colormap[colorIdx]);
                }
            }

            // Draw the heatmap bitmap
            g.DrawImage(heatmapBitmap, 0, 0);

            // Draw colorbar with proper units
            Rectangle colorbarRect = new Rectangle(panelW - colorbarWidth - 5,
                                                  panelH / 4,  // Start 1/4 from top
                                                  colorbarWidth,
                                                  panelH / 2); // Take middle half height

            DrawColorbar(g, colorbarRect, rawMin, rawMax);

            // Add title and amplification info
            using (var font = new Font("Arial", 9, FontStyle.Bold))
            using (var brush = new SolidBrush(Color.White))
            using (var shadowBrush = new SolidBrush(Color.FromArgb(100, 0, 0, 0)))
            {
                // Add title based on panel type
                string title = "";
                if (panelIndex == 2) title = "Velocity Tomography";
                else if (panelIndex == 3) title = "Wavefield Cross-Section";

                if (!string.IsNullOrEmpty(title))
                {
                    // Draw shadow behind title for better visibility
                    SizeF titleSize = g.MeasureString(title, font);
                    g.FillRectangle(shadowBrush, 10, 10, titleSize.Width, titleSize.Height);
                    g.DrawString(title, font, brush, 10, 10);
                }

                // Note about velocity range with proper units
                string amplitudeInfo = "";
                if (panelIndex == 2)
                    amplitudeInfo = $"Range: {FormatVelocityValue(rawMin)} - {FormatVelocityValue(rawMax)}";
                else if (panelIndex == 3)
                    amplitudeInfo = $"Range: {FormatVelocityValue(rawMin)} - {FormatVelocityValue(rawMax)}";

                using (var smallFont = new Font("Arial", 8))
                {
                    SizeF amplSize = g.MeasureString(amplitudeInfo, smallFont);
                    g.FillRectangle(shadowBrush, 10, panelH - amplSize.Height - 5,
                                amplSize.Width, amplSize.Height);
                    g.DrawString(amplitudeInfo, smallFont, brush, 10, panelH - amplSize.Height - 5);
                }
            }

            // Draw TX/RX positions if this is a tomography or wave field view
            if (panelIndex == 2 || panelIndex == 3)
            {
                DrawTransducersOnTomography(g, imageWidth, panelH);
            }

            // Dispose temporary bitmap
            heatmapBitmap.Dispose();
        }
        /// <summary>
        /// Format values for heatmap display with units
        /// </summary>
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
        /// Draw the combined time series with parallel P and S waves next to a transmitter drawing,
        /// keeping the mid-point view as requested
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
            DrawTxRxIllustrationWithMidpoint(g, 10, 10, _panelBitmaps[4].Width - 20, halfHeight - 20);

            // Draw spatial visualization along the path
            lock (_dataLock)
            {
                // Create arrays to hold path data if not already in the frame
                float[] pWavePath;
                float[] sWavePath;

                // Use spatial series if available in the frame
                if (frame.PWaveSpatialSeries != null && frame.SWaveSpatialSeries != null)
                {
                    pWavePath = frame.PWaveSpatialSeries;
                    sWavePath = frame.SWaveSpatialSeries;
                }
                // Otherwise, create spatial representation using time series with a focus on the midpoint
                else if (frame.PWaveTimeSeries != null && frame.SWaveTimeSeries != null)
                {
                    // Create spatial visualization by distributing time series data along the path
                    // with a focus at the midpoint
                    const int PATH_SAMPLES = 100;
                    pWavePath = new float[PATH_SAMPLES];
                    sWavePath = new float[PATH_SAMPLES];

                    // Calculate an envelope to emphasize midpoint
                    for (int i = 0; i < PATH_SAMPLES; i++)
                    {
                        float t = i / (float)(PATH_SAMPLES - 1);

                        // Calculate time index from spatial position (with midpoint emphasis)
                        int timeIndex;

                        // For first half of path, use first half of time series
                        if (t <= 0.5f)
                        {
                            timeIndex = (int)(t * 2 * frame.PWaveTimeSeries.Length / 3);
                        }
                        // For second half of path, use remaining time series
                        else
                        {
                            timeIndex = (int)(frame.PWaveTimeSeries.Length / 3 +
                                      (t - 0.5f) * 2 * frame.PWaveTimeSeries.Length * 2 / 3);
                        }

                        // Ensure index is in bounds
                        timeIndex = Math.Max(0, Math.Min(timeIndex, frame.PWaveTimeSeries.Length - 1));

                        // Get values from time series
                        pWavePath[i] = frame.PWaveTimeSeries[timeIndex];
                        sWavePath[i] = frame.SWaveTimeSeries[timeIndex];

                        // Apply spatial envelope to emphasize midpoint
                        float distFromMid = Math.Abs(t - 0.5f) * 2; // 0 at midpoint, 1 at endpoints
                        float emphasis = 1.0f - 0.7f * distFromMid; // Stronger at midpoint

                        pWavePath[i] *= emphasis;
                        sWavePath[i] *= emphasis;
                    }

                    // Force midpoint to have a visible value if other values are low
                    int midIndex = PATH_SAMPLES / 2;

                    // Check if the middle value is too small
                    if (Math.Abs(pWavePath[midIndex]) < 0.05f * SIGNAL_AMPLIFICATION)
                    {
                        // Determine if we should add a synthetic pulse at midpoint
                        bool addPulse = frame.PWavePathProgress >= 0.5f && frame.PWavePathProgress <= 0.6f;

                        if (addPulse)
                        {
                            // Add synthetic pulse
                            float pulseValue = 0.1f * SIGNAL_AMPLIFICATION;

                            // Apply a small bell curve around the midpoint
                            for (int i = midIndex - 5; i <= midIndex + 5; i++)
                            {
                                if (i >= 0 && i < PATH_SAMPLES)
                                {
                                    float distance = Math.Abs(i - midIndex) / 5.0f;
                                    float factor = (float)Math.Exp(-distance * distance * 4);
                                    pWavePath[i] += pulseValue * factor;
                                }
                            }
                        }
                    }

                    // Same for S-wave
                    if (Math.Abs(sWavePath[midIndex]) < 0.05f * SIGNAL_AMPLIFICATION)
                    {
                        bool addPulse = frame.SWavePathProgress >= 0.5f && frame.SWavePathProgress <= 0.6f;

                        if (addPulse)
                        {
                            float pulseValue = 0.1f * SIGNAL_AMPLIFICATION;

                            for (int i = midIndex - 5; i <= midIndex + 5; i++)
                            {
                                if (i >= 0 && i < PATH_SAMPLES)
                                {
                                    float distance = Math.Abs(i - midIndex) / 5.0f;
                                    float factor = (float)Math.Exp(-distance * distance * 4);
                                    sWavePath[i] += pulseValue * factor;
                                }
                            }
                        }
                    }
                }
                else
                {
                    // If no data is available, create empty arrays
                    pWavePath = new float[2] { 0, 0 };
                    sWavePath = new float[2] { 0, 0 };
                }

                // Draw the parallel waveforms using the prepared spatial data
                DrawParallelWavesWithMidpoint(g, 10, halfHeight,
                                       _panelBitmaps[4].Width - 20, halfHeight - 10,
                                       pWavePath, sWavePath);

                // Do we have wave at the midpoint?
                bool anyNonZeroAtMid = false;
                if (pWavePath.Length > 0 && sWavePath.Length > 0)
                {
                    int midIdx = pWavePath.Length / 2;
                    if (midIdx < pWavePath.Length &&
                        (Math.Abs(pWavePath[midIdx]) > 0.01f || Math.Abs(sWavePath[midIdx]) > 0.01f))
                    {
                        anyNonZeroAtMid = true;
                    }
                }

                // If no signal at midpoint, draw special notice
                if (!anyNonZeroAtMid &&
                    ((frame.PWavePathProgress > 0.4f && frame.PWavePathProgress < 0.6f) ||
                     (frame.SWavePathProgress > 0.4f && frame.SWavePathProgress < 0.6f)))
                {
                    using (Font font = new Font("Arial", 9, FontStyle.Bold))
                    using (SolidBrush textBrush = new SolidBrush(Color.Yellow))
                    {
                        string msg = "Waves passing through midpoint...";
                        SizeF size = g.MeasureString(msg, font);
                        float x = (_panelBitmaps[4].Width - size.Width) / 2;
                        g.DrawString(msg, font, textBrush, x, halfHeight - 25);
                    }
                }
            }
        }
        /// <summary>
        /// Draw TX-RX illustration showing wave propagation with midpoint highlighted
        /// </summary>
        private void DrawTxRxIllustrationWithMidpoint(Graphics g, int x, int y, int width, int height)
        {
            // Drawing area
            int margin = 20;
            int drawWidth = width - 2 * margin;
            int drawHeight = height - 2 * margin;

            // Calculate TX, RX, and midpoint positions
            int txX = x + margin + drawWidth / 5;
            int rxX = x + margin + drawWidth * 4 / 5;
            int midX = (txX + rxX) / 2;
            int centerY = y + height / 2;

            // Draw a label indicating we're showing the mid-point view
            using (Font labelFont = new Font("Arial", 9, FontStyle.Bold))
            using (SolidBrush labelBrush = new SolidBrush(Color.White))
            {
                g.DrawString("Combined View - Midpoint Measurement", labelFont, labelBrush, x + margin, y + margin - 15);
            }

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

            // Draw MIDPOINT icon (diamond)
            using (SolidBrush midBrush = new SolidBrush(Color.Cyan))
            {
                // Create diamond shape
                Point[] diamondPoints = {
            new Point(midX, centerY - 10),
            new Point(midX + 10, centerY),
            new Point(midX, centerY + 10),
            new Point(midX - 10, centerY)
        };
                g.FillPolygon(midBrush, diamondPoints);
                g.DrawPolygon(Pens.White, diamondPoints);

                // MID label - make it larger and more prominent
                using (Font font = new Font("Arial", 9, FontStyle.Bold))
                using (SolidBrush textBrush = new SolidBrush(Color.Lime))
                {
                    g.DrawString("MID", font, textBrush, midX - 12, centerY - 22);
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
                        float frameProgress = (float)_currentFrameIndex / Math.Max(1, _frames.Count - 1);
                        pWaveProgress = Math.Min(1.0f, frameProgress * 2.0f); // P-waves are faster
                        sWaveProgress = Math.Min(1.0f, frameProgress * 1.5f); // S-waves are slower
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

                // P wave label with compressive wave info
                using (Font font = new Font("Arial", 8, FontStyle.Bold))
                using (SolidBrush textBrush = new SolidBrush(Color.DeepSkyBlue))
                {
                    g.DrawString("P", font, textBrush, pWaveX - 4, centerY - 40);
                    g.DrawString("(Compressive)", font, textBrush, pWaveX - 35, centerY - 52);
                }

                // Highlight midpoint when P-wave passes it
                if (pWaveProgress >= 0.5)
                {
                    using (Pen highlightPen = new Pen(Color.DeepSkyBlue, 2))
                    {
                        g.DrawEllipse(highlightPen, midX - 15, centerY - 15, 30, 30);

                        // Add a pulse effect at midpoint
                        if (pWaveProgress >= 0.5f && pWaveProgress <= 0.55f)
                        {
                            using (Pen pulsePen = new Pen(Color.DeepSkyBlue, 4))
                            {
                                g.DrawEllipse(pulsePen, midX - 12, centerY - 12, 24, 24);
                            }
                        }
                    }
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

                // S wave label with shear wave info
                using (Font font = new Font("Arial", 8, FontStyle.Bold))
                using (SolidBrush textBrush = new SolidBrush(Color.Crimson))
                {
                    g.DrawString("S", font, textBrush, sWaveX - 4, centerY + 25);
                    g.DrawString("(Shear)", font, textBrush, sWaveX - 20, centerY + 38);
                }

                // Highlight midpoint when S-wave passes it
                if (sWaveProgress >= 0.5)
                {
                    using (Pen highlightPen = new Pen(Color.Crimson, 2))
                    {
                        // Draw slightly smaller than P-wave highlight to show both
                        g.DrawEllipse(highlightPen, midX - 12, centerY - 12, 24, 24);

                        // Add a pulse effect at midpoint
                        if (sWaveProgress >= 0.5f && sWaveProgress <= 0.55f)
                        {
                            using (Pen pulsePen = new Pen(Color.Crimson, 4))
                            {
                                g.DrawEllipse(pulsePen, midX - 10, centerY - 10, 20, 20);
                            }
                        }
                    }
                }
            }

            // Draw a special indicator for the midpoint measurement
            if ((pWaveProgress > 0.5f || sWaveProgress > 0.5f) &&
                (pWaveProgress < 1.0f || sWaveProgress < 1.0f))
            {
                using (Font font = new Font("Arial", 7))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    string measureText = "Recording at midpoint...";
                    SizeF textSize = g.MeasureString(measureText, font);
                    float textX = midX - textSize.Width / 2;
                    g.DrawString(measureText, font, textBrush, textX, centerY + 25);
                }
            }

            // Draw distance information
            float distanceM = CalculateDistance(_tx, _ty, _tz, _rx, _ry, _rz) * _pixelSize;
            string distanceString = $"Distance: {distanceM:F3} m";

            using (Font font = new Font("Arial", 8))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                g.DrawString(distanceString, font, textBrush, x + margin, y + height - margin - 15);
                g.DrawString($"Midpoint at: {distanceM / 2:F3} m", font, textBrush, x + margin, y + height - margin + 2);
            }
        }
        /// <summary>
        /// Draw parallel P and S waves with additional midpoint data
        /// </summary>
        private void DrawParallelWavesWithMidpoint(Graphics g, int x, int y, int width, int height,
                                          float[] pSeries, float[] sSeries)
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

            // Find min/max values for both series with safe error handling
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

            // Set reasonable defaults if no significant data
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

            // Draw TX, MID, and RX markers on both paths
            using (SolidBrush txBrush = new SolidBrush(Color.Yellow))
            using (SolidBrush rxBrush = new SolidBrush(Color.Cyan))
            using (SolidBrush midBrush = new SolidBrush(Color.Lime))
            {
                // P-wave path
                g.FillEllipse(txBrush, pPathRect.X - 3, pPathRect.Y + pathHeight / 2 - 3, 6, 6);
                g.FillEllipse(rxBrush, pPathRect.Right - 3, pPathRect.Y + pathHeight / 2 - 3, 6, 6);

                // Add midpoint on P-wave path
                int pMidX = pPathRect.X + pPathRect.Width / 2;
                g.FillEllipse(midBrush, pMidX - 3, pPathRect.Y + pathHeight / 2 - 3, 6, 6);

                // S-wave path
                g.FillEllipse(txBrush, sPathRect.X - 3, sPathRect.Y + pathHeight / 2 - 3, 6, 6);
                g.FillEllipse(rxBrush, sPathRect.Right - 3, sPathRect.Y + pathHeight / 2 - 3, 6, 6);

                // Add midpoint on S-wave path
                int sMidX = sPathRect.X + sPathRect.Width / 2;
                g.FillEllipse(midBrush, sMidX - 3, sPathRect.Y + pathHeight / 2 - 3, 6, 6);

                // Add small labels for mid points
                using (Font smallFont = new Font("Arial", 7))
                {
                    g.DrawString("MID", smallFont, midBrush, pMidX - 8, pPathRect.Y - 12);
                    g.DrawString("MID", smallFont, midBrush, sMidX - 8, sPathRect.Y - 12);
                }
            }

            // Draw current wave position markers on paths
            lock (_dataLock)
            {
                if (_frames.Count > 0 && _currentFrameIndex >= 0 && _currentFrameIndex < _frames.Count)
                {
                    // Try to get progress from frame or use simple estimate
                    float pProgress = 0, sProgress = 0;

                    try
                    {
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
                            g.DrawString($"{pProgress:P0}", progressFont, pWaveBrush,
                                pMarkerX - 10, pPathRect.Y);

                            g.DrawString($"{sProgress:P0}", progressFont, sWaveBrush,
                                sMarkerX - 10, sPathRect.Y);
                        }

                        // Highlight midpoints when waves pass them
                        if (pProgress >= 0.5f)
                        {
                            using (Pen highlightPen = new Pen(Color.DeepSkyBlue, 2))
                            {
                                int midX = pPathRect.X + pPathRect.Width / 2;
                                g.DrawEllipse(highlightPen, midX - 5, pPathRect.Y + pathHeight / 2 - 5, 10, 10);
                            }
                        }

                        if (sProgress >= 0.5f)
                        {
                            using (Pen highlightPen = new Pen(Color.Crimson, 2))
                            {
                                int midX = sPathRect.X + sPathRect.Width / 2;
                                g.DrawEllipse(highlightPen, midX - 5, sPathRect.Y + pathHeight / 2 - 5, 10, 10);
                            }
                        }
                    }
                }
            }

            // P-wave area title with compressive wave info
            using (Font titleFont = new Font("Arial", 9, FontStyle.Bold))
            using (SolidBrush pBrush = new SolidBrush(Color.DeepSkyBlue))
            {
                g.DrawString("P-Wave (Compressive)", titleFont, pBrush, pArea.X + 5, pAreaY - 15);
            }

            // S-wave area title with shear wave info
            using (Font titleFont = new Font("Arial", 9, FontStyle.Bold))
            using (SolidBrush sBrush = new SolidBrush(Color.Crimson))
            {
                g.DrawString("S-Wave (Shear)", titleFont, sBrush, sArea.X + 5, sAreaY - 15);
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

            // Draw P-wave spatial series
            if (pSeries.Length > 1)
            {
                DrawWaveSeries(g, pSeries, pArea, minP, maxP, Color.DeepSkyBlue);

             
                int midIndex = pSeries.Length / 2;
                if (midIndex < pSeries.Length)
                {
                    float midX = pArea.X + (midIndex / (float)(pSeries.Length - 1)) * pArea.Width;

                    // Get midpoint value
                    float midpointValue = pSeries[midIndex];

                    // Normalize to fit in graph area
                    float normalizedVal = (midpointValue - minP) / (maxP - minP);
                    normalizedVal = Math.Max(0, Math.Min(1, normalizedVal));

                    float midY = pArea.Y + pArea.Height - normalizedVal * pArea.Height;

                    // Draw vertical line at midpoint
                    using (Pen midPointPen = new Pen(Color.Lime, 1.5f) { DashStyle = DashStyle.Dash })
                    {
                        g.DrawLine(midPointPen, midX, pArea.Y, midX, pArea.Y + pArea.Height);
                    }

                    // Draw marker at the actual data point
                    using (SolidBrush midPointBrush = new SolidBrush(Color.Lime))
                    {
                        g.FillEllipse(midPointBrush, midX - 4, midY - 4, 8, 8);

                        // Draw value label
                        using (Font valueFont = new Font("Arial", 7))
                        {
                            string valueText = $"{midpointValue:F3}";
                            g.DrawString(valueText, valueFont, midPointBrush, midX + 5, midY - 10);
                        }
                    }

                    // Draw "MID" label
                    using (Font labelFont = new Font("Arial", 7, FontStyle.Bold))
                    using (SolidBrush labelBrush = new SolidBrush(Color.Lime))
                    {
                        g.DrawString("MID", labelFont, labelBrush, midX - 10, pArea.Y + 2);
                    }
                }
            }

            // Draw S-wave spatial series
            if (sSeries.Length > 1)
            {
                DrawWaveSeries(g, sSeries, sArea, minS, maxS, Color.Crimson);

               
                int midIndex = sSeries.Length / 2;
                if (midIndex < sSeries.Length)
                {
                    float midX = sArea.X + (midIndex / (float)(sSeries.Length - 1)) * sArea.Width;

                    // Get midpoint value
                    float midpointValue = sSeries[midIndex];

                    // Normalize to fit in graph area
                    float normalizedVal = (midpointValue - minS) / (maxS - minS);
                    normalizedVal = Math.Max(0, Math.Min(1, normalizedVal));

                    float midY = sArea.Y + sArea.Height - normalizedVal * sArea.Height;

                    // Draw vertical line at midpoint
                    using (Pen midPointPen = new Pen(Color.Lime, 1.5f) { DashStyle = DashStyle.Dash })
                    {
                        g.DrawLine(midPointPen, midX, sArea.Y, midX, sArea.Y + sArea.Height);
                    }

                    // Draw marker at the actual data point
                    using (SolidBrush midPointBrush = new SolidBrush(Color.Lime))
                    {
                        g.FillEllipse(midPointBrush, midX - 4, midY - 4, 8, 8);

                        // Draw value label
                        using (Font valueFont = new Font("Arial", 7))
                        {
                            string valueText = $"{midpointValue:F3}";
                            g.DrawString(valueText, valueFont, midPointBrush, midX + 5, midY - 10);
                        }
                    }

                    // Draw "MID" label
                    using (Font labelFont = new Font("Arial", 7, FontStyle.Bold))
                    using (SolidBrush labelBrush = new SolidBrush(Color.Lime))
                    {
                        g.DrawString("MID", labelFont, labelBrush, midX - 10, sArea.Y + 2);
                    }
                }
            }

            // Draw Time and markers if we have travel times
            if (_simulationCompleted && _pWaveTravelTime > 0 && _sWaveTravelTime > 0)
            {
                DrawArrivalMarkers(g, pSeries, sSeries, pArea, sArea);
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

            // Draw amplification factor notice
            using (Font noteFont = new Font("Arial", 7, FontStyle.Italic))
            using (SolidBrush noteBrush = new SolidBrush(Color.Silver))
            {
                g.DrawString($"Amplification: {SIGNAL_AMPLIFICATION}x", noteFont, noteBrush,
                            x + width - 120, y + height - 15);
            }
        }
        /// <summary>
        /// Helper to draw wave series on a specified area
        /// </summary>
        private void DrawWaveSeries(Graphics g, float[] series, Rectangle area, float minVal, float maxVal, Color waveColor)
        {
            if (series.Length <= 1) return;

            int numPoints = Math.Min(series.Length, 1000);
            PointF[] points = new PointF[numPoints];

            for (int i = 0; i < numPoints; i++)
            {
                float t = i / (float)(numPoints - 1);
                float x = area.X + t * area.Width;

                // Calculate source index with bounds checking
                float sourceIdx = t * (series.Length - 1);
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

                // Normalize to fit in graph area
                float normalizedVal = (val - minVal) / (maxVal - minVal);
                // Clamp to prevent drawing outside the area
                normalizedVal = Math.Max(0, Math.Min(1, normalizedVal));

                float yPos = area.Y + area.Height - normalizedVal * area.Height;
                points[i] = new PointF(x, yPos);
            }

            // Draw filled area
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddLine(points[0].X, area.Y + area.Height / 2, points[0].X, points[0].Y);
                for (int i = 1; i < points.Length; i++)
                    path.AddLine(points[i - 1], points[i]);

                path.AddLine(points[points.Length - 1].X, points[points.Length - 1].Y,
                            points[points.Length - 1].X, area.Y + area.Height / 2);
                path.CloseFigure();

                using (LinearGradientBrush fillBrush = new LinearGradientBrush(
                    area, Color.FromArgb(80, waveColor), Color.FromArgb(10, Color.Black),
                    LinearGradientMode.Vertical))
                {
                    g.FillPath(fillBrush, path);
                }
            }

            // Draw the wave line
            using (Pen wavePen = new Pen(waveColor, 1.5f))
            {
                g.DrawLines(wavePen, points);
            }
        }


        /// <summary>
        /// Helper to highlight the midpoint on a wave series
        /// </summary>
        private void HighlightMidpoint(Graphics g, float[] series, Rectangle area, float minVal, float maxVal, Color color)
        {
            if (series.Length <= 1) return;

            // Calculate midpoint index
            int midIndex = series.Length / 2;

            // Safety check
            if (midIndex >= series.Length) return;

            // Calculate position
            float midX = area.X + (midIndex / (float)(series.Length - 1)) * area.Width;

            // Get midpoint value
            float midpointValue = series[midIndex];

            // Normalize to fit in graph area
            float normalizedVal = (midpointValue - minVal) / (maxVal - minVal);
            // Clamp to prevent drawing outside the area
            normalizedVal = Math.Max(0, Math.Min(1, normalizedVal));

            float midY = area.Y + area.Height - normalizedVal * area.Height;

            // Draw vertical line at midpoint
            using (Pen midPointPen = new Pen(Color.Lime, 1.5f) { DashStyle = DashStyle.Dash })
            {
                g.DrawLine(midPointPen, midX, area.Y, midX, area.Y + area.Height);
            }

            // Draw marker at the actual data point
            using (SolidBrush midPointBrush = new SolidBrush(Color.Lime))
            {
                g.FillEllipse(midPointBrush, midX - 4, midY - 4, 8, 8);

                // Draw value label
                using (Font valueFont = new Font("Arial", 7))
                {
                    string valueText = $"{midpointValue:F3}";
                    g.DrawString(valueText, valueFont, midPointBrush, midX + 5, midY - 10);
                }
            }

            // Draw "MID" label
            using (Font labelFont = new Font("Arial", 7, FontStyle.Bold))
            using (SolidBrush labelBrush = new SolidBrush(Color.Lime))
            {
                g.DrawString("MID", labelFont, labelBrush, midX - 10, area.Y + 2);
            }
        }

        /// <summary>
        /// Helper to draw arrival markers
        /// </summary>
        private void DrawArrivalMarkers(Graphics g, float[] pSeries, float[] sSeries, Rectangle pArea, Rectangle sArea)
        {
            // Calculate correct distance for time estimation
            float distanceM = CalculateDistance(_tx, _ty, _tz, _rx, _ry, _rz) * _pixelSize;

            // Log the distance to verify it's not zero
            Logger.Log($"[DrawArrivalMarkers] Using distance: {distanceM:F6} m");
            Logger.Log($"[DrawArrivalMarkers] TX: ({_tx}, {_ty}, {_tz}), RX: ({_rx}, {_ry}, {_rz})");
            Logger.Log($"[DrawArrivalMarkers] P-Wave Velocity: {_pWaveVelocity:F2} m/s, S-Wave Velocity: {_sWaveVelocity:F2} m/s");

            // Ensure we have non-zero values for calculations
            if (distanceM < 0.001f)
            {
                distanceM = 0.001f; // Minimum non-zero distance to avoid division by zero
                Logger.Log("[DrawArrivalMarkers] WARNING: Distance too small, using minimum value");
            }

            // P-wave arrival marker - with bounds checking
            if (pSeries.Length > 0 && _pWaveVelocity > 0)
            {
                float pArrivalX = pArea.X + (float)Math.Min(_pWaveTravelTime, pSeries.Length) / Math.Max(1, pSeries.Length) * pArea.Width;
                using (Pen arrivalPen = new Pen(Color.Yellow, 2))
                {
                    g.DrawLine(arrivalPen, pArrivalX, pArea.Y, pArrivalX, pArea.Y + pArea.Height);

                    using (Font font = new Font("Arial", 8))
                    using (SolidBrush brush = new SolidBrush(Color.Yellow))
                    {
                        g.DrawString("P Arrival", font, brush, pArrivalX - 25, pArea.Y + 2);

                        // Calculate time based on distance and velocity with error checking
                        double estimatedTime = 0;
                        if (_pWaveVelocity > 0.1) // Only calculate if velocity is reasonable
                        {
                            estimatedTime = distanceM / _pWaveVelocity;
                        }
                        else
                        {
                            // Fallback calculation based on step count and dt
                            estimatedTime = _pWaveTravelTime * 1e-6; // Assume dt is in microseconds
                        }

                        // Format with more appropriate precision and non-zero check
                        string timeText;
                        if (estimatedTime < 1e-9)
                        {
                            timeText = "t≈0.001 μs"; // Set minimum display time
                        }
                        else if (estimatedTime < 1e-6)
                        {
                            timeText = $"t≈{estimatedTime * 1e9:F3} ns";
                        }
                        else if (estimatedTime < 1e-3)
                        {
                            timeText = $"t≈{estimatedTime * 1e6:F3} μs";
                        }
                        else if (estimatedTime < 1)
                        {
                            timeText = $"t≈{estimatedTime * 1e3:F3} ms";
                        }
                        else
                        {
                            timeText = $"t≈{estimatedTime:F6} s";
                        }

                        g.DrawString(timeText, font, brush, pArrivalX - 30, pArea.Y + pArea.Height - 15);
                    }
                }
            }

            // S-wave arrival marker - with bounds checking
            if (sSeries.Length > 0 && _sWaveVelocity > 0)
            {
                float sArrivalX = sArea.X + (float)Math.Min(_sWaveTravelTime, sSeries.Length) / Math.Max(1, sSeries.Length) * sArea.Width;
                using (Pen arrivalPen = new Pen(Color.Yellow, 2))
                {
                    g.DrawLine(arrivalPen, sArrivalX, sArea.Y, sArrivalX, sArea.Y + sArea.Height);

                    using (Font font = new Font("Arial", 8))
                    using (SolidBrush brush = new SolidBrush(Color.Yellow))
                    {
                        g.DrawString("S Arrival", font, brush, sArrivalX - 25, sArea.Y + 2);

                        // Calculate time based on distance and velocity with error checking
                        double estimatedTime = 0;
                        if (_sWaveVelocity > 0.1) // Only calculate if velocity is reasonable
                        {
                            estimatedTime = distanceM / _sWaveVelocity;
                        }
                        else
                        {
                            // Fallback calculation based on step count and dt
                            estimatedTime = _sWaveTravelTime * 1e-6; // Assume dt is in microseconds
                        }

                        // Format with more appropriate precision
                        string timeText;
                        if (estimatedTime < 1e-9)
                        {
                            timeText = "t≈0.001 μs"; // Set minimum display time
                        }
                        else if (estimatedTime < 1e-6)
                        {
                            timeText = $"t≈{estimatedTime * 1e9:F3} ns";
                        }
                        else if (estimatedTime < 1e-3)
                        {
                            timeText = $"t≈{estimatedTime * 1e6:F3} μs";
                        }
                        else if (estimatedTime < 1)
                        {
                            timeText = $"t≈{estimatedTime * 1e3:F3} ms";
                        }
                        else
                        {
                            timeText = $"t≈{estimatedTime:F6} s";
                        }

                        g.DrawString(timeText, font, brush, sArrivalX - 30, sArea.Y + sArea.Height - 15);
                    }
                }
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
            float distance = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);

            // Log to verify
            Logger.Log($"[CalculateDistance] Raw pixels: {distance}, In meters: {distance * _pixelSize:F6}");

            return distance;
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
            if (_frames.Count == 0)
            {
                MessageBox.Show("No simulation frames available to export.",
                               "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Show export options dialog
            using (ExportAnimationDialog exportDialog = new ExportAnimationDialog())
            {
                if (exportDialog.ShowDialog() == DialogResult.OK)
                {
                    bool exportAsFrames = exportDialog.ExportAsFrames;
                    int fps = exportDialog.Fps;
                    int quality = exportDialog.Quality;
                    int durationSeconds = exportDialog.DurationSeconds;

                    try
                    {
                        if (exportAsFrames)
                        {
                            // Original frames export logic
                            FolderBrowserDialog dialog = new FolderBrowserDialog
                            {
                                Description = "Select folder to save animation frames"
                            };

                            if (dialog.ShowDialog() == DialogResult.OK)
                            {
                                string folderPath = dialog.SelectedPath;

                                // Show progress dialog
                                using (ProgressForm progress = new ProgressForm("Exporting Animation Frames"))
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
                        }
                        else
                        {
                            // Video export logic
                            SaveFileDialog dialog = new SaveFileDialog
                            {
                                Filter = "Windows Media Video|*.wmv",
                                Title = "Save Animation as WMV Video",
                                DefaultExt = "wmv"
                            };

                            if (dialog.ShowDialog() == DialogResult.OK)
                            {
                                string filePath = dialog.FileName;

                                // Calculate frames based on desired duration
                                int totalFrames = fps * durationSeconds;

                                // Show progress dialog
                                using (ProgressForm progress = new ProgressForm("Creating WMV Video"))
                                {
                                    progress.Show(this);

                                    // Export video in a background thread
                                    Task.Run(() =>
                                    {
                                        try
                                        {
                                            ExportAnimationAsWmv(filePath, fps, quality, totalFrames, progress);

                                            this.BeginInvoke((MethodInvoker)delegate
                                            {
                                                MessageBox.Show($"Video exported successfully!\nSaved to: {filePath}",
                                                              "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                                            });
                                        }
                                        catch (Exception ex)
                                        {
                                            this.BeginInvoke((MethodInvoker)delegate
                                            {
                                                MessageBox.Show($"Error exporting video: {ex.Message}\n{ex.StackTrace}",
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
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error preparing export: {ex.Message}",
                                      "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }
        /// <summary>
        /// Export animation as a WMV video file
        /// </summary>
        private void ExportAnimationAsWmv(string filePath, int fps, int quality, int totalFrames, ProgressForm progress)
        {
            // Add initial diagnostic logging
            Logger.Log($"[ExportAnimationAsWmv] Starting export - using cache: {usingCachedFrames}");
            Logger.Log($"[ExportAnimationAsWmv] Cache manager exists: {cacheManager != null}");

            if (usingCachedFrames && cacheManager != null)
            {
                Logger.Log($"[ExportAnimationAsWmv] Redirecting to cached export method");
                ExportCachedAnimationAsWmv(filePath, fps, quality, totalFrames, progress);
                return;
            }

            lock (_dataLock)
            {
                int frameCount = _frames.Count;

                // Log frames status
                Logger.Log($"[ExportAnimationAsWmv] Frame count: {frameCount}");
                Logger.Log($"[ExportAnimationAsWmv] Current frame index: {_currentFrameIndex}");

                if (frameCount == 0)
                {
                    throw new InvalidOperationException("No frames available to export");
                }

                // Log first few frames to verify data exists
                for (int i = 0; i < Math.Min(3, frameCount); i++)
                {
                    Logger.Log($"[ExportAnimationAsWmv] Frame {i}: TimeStep={_frames[i].TimeStep}, PWaveValue={_frames[i].PWaveValue}");
                }

                Logger.Log($"[ExportAnimationAsWmv] Exporting {frameCount} frames as {totalFrames} output frames at {fps} fps");

                int currentIndex = _currentFrameIndex;
                int width = _mainPanel.Width;
                int height = _mainPanel.Height;

                try
                {
                    WmvWriter wmvWriter = new WmvWriter(filePath, width, height, fps, quality);
                    double frameStep = (double)(frameCount - 1) / (totalFrames - 1);

                    for (int i = 0; i < totalFrames; i++)
                    {
                        int sourceFrameIndex = (int)Math.Round(i * frameStep);
                        sourceFrameIndex = Math.Max(0, Math.Min(frameCount - 1, sourceFrameIndex));

                        Logger.Log($"[ExportAnimationAsWmv] Processing frame {i}/{totalFrames}, source index: {sourceFrameIndex}");

                        // Update progress on UI thread
                        int progressPercent = (i + 1) * 100 / totalFrames;
                        this.BeginInvoke((MethodInvoker)delegate
                        {
                            progress.UpdateProgress(progressPercent);
                        });

                        // Update to this frame
                        _currentFrameIndex = sourceFrameIndex;

                        // Log before attempting UI update
                        Logger.Log($"[ExportAnimationAsWmv] Attempting UI update for frame {i}");

                        // Update visualization on UI thread and wait
                        using (ManualResetEvent resetEvent = new ManualResetEvent(false))
                        {
                            this.BeginInvoke((MethodInvoker)delegate
                            {
                                try
                                {
                                    Logger.Log($"[ExportAnimationAsWmv] Inside UI thread for frame {i}");
                                    UpdateVisualization();
                                    Logger.Log($"[ExportAnimationAsWmv] UpdateVisualization completed for frame {i}");
                                }
                                catch (Exception updateEx)
                                {
                                    Logger.Log($"[ExportAnimationAsWmv] Error in UpdateVisualization: {updateEx.Message}");
                                    Logger.Log($"[ExportAnimationAsWmv] Stack trace: {updateEx.StackTrace}");
                                }
                                finally
                                {
                                    resetEvent.Set();
                                }
                            });

                            if (!resetEvent.WaitOne(5000)) // 5 second timeout
                            {
                                Logger.Log($"[ExportAnimationAsWmv] Warning: Visualization update timeout at frame {i}");
                                Logger.Log($"[ExportAnimationAsWmv] Frame data exists: {_frames[sourceFrameIndex] != null}");

                                // Log more details about the frame
                                if (_frames[sourceFrameIndex] != null)
                                {
                                    var frame = _frames[sourceFrameIndex];
                                    Logger.Log($"[ExportAnimationAsWmv] Frame {sourceFrameIndex} details: TimeStep={frame.TimeStep}, PWaveTimeSeries length={frame.PWaveTimeSeries?.Length}");
                                }
                            }
                            else
                            {
                                Logger.Log($"[ExportAnimationAsWmv] Successfully updated frame {i}");
                            }
                        }

                        // Create combined image
                        Bitmap combined = CombinePanelsForExport();
                        wmvWriter.AddFrame(combined);
                        combined.Dispose();

                        if (i % 10 == 0)
                        {
                            Logger.Log($"[ExportAnimationAsWmv] Progress: {progressPercent}% (frame {i}/{totalFrames})");
                        }
                    }

                    wmvWriter.Close();
                    Logger.Log($"[ExportAnimationAsWmv] Export completed successfully");
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ExportAnimationAsWmv] Error during export: {ex.Message}");
                    Logger.Log($"[ExportAnimationAsWmv] Stack trace: {ex.StackTrace}");
                    throw;
                }
                finally
                {
                    _currentFrameIndex = currentIndex;
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        UpdateVisualization();
                    });
                }
            }
        }

        private void RenderFrameToExportBitmap(SimulationFrame frame, Bitmap targetBitmap)
        {
            using (Graphics g = Graphics.FromImage(targetBitmap))
            {
                g.Clear(Color.Black);

                // Calculate panel layout
                int rowCount = 2;
                int colCount = 3;
                int panelWidth = targetBitmap.Width / colCount;
                int panelHeight = targetBitmap.Height / rowCount;

                // Create temporary bitmaps for each panel
                Bitmap[] tempPanelBitmaps = new Bitmap[6];

                try
                {
                    // Create temporary bitmaps with the correct size
                    for (int i = 0; i < 6; i++)
                    {
                        tempPanelBitmaps[i] = new Bitmap(Math.Max(1, panelWidth), Math.Max(1, panelHeight));
                    }

                    // Render each panel to its temporary bitmap
                    using (Graphics g0 = Graphics.FromImage(tempPanelBitmaps[0]))
                    {
                        DrawTimeSeries(g0, frame.PWaveTimeSeries, 0);
                    }

                    using (Graphics g1 = Graphics.FromImage(tempPanelBitmaps[1]))
                    {
                        DrawTimeSeries(g1, frame.SWaveTimeSeries, 1);
                    }

                    using (Graphics g2 = Graphics.FromImage(tempPanelBitmaps[2]))
                    {
                        DrawHeatmap(g2, frame.VelocityTomography, 2);
                    }

                    using (Graphics g3 = Graphics.FromImage(tempPanelBitmaps[3]))
                    {
                        DrawHeatmap(g3, frame.WavefieldCrossSection, 3);
                    }

                    using (Graphics g4 = Graphics.FromImage(tempPanelBitmaps[4]))
                    {
                        DrawCombinedWaveVisualization(g4, frame);
                    }

                    using (Graphics g5 = Graphics.FromImage(tempPanelBitmaps[5]))
                    {
                        DrawInformationPanel(g5);
                    }

                    // Now composite all panels into the target bitmap
                    for (int i = 0; i < 6; i++)
                    {
                        int row = i / colCount;
                        int col = i % colCount;
                        int x = col * panelWidth;
                        int y = row * panelHeight;

                        // Draw the panel
                        g.DrawImage(tempPanelBitmaps[i], x, y, panelWidth, panelHeight);

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

                        // Draw borders
                        using (Pen borderPen = new Pen(Color.Gray, 1))
                        {
                            g.DrawRectangle(borderPen, x, y, panelWidth - 1, panelHeight - 1);
                        }
                    }

                    // Draw step info at the bottom
                    using (Font font = new Font("Arial", 12, FontStyle.Bold))
                    using (Brush brush = new SolidBrush(Color.White))
                    using (Brush bgBrush = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                    {
                        string info = $"Step: {frame.TimeStep}";
                        if (_simulationCompleted)
                        {
                            info += $"   P-Wave: {_pWaveVelocity:F2} m/s   S-Wave: {_sWaveVelocity:F2} m/s";
                        }

                        SizeF textSize = g.MeasureString(info, font);
                        float textX = (targetBitmap.Width - textSize.Width) / 2;
                        float textY = targetBitmap.Height - textSize.Height - 10;

                        g.FillRectangle(bgBrush, textX - 10, textY - 5, textSize.Width + 20, textSize.Height + 10);
                        g.DrawString(info, font, brush, textX, textY);
                    }
                }
                finally
                {
                    // Clean up temporary bitmaps
                    for (int i = 0; i < 6; i++)
                    {
                        tempPanelBitmaps[i]?.Dispose();
                    }
                }
            }
        }
        private void ExportCachedAnimationAsWmv(string filePath, int fps, int quality, int totalFrames, ProgressForm progress)
        {
            lock (_dataLock)
            {
                int frameCount = _frames.Count;
                if (frameCount == 0)
                {
                    throw new InvalidOperationException("No frames available to export");
                }

                Logger.Log($"[Visualizer] Exporting cached animation: {frameCount} frames at {fps} fps");

                int currentIndex = _currentFrameIndex;
                int width = _mainPanel.Width;
                int height = _mainPanel.Height;

                try
                {
                    WmvWriter wmvWriter = new WmvWriter(filePath, width, height, fps, quality);

                    // Calculate frame distribution
                    double frameStep = (double)(frameCount - 1) / (totalFrames - 1);

                    // Create a bitmap for rendering
                    Bitmap exportBitmap = new Bitmap(width, height);

                    for (int i = 0; i < totalFrames; i++)
                    {
                        // Calculate source frame index
                        int sourceFrameIndex = (int)Math.Round(i * frameStep);
                        sourceFrameIndex = Math.Max(0, Math.Min(frameCount - 1, sourceFrameIndex));

                        // Update progress
                        int progressPercent = (i + 1) * 100 / totalFrames;
                        this.BeginInvoke((MethodInvoker)delegate
                        {
                            progress.UpdateProgress(progressPercent);
                        });

                        // Load the cached frame
                        currentCachedFrame = cacheManager.LoadFrame(sourceFrameIndex);
                        if (currentCachedFrame == null)
                        {
                            Logger.Log($"[Visualizer] Failed to load cached frame {sourceFrameIndex}");
                            continue;
                        }

                        // Convert cached frame to visualization frame
                        var frame = new SimulationFrame
                        {
                            TimeStep = currentCachedFrame.TimeStep,
                            PWaveValue = currentCachedFrame.PWaveValue,
                            SWaveValue = currentCachedFrame.SWaveValue,
                            PWavePathProgress = currentCachedFrame.PWavePathProgress,
                            SWavePathProgress = currentCachedFrame.SWavePathProgress,
                            VelocityTomography = currentCachedFrame.Tomography,
                            WavefieldCrossSection = currentCachedFrame.CrossSection,
                            PWaveTimeSeries = currentCachedFrame.PWaveTimeSeries ?? new float[1],
                            SWaveTimeSeries = currentCachedFrame.SWaveTimeSeries ?? new float[1],
                            PWaveSpatialSeries = ExtractSpatialSeries(currentCachedFrame.VX, true),
                            SWaveSpatialSeries = ExtractSpatialSeries(currentCachedFrame.VY, false)
                        };

                        // Render the frame directly
                        RenderFrameToExportBitmap(frame, exportBitmap);

                        // Add frame to WMV
                        wmvWriter.AddFrame(exportBitmap);

                        if (i % 10 == 0)
                        {
                            Logger.Log($"[Visualizer] Export progress: {progressPercent}%");
                        }
                    }

                    // Clean up
                    exportBitmap.Dispose();

                    // Finalize the WMV file
                    wmvWriter.Close();

                    Logger.Log($"[Visualizer] Export completed successfully");
                }
                finally
                {
                    // Restore original frame
                    _currentFrameIndex = currentIndex;
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        UpdateVisualization();
                    });
                }
            }
        }
        private void AddCacheMenuItems()
        {
            // Create File menu if it doesn't exist
            ToolStripMenuItem fileMenu = null;
            foreach (ToolStripItem item in this.MainMenuStrip.Items)
            {
                if (item is ToolStripMenuItem menuItem && menuItem.Text == "&File")
                {
                    fileMenu = menuItem;
                    break;
                }
            }

            if (fileMenu == null)
            {
                fileMenu = new ToolStripMenuItem("&File");
                this.MainMenuStrip.Items.Insert(0, fileMenu);
            }

            // Add separator if there are existing items
            if (fileMenu.DropDownItems.Count > 0)
            {
                fileMenu.DropDownItems.Add(new ToolStripSeparator());
            }

            // Add Load Cached Simulation menu item
            var loadCacheItem = new ToolStripMenuItem("Load Cached Simulation...");
            loadCacheItem.ShortcutKeys = Keys.Control | Keys.O;
            loadCacheItem.Click += (s, e) =>
            {
                using (var dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "Select cache directory containing simulation frames";
                    dialog.ShowNewFolderButton = false;

                    if (dialog.ShowDialog() == DialogResult.OK)
                    {
                        LoadFromCache(dialog.SelectedPath);
                    }
                }
            };
            fileMenu.DropDownItems.Add(loadCacheItem);

            // Add Export Cache Info menu item
            var exportCacheInfoItem = new ToolStripMenuItem("Export Cache Info...");
            exportCacheInfoItem.Click += (s, e) =>
            {
                if (!usingCachedFrames || cacheManager == null)
                {
                    MessageBox.Show("No cached simulation is currently loaded.",
                                   "No Cache", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                using (var dialog = new SaveFileDialog())
                {
                    dialog.Filter = "Text Files|*.txt";
                    dialog.FileName = "cache_info.txt";

                    if (dialog.ShowDialog() == DialogResult.OK)
                    {
                        try
                        {
                            using (var writer = new StreamWriter(dialog.FileName))
                            {
                                writer.WriteLine($"Cache Directory: {cacheManager.CacheDirectory}");
                                writer.WriteLine($"Frame Count: {cacheManager.FrameCount}");
                                writer.WriteLine($"Simulation Dimensions: {_width}x{_height}x{_depth}");
                                writer.WriteLine($"Created: {Directory.GetCreationTime(cacheManager.CacheDirectory)}");
                            }

                            MessageBox.Show("Cache information exported successfully.",
                                           "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Error exporting cache info: {ex.Message}",
                                           "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                }
            };
            fileMenu.DropDownItems.Add(exportCacheInfoItem);
        }
        private void CreateMenuStrip()
        {
            this.MainMenuStrip = new MenuStrip();
            this.MainMenuStrip.BackColor = Color.FromArgb(40, 40, 40);
            this.MainMenuStrip.ForeColor = Color.White;
            this.Controls.Add(this.MainMenuStrip);

            AddCacheMenuItems();
        }

        public void LoadSavedSimulationData(
    double pWaveVelocity, double sWaveVelocity, double vpVsRatio,
    int pWaveTravelTime, int sWaveTravelTime, int totalTimeSteps,
    double[,,] vxData, double[,,] vyData, double[,,] vzData)
        {
            // Store the simulation results
            _simulationCompleted = true;
            _pWaveVelocity = pWaveVelocity;
            _sWaveVelocity = sWaveVelocity;
            _vpVsRatio = vpVsRatio;
            _pWaveTravelTime = pWaveTravelTime;
            _sWaveTravelTime = sWaveTravelTime;
            _totalTimeSteps = totalTimeSteps;
            _simulationStatus = "Completed";

            // Create a frame with calculated data
            var frame = new SimulationFrame
            {
                TimeStep = sWaveTravelTime,
                PWavePathProgress = 1.0f,
                SWavePathProgress = 1.0f
            };

            // Generate time series based on arrival times
            int seriesLength = Math.Max(100, sWaveTravelTime * 2);
            frame.PWaveTimeSeries = new float[seriesLength];
            frame.SWaveTimeSeries = new float[seriesLength];

            // Create wave patterns at arrival times
            for (int i = 0; i < seriesLength; i++)
            {
                // Background noise
                frame.PWaveTimeSeries[i] = (float)(new Random(i).NextDouble() - 0.5) * 0.02f;
                frame.SWaveTimeSeries[i] = (float)(new Random(i + 100).NextDouble() - 0.5) * 0.02f;

                // P-wave arrival peak
                if (i >= pWaveTravelTime && i < pWaveTravelTime + 30)
                {
                    float envelope = (float)Math.Exp(-(i - pWaveTravelTime) * 0.1);
                    frame.PWaveTimeSeries[i] = (float)(Math.Sin((i - pWaveTravelTime) * 0.5) * envelope);
                }

                // S-wave arrival peak
                if (i >= sWaveTravelTime && i < sWaveTravelTime + 40)
                {
                    float envelope = (float)Math.Exp(-(i - sWaveTravelTime) * 0.08);
                    frame.SWaveTimeSeries[i] = (float)(Math.Sin((i - sWaveTravelTime) * 0.3) * envelope);
                }
            }

            // Store sample values
            frame.PWaveValue = frame.PWaveTimeSeries[Math.Min(pWaveTravelTime, frame.PWaveTimeSeries.Length - 1)];
            frame.SWaveValue = frame.SWaveTimeSeries[Math.Min(sWaveTravelTime, frame.SWaveTimeSeries.Length - 1)];

            // Calculate tomography and cross-section views
            frame.VelocityTomography = ComputeVelocityTomography(_tx, _ty, _tz, _rx, _ry, _rz, vxData, vyData, vzData);
            frame.WavefieldCrossSection = ExtractCrossSection(_tx, _ty, _tz, _rx, _ry, _rz, vxData, vyData, vzData);

            // Add the frame
            lock (_dataLock)
            {
                _frames.Clear();
                _frames.Add(frame);
                _currentFrameIndex = 0;
            }

            // Update UI
            this.BeginInvoke((MethodInvoker)delegate {
                this.Text = $"Acoustic Simulation Results - P-Wave: {_pWaveVelocity:F2} m/s, S-Wave: {_sWaveVelocity:F2} m/s";
                if (_timelineTrackBar != null)
                    _timelineTrackBar.Maximum = 0;
                UpdateVisualization();
            });
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

                    // Update detach button position (only for panels 0-4)
                    if (i < 5 && _detachButtons[i] != null)
                    {
                        _detachButtons[i].Location = new Point(8, _subPanels[i].Height - 36);
                        _detachButtons[i].BringToFront(); // Ensure button stays on top
                    }
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
            // Stop timers first
            _playbackTimer.Stop();
            _uiUpdateTimer.Stop();

            // Clear images from PictureBoxes first
            for (int i = 0; i < 6; i++)
            {
                if (_pictureBoxes[i] != null)
                {
                    _pictureBoxes[i].Image = null;
                }
            }

            // Close any detached windows
            for (int i = 0; i < 6; i++)
            {
                if (_isPanelDetached[i] && _detachedWindows[i] != null && !_detachedWindows[i].IsDisposed)
                {
                    try
                    {
                        // First find and clear the PictureBox in the detached window
                        foreach (Control control in _detachedWindows[i].Controls)
                        {
                            if (control is PictureBox pictureBox)
                            {
                                Bitmap oldImage = pictureBox.Image as Bitmap;
                                pictureBox.Image = null;

                                if (oldImage != null)
                                {
                                    try
                                    {
                                        oldImage.Dispose();
                                    }
                                    catch
                                    {
                                        // Ignore errors while disposing the image
                                    }
                                }
                                break;
                            }
                        }

                        // Now we can safely close the window
                        _detachedWindows[i].Close();
                        _detachedWindows[i].Dispose();
                    }
                    catch (Exception ex)
                    {
                        // Log the error but continue
                        Logger.Log($"[FormClosing] Error while closing detached window: {ex.Message}");
                    }
                    _detachedWindows[i] = null;
                }
            }

            // Dispose of bitmaps
            for (int i = 0; i < 6; i++)
            {
                try
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

                    // Clean up detach button icons
                    if (i < 5 && _detachButtons[i] != null && _detachButtons[i].Image != null)
                    {
                        _detachButtons[i].Image.Dispose();
                        _detachButtons[i].Image = null;
                    }
                }
                catch (Exception ex)
                {
                    // Log the error but continue
                    Logger.Log($"[FormClosing] Error while disposing bitmap {i}: {ex.Message}");
                }
            }

            // Dispose of icons
            if (_playIcon != null) { _playIcon.Dispose(); _playIcon = null; }
            if (_pauseIcon != null) { _pauseIcon.Dispose(); _pauseIcon = null; }
            if (_exportIcon != null) { _exportIcon.Dispose(); _exportIcon = null; }
            if (_animationIcon != null) { _animationIcon.Dispose(); _animationIcon = null; }
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
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                cacheManager?.Dispose();

             
            }

            base.Dispose(disposing);
        }

        #endregion

    }
}
