using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS
{
    /// <summary>
    /// Visualizes 2-D and 3-D view of damage / stress / strain data with
    /// support for sliders and color selection.
    /// </summary>
    public sealed class FailurePointVisualizer : IDisposable
    {
        #region Public API and Enums
        private int _wireframeCoarseness = 2; // Default value

        private TrackBar _wireframeCoarsenessSlider;
        public enum ColorMapMode { Damage, Stress, Strain }

        public struct Point3D
        {
            public int X, Y, Z;
            public Point3D(int x, int y, int z) { X = x; Y = y; Z = z; }
        }

        // Constructor with standard parameters
        public FailurePointVisualizer(int w, int h, int d, byte materialId = 0)
        {
            try
            {
                // Check for valid dimensions
                _w = Math.Max(1, w);
                _h = Math.Max(1, h);
                _d = Math.Max(1, d);
                _matId = materialId;

                // Check if we need to automatically downsample due to volume size
                DetermineDownsamplingFactor();

                // Initialize color caches
                InitializeColorCaches();

                Logger.Log($"[FailurePointVisualizer] Created with dimensions {_w}x{_h}x{_d}, material ID {materialId}, downsample {_downsampleFactor}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error in constructor: {ex.Message}");
            }
        }
        public void SetWireframeCoarseness(int coarseness)
        {
            _wireframeCoarseness = Math.Max(1, Math.Min(10, coarseness));
            InvalidateVisualization();
        }
        public void InitializeControls(TrackBar tbX, TrackBar tbY, TrackBar tbZ, ComboBox cbo, PictureBox host)
        {
            // Call the full version with null for wireframe slider
            InitializeControls(tbX, tbY, tbZ, cbo, host, null);
        }
        // Initialize trackbar controls
        public void InitializeControls(TrackBar tbX, TrackBar tbY, TrackBar tbZ, PictureBox host)
        {
            InitializeControls(tbX, tbY, tbZ, null, host);
        }

        // Initialize trackbars only
        public void InitializeControls(TrackBar tbX, TrackBar tbY, TrackBar tbZ)
        {
            InitializeControls(tbX, tbY, tbZ, null, null);
        }

        // Complete initialization with all controls
        public void InitializeControls(TrackBar tbX, TrackBar tbY, TrackBar tbZ, ComboBox cbo, PictureBox host, TrackBar wireframeSlider)
        {
            try
            {
                _tbX = tbX; _tbY = tbY; _tbZ = tbZ; _host = host;

                if (_tbX != null)
                {
                    _tbX.Minimum = 0;
                    _tbX.Maximum = Math.Max(1, _w - 1);
                    _tbX.Value = _w / 2;
                    _tbX.Scroll += (_, __) => {
                        _axis = Axis.X;
                        InvalidateVisualization();
                    };
                }

                if (_tbY != null)
                {
                    _tbY.Minimum = 0;
                    _tbY.Maximum = Math.Max(1, _h - 1);
                    _tbY.Value = _h / 2;
                    _tbY.Scroll += (_, __) => {
                        _axis = Axis.Y;
                        InvalidateVisualization();
                    };
                }

                if (_tbZ != null)
                {
                    _tbZ.Minimum = 0;
                    _tbZ.Maximum = Math.Max(1, _d - 1);
                    _tbZ.Value = _d / 2;
                    _tbZ.Scroll += (_, __) => {
                        _axis = Axis.Z;
                        InvalidateVisualization();
                    };
                }

                if (cbo != null)
                {
                    cbo.Items.Clear();
                    cbo.Items.AddRange(new object[] { "Damage", "Stress", "Strain" });
                    cbo.SelectedIndex = 0;
                    cbo.SelectedIndexChanged += (_, __) =>
                    {
                        _mode = (ColorMapMode)cbo.SelectedIndex;
                        InvalidateVisualization();
                    };
                }

                // Initialize wireframe coarseness slider
                if (wireframeSlider != null)
                {
                    _wireframeCoarsenessSlider = wireframeSlider;
                    _wireframeCoarsenessSlider.Minimum = 1;
                    _wireframeCoarsenessSlider.Maximum = 10;
                    _wireframeCoarsenessSlider.Value = _wireframeCoarseness;
                    _wireframeCoarsenessSlider.Scroll += (_, __) => {
                        _wireframeCoarseness = _wireframeCoarsenessSlider.Value;
                        InvalidateVisualization();
                    };
                }

                if (_host != null)
                {
                    _host.Paint += Host_Paint;
                    _host.MouseDown += Host_MouseDown;
                    _host.MouseMove += Host_MouseMove;
                    _host.MouseUp += Host_MouseUp;
                    _host.MouseWheel += Host_MouseWheel;
                    _host.Resize += (_, __) => InvalidateVisualization();
                }

                Logger.Log("[FailurePointVisualizer] Controls initialized");
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error initializing controls: {ex.Message}");
            }
        }

        // Set view parameters for 3D visualization
        public void SetViewParameters(float rotX, float rotY, float zoom, PointF pan)
        {
            _rotX = rotX; _rotY = rotY; _zoom = zoom; _pan = pan;
            InvalidateVisualization();
        }

        // Set active slice positions
        public void SetSlicePositions(int x, int y, int z)
        {
            if (_tbX != null) _tbX.Value = Clamp(x, 0, _w - 1);
            if (_tbY != null) _tbY.Value = Clamp(y, 0, _h - 1);
            if (_tbZ != null) _tbZ.Value = Clamp(z, 0, _d - 1);
            InvalidateVisualization();
        }

        // Get current slice positions
        public int GetSliceX() => _tbX?.Value ?? _w / 2;
        public int GetSliceY() => _tbY?.Value ?? _h / 2;
        public int GetSliceZ() => _tbZ?.Value ?? _d / 2;

        // Data setters with different signatures for compatibility
        public void SetData(ILabelVolumeData lbl, double[,,] dmg) =>
            SetData(lbl, dmg, null, null);

        public void SetData(ILabelVolumeData lbl, double[,,] dmg, float[,,] strain) =>
            SetData(lbl, dmg, null, strain);

        // Main SetData method
        public void SetData(ILabelVolumeData lbl, double[,,] dmg,
                        float[,,] stress, float[,,] strain)
        {
            try
            {
                // Check if dimensions match or if we need to adjust
                if (dmg != null && (dmg.GetLength(0) != _w || dmg.GetLength(1) != _h || dmg.GetLength(2) != _d))
                {
                    // If dimensions don't match, we need to decide:
                    // 1. Use the new dimensions and reinitialize
                    // 2. Or resample data to match current dimensions
                    // For simplicity and to ensure UI stays consistent, we'll use approach #2
                    Logger.Log($"[FailurePointVisualizer] Data dimensions ({dmg.GetLength(0)}x{dmg.GetLength(1)}x{dmg.GetLength(2)}) " +
                              $"don't match expected ({_w}x{_h}x{_d}) - will resample");

                    // We'll use the original data but resample on access
                    _originalDimensions = new int[] { dmg.GetLength(0), dmg.GetLength(1), dmg.GetLength(2) };
                    _needsResampling = true;
                }
                else
                {
                    _needsResampling = false;
                    _originalDimensions = null;
                }

                // Store data references
                _labels = lbl;
                _dmg = dmg;
                _stress = stress;
                _strain = strain;

                // Clear cached values
                _cachedMaxStress = 0;
                _cachedMaxStrain = 0;
                _cachedMaxDamage = 0;

                // If we have damage data but no stress/strain, create computed versions
                if (_dmg != null && (_stress == null || _strain == null))
                {
                    ComputeStressAndStrainFields();
                }

                // Initialize color caches if needed
                if (!_colorCachesInitialized)
                {
                    InitializeColorCaches();
                }

                // Calculate data ranges using sampling for better performance
                CalculateDataRanges();

                // Invalidate any cached visualization
                InvalidateVisualization();

                Logger.Log($"[FailurePointVisualizer] Data set - Damage:{_dmg != null}, Stress:{_stress != null}, Strain:{_strain != null}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error setting data: {ex.Message}");
            }
        }

        // Set failure point by coordinates struct
        public void SetFailurePoint(bool detected, Point3D vox) =>
            SetFailurePoint(detected, vox.X, vox.Y, vox.Z);

        // Set failure point by explicit parameters
        public void SetFailurePoint(bool detected, int x, int y, int z)
        {
            try
            {
                _failureDetected = detected;
                _failX = x; _failY = y; _failZ = z;

                if (detected)
                {
                    Logger.Log($"[FailurePointVisualizer] Failure point set at ({x},{y},{z})");
                    // Auto-navigate to failure point
                    SetSlicePositions(x, y, z);
                }
                else
                {
                    Logger.Log("[FailurePointVisualizer] No failure point detected");
                }

                InvalidateVisualization();
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error setting failure point: {ex.Message}");
            }
        }

        // Set failure point with nullable params for compatibility
        public void SetFailurePoint(bool detected, int? x, int? y, int? z)
        {
            SetFailurePoint(detected,
                        x.GetValueOrDefault(-1),
                        y.GetValueOrDefault(-1),
                        z.GetValueOrDefault(-1));
        }

        // Toggle volume visualization
        public void SetShowVolume(bool show)
        {
            _showVolume = show;
            InvalidateVisualization();
        }

        // Create visualization for external use
        public Bitmap CreateVisualization(int width, int height, ColorMapMode mode)
        {
            try
            {
                // Make sure dimensions are valid
                width = Math.Max(width, 10);
                height = Math.Max(height, 10);

                // Check memory availability
                EnsureMemoryAvailable();

                // Create the visualization bitmap
                Bitmap result = new Bitmap(width, height, PixelFormat.Format32bppArgb);

                using (Graphics g = Graphics.FromImage(result))
                {
                    g.Clear(Color.FromArgb(40, 40, 40));
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                    // Create layout for visualization
                    int controlHeight = 50;
                    int viewportHeight = height - controlHeight;

                    // Draw slice view
                    Rectangle sliceRect = new Rectangle(10, controlHeight, (int)(width * 0.6f), viewportHeight - 20);
                    DrawSlice(g, sliceRect, mode);

                    // Draw 3D view
                    Rectangle view3dRect = new Rectangle((int)(width * 0.6f) + 20, controlHeight,
                                                     (int)(width * 0.4f) - 30, viewportHeight - 20);
                    Draw3DView(g, view3dRect, mode);

                    // Draw controls area
                    DrawControlsInfo(g, new Rectangle(10, 10, width - 20, controlHeight - 10));

                    // Draw color legend
                    DrawColorLegend(g, 10, height - 30, 200, 20, mode);

                    // Draw failure point if detected
                    if (_failureDetected)
                    {
                        // Draw failure point marker in 3D view
                        DrawFailurePointIn3DView(g, view3dRect);

                        // If failure point is on the current slice, draw marker
                        if ((_axis == Axis.X && _failX == GetSliceX()) ||
                            (_axis == Axis.Y && _failY == GetSliceY()) ||
                            (_axis == Axis.Z && _failZ == GetSliceZ()))
                        {
                            DrawFailurePointInSlice(g, sliceRect);
                        }
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error creating visualization: {ex.Message}");

                // Return a simple error bitmap
                Bitmap errorBmp = new Bitmap(width, height);
                using (Graphics g = Graphics.FromImage(errorBmp))
                {
                    g.Clear(Color.FromArgb(40, 40, 40));
                    using (Font font = new Font("Arial", 10))
                    using (SolidBrush brush = new SolidBrush(Color.White))
                    {
                        string message = $"Error creating visualization: {ex.Message}";
                        g.DrawString(message, font, brush, 10, 10);
                    }
                }
                return errorBmp;
            }
        }

        // Clean up resources
        public void Dispose()
        {
            try
            {
                // Dispose cached bitmaps
                if (_failurePointCache != null)
                {
                    _failurePointCache.Dispose();
                    _failurePointCache = null;
                }

                foreach (var bitmap in _legendBitmapCache.Values)
                {
                    bitmap?.Dispose();
                }
                _legendBitmapCache.Clear();

                // Remove event handlers
                if (_host != null)
                {
                    _host.Paint -= Host_Paint;
                    _host.MouseDown -= Host_MouseDown;
                    _host.MouseMove -= Host_MouseMove;
                    _host.MouseUp -= Host_MouseUp;
                    _host.MouseWheel -= Host_MouseWheel;
                }

                // Clear data references
                _dmg = null;
                _stress = null;
                _strain = null;
                _labels = null;

                Logger.Log("[FailurePointVisualizer] Resources disposed");
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error in Dispose: {ex.Message}");
            }
        }

        #endregion

        #region Private Constants and Fields

        // Constants for performance tuning
        private static class PerformanceTuning
        {
            // Automatically downsample when volume exceeds this size
            public const long MAX_VOLUME_SIZE = 50_000_000; // Reduced to 50 million voxels

            // Maximum number of points to render for 3D view
            public const int MAX_POINTS_3D = 8000; // Reduced to 8000 points for better performance

            // Maximum slice resolution before downsampling
            public const int MAX_SLICE_RESOLUTION = 600; // Reduced to 600 for better performance

            // Memory limit for bitmap processing in bytes
            public const long MEMORY_LIMIT = 300 * 1024 * 1024; // Reduced to 300 MB

            // Timeout for visualization operations in milliseconds
            public const int VISUALIZATION_TIMEOUT = 3000; // Reduced to 3 seconds

            // Maximum number of wireframe lines to draw
            public const int MAX_WIREFRAME_LINES = 10000;
        }

        // Dimensions and data
        private readonly int _w, _h, _d;
        private readonly byte _matId;
        private double[,,] _dmg;
        private float[,,] _stress, _strain;
        private ILabelVolumeData _labels;
        private int _downsampleFactor = 1;
        private bool _needsResampling = false;
        private int[] _originalDimensions = null;

        // UI Controls
        private TrackBar _tbX, _tbY, _tbZ;
        private PictureBox _host;
        private Axis _axis = Axis.Z;  // Default to Z-axis
        private ColorMapMode _mode = ColorMapMode.Damage;
        private bool _showVolume = true;

        // Data ranges for better color mapping
        private double _minDamage = 0, _maxDamage = 1.0, _cachedMaxDamage = 0;
        private double _minStress = 0, _maxStress = 100.0, _cachedMaxStress = 0;
        private double _minStrain = 0, _maxStrain = 0.05, _cachedMaxStrain = 0;

        // Color caches
        private readonly Color[] _damageColorCache = new Color[256];
        private readonly Color[] _stressColorCache = new Color[256];
        private readonly Color[] _strainColorCache = new Color[256];
        private bool _colorCachesInitialized = false;

        // 3D Rendering state
        private float _rotX = 30f, _rotY = 30f, _zoom = 1f;
        private PointF _pan = new PointF(0, 0);
        private bool _isLeftDragging = false;
        private bool _isRightDragging = false;
        private Point _lastMousePosition;

        // Visualization caches
        private Bitmap _failurePointCache;
        private readonly Dictionary<string, Bitmap> _legendBitmapCache = new Dictionary<string, Bitmap>();

        // Performance optimization
        private readonly object _renderLock = new object();
        private volatile bool _isRendering = false;
        private CancellationTokenSource _renderCancellation;

        // Failure point data
        private bool _failureDetected = false;
        private int _failX = -1, _failY = -1, _failZ = -1;

        // Enum for slice axis
        private enum Axis { X, Y, Z }

        #endregion

        #region Internal Methods - Initialization and Data Processing

        // Determine if automatic downsampling is needed based on volume size
        private void DetermineDownsamplingFactor()
        {
            long totalVoxels = (long)_w * _h * _d;

            if (totalVoxels > PerformanceTuning.MAX_VOLUME_SIZE)
            {
                // Calculate a reasonable downsampling factor
                double factor = Math.Pow(totalVoxels / (double)PerformanceTuning.MAX_VOLUME_SIZE, 1.0 / 3.0);
                _downsampleFactor = Math.Max(1, (int)Math.Ceiling(factor));

                Logger.Log($"[FailurePointVisualizer] Auto-downsampling by factor {_downsampleFactor} for volume of {totalVoxels} voxels");
            }
            else
            {
                _downsampleFactor = 1;
            }
        }

        // Initialize color caches for better performance
        private void InitializeColorCaches()
        {
            try
            {
                if (_colorCachesInitialized)
                    return;

                // Thread-safe initialization
                lock (_damageColorCache)
                {
                    if (_colorCachesInitialized)
                        return;

                    for (int i = 0; i < 256; i++)
                    {
                        double normalizedValue = i / 255.0;

                        // Damage: Green (0) to Red (1) with improved gradient
                        _damageColorCache[i] = Color.FromArgb(
                            (int)(normalizedValue * 255),
                            (int)((1 - normalizedValue) * 255),
                            0);

                        // Stress: Blue (0) to Red (1) with improved gradient
                        _stressColorCache[i] = Color.FromArgb(
                            (int)(normalizedValue * 255),
                            0,
                            (int)((1 - normalizedValue) * 255));

                        // Strain: Blue (0) to Yellow (1) with improved gradient
                        _strainColorCache[i] = Color.FromArgb(
                            (int)(normalizedValue * 255),
                            (int)(normalizedValue * 255),
                            (int)((1 - normalizedValue) * 255));
                    }

                    _colorCachesInitialized = true;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error initializing color caches: {ex.Message}");
            }
        }

        // Calculate data ranges for better color mapping
        private void CalculateDataRanges()
        {
            try
            {
                // Skip if no data available
                if (_dmg == null || _labels == null)
                    return;

                // Use sampling to improve performance
                int sampleStep = Math.Max(1, DetermineSampleStep() * 2);

                // Temporary storage for min/max values
                double minDamage = double.MaxValue;
                double maxDamage = double.MinValue;
                double minStress = double.MaxValue;
                double maxStress = double.MinValue;
                double minStrain = double.MaxValue;
                double maxStrain = double.MinValue;

                // Sample count for stats
                int sampleCount = 0;

                // Get dimensions
                int effectiveW = _needsResampling ? _originalDimensions[0] : _w;
                int effectiveH = _needsResampling ? _originalDimensions[1] : _h;
                int effectiveD = _needsResampling ? _originalDimensions[2] : _d;

                // Sample the data to find ranges
                for (int z = 0; z < effectiveD; z += sampleStep)
                {
                    for (int y = 0; y < effectiveH; y += sampleStep)
                    {
                        for (int x = 0; x < effectiveW; x += sampleStep)
                        {
                            // Skip if not in material
                            if (!IsPointInMaterial(x, y, z))
                                continue;

                            sampleCount++;

                            // Damage data
                            if (_dmg != null)
                            {
                                double damage = GetDamageAt(x, y, z);
                                if (damage > 0) // Ignore 0 values for better scaling
                                {
                                    minDamage = Math.Min(minDamage, damage);
                                    maxDamage = Math.Max(maxDamage, damage);
                                }
                            }

                            // Stress data
                            if (_stress != null)
                            {
                                float stress = GetStressAt(x, y, z);
                                if (stress > 0) // Ignore 0 values for better scaling
                                {
                                    minStress = Math.Min(minStress, stress);
                                    maxStress = Math.Max(maxStress, stress);
                                }
                            }

                            // Strain data
                            if (_strain != null)
                            {
                                float strain = GetStrainAt(x, y, z);
                                if (strain > 0) // Ignore 0 values for better scaling
                                {
                                    minStrain = Math.Min(minStrain, strain);
                                    maxStrain = Math.Max(maxStrain, strain);
                                }
                            }
                        }
                    }
                }

                // Apply reasonable defaults if no valid data found
                if (minDamage == double.MaxValue || maxDamage == double.MinValue)
                {
                    minDamage = 0;
                    maxDamage = 1.0;
                }

                if (minStress == double.MaxValue || maxStress == double.MinValue)
                {
                    minStress = 0;
                    maxStress = 100.0;
                }

                if (minStrain == double.MaxValue || maxStrain == double.MinValue)
                {
                    minStrain = 0;
                    maxStrain = 0.05;
                }

                // Store values
                _minDamage = minDamage;
                _maxDamage = maxDamage;
                _minStress = minStress;
                _maxStress = maxStress;
                _minStrain = minStrain;
                _maxStrain = maxStrain;

                // Update cached values
                _cachedMaxDamage = maxDamage;
                _cachedMaxStress = maxStress;
                _cachedMaxStrain = maxStrain;

                Logger.Log($"[FailurePointVisualizer] Data ranges: Damage={minDamage:F3}-{maxDamage:F3}, Stress={minStress:F1}-{maxStress:F1}, Strain={minStrain:F5}-{maxStrain:F5}, SampleCount={sampleCount}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error calculating data ranges: {ex.Message}");
            }
        }

        // Helper methods to safely get values at coordinates
        private double GetDamageAt(int x, int y, int z)
        {
            if (_dmg == null) return 0;

            if (_needsResampling)
            {
                // Map coordinates from original dimensions
                int origX = (int)((float)x / _originalDimensions[0] * _w);
                int origY = (int)((float)y / _originalDimensions[1] * _h);
                int origZ = (int)((float)z / _originalDimensions[2] * _d);

                // Clamp to valid range
                origX = Clamp(origX, 0, _w - 1);
                origY = Clamp(origY, 0, _h - 1);
                origZ = Clamp(origZ, 0, _d - 1);

                return _dmg[origX, origY, origZ];
            }

            return _dmg[x, y, z];
        }

        private float GetStressAt(int x, int y, int z)
        {
            if (_stress == null) return 0;

            if (_needsResampling)
            {
                // Map coordinates from original dimensions
                int origX = (int)((float)x / _originalDimensions[0] * _w);
                int origY = (int)((float)y / _originalDimensions[1] * _h);
                int origZ = (int)((float)z / _originalDimensions[2] * _d);

                // Clamp to valid range
                origX = Clamp(origX, 0, _w - 1);
                origY = Clamp(origY, 0, _h - 1);
                origZ = Clamp(origZ, 0, _d - 1);

                return _stress[origX, origY, origZ];
            }

            return _stress[x, y, z];
        }

        private float GetStrainAt(int x, int y, int z)
        {
            if (_strain == null) return 0;

            if (_needsResampling)
            {
                // Map coordinates from original dimensions
                int origX = (int)((float)x / _originalDimensions[0] * _w);
                int origY = (int)((float)y / _originalDimensions[1] * _h);
                int origZ = (int)((float)z / _originalDimensions[2] * _d);

                // Clamp to valid range
                origX = Clamp(origX, 0, _w - 1);
                origY = Clamp(origY, 0, _h - 1);
                origZ = Clamp(origZ, 0, _d - 1);

                return _strain[origX, origY, origZ];
            }

            return _strain[x, y, z];
        }

        // Compute stress and strain fields from damage data
        private void ComputeStressAndStrainFields()
        {
            if (_dmg == null) return;

            try
            {
                // Calculate dimensions based on current settings
                int effectiveW = _needsResampling ? _originalDimensions[0] : _w;
                int effectiveH = _needsResampling ? _originalDimensions[1] : _h;
                int effectiveD = _needsResampling ? _originalDimensions[2] : _d;

                // Create arrays if needed
                if (_stress == null || _stress.GetLength(0) != effectiveW ||
                    _stress.GetLength(1) != effectiveH || _stress.GetLength(2) != effectiveD)
                {
                    _stress = new float[effectiveW, effectiveH, effectiveD];
                }

                if (_strain == null || _strain.GetLength(0) != effectiveW ||
                    _strain.GetLength(1) != effectiveH || _strain.GetLength(2) != effectiveD)
                {
                    _strain = new float[effectiveW, effectiveH, effectiveD];
                }

                // Reset cached values
                _cachedMaxStress = 0;
                _cachedMaxStrain = 0;

                // Try parallel processing for better performance
                try
                {
                    Parallel.For(0, effectiveD, z =>
                    {
                        for (int y = 0; y < effectiveH; y++)
                        {
                            for (int x = 0; x < effectiveW; x++)
                            {
                                // Skip if not in our material
                                if (_labels != null && !IsPointInMaterial(x, y, z))
                                {
                                    _stress[x, y, z] = 0;
                                    _strain[x, y, z] = 0;
                                    continue;
                                }

                                // Get damage value and calculate derived fields
                                double damage = _dmg[x, y, z];

                                // Compute approximated stress (inverse relationship with damage)
                                // Higher damage = lower stress capacity
                                _stress[x, y, z] = (float)(100.0 * (1.0 - damage));

                                // Compute approximated strain (proportional to damage)
                                // Higher damage = higher strain
                                _strain[x, y, z] = (float)(0.05 * damage);
                            }
                        }
                    });
                }
                catch (Exception ex)
                {
                    // Fall back to serial processing if parallel fails
                    Logger.Log($"[FailurePointVisualizer] Parallel computation failed, falling back to serial: {ex.Message}");

                    for (int z = 0; z < effectiveD; z++)
                    {
                        for (int y = 0; y < effectiveH; y++)
                        {
                            for (int x = 0; x < effectiveW; x++)
                            {
                                // Skip if not in our material
                                if (_labels != null && !IsPointInMaterial(x, y, z))
                                {
                                    _stress[x, y, z] = 0;
                                    _strain[x, y, z] = 0;
                                    continue;
                                }

                                // Get damage value and calculate derived fields
                                double damage = _dmg[x, y, z];

                                // Compute approximated stress (inverse relationship with damage)
                                _stress[x, y, z] = (float)(100.0 * (1.0 - damage));

                                // Compute approximated strain (proportional to damage)
                                _strain[x, y, z] = (float)(0.05 * damage);
                            }
                        }
                    }
                }

                Logger.Log("[FailurePointVisualizer] Computed stress and strain fields from damage data");
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error computing stress/strain fields: {ex.Message}");
            }
        }

        // Invalidate cached visualization
        private void InvalidateVisualization()
        {
            try
            {
                lock (_renderLock)
                {
                    // Cancel any in-progress rendering
                    _renderCancellation?.Cancel();

                    // Dispose old visualization
                    if (_failurePointCache != null && !_isLeftDragging && !_isRightDragging)
                    {
                        _failurePointCache.Dispose();
                        _failurePointCache = null;
                    }
                }

                // Force redraw
                _host?.Invalidate();
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error invalidating visualization: {ex.Message}");
            }
        }

        // Memory management helper - ensure we have enough memory
        private void EnsureMemoryAvailable()
        {
            try
            {
                // Force garbage collection to free memory
                GC.Collect();
                GC.WaitForPendingFinalizers();

                // Check if we have enough memory
                long availableMemory = GC.GetTotalMemory(true);
                if (availableMemory > PerformanceTuning.MEMORY_LIMIT)
                {
                    // Log warning
                    Logger.Log($"[FailurePointVisualizer] Memory usage is high: {availableMemory / (1024 * 1024)} MB");

                    // Clear caches to free memory
                    foreach (var bitmap in _legendBitmapCache.Values)
                    {
                        bitmap?.Dispose();
                    }
                    _legendBitmapCache.Clear();

                    // Force garbage collection again
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error checking memory: {ex.Message}");
            }
        }

        #endregion

        #region Data Access Helpers

        // Check if point is in the selected material
        private bool IsPointInMaterial(int x, int y, int z)
        {
            try
            {
                if (_labels == null)
                    return true; // No material info, assume everything is valid

                // Handle resampling if needed
                if (_needsResampling && _originalDimensions != null)
                {
                    // Make sure coordinates are in bounds
                    if (x < 0 || y < 0 || z < 0 ||
                        x >= _originalDimensions[0] ||
                        y >= _originalDimensions[1] ||
                        z >= _originalDimensions[2])
                        return false;

                    // Need to map from original coordinates to our current dimensions
                    int mappedX = (int)((float)x / _originalDimensions[0] * _w);
                    int mappedY = (int)((float)y / _originalDimensions[1] * _h);
                    int mappedZ = (int)((float)z / _originalDimensions[2] * _d);

                    // Clamp to valid range
                    mappedX = Clamp(mappedX, 0, _w - 1);
                    mappedY = Clamp(mappedY, 0, _h - 1);
                    mappedZ = Clamp(mappedZ, 0, _d - 1);

                    return _labels[mappedX, mappedY, mappedZ] == _matId;
                }
                else
                {
                    // Direct access
                    if (x < 0 || y < 0 || z < 0 || x >= _w || y >= _h || z >= _d)
                        return false;

                    return _labels[x, y, z] == _matId;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error checking material: {ex.Message}");
                return false;
            }
        }

        // Get data value for a position based on visualization mode
        private double GetValueForPosition(int x, int y, int z, ColorMapMode mode)
        {
            try
            {
                // Check bounds first to avoid exceptions
                if (!IsPointInMaterial(x, y, z))
                    return 0;

                // Handle resampling if needed
                if (_needsResampling && _originalDimensions != null)
                {
                    // Map from our coordinates to original dimensions
                    int origX = (int)((float)x / _w * _originalDimensions[0]);
                    int origY = (int)((float)y / _h * _originalDimensions[1]);
                    int origZ = (int)((float)z / _d * _originalDimensions[2]);

                    // Clamp to valid range
                    origX = Clamp(origX, 0, _originalDimensions[0] - 1);
                    origY = Clamp(origY, 0, _originalDimensions[1] - 1);
                    origZ = Clamp(origZ, 0, _originalDimensions[2] - 1);

                    // Get value from the original data
                    switch (mode)
                    {
                        case ColorMapMode.Damage:
                            return _dmg != null ? _dmg[origX, origY, origZ] : 0;

                        case ColorMapMode.Stress:
                            return _stress != null ? _stress[origX, origY, origZ] : 0;

                        case ColorMapMode.Strain:
                            return _strain != null ? _strain[origX, origY, origZ] : 0;

                        default:
                            return 0;
                    }
                }
                else
                {
                    // Direct access
                    switch (mode)
                    {
                        case ColorMapMode.Damage:
                            return _dmg != null ? _dmg[x, y, z] : 0;

                        case ColorMapMode.Stress:
                            return _stress != null ? _stress[x, y, z] : 0;

                        case ColorMapMode.Strain:
                            return _strain != null ? _strain[x, y, z] : 0;

                        default:
                            return 0;
                    }
                }
            }
            catch (Exception ex)
            {
                // Log error but don't crash
                Logger.Log($"[FailurePointVisualizer] Error getting value at ({x},{y},{z}): {ex.Message}");
                return 0;
            }
        }

        // Get maximum value for the current mode
        private double GetMaxValueForMode(ColorMapMode mode)
        {
            try
            {
                switch (mode)
                {
                    case ColorMapMode.Damage:
                        // Use pre-computed damage range
                        return _cachedMaxDamage > 0 ? _cachedMaxDamage : 1.0;

                    case ColorMapMode.Stress:
                        // Use pre-computed stress range
                        return _cachedMaxStress > 0 ? _cachedMaxStress : 100.0;

                    case ColorMapMode.Strain:
                        // Use pre-computed strain range
                        return _cachedMaxStrain > 0 ? _cachedMaxStrain : 0.05;

                    default:
                        return 1.0;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error finding max value: {ex.Message}");
                return mode == ColorMapMode.Damage ? 1.0 :
                       mode == ColorMapMode.Stress ? 100.0 : 0.05;
            }
        }

        // Get minimum value for the current mode
        private double GetMinValueForMode(ColorMapMode mode)
        {
            try
            {
                switch (mode)
                {
                    case ColorMapMode.Damage:
                        // Use pre-computed damage range
                        return _minDamage;

                    case ColorMapMode.Stress:
                        // Use pre-computed stress range
                        return _minStress;

                    case ColorMapMode.Strain:
                        // Use pre-computed strain range
                        return _minStrain;

                    default:
                        return 0.0;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error finding min value: {ex.Message}");
                return 0.0;
            }
        }

        // Helper method to scan for maximum value in an array
        private double ScanForMaxValue<T>(T[,,] array, int sampleStep) where T : IComparable
        {
            double maxValue = 0;

            // Get dimensions
            int arrayW = array.GetLength(0);
            int arrayH = array.GetLength(1);
            int arrayD = array.GetLength(2);

            // Sample the array
            for (int z = 0; z < arrayD; z += sampleStep)
            {
                for (int y = 0; y < arrayH; y += sampleStep)
                {
                    for (int x = 0; x < arrayW; x += sampleStep)
                    {
                        if (_labels == null || IsPointInMaterial(x, y, z))
                        {
                            // Convert to double for comparison
                            double value = Convert.ToDouble(array[x, y, z]);
                            maxValue = Math.Max(maxValue, value);
                        }
                    }
                }
            }

            return maxValue;
        }

        // Determine appropriate sampling step based on volume size
        private int DetermineSampleStep()
        {
            // Get dimensions
            int effectiveW = _needsResampling ? _originalDimensions[0] : _w;
            int effectiveH = _needsResampling ? _originalDimensions[1] : _h;
            int effectiveD = _needsResampling ? _originalDimensions[2] : _d;

            // Calculate total voxels
            long totalVoxels = (long)effectiveW * effectiveH * effectiveD;

            // Use a step size that samples approximately 1 million voxels
            int step = (int)Math.Max(1, Cbrt(totalVoxels / 1_000_000));

            return Math.Max(1, step * _downsampleFactor);
        }

        public static double Cbrt(double x)
        {
            // Special values
            if (double.IsNaN(x) || double.IsInfinity(x))
                return double.NaN;        // IEEE-754 compatible
            if (x == 0.0)
                return 0.0;

            // Preserve the sign, operate on magnitude
            double sign = x < 0 ? -1.0 : 1.0;
            double a = Math.Abs(x);

            // Good first guess: 2^(log2(a)/3)
            double guess = Math.Pow(2, Math.Log(a, 2) / 3.0);

            // Newton–Raphson iteration:  s_{n+1} = (2s_n + a / s_n^2) / 3
            const double eps = 1e-15;          // ~ machine ϵ for double
            double s = guess, prev;
            do
            {
                prev = s;
                s = (2.0 * s + a / (s * s)) / 3.0;
            } while (Math.Abs(s - prev) > eps * Math.Abs(s));

            return sign * s;
        }

        // Color mapping helper
        private Color MapToColor(double value, ColorMapMode mode)
        {
            try
            {
                // Get the min and max values for normalization
                double minVal = GetMinValueForMode(mode);
                double maxVal = GetMaxValueForMode(mode);
                double valueRange = maxVal - minVal;
                double normalizedValue = 0.0;

                if (valueRange <= 0)
                    valueRange = 1.0; // Avoid division by zero

                // Special case for very small values - use logarithmic scale
                if (mode == ColorMapMode.Damage && maxVal < 0.01 && value > 0)
                {
                    // Use log scale for tiny damage values
                    double logMin = Math.Log10(Math.Max(1e-10, minVal));
                    double logMax = Math.Log10(Math.Max(1e-9, maxVal));
                    double logVal = Math.Log10(Math.Max(1e-10, value));

                    // Normalize in log space
                    if (logMax > logMin)
                        normalizedValue = Math.Min(1.0, Math.Max(0.0, (logVal - logMin) / (logMax - logMin)));
                    else
                        normalizedValue = value > 0 ? 1.0 : 0.0;
                }
                else
                {
                    // Standard linear normalization
                    normalizedValue = Math.Min(1.0, Math.Max(0.0, (value - minVal) / valueRange));
                }

                // Get the color from cache
                return GetColorForMode(normalizedValue, mode);
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error mapping color: {ex.Message}");
                return Color.White;
            }
        }
        // Get color from cache for a normalized value
        private Color GetColorForMode(double normalizedValue, ColorMapMode mode)
        {
            try
            {
                // Initialize color caches if needed
                if (!_colorCachesInitialized)
                {
                    InitializeColorCaches();
                }

                // Ensure value is in [0,1] range
                normalizedValue = Math.Max(0, Math.Min(1, normalizedValue));

                // Convert to index
                int index = (int)(normalizedValue * 255);

                // Return from appropriate cache
                switch (mode)
                {
                    case ColorMapMode.Damage:
                        return _damageColorCache[index];
                    case ColorMapMode.Stress:
                        return _stressColorCache[index];
                    case ColorMapMode.Strain:
                        return _strainColorCache[index];
                    default:
                        return Color.White;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error getting color: {ex.Message}");
                return Color.White;
            }
        }

        #endregion

        #region Drawing Methods

        // Draw a 2D slice visualizing the data
        private void DrawSlice(Graphics g, Rectangle rect, ColorMapMode mode)
        {
            if (_dmg == null) return;

            try
            {
                int sliceIdx = (_axis == Axis.X) ? GetSliceX() :
                               (_axis == Axis.Y) ? GetSliceY() : GetSliceZ();

                // Draw background and border
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(30, 30, 30)))
                {
                    g.FillRectangle(brush, rect);
                }

                using (Pen pen = new Pen(Color.Gray))
                {
                    g.DrawRectangle(pen, rect);
                }

                // Draw slice title
                using (Font titleFont = new Font("Arial", 10, FontStyle.Bold))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    string title = $"{_axis}-Axis Slice (pos: {sliceIdx})";
                    g.DrawString(title, titleFont, brush, rect.X + 5, rect.Y - 20);
                }

                // Calculate proper scaling
                int displayWidth = rect.Width;
                int displayHeight = rect.Height;
                int dataWidth, dataHeight;

                switch (_axis)
                {
                    case Axis.X:
                        dataWidth = _d;
                        dataHeight = _h;
                        break;
                    case Axis.Y:
                        dataWidth = _w;
                        dataHeight = _d;
                        break;
                    default: // Z
                        dataWidth = _w;
                        dataHeight = _h;
                        break;
                }

                // Check if dimensions exceed our limits - if so, use downsampling
                float scaleX = (float)displayWidth / dataWidth;
                float scaleY = (float)displayHeight / dataHeight;

                // Determine if we need to apply subsampling
                bool needsSubsampling = dataWidth > PerformanceTuning.MAX_SLICE_RESOLUTION ||
                                        dataHeight > PerformanceTuning.MAX_SLICE_RESOLUTION;

                int subsampleFactor = needsSubsampling ?
                    Math.Max(1, Math.Max(dataWidth, dataHeight) / PerformanceTuning.MAX_SLICE_RESOLUTION) : 1;

                // Draw slice efficiently using optimized DirectBitmap approach
                using (DirectBitmap directBitmap = new DirectBitmap(displayWidth, displayHeight))
                {
                    // Process the slice data
                    if (needsSubsampling)
                    {
                        // Using subsampling for large slices
                        DrawSliceWithSubsampling(directBitmap, displayWidth, displayHeight, dataWidth, dataHeight,
                                              sliceIdx, subsampleFactor, mode);
                    }
                    else
                    {
                        // Full resolution for reasonable sized slices
                        DrawSliceFullResolution(directBitmap, displayWidth, displayHeight, dataWidth, dataHeight,
                                             sliceIdx, mode);
                    }

                    // Draw the bitmap to the graphics context
                    g.DrawImage(directBitmap.Bitmap, rect);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error drawing slice: {ex.Message}");

                // Draw error message
                using (Font font = new Font("Arial", 10))
                using (SolidBrush brush = new SolidBrush(Color.Red))
                {
                    g.DrawString($"Error drawing slice: {ex.Message}", font, brush, rect.X + 10, rect.Y + 30);
                }
            }
        }

        // Draw slice with subsampling for large data
        private void DrawSliceWithSubsampling(DirectBitmap bmp, int displayWidth, int displayHeight,
                                           int dataWidth, int dataHeight, int sliceIdx, int subsampleFactor,
                                           ColorMapMode mode)
        {
            // Calculate how many data points per display pixel
            float dataToDisplayRatioX = (float)dataWidth / displayWidth;
            float dataToDisplayRatioY = (float)dataHeight / displayHeight;

            // Process each display pixel
            Parallel.For(0, displayHeight, j =>
            {
                for (int i = 0; i < displayWidth; i++)
                {
                    // Calculate the data region this display pixel represents
                    int startDataX = (int)(i * dataToDisplayRatioX);
                    int endDataX = (int)((i + 1) * dataToDisplayRatioX);
                    int startDataY = (int)(j * dataToDisplayRatioY);
                    int endDataY = (int)((j + 1) * dataToDisplayRatioY);

                    // Apply averaging over the data region
                    double sum = 0;
                    int count = 0;
                    bool hasMaterial = false;

                    // Sample the data region with the subsample factor
                    for (int dy = startDataY; dy < endDataY; dy += subsampleFactor)
                    {
                        for (int dx = startDataX; dx < endDataX; dx += subsampleFactor)
                        {
                            // Map to 3D coordinates based on the current axis
                            int x3d, y3d, z3d;

                            switch (_axis)
                            {
                                case Axis.X:
                                    x3d = sliceIdx;
                                    y3d = dy;
                                    z3d = dx;
                                    break;
                                case Axis.Y:
                                    x3d = dx;
                                    y3d = sliceIdx;
                                    z3d = dy;
                                    break;
                                default: // Z
                                    x3d = dx;
                                    y3d = dy;
                                    z3d = sliceIdx;
                                    break;
                            }

                            // Check if this is a valid material voxel
                            if (_labels != null && !IsPointInMaterial(x3d, y3d, z3d))
                                continue;

                            hasMaterial = true;

                            // Add value to sum
                            sum += GetValueForPosition(x3d, y3d, z3d, mode);
                            count++;
                        }
                    }

                    // If we have material voxels in this region, calculate the average value
                    if (hasMaterial && count > 0)
                    {
                        double avgValue = sum / count;

                        // Map to color
                        Color color = MapToColor(avgValue, mode);

                        // Set pixel color
                        bmp.SetPixel(i, j, color);
                    }
                }
            });
        }

        // Draw slice at full resolution
        private void DrawSliceFullResolution(DirectBitmap bmp, int displayWidth, int displayHeight,
                                          int dataWidth, int dataHeight, int sliceIdx, ColorMapMode mode)
        {
            Parallel.For(0, displayHeight, j =>
            {
                for (int i = 0; i < displayWidth; i++)
                {
                    // Map from display coordinates to data coordinates
                    int dataX = (int)((float)i / displayWidth * dataWidth);
                    int dataY = (int)((float)j / displayHeight * dataHeight);

                    // Clamp to valid range
                    dataX = Clamp(dataX, 0, dataWidth - 1);
                    dataY = Clamp(dataY, 0, dataHeight - 1);

                    // Get 3D coordinates based on the current axis
                    int x3d, y3d, z3d;

                    switch (_axis)
                    {
                        case Axis.X:
                            x3d = sliceIdx;
                            y3d = dataY;
                            z3d = dataX;
                            break;
                        case Axis.Y:
                            x3d = dataX;
                            y3d = sliceIdx;
                            z3d = dataY;
                            break;
                        default: // Z
                            x3d = dataX;
                            y3d = dataY;
                            z3d = sliceIdx;
                            break;
                    }

                    // Check if this is a valid material voxel
                    if (_labels != null && !IsPointInMaterial(x3d, y3d, z3d))
                        continue;

                    // Get value based on the visualization mode
                    double value = GetValueForPosition(x3d, y3d, z3d, mode);

                    // Map to color
                    Color color = MapToColor(value, mode);

                    // Set pixel color
                    bmp.SetPixel(i, j, color);
                }
            });
        }

        // Draw the 3D visualization
        private void Draw3DView(Graphics g, Rectangle rect, ColorMapMode mode)
        {
            if (_dmg == null) return;

            try
            {
                // Draw background and border
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(20, 20, 20)))
                {
                    g.FillRectangle(brush, rect);
                }

                using (Pen pen = new Pen(Color.Gray))
                {
                    g.DrawRectangle(pen, rect);
                }

                // Draw 3D view title
                using (Font titleFont = new Font("Arial", 10, FontStyle.Bold))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    string title = "3D Wireframe View";
                    g.DrawString(title, titleFont, brush, rect.X + 5, rect.Y - 20);
                }

                // Create 3D transform matrix
                Matrix3D transform = new Matrix3D();
                transform.RotateX(_rotX * (float)Math.PI / 180);
                transform.RotateY(_rotY * (float)Math.PI / 180);

                // Calculate center and scale for projection
                float centerX = rect.X + rect.Width / 2f + _pan.X;
                float centerY = rect.Y + rect.Height / 2f + _pan.Y;
                float scale = Math.Min(rect.Width, rect.Height) /
                            Math.Max(Math.Max(_w, _h), _d) * _zoom * 0.8f;

                // Project a 3D point to 2D
                Func<float, float, float, PointF> projectPoint = (x, y, z) => {
                    // Center coordinates
                    float fx = x - _w / 2f;
                    float fy = y - _h / 2f;
                    float fz = z - _d / 2f;

                    // Apply 3D transformation
                    Vector3 v = transform.Transform(new Vector3(fx, fy, fz));

                    // Project to 2D
                    return new PointF(
                        centerX + v.X * scale,
                        centerY + v.Y * scale
                    );
                };

                // Draw wireframe
                DrawWireframeWithMaterial(g, projectPoint, rect.Width, rect.Height, mode);

                // Draw volume outline
                DrawVolumeOutline(g, projectPoint);

                // Draw slices if showing volume
                if (_showVolume)
                {
                    // Get current slice positions
                    int sliceX = GetSliceX();
                    int sliceY = GetSliceY();
                    int sliceZ = GetSliceZ();

                    // Draw the slice planes
                    DrawSlicePlane(g, projectPoint, Axis.X, sliceX, mode);
                    DrawSlicePlane(g, projectPoint, Axis.Y, sliceY, mode);
                    DrawSlicePlane(g, projectPoint, Axis.Z, sliceZ, mode);
                }

                // Draw axes labels for orientation
                DrawAxesLabels(g, projectPoint);

                // Draw failure point if detected
                if (_failureDetected)
                {
                    DrawFailurePointMarker(g, projectPoint);
                }

                // Add interaction hint at the bottom
                using (Font font = new Font("Arial", 8))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    g.DrawString("Click+Drag: Rotate | Right+Drag: Pan | Scroll: Zoom",
                                font, textBrush, rect.X + 5, rect.Y + rect.Height - 20);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error drawing 3D view: {ex.Message}");

                // Draw error message
                using (Font font = new Font("Arial", 10))
                using (SolidBrush brush = new SolidBrush(Color.Red))
                {
                    g.DrawString($"Error drawing 3D view: {ex.Message}", font, brush, rect.X + 10, rect.Y + 30);
                }
            }
        }

        // Draw the volume outline
        private void DrawVolumeOutline(Graphics g, Func<float, float, float, PointF> projectPoint)
        {
            try
            {
                // Get the 8 corners of the volume
                PointF[] corners = new PointF[8];
                corners[0] = projectPoint(0, 0, 0);
                corners[1] = projectPoint(_w, 0, 0);
                corners[2] = projectPoint(_w, _h, 0);
                corners[3] = projectPoint(0, _h, 0);
                corners[4] = projectPoint(0, 0, _d);
                corners[5] = projectPoint(_w, 0, _d);
                corners[6] = projectPoint(_w, _h, _d);
                corners[7] = projectPoint(0, _h, _d);

                // Create a GraphicsPath for more efficient drawing
                using (GraphicsPath path = new GraphicsPath())
                {
                    // Bottom rectangle
                    path.AddLine(corners[0], corners[1]);
                    path.AddLine(corners[1], corners[2]);
                    path.AddLine(corners[2], corners[3]);
                    path.AddLine(corners[3], corners[0]);

                    // Top rectangle
                    path.AddLine(corners[4], corners[5]);
                    path.AddLine(corners[5], corners[6]);
                    path.AddLine(corners[6], corners[7]);
                    path.AddLine(corners[7], corners[4]);

                    // Connecting lines
                    path.AddLine(corners[0], corners[4]);
                    path.AddLine(corners[1], corners[5]);
                    path.AddLine(corners[2], corners[6]);
                    path.AddLine(corners[3], corners[7]);

                    // Draw the edges in one operation
                    using (Pen pen = new Pen(Color.FromArgb(150, 200, 200, 200), 1))
                    {
                        g.DrawPath(pen, path);
                    }
                }

                // Draw translucent faces for better 3D appearance
                if (_showVolume)
                {
                    // Draw 6 faces with semitransparent fill
                    using (SolidBrush brush = new SolidBrush(Color.FromArgb(20, 150, 150, 150)))
                    {
                        // Bottom face
                        g.FillPolygon(brush, new PointF[] { corners[0], corners[1], corners[2], corners[3] });

                        // Top face
                        g.FillPolygon(brush, new PointF[] { corners[4], corners[5], corners[6], corners[7] });

                        // Side faces
                        g.FillPolygon(brush, new PointF[] { corners[0], corners[1], corners[5], corners[4] });
                        g.FillPolygon(brush, new PointF[] { corners[1], corners[2], corners[6], corners[5] });
                        g.FillPolygon(brush, new PointF[] { corners[2], corners[3], corners[7], corners[6] });
                        g.FillPolygon(brush, new PointF[] { corners[3], corners[0], corners[4], corners[7] });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error drawing volume outline: {ex.Message}");
            }
        }

        // Draw a wireframe visualization for the 3D view
        private void DrawWireframeWithMaterial(Graphics g, Func<float, float, float, PointF> projectPoint,
                                      int width, int height, ColorMapMode mode)
        {
            try
            {
                // Calculate an appropriate step size based on volume dimensions and wireframe coarseness
                int totalSize = _w * _h * _d;
                int baseStep = Math.Max(1, (int)Cbrt(totalSize / 4000));
                int step = Math.Max(1, baseStep * _wireframeCoarseness / 2);

                // Larger step for lines that form the grid
                int gridStep = step * 2;

                // Determine if we're drawing internal points
                bool drawInterior = _showVolume;

                // Prepare wireframe lines with depth info for sorting
                List<(PointF, PointF, float, Color)> wireframeLines = new List<(PointF, PointF, float, Color)>();

                // Keep track of material presence
                bool hasMaterialPoints = false;

                // Draw lines along each axis (X, Y, Z) with a larger step for better performance
                // X-direction lines
                for (int z = 0; z < _d; z += gridStep)
                {
                    for (int y = 0; y < _h; y += gridStep)
                    {
                        PointF? lastPoint = null;
                        double lastValue = 0;
                        bool isMaterial = false;

                        for (int x = 0; x < _w; x += step)
                        {
                            // Check if point is in material
                            isMaterial = IsPointInMaterial(x, y, z);

                            // Skip if not in material
                            if (!isMaterial)
                            {
                                lastPoint = null;
                                continue;
                            }

                            hasMaterialPoints = true;

                            // Skip interior points if not showing volume
                            bool isEdge = IsEdgeVoxel(x, y, z);
                            if (!drawInterior && !isEdge)
                            {
                                lastPoint = null;
                                continue;
                            }

                            // Get value and project point
                            double value = GetValueForPosition(x, y, z, mode);
                            PointF point = projectPoint(x, y, z);

                            // Connect to previous point if exists
                            if (lastPoint.HasValue)
                            {
                                // Calculate average value for line color
                                double avgValue = (value + lastValue) / 2;
                                Color lineColor = MapToColor(avgValue, mode);

                                // Calculate depth for sorting
                                float depth = -(x + _w / 2) + (y + _h / 2) + (z + _d / 2); // Simple depth heuristic

                                wireframeLines.Add((lastPoint.Value, point, depth, lineColor));

                                // Check if we've reached the limit
                                if (wireframeLines.Count >= PerformanceTuning.MAX_WIREFRAME_LINES)
                                    break;
                            }

                            lastPoint = point;
                            lastValue = value;
                        }
                    }

                    // Check if we've reached the limit
                    if (wireframeLines.Count >= PerformanceTuning.MAX_WIREFRAME_LINES)
                        break;
                }

                // Y-direction lines (if not at max)
                if (wireframeLines.Count < PerformanceTuning.MAX_WIREFRAME_LINES)
                {
                    for (int z = 0; z < _d; z += gridStep)
                    {
                        for (int x = 0; x < _w; x += gridStep)
                        {
                            PointF? lastPoint = null;
                            double lastValue = 0;
                            bool isMaterial = false;

                            for (int y = 0; y < _h; y += step)
                            {
                                // Check if point is in material
                                isMaterial = IsPointInMaterial(x, y, z);

                                // Skip if not in material
                                if (!isMaterial)
                                {
                                    lastPoint = null;
                                    continue;
                                }

                                hasMaterialPoints = true;

                                // Skip interior points if not showing volume
                                bool isEdge = IsEdgeVoxel(x, y, z);
                                if (!drawInterior && !isEdge)
                                {
                                    lastPoint = null;
                                    continue;
                                }

                                // Get value and project point
                                double value = GetValueForPosition(x, y, z, mode);
                                PointF point = projectPoint(x, y, z);

                                // Connect to previous point if exists
                                if (lastPoint.HasValue)
                                {
                                    // Calculate average value for line color
                                    double avgValue = (value + lastValue) / 2;
                                    Color lineColor = MapToColor(avgValue, mode);

                                    // Calculate depth for sorting
                                    float depth = -(x + _w / 2) + (y + _h / 2) + (z + _d / 2);

                                    wireframeLines.Add((lastPoint.Value, point, depth, lineColor));

                                    // Check if we've reached the limit
                                    if (wireframeLines.Count >= PerformanceTuning.MAX_WIREFRAME_LINES)
                                        break;
                                }

                                lastPoint = point;
                                lastValue = value;
                            }
                        }

                        // Check if we've reached the limit
                        if (wireframeLines.Count >= PerformanceTuning.MAX_WIREFRAME_LINES)
                            break;
                    }
                }

                // Z-direction lines (if not at max)
                if (wireframeLines.Count < PerformanceTuning.MAX_WIREFRAME_LINES)
                {
                    for (int y = 0; y < _h; y += gridStep)
                    {
                        for (int x = 0; x < _w; x += gridStep)
                        {
                            PointF? lastPoint = null;
                            double lastValue = 0;
                            bool isMaterial = false;

                            for (int z = 0; z < _d; z += step)
                            {
                                // Check if point is in material
                                isMaterial = IsPointInMaterial(x, y, z);

                                // Skip if not in material
                                if (!isMaterial)
                                {
                                    lastPoint = null;
                                    continue;
                                }

                                hasMaterialPoints = true;

                                // Skip interior points if not showing volume
                                bool isEdge = IsEdgeVoxel(x, y, z);
                                if (!drawInterior && !isEdge)
                                {
                                    lastPoint = null;
                                    continue;
                                }

                                // Get value and project point
                                double value = GetValueForPosition(x, y, z, mode);
                                PointF point = projectPoint(x, y, z);

                                // Connect to previous point if exists
                                if (lastPoint.HasValue)
                                {
                                    // Calculate average value for line color
                                    double avgValue = (value + lastValue) / 2;
                                    Color lineColor = MapToColor(avgValue, mode);

                                    // Calculate depth for sorting
                                    float depth = -(x + _w / 2) + (y + _h / 2) + (z + _d / 2);

                                    wireframeLines.Add((lastPoint.Value, point, depth, lineColor));

                                    // Check if we've reached the limit
                                    if (wireframeLines.Count >= PerformanceTuning.MAX_WIREFRAME_LINES)
                                        break;
                                }

                                lastPoint = point;
                                lastValue = value;
                            }
                        }

                        // Check if we've reached the limit
                        if (wireframeLines.Count >= PerformanceTuning.MAX_WIREFRAME_LINES)
                            break;
                    }
                }

                // Sort the lines by depth value for better rendering
                wireframeLines.Sort((a, b) => a.Item3.CompareTo(b.Item3));

                // Draw the lines
                using (Pen pen = new Pen(Color.White, 1))
                {
                    foreach (var (start, end, depth, color) in wireframeLines)
                    {
                        pen.Color = color;
                        g.DrawLine(pen, start, end);
                    }
                }

                // Add stats info at the bottom
                using (Font font = new Font("Arial", 8))
                using (SolidBrush brushWhite = new SolidBrush(Color.White))
                {
                    // Add wireframe info
                    string infoText = $"Wireframe: {wireframeLines.Count} lines, Detail: {_wireframeCoarseness}/10";
                    g.DrawString(infoText, font, brushWhite, 5, height - 35);

                    // Add warning if no material points found
                    if (!hasMaterialPoints && _dmg != null && _labels != null)
                    {
                        string warningText = "Warning: No material points found in visualization";
                        g.DrawString(warningText, font, new SolidBrush(Color.Red), 5, height - 20);
                    }

                    // Add data range info
                    double minVal = GetMinValueForMode(mode);
                    double maxVal = GetMaxValueForMode(mode);
                    string rangeText = $"{mode} Range: {minVal:F5} - {maxVal:F5}";
                    g.DrawString(rangeText, font, brushWhite, 5, height - 50);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error drawing wireframe: {ex.Message}");

                // Draw error message directly in the visualization
                using (Font font = new Font("Arial", 9))
                using (SolidBrush brush = new SolidBrush(Color.Red))
                {
                    g.DrawString($"Error drawing wireframe: {ex.Message}", font, brush, 10, height - 20);
                }
            }
        }
        public void ComputeDerivedFields(double peakStress)
        {
            try
            {
                if (_dmg == null) return;

                // Calculate dimensions
                int w = _dmg.GetLength(0);
                int h = _dmg.GetLength(1);
                int d = _dmg.GetLength(2);

                // Create stress and strain arrays if needed
                if (_stress == null || _stress.GetLength(0) != w ||
                    _stress.GetLength(1) != h || _stress.GetLength(2) != d)
                {
                    _stress = new float[w, h, d];
                }

                if (_strain == null || _strain.GetLength(0) != w ||
                    _strain.GetLength(1) != h || _strain.GetLength(2) != d)
                {
                    _strain = new float[w, h, d];
                }

                // Compute stress and strain from damage
                Parallel.For(0, d, z =>
                {
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            if (_labels == null || IsPointInMaterial(x, y, z))
                            {
                                double damage = _dmg[x, y, z];

                                // Compute stress (inverse relation to damage)
                                // Higher damage = lower stress capacity
                                _stress[x, y, z] = (float)(peakStress * (1.0 - damage));

                                // Compute strain (directly related to damage)
                                // Higher damage = higher strain
                                _strain[x, y, z] = (float)(0.05 * damage);
                            }
                            else
                            {
                                _stress[x, y, z] = 0;
                                _strain[x, y, z] = 0;
                            }
                        }
                    }
                });

                // Recalculate data ranges
                CalculateDataRanges();

                Logger.Log("[FailurePointVisualizer] Computed derived stress and strain fields");
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error computing derived fields: {ex.Message}");
            }
        }

        // Helper method to check if a voxel is at the edge of the material
        private bool IsEdgeVoxel(int x, int y, int z)
        {
            // Check immediate neighbors (6-connected)
            for (int dz = -1; dz <= 1; dz += 1)
            {
                for (int dy = -1; dy <= 1; dy += 1)
                {
                    for (int dx = -1; dx <= 1; dx += 1)
                    {
                        // Skip center point and diagonal neighbors
                        if (Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz) != 1)
                            continue;

                        int nx = x + dx;
                        int ny = y + dy;
                        int nz = z + dz;

                        // Check if neighbor is out of bounds
                        if (nx < 0 || nx >= _w || ny < 0 || ny >= _h || nz < 0 || nz >= _d)
                            return true;

                        // Check if neighbor is not in material
                        if (!IsPointInMaterial(nx, ny, nz))
                            return true;
                    }
                }
            }

            return false;
        }

        // Draw failure point marker in the 3D view
        private void DrawFailurePointMarker(Graphics g, Func<float, float, float, PointF> projectPoint)
        {
            try
            {
                if (!_failureDetected || _failX < 0 || _failY < 0 || _failZ < 0)
                    return;

                // Project failure point to screen
                PointF failurePoint = projectPoint(_failX, _failY, _failZ);

                // Draw marker (cross with circle)
                int markerSize = 7;

                // Draw outer halo for better visibility
                using (Pen haloPen = new Pen(Color.FromArgb(80, 255, 255, 255), 3))
                {
                    g.DrawEllipse(haloPen,
                                 failurePoint.X - markerSize - 2,
                                 failurePoint.Y - markerSize - 2,
                                 (markerSize + 2) * 2,
                                 (markerSize + 2) * 2);
                }

                // Draw marker
                using (Pen pen = new Pen(Color.Red, 2))
                {
                    // Draw cross
                    g.DrawLine(pen,
                              failurePoint.X - markerSize,
                              failurePoint.Y,
                              failurePoint.X + markerSize,
                              failurePoint.Y);
                    g.DrawLine(pen,
                              failurePoint.X,
                              failurePoint.Y - markerSize,
                              failurePoint.X,
                              failurePoint.Y + markerSize);

                    // Draw circle
                    g.DrawEllipse(pen,
                                 failurePoint.X - markerSize,
                                 failurePoint.Y - markerSize,
                                 markerSize * 2,
                                 markerSize * 2);
                }

                // Fill center point
                using (SolidBrush brush = new SolidBrush(Color.Red))
                {
                    g.FillEllipse(brush,
                                 failurePoint.X - 3,
                                 failurePoint.Y - 3,
                                 6, 6);
                }

                // Add failure point text with background
                string label = $"Failure Point ({_failX},{_failY},{_failZ})";
                using (Font font = new Font("Arial", 8, FontStyle.Bold))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    SizeF textSize = g.MeasureString(label, font);

                    // Background
                    using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                    {
                        g.FillRectangle(bgBrush,
                                       failurePoint.X + markerSize + 2,
                                       failurePoint.Y - textSize.Height / 2,
                                       textSize.Width,
                                       textSize.Height);
                    }

                    // Text
                    g.DrawString(label, font, textBrush,
                                failurePoint.X + markerSize + 2,
                                failurePoint.Y - textSize.Height / 2);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error drawing failure point: {ex.Message}");
            }
        }

        // Draw a slice plane in 3D
        private void DrawSlicePlane(Graphics g, Func<float, float, float, PointF> projectPoint,
                                Axis axis, int slicePos, ColorMapMode mode)
        {
            try
            {
                // Skip if we're not showing slices
                if (!_showVolume) return;

                PointF[] corners = new PointF[4];

                // Get the corner points based on the axis
                switch (axis)
                {
                    case Axis.X:
                        corners[0] = projectPoint(slicePos, 0, 0);
                        corners[1] = projectPoint(slicePos, _h, 0);
                        corners[2] = projectPoint(slicePos, _h, _d);
                        corners[3] = projectPoint(slicePos, 0, _d);
                        break;

                    case Axis.Y:
                        corners[0] = projectPoint(0, slicePos, 0);
                        corners[1] = projectPoint(_w, slicePos, 0);
                        corners[2] = projectPoint(_w, slicePos, _d);
                        corners[3] = projectPoint(0, slicePos, _d);
                        break;

                    case Axis.Z:
                        corners[0] = projectPoint(0, 0, slicePos);
                        corners[1] = projectPoint(_w, 0, slicePos);
                        corners[2] = projectPoint(_w, _h, slicePos);
                        corners[3] = projectPoint(0, _h, slicePos);
                        break;
                }

                // Draw the slice plane with a semitransparent color based on mode
                Color planeColor;
                switch (mode)
                {
                    case ColorMapMode.Damage:
                        planeColor = Color.FromArgb(40, 255, 100, 100);
                        break;
                    case ColorMapMode.Stress:
                        planeColor = Color.FromArgb(40, 100, 100, 255);
                        break;
                    case ColorMapMode.Strain:
                        planeColor = Color.FromArgb(40, 100, 255, 100);
                        break;
                    default:
                        planeColor = Color.FromArgb(40, 200, 200, 200);
                        break;
                }

                using (SolidBrush brush = new SolidBrush(planeColor))
                {
                    g.FillPolygon(brush, corners);
                }

                // Draw the edge of the slice plane
                using (Pen pen = new Pen(Color.FromArgb(180, planeColor), 1.5f))
                {
                    g.DrawPolygon(pen, corners);
                }

                // Add a small label to identify the slice
                using (Font font = new Font("Arial", 8, FontStyle.Bold))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    string label = $"{axis}={slicePos}";

                    // Calculate center point
                    float cx = (corners[0].X + corners[1].X + corners[2].X + corners[3].X) / 4;
                    float cy = (corners[0].Y + corners[1].Y + corners[2].Y + corners[3].Y) / 4;

                    // Draw text with background for readability
                    SizeF textSize = g.MeasureString(label, font);
                    using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                    {
                        g.FillRectangle(bgBrush, cx - textSize.Width / 2, cy - textSize.Height / 2,
                                       textSize.Width, textSize.Height);
                    }

                    using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    {
                        g.DrawString(label, font, textBrush, cx, cy, sf);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error drawing slice plane: {ex.Message}");
            }
        }

        // Draw axes labels for orientation
        private void DrawAxesLabels(Graphics g, Func<float, float, float, PointF> projectPoint)
        {
            try
            {
                // Draw X, Y, Z axis indicators
                PointF origin = projectPoint(0, 0, 0);

                // Draw axes lines
                float axisLength = 30.0f;
                PointF xAxis = projectPoint(axisLength, 0, 0);
                PointF yAxis = projectPoint(0, axisLength, 0);
                PointF zAxis = projectPoint(0, 0, axisLength);

                using (Pen redPen = new Pen(Color.Red, 2f))
                using (Pen greenPen = new Pen(Color.Lime, 2f))
                using (Pen bluePen = new Pen(Color.DeepSkyBlue, 2f))
                {
                    // Draw three axis lines
                    g.DrawLine(redPen, origin, xAxis);
                    g.DrawLine(greenPen, origin, yAxis);
                    g.DrawLine(bluePen, origin, zAxis);
                }

                // Draw labels
                using (Font font = new Font("Arial", 9, FontStyle.Bold))
                {
                    // X-axis in red
                    using (SolidBrush brush = new SolidBrush(Color.Red))
                    {
                        g.DrawString("X", font, brush, xAxis.X, xAxis.Y);
                    }

                    // Y-axis in green
                    using (SolidBrush brush = new SolidBrush(Color.Lime))
                    {
                        g.DrawString("Y", font, brush, yAxis.X, yAxis.Y);
                    }

                    // Z-axis in blue
                    using (SolidBrush brush = new SolidBrush(Color.DeepSkyBlue))
                    {
                        g.DrawString("Z", font, brush, zAxis.X, zAxis.Y);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error drawing axes labels: {ex.Message}");
            }
        }

        // Draw failure point in the 3D view
        private void DrawFailurePointIn3DView(Graphics g, Rectangle rect)
        {
            if (!_failureDetected) return;

            try
            {
                // Create 3D transform
                Matrix3D transform = new Matrix3D();
                transform.RotateX(_rotX * (float)Math.PI / 180);
                transform.RotateY(_rotY * (float)Math.PI / 180);

                // Calculate center and scale
                float centerX = rect.X + rect.Width / 2f + _pan.X;
                float centerY = rect.Y + rect.Height / 2f + _pan.Y;
                float scale = Math.Min(rect.Width, rect.Height) /
                            Math.Max(Math.Max(_w, _h), _d) * _zoom * 0.8f;

                // Project failure point
                PointF failurePoint = new PointF();
                {
                    // Center coordinates
                    float fx = _failX - _w / 2f;
                    float fy = _failY - _h / 2f;
                    float fz = _failZ - _d / 2f;

                    // Apply 3D transformation
                    Vector3 v = transform.Transform(new Vector3(fx, fy, fz));

                    // Project to 2D
                    failurePoint = new PointF(
                        centerX + v.X * scale,
                        centerY + v.Y * scale
                    );
                }

                // Draw lines to failure point from three axes
                using (Pen dashPen = new Pen(Color.FromArgb(120, 255, 100, 100), 1))
                {
                    dashPen.DashStyle = DashStyle.Dash;

                    // Project points at the same X, Y, Z as failure point on each axis
                    PointF xAxisPoint = GetProjectedPoint(transform, _failX, 0, 0, centerX, centerY, scale);
                    PointF yAxisPoint = GetProjectedPoint(transform, 0, _failY, 0, centerX, centerY, scale);
                    PointF zAxisPoint = GetProjectedPoint(transform, 0, 0, _failZ, centerX, centerY, scale);

                    // Draw guide lines to each axis
                    g.DrawLine(dashPen, failurePoint, xAxisPoint);
                    g.DrawLine(dashPen, failurePoint, yAxisPoint);
                    g.DrawLine(dashPen, failurePoint, zAxisPoint);
                }

                // Draw failure point marker with halo effect for better visibility
                int markerSize = 6;

                // Draw outer halo
                using (Pen haloPen = new Pen(Color.FromArgb(80, 255, 255, 255), 4))
                {
                    g.DrawEllipse(haloPen,
                        failurePoint.X - markerSize - 2,
                        failurePoint.Y - markerSize - 2,
                        (markerSize + 2) * 2,
                        (markerSize + 2) * 2);
                }

                // Draw marker
                using (Pen pen = new Pen(Color.Red, 2))
                {
                    g.DrawLine(pen, failurePoint.X - markerSize, failurePoint.Y, failurePoint.X + markerSize, failurePoint.Y);
                    g.DrawLine(pen, failurePoint.X, failurePoint.Y - markerSize, failurePoint.X, failurePoint.Y + markerSize);
                    g.DrawEllipse(pen, failurePoint.X - markerSize, failurePoint.Y - markerSize, markerSize * 2, markerSize * 2);
                }

                // Fill marker
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(200, 255, 50, 50)))
                {
                    g.FillEllipse(brush, failurePoint.X - markerSize + 1, failurePoint.Y - markerSize + 1,
                                 (markerSize - 1) * 2, (markerSize - 1) * 2);
                }

                // Draw label with background for better readability
                using (Font font = new Font("Arial", 9, FontStyle.Bold))
                {
                    string label = $"Failure ({_failX},{_failY},{_failZ})";
                    SizeF textSize = g.MeasureString(label, font);

                    float labelX = failurePoint.X + markerSize + 4;
                    float labelY = failurePoint.Y - textSize.Height / 2;

                    // Draw text background
                    using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
                    {
                        g.FillRectangle(bgBrush, labelX - 2, labelY - 2, textSize.Width + 4, textSize.Height + 4);
                    }

                    // Draw text
                    using (SolidBrush textBrush = new SolidBrush(Color.White))
                    {
                        g.DrawString(label, font, textBrush, labelX, labelY);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error drawing failure point: {ex.Message}");
            }
        }

        // Draw failure point in 2D slice view
        private void DrawFailurePointInSlice(Graphics g, Rectangle sliceRect)
        {
            try
            {
                // Project to 2D coordinates in slice view
                float xRatio, yRatio;
                switch (_axis)
                {
                    case Axis.X:
                        xRatio = (float)_failZ / (_d - 1);
                        yRatio = (float)_failY / (_h - 1);
                        break;
                    case Axis.Y:
                        xRatio = (float)_failX / (_w - 1);
                        yRatio = (float)_failZ / (_d - 1);
                        break;
                    default: // Z
                        xRatio = (float)_failX / (_w - 1);
                        yRatio = (float)_failY / (_h - 1);
                        break;
                }

                // Convert to screen coordinates
                int markerX = sliceRect.X + (int)(xRatio * sliceRect.Width);
                int markerY = sliceRect.Y + (int)(yRatio * sliceRect.Height);

                // Draw marker
                using (Pen markerPen = new Pen(Color.Red, 2))
                {
                    g.DrawLine(markerPen, markerX - 10, markerY, markerX + 10, markerY);
                    g.DrawLine(markerPen, markerX, markerY - 10, markerX, markerY + 10);
                    g.DrawEllipse(markerPen, markerX - 5, markerY - 5, 10, 10);
                }

                // Draw label
                using (Font font = new Font("Arial", 8, FontStyle.Bold))
                using (SolidBrush brush = new SolidBrush(Color.White))
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Near })
                {
                    g.DrawString("Failure Point", font, brush, markerX + 15, markerY - 5, sf);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error drawing failure point in slice: {ex.Message}");
            }
        }

        // Helper method to project a 3D point to 2D
        private PointF GetProjectedPoint(Matrix3D transform, float x, float y, float z,
                                      float centerX, float centerY, float scale)
        {
            // Center coordinates
            float fx = x - _w / 2f;
            float fy = y - _h / 2f;
            float fz = z - _d / 2f;

            // Apply 3D transformation
            Vector3 v = transform.Transform(new Vector3(fx, fy, fz));

            // Project to 2D
            return new PointF(
                centerX + v.X * scale,
                centerY + v.Y * scale
            );
        }

        // Draw the controls info area
        private void DrawControlsInfo(Graphics g, Rectangle rect)
        {
            try
            {
                // Draw background
                using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(60, 60, 60)))
                {
                    g.FillRectangle(bgBrush, rect);
                }

                // Draw controls info text
                using (Font font = new Font("Arial", 9))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    string axisName = _axis.ToString();
                    string modeName = _mode.ToString();
                    int slicePos = (_axis == Axis.X) ? GetSliceX() :
                                   (_axis == Axis.Y) ? GetSliceY() : GetSliceZ();

                    // Get the value ranges for better info display
                    double minValue = GetMinValueForMode(_mode);
                    double maxValue = GetMaxValueForMode(_mode);

                    // Format axis and value info
                    string info = $"View: {modeName} ({minValue:F3}-{maxValue:F3}) | Axis: {axisName} | Position: {slicePos}";

                    if (_failureDetected)
                    {
                        info += $" | Failure at: ({_failX},{_failY},{_failZ})";
                    }

                    g.DrawString(info, font, brush, rect.X + 5, rect.Y + 5);

                    // Draw interaction hint
                    g.DrawString("X/Y/Z: Select slice axis | Drag: Rotate/Pan | Scroll: Zoom",
                                font, brush, rect.X + 5, rect.Y + 22);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error drawing controls info: {ex.Message}");
            }
        }

        // Draw the color legend
        private void DrawColorLegend(Graphics g, int x, int y, int width, int height, ColorMapMode mode)
        {
            try
            {
                // Use cached bitmap if available or create a new one
                string cacheKey = $"{mode}_{width}_{height}";
                Bitmap legendBmp = GetCachedLegendBitmap(cacheKey, width, height, mode);

                // Draw the bitmap
                g.DrawImage(legendBmp, x, y);
            }
            catch (Exception ex)
            {
                // Fallback to simple drawing if caching fails
                Logger.Log($"[FailurePointVisualizer] Error drawing color legend: {ex.Message}");

                // Simple gradient
                using (LinearGradientBrush gradientBrush = new LinearGradientBrush(
                    new Rectangle(x, y, width - 50, height),
                    GetColorForMode(0, mode),
                    GetColorForMode(1, mode),
                    LinearGradientMode.Horizontal))
                {
                    g.FillRectangle(gradientBrush, x, y, width - 50, height);
                }

                // Draw labels
                using (Font font = new Font("Arial", 8))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    string minLabel = $"{GetMinValueForMode(mode):F3}";
                    string maxLabel = $"{GetMaxValueForMode(mode):F3}";
                    g.DrawString(minLabel, font, brush, x, y + height + 2);
                    g.DrawString(maxLabel, font, brush, x + width - 70, y + height + 2);
                    g.DrawString(mode.ToString(), font, brush, x + width - 40, y);
                }
            }
        }

        // Get or create a cached legend bitmap
        private Bitmap GetCachedLegendBitmap(string cacheKey, int width, int height, ColorMapMode mode)
        {
            // Check if we have a cached version
            if (_legendBitmapCache.TryGetValue(cacheKey, out Bitmap cachedBitmap))
            {
                return cachedBitmap;
            }

            // Create a new bitmap with some extra height for labels
            Bitmap legendBmp = new Bitmap(width, height + 20, PixelFormat.Format32bppArgb);

            using (Graphics g = Graphics.FromImage(legendBmp))
            {
                g.Clear(Color.Transparent);

                // Get proper min/max values for the mode
                double minValue = GetMinValueForMode(mode);
                double maxValue = GetMaxValueForMode(mode);

                // Draw gradient
                using (LinearGradientBrush gradientBrush = new LinearGradientBrush(
                    new Rectangle(0, 0, width - 50, height),
                    GetColorForMode(0, mode),
                    GetColorForMode(1, mode),
                    LinearGradientMode.Horizontal))
                {
                    g.FillRectangle(gradientBrush, 0, 0, width - 50, height);
                }

                // Draw border
                using (Pen pen = new Pen(Color.White, 1))
                {
                    g.DrawRectangle(pen, 0, 0, width - 50, height);
                }

                // Draw labels
                using (Font font = new Font("Arial", 8))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    string minLabel;
                    string maxLabel;
                    string midLabel;

                    switch (mode)
                    {
                        case ColorMapMode.Damage:
                            minLabel = minValue.ToString("F3");
                            maxLabel = maxValue.ToString("F3");
                            midLabel = ((minValue + maxValue) / 2).ToString("F3");
                            break;
                        case ColorMapMode.Stress:
                            minLabel = minValue.ToString("F1") + " MPa";
                            maxLabel = maxValue.ToString("F1") + " MPa";
                            midLabel = ((minValue + maxValue) / 2).ToString("F1") + " MPa";
                            break;
                        case ColorMapMode.Strain:
                            minLabel = minValue.ToString("F4");
                            maxLabel = maxValue.ToString("F4");
                            midLabel = ((minValue + maxValue) / 2).ToString("F4");
                            break;
                        default:
                            minLabel = "Min";
                            maxLabel = "Max";
                            midLabel = "Mid";
                            break;
                    }

                    // Draw value labels at min, middle, and max
                    g.DrawString(minLabel, font, brush, 0, height + 2);
                    g.DrawString(midLabel, font, brush, (width - 50) / 2 - 15, height + 2);
                    g.DrawString(maxLabel, font, brush, width - 80, height + 2);

                    // Draw mode label
                    g.DrawString(mode.ToString(), font, brush, width - 45, 4);
                }
            }

            // Limit cache size - clear if too many items
            if (_legendBitmapCache.Count > 10)
            {
                // Dispose all bitmaps
                foreach (var bitmap in _legendBitmapCache.Values)
                {
                    bitmap?.Dispose();
                }
                _legendBitmapCache.Clear();
            }

            // Cache the bitmap
            _legendBitmapCache[cacheKey] = legendBmp;

            return legendBmp;
        }

        #endregion

        #region Event Handlers

        // Host paint handler
        private void Host_Paint(object sender, PaintEventArgs e)
        {
            try
            {
                // If we have a cached visualization, use it
                if (_failurePointCache != null)
                {
                    e.Graphics.DrawImage(_failurePointCache, 0, 0, _host.Width, _host.Height);
                    return;
                }

                // Check if we have data
                if (_dmg == null)
                {
                    DrawNoDataMessage(e.Graphics, _host.ClientRectangle);
                    return;
                }

                // PROBLEM: We show "Generating" for every little drag operation
                // FIX: Add quick preview rendering for interactive operations
                if (_isRendering && (_isLeftDragging || _isRightDragging))
                {
                    // For interactive operations, draw a quick preview instead of "Generating..."
                    DrawQuickPreview(e.Graphics, _host.ClientRectangle);
                    return;
                }

                // Start rendering in background
                RenderVisualizationAsync();

                // Draw a placeholder while rendering
                DrawRenderingMessage(e.Graphics, _host.ClientRectangle);
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error in paint handler: {ex.Message}");
                DrawErrorMessage(e.Graphics, _host.ClientRectangle, ex.Message);
            }
        }
        private void DrawQuickPreview(Graphics g, Rectangle rect)
        {
            g.Clear(Color.FromArgb(30, 30, 40));

            try
            {
                // Draw low-quality preview with just basic wireframe
                Matrix3D transform = new Matrix3D();
                transform.RotateX(_rotX * (float)Math.PI / 180);
                transform.RotateY(_rotY * (float)Math.PI / 180);

                // Quick function to project a point
                Func<float, float, float, PointF> projectPoint = (x, y, z) => {
                    float centerX = rect.Width / 2f + _pan.X;
                    float centerY = rect.Height / 2f + _pan.Y;
                    float scale = Math.Min(rect.Width, rect.Height) /
                                Math.Max(Math.Max(_w, _h), _d) * _zoom * 0.8f;

                    Vector3 v = transform.Transform(new Vector3(
                        x - _w / 2f,
                        y - _h / 2f,
                        z - _d / 2f
                    ));

                    return new PointF(
                        centerX + v.X * scale,
                        centerY + v.Y * scale
                    );
                };

                // Just draw the volume bounding box
                DrawVolumeOutline(g, projectPoint);

                // Draw "Interactive preview" message
                using (Font font = new Font("Arial", 10))
                using (SolidBrush brush = new SolidBrush(Color.White))
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center })
                {
                    g.DrawString("Interactive preview - release mouse to render",
                                font, brush, rect.Width / 2, rect.Height - 20, sf);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error drawing preview: {ex.Message}");
            }
        }

        // Draw message when no data is available
        private void DrawNoDataMessage(Graphics g, Rectangle rect)
        {
            g.Clear(Color.FromArgb(30, 30, 30));
            using (Font font = new Font("Arial", 12))
            using (SolidBrush brush = new SolidBrush(Color.White))
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString("No data available. Run simulation first.", font, brush, rect, sf);
            }
        }

        // Draw message when rendering is in progress
        private void DrawRenderingMessage(Graphics g, Rectangle rect)
        {
            g.Clear(Color.FromArgb(30, 30, 40));
            using (Font font = new Font("Arial", 12))
            using (SolidBrush brush = new SolidBrush(Color.White))
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString("Generating visualization...", font, brush, rect, sf);
            }
        }

        // Draw error message
        private void DrawErrorMessage(Graphics g, Rectangle rect, string message)
        {
            g.Clear(Color.FromArgb(40, 30, 30));
            using (Font font = new Font("Arial", 10))
            using (SolidBrush brush = new SolidBrush(Color.Red))
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString($"Error: {message}", font, brush, rect, sf);
            }
        }

        // Asynchronously render the visualization
        private void RenderVisualizationAsync()
        {
            // Don't start another render if one is in progress
            if (_isRendering)
                return;

            // Create a new cancellation token source
            _renderCancellation?.Dispose();
            _renderCancellation = new CancellationTokenSource();
            var token = _renderCancellation.Token;

            // Set rendering flag
            _isRendering = true;

            // Start rendering in background
            Task.Run(() =>
            {
                try
                {
                    // Check if we should render (not while dragging)
                    if (_isLeftDragging || _isRightDragging)
                    {
                        _isRendering = false;
                        return;
                    }

                    // Clear memory
                    EnsureMemoryAvailable();

                    // Create the visualization
                    var colorMode = _mode;
                    Bitmap visualization = CreateVisualization(_host.Width, _host.Height, colorMode);

                    // Update the UI
                    if (!token.IsCancellationRequested && _host.IsHandleCreated)
                    {
                        _host.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                // Clear existing cache
                                lock (_renderLock)
                                {
                                    if (_failurePointCache != null)
                                    {
                                        _failurePointCache.Dispose();
                                    }
                                    _failurePointCache = visualization;
                                }

                                // FIX: Always invalidate when rendering completes
                                _isRendering = false;
                                _host.Invalidate();
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"[FailurePointVisualizer] Error updating UI: {ex.Message}");
                                _isRendering = false;
                            }
                        }));
                    }
                    else
                    {
                        // Cancelled or host disposed
                        visualization?.Dispose();
                        _isRendering = false;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[FailurePointVisualizer] Error rendering visualization: {ex.Message}");
                    _isRendering = false;

                    // FIX: Invalidate on error to remove "Generating" message
                    if (_host != null && _host.IsHandleCreated)
                    {
                        _host.BeginInvoke(new Action(() => _host.Invalidate()));
                    }
                }
            }, token);

            // Add timeout to prevent hanging
            Task.Delay(PerformanceTuning.VISUALIZATION_TIMEOUT, token).ContinueWith(t =>
            {
                if (!t.IsCanceled && _isRendering)
                {
                    Logger.Log("[FailurePointVisualizer] Visualization timeout - cancelling");
                    _renderCancellation?.Cancel();
                    _isRendering = false;

                    // FIX: Invalidate after timeout to remove "Generating" message
                    if (_host != null && _host.IsHandleCreated)
                    {
                        _host.BeginInvoke(new Action(() => _host.Invalidate()));
                    }
                }
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        // Mouse handlers for 3D interaction
        private void Host_MouseDown(object sender, MouseEventArgs e)
        {
            _lastMousePosition = e.Location;

            if (e.Button == MouseButtons.Left)
            {
                _isLeftDragging = true;
            }
            else if (e.Button == MouseButtons.Right)
            {
                _isRightDragging = true;
            }
        }

        private void Host_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isLeftDragging)
            {
                // Rotate the view
                _rotY += (e.X - _lastMousePosition.X) * 0.5f;
                _rotX += (e.Y - _lastMousePosition.Y) * 0.5f;

                // Limit rotation
                _rotX = Math.Max(-90, Math.Min(90, _rotX));

                _lastMousePosition = e.Location;

                // During dragging, just invalidate but don't clear the cache
                // This will use our quick preview rendering
                _host?.Invalidate();
            }
            else if (_isRightDragging)
            {
                // Pan the view
                _pan.X += (e.X - _lastMousePosition.X);
                _pan.Y += (e.Y - _lastMousePosition.Y);

                _lastMousePosition = e.Location;

                // During dragging, just invalidate but don't clear the cache
                // This will use our quick preview rendering
                _host?.Invalidate();
            }
        }
        public void DrawPreview(Graphics g, Rectangle rect)
        {
            g.Clear(Color.FromArgb(30, 30, 40));

            try
            {
                // Draw basic wireframe outline only
                Matrix3D transform = new Matrix3D();
                transform.RotateX(_rotX * (float)Math.PI / 180);
                transform.RotateY(_rotY * (float)Math.PI / 180);

                // Quick function to project a point
                Func<float, float, float, PointF> projectPoint = (x, y, z) => {
                    float centerX = rect.Width / 2f + _pan.X;
                    float centerY = rect.Height / 2f + _pan.Y;
                    float scale = Math.Min(rect.Width, rect.Height) /
                                Math.Max(Math.Max(_w, _h), _d) * _zoom * 0.8f;

                    Vector3 v = transform.Transform(new Vector3(
                        x - _w / 2f,
                        y - _h / 2f,
                        z - _d / 2f
                    ));

                    return new PointF(
                        centerX + v.X * scale,
                        centerY + v.Y * scale
                    );
                };

                // Draw volume outline
                DrawVolumeOutline(g, projectPoint);

                // Draw failure point if detected
                if (_failureDetected)
                {
                    DrawFailurePointMarker(g, projectPoint);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FailurePointVisualizer] Error drawing preview: {ex.Message}");
            }
        }
        private void Host_MouseUp(object sender, MouseEventArgs e)
        {
            bool wasLeftDragging = _isLeftDragging;
            bool wasRightDragging = _isRightDragging;

            _isLeftDragging = false;
            _isRightDragging = false;

            // Force a final high-quality render when dragging stops
            if (wasLeftDragging || wasRightDragging)
            {
                // Clear cache to force redraw
                lock (_renderLock)
                {
                    if (_failurePointCache != null)
                    {
                        _failurePointCache.Dispose();
                        _failurePointCache = null;
                    }
                }

                // Force redraw now that we're not dragging
                _host?.Invalidate();
            }
        }

        private void Host_MouseWheel(object sender, MouseEventArgs e)
        {
            // Zoom in/out
            _zoom *= (e.Delta > 0) ? 1.1f : 0.9f;

            // Limit zoom
            _zoom = Math.Max(0.1f, Math.Min(5.0f, _zoom));

            InvalidateVisualization();
        }

        #endregion

        #region Helper Classes and Structs

        // High-performance direct bitmap class for faster rendering
        private class DirectBitmap : IDisposable
        {
            public Bitmap Bitmap { get; private set; }
            public Int32[] Bits { get; private set; }
            public bool Disposed { get; private set; }
            public int Height { get; private set; }
            public int Width { get; private set; }

            protected GCHandle BitsHandle { get; private set; }

            public DirectBitmap(int width, int height)
            {
                Width = width;
                Height = height;
                Bits = new Int32[width * height];
                BitsHandle = GCHandle.Alloc(Bits, GCHandleType.Pinned);
                Bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, BitsHandle.AddrOfPinnedObject());
            }

            public void SetPixel(int x, int y, Color color)
            {
                int index = x + (y * Width);
                if (index >= 0 && index < Bits.Length)
                {
                    Bits[index] = color.ToArgb();
                }
            }

            public Color GetPixel(int x, int y)
            {
                int index = x + (y * Width);
                if (index >= 0 && index < Bits.Length)
                {
                    int col = Bits[index];
                    return Color.FromArgb(col);
                }
                return Color.Black;
            }

            public void Dispose()
            {
                if (Disposed) return;
                Disposed = true;
                Bitmap.Dispose();
                BitsHandle.Free();
            }
        }

        // Vector3 struct for 3D transformations
        private struct Vector3
        {
            public float X, Y, Z;

            public Vector3(float x, float y, float z)
            {
                X = x;
                Y = y;
                Z = z;
            }
        }

        // Simple 3D transformation matrix
        private class Matrix3D
        {
            private float[] m = new float[16]; // 4x4 matrix in column-major order

            public Matrix3D()
            {
                // Initialize as identity matrix
                m[0] = 1; m[4] = 0; m[8] = 0; m[12] = 0;
                m[1] = 0; m[5] = 1; m[9] = 0; m[13] = 0;
                m[2] = 0; m[6] = 0; m[10] = 1; m[14] = 0;
                m[3] = 0; m[7] = 0; m[11] = 0; m[15] = 1;
            }

            public void RotateX(float angle)
            {
                float c = (float)Math.Cos(angle);
                float s = (float)Math.Sin(angle);

                float m1 = m[1], m5 = m[5], m9 = m[9], m13 = m[13];
                float m2 = m[2], m6 = m[6], m10 = m[10], m14 = m[14];

                m[1] = m1 * c + m2 * s;
                m[5] = m5 * c + m6 * s;
                m[9] = m9 * c + m10 * s;
                m[13] = m13 * c + m14 * s;

                m[2] = m2 * c - m1 * s;
                m[6] = m6 * c - m5 * s;
                m[10] = m10 * c - m9 * s;
                m[14] = m14 * c - m13 * s;
            }

            public void RotateY(float angle)
            {
                float c = (float)Math.Cos(angle);
                float s = (float)Math.Sin(angle);

                float m0 = m[0], m4 = m[4], m8 = m[8], m12 = m[12];
                float m2 = m[2], m6 = m[6], m10 = m[10], m14 = m[14];

                m[0] = m0 * c - m2 * s;
                m[4] = m4 * c - m6 * s;
                m[8] = m8 * c - m10 * s;
                m[12] = m12 * c - m14 * s;

                m[2] = m0 * s + m2 * c;
                m[6] = m4 * s + m6 * c;
                m[10] = m8 * s + m10 * c;
                m[14] = m12 * s + m14 * c;
            }

            public Vector3 Transform(Vector3 v)
            {
                return new Vector3(
                    m[0] * v.X + m[4] * v.Y + m[8] * v.Z + m[12],
                    m[1] * v.X + m[5] * v.Y + m[9] * v.Z + m[13],
                    m[2] * v.X + m[6] * v.Y + m[10] * v.Z + m[14]
                );
            }
        }

        // Helper method to clamp values to a range
        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
        #endregion
    }
}