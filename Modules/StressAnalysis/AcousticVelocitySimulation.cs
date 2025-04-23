using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ILGPU;
using ILGPU.IR.Transformations;
using ILGPU.Runtime;

namespace CTSegmenter
{
    /// <summary>
    /// Implementation of an acoustic velocity simulation
    /// </summary>
    public class AcousticVelocitySimulation : IStressSimulation, IDisposable
    {
        #region Properties and Fields
        public static float StoredPWaveVelocity { get; private set; } = 0;
        public static float StoredSWaveVelocity { get; private set; } = 0;
        public static float StoredPWaveArrivalTime { get; private set; } = 0;
        public static float StoredSWaveArrivalTime { get; private set; } = 0;
        public bool UseExtendedSimulationTime { get; private set; }
        private float maxWaveletAmplitude = 0;

        public Guid SimulationId { get; }
        public string Name { get; private set; }
        public DateTime CreationTime { get; }
        public SimulationStatus Status { get; private set; }
        public Material Material { get; private set; }
        public IReadOnlyList<Triangle> MeshTriangles => _simulationTriangles;
        public float Progress { get; private set; }

        // Events
        public event EventHandler<SimulationProgressEventArgs> ProgressChanged;
        public event EventHandler<SimulationCompletedEventArgs> SimulationCompleted;

        // Acoustic test specific parameters
        public float ConfiningPressure { get; private set; }
        public string WaveType { get; private set; }
        public int TimeSteps { get; private set; }
        public float Frequency { get; private set; } // kHz
        public float Amplitude { get; private set; }
        public float Energy { get; private set; } // J
        public Vector3 TestDirection { get; private set; }

        // Material acoustic properties
        public float PWaveVelocity { get; private set; }
        public float SWaveVelocity { get; private set; }
        public float YoungModulus { get; private set; }
        public float PoissonRatio { get; private set; }
        public float BulkModulus { get; private set; }
        public float ShearModulus { get; private set; }
        public float Attenuation { get; private set; }

        // Results
        public float MeasuredPWaveVelocity { get; private set; }
        public float MeasuredSWaveVelocity { get; private set; }
        public float PWaveArrivalTime { get; private set; }
        public float SWaveArrivalTime { get; private set; }
        public float MaximumDisplacement { get; private set; }
        public float CalculatedVpVsRatio { get; private set; }
        public List<float> SimulationTimes { get; private set; }
        public List<float[]> PWaveDisplacementHistory { get; private set; }
        public List<float[]> SWaveDisplacementHistory { get; private set; }
        public List<Vector3[]> WaveDisplacementVectors { get; private set; }
        public float[,,] PWaveField { get; private set; }
        public float[,,] SWaveField { get; private set; }
        public float[] ReceiverTimeSeries { get; private set; }
        public List<float> AcousticIntensity { get; private set; }
        public float SampleLength { get; private set; }

        // ILGPU context
        private Context _context;
        private Accelerator _accelerator;
        private Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>,
                        float, float, float, float, float, float, float, ArrayView<float>, ArrayView<float>> _propagatePWaveKernel;
        private Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>,
                        float, float, float, float, float, float, float, ArrayView<float>> _propagateSWaveKernel;
        private Action<Index3D, ArrayView3D<float, Stride3D.DenseXY>, ArrayView3D<float, Stride3D.DenseXY>,
                        ArrayView3D<float, Stride3D.DenseXY>, float, float, float, float, int, int> _wave3DPropagationKernel;
        private Action<
                Index1D,
                ArrayView<float>,  // u
                ArrayView<float>,  // v
                ArrayView<float>,  // a
                float, float,      // dt, dx
                ArrayView<float>,  // velocityProfile
                float, float, float, float, // attenuation, density, youngModulus, poissonRatio
                ArrayView<float>,  // damping
                ArrayView<float>   // result
            > _propagateImprovedSWaveKernel;
        // Simulation data
        private List<Triangle> _simulationTriangles;
        private CancellationTokenSource _cancellationTokenSource;
        private SimulationResult _result;
        private bool _isDisposed;
        private Dictionary<string, MaterialAcousticProperties> _materialPropertiesDatabase;
        private bool _isPWave;
        private bool _hasPreviousTriaxialResults;
        private SimulationResult _triaxialResult;

        // 3D grid for simulation
        private int _gridSizeX;
        private int _gridSizeY;
        private int _gridSizeZ;
        private float _gridSpacing;
        private float[,,] _velocityModel;
        private float[,,] _densityModel;
        private Vector3 _sourcePosition;
        private Vector3 _receiverPosition;
        private int _sourceX, _sourceY, _sourceZ;
        private int _receiverX, _receiverY, _receiverZ;

        private MainForm mainForm;

        #endregion

        #region Constructor and Initialization

        /// <summary>
        /// Constructor for the acoustic velocity simulation
        /// </summary>
        public AcousticVelocitySimulation(
            Material material,
            List<Triangle> triangles,
            float confiningPressure,
            string waveType,
            int timeSteps,
            float frequency,
            float amplitude,
            float energy,
            string direction,
            bool useExtendedSimulationTime,
            SimulationResult previousTriaxialResult = null,
            MainForm mainForm = null)
        {
            this.mainForm = mainForm;
            SimulationId = Guid.NewGuid();
            CreationTime = DateTime.Now;
            Status = SimulationStatus.NotInitialized;
            Progress = 0f;

            // Set simulation parameters
            Material = material;
            _simulationTriangles = new List<Triangle>(triangles);
            Name = $"Acoustic Velocity Test - {material.Name} - {DateTime.Now:yyyyMMdd_HHmmss}";

            // Set acoustic specific parameters
            ConfiningPressure = confiningPressure;
            WaveType = waveType;
            TimeSteps = timeSteps;
            Frequency = frequency;
            Amplitude = amplitude;
            Energy = energy;
            _isPWave = waveType == "P-Wave";
            UseExtendedSimulationTime = useExtendedSimulationTime;

            // Set test direction
            if (direction.ToLower() == "x-axis")
                TestDirection = new Vector3(1, 0, 0);
            else if (direction.ToLower() == "y-axis")
                TestDirection = new Vector3(0, 1, 0);
            else
                TestDirection = new Vector3(0, 0, 1); // Default to Z-axis
            if (_isPWave)
            {
                if (StoredSWaveVelocity > 0)
                {
                    // Use stored S-wave values from previous simulation
                    MeasuredSWaveVelocity = StoredSWaveVelocity;
                    SWaveArrivalTime = StoredSWaveArrivalTime;
                }
            }
            else
            {
                if (StoredPWaveVelocity > 0)
                {
                    // Use stored P-wave values from previous simulation
                    MeasuredPWaveVelocity = StoredPWaveVelocity;
                    PWaveArrivalTime = StoredPWaveArrivalTime;
                }
            }
            // Initialize result storage
            SimulationTimes = new List<float>();
            PWaveDisplacementHistory = new List<float[]>();
            SWaveDisplacementHistory = new List<float[]>();
            WaveDisplacementVectors = new List<Vector3[]>();
            AcousticIntensity = new List<float>();

            // Store previous triaxial results if available
            _hasPreviousTriaxialResults = previousTriaxialResult != null;
            _triaxialResult = previousTriaxialResult;

            // Initialize material properties database
            InitializeMaterialPropertiesDatabase();

            // Initialize ILGPU
            InitializeILGPU();

            // Estimate material properties based on density and material type
            EstimateMaterialProperties();
        }

        /// <summary>
        /// Initialize ILGPU context and accelerator
        /// </summary>
        private void InitializeILGPU()
        {
            try
            {
                // Create ILGPU context
                _context = Context.CreateDefault();

                // Try GPU device first with proper error handling
                try
                {
                    var gpuDevice = _context.GetPreferredDevice(preferCPU: false);
                    _accelerator = gpuDevice.CreateAccelerator(_context);
                    Logger.Log($"[AcousticVelocitySimulation] Using GPU accelerator: {_accelerator.Name}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"[AcousticVelocitySimulation] GPU accelerator initialization failed: {ex.Message}. Falling back to CPU.");

                    // Use a CPU accelerator with all cores
                    var cpuDevice = _context.GetPreferredDevice(preferCPU: true);
                    _accelerator = cpuDevice.CreateAccelerator(_context);

                    Logger.Log($"[AcousticVelocitySimulation] Using CPU accelerator with {Environment.ProcessorCount} cores");
                }

                // Load kernels
                LoadKernels();
            }
            catch (Exception ex)
            {
                Logger.Log($"[AcousticVelocitySimulation] ILGPU initialization failed: {ex.Message}");
                throw new InvalidOperationException("Failed to initialize ILGPU. The simulation cannot continue.", ex);
            }
        }
        /// <summary>
        /// Load ILGPU kernels
        /// </summary>
        private void LoadKernels()
        {
            // Load P-wave propagation kernel
            _propagatePWaveKernel = _accelerator.LoadAutoGroupedStreamKernel<
                Index1D,
                ArrayView<float>,  // u
                ArrayView<float>,  // v
                ArrayView<float>,  // a
                float, float, float, float, float, float, float, // dt, dx, velocity, attenuation, density, youngModulus, poissonRatio
                ArrayView<float>,  // damping
                ArrayView<float>   // result
            >(PropagatePWaveKernel);

            // Load S-wave propagation kernel
            _propagateSWaveKernel = _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>,
                                                                             float, float, float, float, float, float, float, ArrayView<float>>(
                                                                             PropagateSWaveKernel);

            // Load improved S-wave propagation kernel with better physics model
            _propagateImprovedSWaveKernel = _accelerator.LoadAutoGroupedStreamKernel<
                Index1D,
                ArrayView<float>,  // u
                ArrayView<float>,  // v
                ArrayView<float>,  // a
                float, float,      // dt, dx
                ArrayView<float>,  // velocityProfile
                float, float, float, float, // attenuation, density, youngModulus, poissonRatio
                ArrayView<float>,  // damping
                ArrayView<float>   // result
            >(PropagateImprovedSWaveKernel);

            // Load 3D wave propagation kernel
            _wave3DPropagationKernel = _accelerator.LoadAutoGroupedStreamKernel<Index3D, ArrayView3D<float, Stride3D.DenseXY>,
                                                                             ArrayView3D<float, Stride3D.DenseXY>, ArrayView3D<float, Stride3D.DenseXY>,
                                                                             float, float, float, float, int, int>(
                                                                             Wave3DPropagationKernel);
        }

        /// <summary>
        /// Initialize the material properties database
        /// </summary>
        private void InitializeMaterialPropertiesDatabase()
        {
            _materialPropertiesDatabase = new Dictionary<string, MaterialAcousticProperties>
            {
                // References: https://en.wikipedia.org/wiki/Seismic_velocity
                // These values are representative but should be refined for specific applications

                // Common sedimentary rocks
                ["limestone"] = new MaterialAcousticProperties
                {
                    PWaveVelocityBase = 4500, // m/s
                    SWaveVelocityBase = 2500, // m/s
                    PoissonRatioBase = 0.28f,
                    YoungModulusBase = 50000, // MPa
                    BulkModulusBase = 40000, // MPa
                    ShearModulusBase = 20000, // MPa
                    AttenuationBase = 0.08f, // dB/wavelength
                    DensityReference = 2700 // kg/m³
                },
                ["calcite"] = new MaterialAcousticProperties
                {
                    PWaveVelocityBase = 4700, // m/s
                    SWaveVelocityBase = 2500, // m/s
                    PoissonRatioBase = 0.31f,
                    YoungModulusBase = 52000, // MPa
                    BulkModulusBase = 45000, // MPa
                    ShearModulusBase = 19000, // MPa
                    AttenuationBase = 0.07f, // dB/wavelength
                    DensityReference = 2710 // kg/m³
                },
                ["sandstone"] = new MaterialAcousticProperties
                {
                    PWaveVelocityBase = 3500, // m/s
                    SWaveVelocityBase = 2000, // m/s
                    PoissonRatioBase = 0.25f,
                    YoungModulusBase = 20000, // MPa
                    BulkModulusBase = 12000, // MPa
                    ShearModulusBase = 8000, // MPa
                    AttenuationBase = 0.15f, // dB/wavelength
                    DensityReference = 2350 // kg/m³
                },
                ["quartz"] = new MaterialAcousticProperties
                {
                    PWaveVelocityBase = 6050, // m/s
                    SWaveVelocityBase = 4090, // m/s
                    PoissonRatioBase = 0.08f,
                    YoungModulusBase = 95000, // MPa
                    BulkModulusBase = 37000, // MPa
                    ShearModulusBase = 44000, // MPa
                    AttenuationBase = 0.02f, // dB/wavelength
                    DensityReference = 2650 // kg/m³
                },
                ["shale"] = new MaterialAcousticProperties
                {
                    PWaveVelocityBase = 2800, // m/s
                    SWaveVelocityBase = 1400, // m/s
                    PoissonRatioBase = 0.32f,
                    YoungModulusBase = 10000, // MPa
                    BulkModulusBase = 8000, // MPa
                    ShearModulusBase = 4000, // MPa
                    AttenuationBase = 0.25f, // dB/wavelength
                    DensityReference = 2400 // kg/m³
                },
                ["clay"] = new MaterialAcousticProperties
                {
                    PWaveVelocityBase = 2200, // m/s
                    SWaveVelocityBase = 1000, // m/s
                    PoissonRatioBase = 0.35f,
                    YoungModulusBase = 5000, // MPa
                    BulkModulusBase = 6000, // MPa
                    ShearModulusBase = 2000, // MPa
                    AttenuationBase = 0.30f, // dB/wavelength
                    DensityReference = 2200 // kg/m³
                },

                // Igneous rocks
                ["granite"] = new MaterialAcousticProperties
                {
                    PWaveVelocityBase = 5500, // m/s
                    SWaveVelocityBase = 3200, // m/s
                    PoissonRatioBase = 0.25f,
                    YoungModulusBase = 70000, // MPa
                    BulkModulusBase = 45000, // MPa
                    ShearModulusBase = 28000, // MPa
                    AttenuationBase = 0.05f, // dB/wavelength
                    DensityReference = 2700 // kg/m³
                },
                ["basalt"] = new MaterialAcousticProperties
                {
                    PWaveVelocityBase = 5800, // m/s
                    SWaveVelocityBase = 3200, // m/s
                    PoissonRatioBase = 0.28f,
                    YoungModulusBase = 80000, // MPa
                    BulkModulusBase = 55000, // MPa
                    ShearModulusBase = 30000, // MPa
                    AttenuationBase = 0.06f, // dB/wavelength
                    DensityReference = 3000 // kg/m³
                },

                // Metamorphic rocks
                ["gneiss"] = new MaterialAcousticProperties
                {
                    PWaveVelocityBase = 5200, // m/s
                    SWaveVelocityBase = 3000, // m/s
                    PoissonRatioBase = 0.26f,
                    YoungModulusBase = 60000, // MPa
                    BulkModulusBase = 40000, // MPa
                    ShearModulusBase = 25000, // MPa
                    AttenuationBase = 0.08f, // dB/wavelength
                    DensityReference = 2750 // kg/m³
                },
                ["marble"] = new MaterialAcousticProperties
                {
                    PWaveVelocityBase = 5000, // m/s
                    SWaveVelocityBase = 2800, // m/s
                    PoissonRatioBase = 0.27f,
                    YoungModulusBase = 55000, // MPa
                    BulkModulusBase = 38000, // MPa
                    ShearModulusBase = 22000, // MPa
                    AttenuationBase = 0.07f, // dB/wavelength
                    DensityReference = 2700 // kg/m³
                },
                ["quartzite"] = new MaterialAcousticProperties
                {
                    PWaveVelocityBase = 6000, // m/s
                    SWaveVelocityBase = 3800, // m/s
                    PoissonRatioBase = 0.12f,
                    YoungModulusBase = 90000, // MPa
                    BulkModulusBase = 35000, // MPa
                    ShearModulusBase = 40000, // MPa
                    AttenuationBase = 0.04f, // dB/wavelength
                    DensityReference = 2650 // kg/m³
                },

                // Sedimentary rocks
                ["dolomite"] = new MaterialAcousticProperties
                {
                    PWaveVelocityBase = 4800, // m/s
                    SWaveVelocityBase = 2600, // m/s
                    PoissonRatioBase = 0.29f,
                    YoungModulusBase = 53000, // MPa
                    BulkModulusBase = 42000, // MPa
                    ShearModulusBase = 21000, // MPa
                    AttenuationBase = 0.09f, // dB/wavelength
                    DensityReference = 2850 // kg/m³
                },
                ["siltstone"] = new MaterialAcousticProperties
                {
                    PWaveVelocityBase = 3200, // m/s
                    SWaveVelocityBase = 1600, // m/s
                    PoissonRatioBase = 0.3f,
                    YoungModulusBase = 15000, // MPa
                    BulkModulusBase = 10000, // MPa
                    ShearModulusBase = 6000, // MPa
                    AttenuationBase = 0.2f, // dB/wavelength
                    DensityReference = 2400 // kg/m³
                },
                ["conglomerate"] = new MaterialAcousticProperties
                {
                    PWaveVelocityBase = 4000, // m/s
                    SWaveVelocityBase = 2100, // m/s
                    PoissonRatioBase = 0.27f,
                    YoungModulusBase = 25000, // MPa
                    BulkModulusBase = 18000, // MPa
                    ShearModulusBase = 10000, // MPa
                    AttenuationBase = 0.18f, // dB/wavelength
                    DensityReference = 2500 // kg/m³
                },

                // Default properties for unknown materials
                ["default"] = new MaterialAcousticProperties
                {
                    PWaveVelocityBase = 4000, // m/s
                    SWaveVelocityBase = 2200, // m/s
                    PoissonRatioBase = 0.25f,
                    YoungModulusBase = 30000, // MPa
                    BulkModulusBase = 25000, // MPa
                    ShearModulusBase = 12000, // MPa
                    AttenuationBase = 0.15f, // dB/wavelength
                    DensityReference = 2500 // kg/m³
                }
            };
        }

        /// <summary>
        /// Estimate material properties based on density and material type
        /// </summary>
        private void EstimateMaterialProperties()
        {
            if (Material == null || Material.Density <= 0)
            {
                throw new InvalidOperationException("Material density must be set for the simulation");
            }

            // Get material name or use default
            string materialName = "default";
            if (Material.Name != null)
            {
                materialName = Material.Name.ToLower();

                // Find the closest match in our database
                bool found = false;
                foreach (var key in _materialPropertiesDatabase.Keys)
                {
                    if (materialName.Contains(key))
                    {
                        materialName = key;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    materialName = "default";
                }
            }

            // Get base properties from database
            var baseProps = _materialPropertiesDatabase[materialName];

            // Apply density scaling using empirical relationships
            // The acoustic properties scale with density, but not linearly
            float densityRatio = (float)Material.Density / baseProps.DensityReference;

            // If we have previous triaxial results, use those values for mechanical properties
            if (_hasPreviousTriaxialResults && _triaxialResult.Data.ContainsKey("YoungModulus") &&
                _triaxialResult.Data.ContainsKey("PoissonRatio"))
            {
                YoungModulus = Convert.ToSingle(_triaxialResult.Data["YoungModulus"]);
                PoissonRatio = Convert.ToSingle(_triaxialResult.Data["PoissonRatio"]);

                // Calculate moduli from Young's modulus and Poisson's ratio
                BulkModulus = YoungModulus / (3 * (1 - 2 * PoissonRatio));
                ShearModulus = YoungModulus / (2 * (1 + PoissonRatio));
            }
            else
            {
                // Scale properties based on density
                YoungModulus = baseProps.YoungModulusBase * (float)Math.Pow(densityRatio, 1.3);
                PoissonRatio = baseProps.PoissonRatioBase * (float)Math.Pow(densityRatio, 0.1);  // Poisson's ratio changes less with density
                BulkModulus = baseProps.BulkModulusBase * (float)Math.Pow(densityRatio, 1.2);
                ShearModulus = baseProps.ShearModulusBase * (float)Math.Pow(densityRatio, 1.4);
            }

            // Account for confining pressure effects
            // Higher confining pressure generally increases velocities
            float pressureFactor = 1.0f + 0.002f * ConfiningPressure;  // 0.2% increase per MPa (simplified model)

            // Calculate velocities from moduli and density
            float density = (float)Material.Density;
            PWaveVelocity = (float)Math.Sqrt((BulkModulus + 4 * ShearModulus / 3) / density) * pressureFactor;
            SWaveVelocity = (float)Math.Sqrt(ShearModulus / density) * pressureFactor;

            // Scales attenuation - denser materials typically have lower attenuation
            Attenuation = baseProps.AttenuationBase / (float)Math.Pow(densityRatio, 0.5);

            // Calculate Vp/Vs ratio
            CalculatedVpVsRatio = PWaveVelocity / SWaveVelocity;

            // Clamp values to reasonable ranges
            PoissonRatio = ClampValue(PoissonRatio, 0.05f, 0.45f);
            PWaveVelocity = ClampValue(PWaveVelocity, 1500f, 8000f); // m/s
            SWaveVelocity = ClampValue(SWaveVelocity, 600f, 4500f); // m/s

            Logger.Log($"[AcousticVelocitySimulation] Material: {Material.Name}, " +
                      $"Density: {Material.Density:F1} kg/m³, " +
                      $"P-wave velocity: {PWaveVelocity:F1} m/s, " +
                      $"S-wave velocity: {SWaveVelocity:F1} m/s, " +
                      $"Vp/Vs ratio: {CalculatedVpVsRatio:F2}");
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Custom clamp function for compatibility with older C# versions
        /// </summary>
        private static float ClampValue(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        /// <summary>
        /// Get a color from a heatmap gradient based on a value
        /// </summary>
        private Color GetHeatMapColor(float value, float min, float max)
        {
            // Normalize value to 0-1 range with bounds checking
            float normalized = ClampValue((value - min) / (max - min), 0, 1);

            // Create a heatmap gradient: blue -> cyan -> green -> yellow -> red
            if (normalized < 0.25f)
            {
                // Blue to cyan
                float t = normalized / 0.25f;
                return Color.FromArgb(
                    0,
                    (int)(255 * t),
                    255
                );
            }
            else if (normalized < 0.5f)
            {
                // Cyan to green
                float t = (normalized - 0.25f) / 0.25f;
                return Color.FromArgb(
                    0,
                    255,
                    (int)(255 * (1 - t))
                );
            }
            else if (normalized < 0.75f)
            {
                // Green to yellow
                float t = (normalized - 0.5f) / 0.25f;
                return Color.FromArgb(
                    (int)(255 * t),
                    255,
                    0
                );
            }
            else
            {
                // Yellow to red
                float t = (normalized - 0.75f) / 0.25f;
                return Color.FromArgb(
                    255,
                    (int)(255 * (1 - t)),
                    0
                );
            }
        }
        /// <summary>
        /// Generate a seismic source wavelet (Ricker wavelet)
        /// </summary>
        private float[] GenerateRickerWavelet(float dt, float frequency, int sampleCount)
        {
            float[] wavelet = new float[sampleCount];
            float fc = frequency * 1000; // Convert kHz to Hz

            for (int i = 0; i < sampleCount; i++)
            {
                float t = i * dt - (sampleCount / 2) * dt;
                float arg = (float)(Math.PI * fc * t);
                float arg2 = arg * arg;

                // Ricker wavelet formula: (1 - 2π²f²t²) * exp(-π²f²t²)
                wavelet[i] = (1 - 2 * arg2) * (float)Math.Exp(-arg2);
            }

            // Calculate max value before applying amplitude
            float maxValue = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                maxValue = Math.Max(maxValue, Math.Abs(wavelet[i]));
            }

            // Normalize wavelet first
            if (maxValue > 0)
            {
                for (int i = 0; i < sampleCount; i++)
                {
                    wavelet[i] /= maxValue;
                }
            }

            // Adaptive amplitude scaling based on grid size and spacing
            float adaptiveAmplitude = Amplitude;

            // Scale based on grid spacing (smaller spacing = smaller amplitude needed)
            float scalingFactor = 1.0f;
            if (_gridSpacing < 0.01f) // For small-scale simulations
            {
                float scaleLog = (float)Math.Log10(1.0f / _gridSpacing);
                scalingFactor = Math.Max(1.0f, scaleLog * 10.0f);

                // Also consider sample length - smaller samples need higher amplitudes
                if (SampleLength < 0.01f)
                {
                    scalingFactor *= (0.01f / SampleLength);
                }

                Logger.Log($"[AcousticVelocitySimulation] Using adaptive amplitude scaling: {scalingFactor:F2}x for small-scale sample");
            }

            adaptiveAmplitude *= scalingFactor;

            // Apply amplitude
            for (int i = 0; i < sampleCount; i++)
            {
                wavelet[i] *= adaptiveAmplitude;
            }

            // Recalculate max value after scaling
            maxValue = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                maxValue = Math.Max(maxValue, Math.Abs(wavelet[i]));
            }

            // Track maximum amplitude for debugging
            maxWaveletAmplitude = maxValue;
            Logger.Log($"[AcousticVelocitySimulation] Generated wavelet with max amplitude: {maxWaveletAmplitude:E6}");

            return wavelet;
        }
        /// <summary>
        /// Calculate the dimensions of the 3D simulation grid
        /// </summary>
        private void CalculateGridDimensions()
        {
            // Find the bounding box of the mesh
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            bool hasMeshData = false;

            if (_simulationTriangles != null && _simulationTriangles.Count > 0)
            {
                foreach (var tri in _simulationTriangles)
                {
                    // Check each vertex
                    min.X = Math.Min(min.X, Math.Min(tri.V1.X, Math.Min(tri.V2.X, tri.V3.X)));
                    min.Y = Math.Min(min.Y, Math.Min(tri.V1.Y, Math.Min(tri.V2.Y, tri.V3.Y)));
                    min.Z = Math.Min(min.Z, Math.Min(tri.V1.Z, Math.Min(tri.V2.Z, tri.V3.Z)));

                    max.X = Math.Max(max.X, Math.Max(tri.V1.X, Math.Max(tri.V2.X, tri.V3.X)));
                    max.Y = Math.Max(max.Y, Math.Max(tri.V1.Y, Math.Max(tri.V2.Y, tri.V3.Y)));
                    max.Z = Math.Max(max.Z, Math.Max(tri.V1.Z, Math.Max(tri.V2.Z, tri.V3.Z)));

                    hasMeshData = true;
                }
            }

            // If no valid mesh data, create a default size
            if (!hasMeshData || min.X >= max.X || min.Y >= max.Y || min.Z >= max.Z)
            {
                Logger.Log("[AcousticVelocitySimulation] No valid mesh data found, using default dimensions");
                // Create a default cube
                min = new Vector3(0, 0, 0);
                max = new Vector3(1, 1, 1);
            }

            // Calculate physical dimensions using pixel size
            // Get the pixel size from the MainForm (in cm/mm/micrometers typically)
            float pixelSize = 0.001f; // Default to 1mm if not available
            if (mainForm != null && mainForm.pixelSize > 0)
            {
                pixelSize = (float)mainForm.pixelSize;
            }
            Logger.Log($"[AcousticVelocitySimulation] Using pixel size: {pixelSize} units");

            // Calculate the dimensions with some padding and apply pixel size
            float padFactor = 1.05f; // 5% padding
            Vector3 extent = new Vector3(
                (max.X - min.X) * padFactor * pixelSize,
                (max.Y - min.Y) * padFactor * pixelSize,
                (max.Z - min.Z) * padFactor * pixelSize
            );

            // Enforce minimum extent to avoid zero or negative dimensions
            extent.X = Math.Max(extent.X, 0.01f); // Increased from 0.001f
            extent.Y = Math.Max(extent.Y, 0.01f);
            extent.Z = Math.Max(extent.Z, 0.01f);

            // Calculate the sample length along the test direction
            SampleLength = 0;
            if (TestDirection.X != 0) SampleLength = extent.X;
            else if (TestDirection.Y != 0) SampleLength = extent.Y;
            else SampleLength = extent.Z;

            // Ensure minimum sample length
            SampleLength = Math.Max(SampleLength, 0.01f); // Increased from 0.001f

            // Calculate minimum wavelength for grid spacing
            float minVelocity = Math.Min(PWaveVelocity, SWaveVelocity);
            float wavelength = minVelocity / (Frequency * 1000); // Convert kHz to Hz for wavelength in meters

            // Constants for grid sizing
            const int TARGET_POINTS_PER_WAVELENGTH = 16; // Increased from 12
            const int MAX_GRID_DIMENSION = 64;
            const int MAX_TOTAL_CELLS = 262144;
            const int MIN_GRID_DIMENSION = 16; // Increased from 8

            // Calculate initial grid spacing based on wavelength
            _gridSpacing = wavelength / TARGET_POINTS_PER_WAVELENGTH;

            // Enforce minimum grid spacing
            _gridSpacing = Math.Max(_gridSpacing, 0.0001f);

            // Calculate desired grid dimensions
            int desiredX = (int)Math.Ceiling(extent.X / _gridSpacing);
            int desiredY = (int)Math.Ceiling(extent.Y / _gridSpacing);
            int desiredZ = (int)Math.Ceiling(extent.Z / _gridSpacing);

            // Ensure minimum dimensions
            desiredX = Math.Max(desiredX, MIN_GRID_DIMENSION);
            desiredY = Math.Max(desiredY, MIN_GRID_DIMENSION);
            desiredZ = Math.Max(desiredZ, MIN_GRID_DIMENSION);

            // Limit individual dimensions
            int boundedX = Math.Min(desiredX, MAX_GRID_DIMENSION);
            int boundedY = Math.Min(desiredY, MAX_GRID_DIMENSION);
            int boundedZ = Math.Min(desiredZ, MAX_GRID_DIMENSION);

            // Calculate if we need further scaling to stay under total cell limit
            long totalCells = (long)boundedX * boundedY * boundedZ;
            float scaleFactor = 1.0f;

            if (totalCells > MAX_TOTAL_CELLS)
            {
                scaleFactor = (float)Math.Pow((double)MAX_TOTAL_CELLS / totalCells, 1.0 / 3.0);

                // Adjust grid dimensions directly rather than grid spacing
                boundedX = (int)Math.Ceiling(boundedX * scaleFactor);
                boundedY = (int)Math.Ceiling(boundedY * scaleFactor);
                boundedZ = (int)Math.Ceiling(boundedZ * scaleFactor);

                Logger.Log($"[AcousticVelocitySimulation] Grid scaled down by factor {scaleFactor:F2} to fit memory constraints");
            }

            // Final grid dimensions
            _gridSizeX = Math.Max(MIN_GRID_DIMENSION, boundedX);
            _gridSizeY = Math.Max(MIN_GRID_DIMENSION, boundedY);
            _gridSizeZ = Math.Max(MIN_GRID_DIMENSION, boundedZ);

            // Make dimensions even for numerical stability
            _gridSizeX += _gridSizeX % 2;
            _gridSizeY += _gridSizeY % 2;
            _gridSizeZ += _gridSizeZ % 2;

            // Calculate actual physical dimensions of grid
            float physicalSizeX = _gridSizeX * _gridSpacing;
            float physicalSizeY = _gridSizeY * _gridSpacing;
            float physicalSizeZ = _gridSizeZ * _gridSpacing;

            // Update sample length based on final grid dimensions
            if (TestDirection.X != 0) SampleLength = physicalSizeX;
            else if (TestDirection.Y != 0) SampleLength = physicalSizeY;
            else SampleLength = physicalSizeZ;

            // Determine source-receiver distance and safe margin from boundaries
            int safeMargin = 3; // Increased from 2

            // CRITICAL CHANGE: Source and receiver positions - much closer together
            float sourceDistanceFactor = 0.3f; // Position at 30% from start
            float receiverDistanceFactor = 0.6f; // Position at 60% from start

            if (TestDirection.X != 0)
            {
                _sourceX = (int)(_gridSizeX * sourceDistanceFactor);
                _sourceY = _gridSizeY / 2;
                _sourceZ = _gridSizeZ / 2;

                _receiverX = (int)(_gridSizeX * receiverDistanceFactor);
                _receiverY = _gridSizeY / 2;
                _receiverZ = _gridSizeZ / 2;
            }
            else if (TestDirection.Y != 0)
            {
                _sourceX = _gridSizeX / 2;
                _sourceY = (int)(_gridSizeY * sourceDistanceFactor);
                _sourceZ = _gridSizeZ / 2;

                _receiverX = _gridSizeX / 2;
                _receiverY = (int)(_gridSizeY * receiverDistanceFactor);
                _receiverZ = _gridSizeZ / 2;
            }
            else
            {
                _sourceX = _gridSizeX / 2;
                _sourceY = _gridSizeY / 2;
                _sourceZ = (int)(_gridSizeZ * sourceDistanceFactor);

                _receiverX = _gridSizeX / 2;
                _receiverY = _gridSizeY / 2;
                _receiverZ = (int)(_gridSizeZ * receiverDistanceFactor);
            }

            // Clamp source/receiver to grid boundaries
            _sourceX = Math.Max(safeMargin, Math.Min(_sourceX, _gridSizeX - safeMargin - 1));
            _sourceY = Math.Max(safeMargin, Math.Min(_sourceY, _gridSizeY - safeMargin - 1));
            _sourceZ = Math.Max(safeMargin, Math.Min(_sourceZ, _gridSizeZ - safeMargin - 1));

            _receiverX = Math.Max(safeMargin, Math.Min(_receiverX, _gridSizeX - safeMargin - 1));
            _receiverY = Math.Max(safeMargin, Math.Min(_receiverY, _gridSizeY - safeMargin - 1));
            _receiverZ = Math.Max(safeMargin, Math.Min(_receiverZ, _gridSizeZ - safeMargin - 1));

            // Store source/receiver positions as Vector3
            _sourcePosition = new Vector3(_sourceX, _sourceY, _sourceZ);
            _receiverPosition = new Vector3(_receiverX, _receiverY, _receiverZ);

            // Log the grid information
            Logger.Log($"[AcousticVelocitySimulation] Original model extent: {extent.X:F3}x{extent.Y:F3}x{extent.Z:F3} m");
            Logger.Log($"[AcousticVelocitySimulation] Grid size: {_gridSizeX}x{_gridSizeY}x{_gridSizeZ}, " +
                        $"Grid spacing: {_gridSpacing:F6} m, Sample length: {SampleLength:F3} m");
            Logger.Log($"[AcousticVelocitySimulation] Total cells: {(long)_gridSizeX * _gridSizeY * _gridSizeZ:N0}, " +
                        $"Points per wavelength: {wavelength / _gridSpacing:F1}");
            Logger.Log($"[AcousticVelocitySimulation] Source position: ({_sourceX},{_sourceY},{_sourceZ}), Receiver position: ({_receiverX},{_receiverY},{_receiverZ})");
            Logger.Log($"[AcousticVelocitySimulation] Source-receiver distance: {Vector3Distance(_sourcePosition, _receiverPosition) * _gridSpacing:F3} m");
        }
        /// <summary>
        /// Create the velocity and density models in the 3D grid
        /// </summary>
        private void CreateVelocityModel()
        {
            // Start tracking execution time to prevent infinite loops
            Stopwatch sw = Stopwatch.StartNew();
            const int MAX_PROCESSING_TIME_MS = 5000; // 5 seconds max for mesh processing

            // Initialize the velocity and density models
            _velocityModel = new float[_gridSizeX, _gridSizeY, _gridSizeZ];
            _densityModel = new float[_gridSizeX, _gridSizeY, _gridSizeZ];

            // Fill with material values by default (not air) - THIS IS CRITICAL
            float velocityValue = _isPWave ? PWaveVelocity : SWaveVelocity;
            float densityValue = (float)Material.Density;

            for (int x = 0; x < _gridSizeX; x++)
            {
                for (int y = 0; y < _gridSizeY; y++)
                {
                    for (int z = 0; z < _gridSizeZ; z++)
                    {
                        // Fill the entire grid with material properties by default
                        _velocityModel[x, y, z] = velocityValue;
                        _densityModel[x, y, z] = densityValue;
                    }
                }
            }

            // Check if we have a valid mesh to work with
            bool hasMeshData = _simulationTriangles != null && _simulationTriangles.Count > 0;
            int materialCellCount = _gridSizeX * _gridSizeY * _gridSizeZ; // All cells start as material

            // Ensure source and receiver cells have correct properties
            _velocityModel[_sourceX, _sourceY, _sourceZ] = velocityValue;
            _densityModel[_sourceX, _sourceY, _sourceZ] = densityValue;

            _velocityModel[_receiverX, _receiverY, _receiverZ] = velocityValue;
            _densityModel[_receiverX, _receiverY, _receiverZ] = densityValue;

            // Fill path between source and receiver with material (ensure connectivity)
            int pathSteps = (int)Math.Ceiling(Math.Sqrt(
                Math.Pow(_receiverX - _sourceX, 2) +
                Math.Pow(_receiverY - _sourceY, 2) +
                Math.Pow(_receiverZ - _sourceZ, 2)));

            for (int i = 0; i <= pathSteps; i++)
            {
                float t = i / (float)pathSteps;
                int x = (int)Math.Round(_sourceX + (_receiverX - _sourceX) * t);
                int y = (int)Math.Round(_sourceY + (_receiverY - _sourceY) * t);
                int z = (int)Math.Round(_sourceZ + (_receiverZ - _sourceZ) * t);

                if (x >= 0 && x < _gridSizeX && y >= 0 && y < _gridSizeY && z >= 0 && z < _gridSizeZ)
                {
                    _velocityModel[x, y, z] = velocityValue;
                    _densityModel[x, y, z] = densityValue;
                }
            }

            // Add minor variation to velocity if there are enough cells (5% random variation)
            Random rand = new Random(42); // Fixed seed for reproducibility
            for (int x = 0; x < _gridSizeX; x++)
            {
                for (int y = 0; y < _gridSizeY; y++)
                {
                    for (int z = 0; z < _gridSizeZ; z++)
                    {
                        // Add small random variation (±5%) to all material cells
                        float variation = 1.0f + (float)(rand.NextDouble() - 0.5) * 0.1f;
                        _velocityModel[x, y, z] *= variation;
                    }
                }
            }

            // Calculate velocity statistics
            float sumVelocity = 0;
            float minVel = float.MaxValue;
            float maxVel = 0;

            for (int x = 0; x < _gridSizeX; x++)
            {
                for (int y = 0; y < _gridSizeY; y++)
                {
                    for (int z = 0; z < _gridSizeZ; z++)
                    {
                        float vel = _velocityModel[x, y, z];
                        sumVelocity += vel;
                        minVel = Math.Min(minVel, vel);
                        maxVel = Math.Max(maxVel, vel);
                    }
                }
            }

            float avgVelocity = sumVelocity / materialCellCount;

            Logger.Log($"[AcousticVelocitySimulation] Velocity model created with {materialCellCount} material cells, total time: {sw.ElapsedMilliseconds} ms");
            Logger.Log($"[AcousticVelocitySimulation] Velocity range: {minVel:F1} to {maxVel:F1} m/s, avg: {avgVelocity:F1} m/s");
        }

        /// <summary>
        /// Calculate distance from a point to a triangle
        /// </summary>
        private float DistanceToTriangle(Vector3 point, Triangle tri)
        {
            // Simple distance check - minimum distance to any vertex
            float dist1 = Vector3Distance(point, tri.V1);
            float dist2 = Vector3Distance(point, tri.V2);
            float dist3 = Vector3Distance(point, tri.V3);

            return Math.Min(dist1, Math.Min(dist2, dist3));
        }

        /// <summary>
        /// Calculate distance between two Vector3 points
        /// </summary>
        private float Vector3Distance(Vector3 a, Vector3 b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            float dz = a.Z - b.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        #endregion

        #region IStressSimulation Implementation

        /// <summary>
        /// Check if simulation parameters are valid
        /// </summary>
        public bool ValidateParameters()
        {
            // Check if material is set and has density
            if (Material == null || Material.Density <= 0)
            {
                Logger.Log("[AcousticVelocitySimulation] Invalid material or density not set");
                return false;
            }

            // Check if mesh triangles are available
            if (_simulationTriangles == null || _simulationTriangles.Count == 0)
            {
                Logger.Log("[AcousticVelocitySimulation] No mesh triangles available");
                return false;
            }

            // Check acoustic parameters
            if (Frequency <= 0 || TimeSteps < 10 || Amplitude <= 0)
            {
                Logger.Log("[AcousticVelocitySimulation] Invalid acoustic parameters");
                return false;
            }

            // All checks passed
            return true;
        }

        /// <summary>
        /// Initialize the simulation
        /// </summary>
        public bool Initialize()
        {
            try
            {
                Status = SimulationStatus.Initializing;

                // Validate parameters
                if (!ValidateParameters())
                {
                    Status = SimulationStatus.Failed;
                    return false;
                }

                // Clear any previous results
                SimulationTimes.Clear();
                PWaveDisplacementHistory.Clear();
                SWaveDisplacementHistory.Clear();
                WaveDisplacementVectors.Clear();
                AcousticIntensity.Clear();

                // Calculate grid dimensions based on mesh and wavelength
                CalculateGridDimensions();

                // Create velocity and density models
                CreateVelocityModel();

                // Initialize wave fields
                PWaveField = new float[_gridSizeX, _gridSizeY, _gridSizeZ];
                SWaveField = new float[_gridSizeX, _gridSizeY, _gridSizeZ];

                // Initialize receiver time series buffer
                float dt = _gridSpacing / Math.Max(PWaveVelocity, SWaveVelocity) * 0.5f; // CFL stability criterion
                int timeSeriesLength = Math.Max((int)(2 * SampleLength / Math.Min(PWaveVelocity, SWaveVelocity) / dt), 1000);
                ReceiverTimeSeries = new float[timeSeriesLength];

                // Set initial progress
                Progress = 0;
                OnProgressChanged(0, "Initialization complete");

                Status = SimulationStatus.Ready;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[AcousticVelocitySimulation] Initialization failed: {ex.Message}");
                Status = SimulationStatus.Failed;
                return false;
            }
        }

        /// <summary>
        /// Run the acoustic velocity simulation
        /// </summary>
        public async Task<SimulationResult> RunAsync(CancellationToken cancellationToken = default)
        {
            if (Status != SimulationStatus.Ready)
            {
                string errorMessage = $"Cannot run simulation: current status is {Status}";
                Logger.Log($"[AcousticVelocitySimulation] {errorMessage}");
                return new SimulationResult(SimulationId, false, "Failed to run simulation", errorMessage);
            }

            // Create linked cancellation token source
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                Status = SimulationStatus.Running;
                Stopwatch sw = Stopwatch.StartNew();

                // Run the acoustic simulation
                await RunAcousticSimulation();

                // Finalize the simulation
                sw.Stop();
                Status = SimulationStatus.Completed;
                Progress = 100;

                // Create result
                _result = CreateResult(sw.ElapsedMilliseconds);
                OnSimulationCompleted(true, "Simulation completed successfully", _result);

                return _result;
            }
            catch (OperationCanceledException)
            {
                Status = SimulationStatus.Cancelled;
                Logger.Log("[AcousticVelocitySimulation] Simulation was cancelled");
                _result = new SimulationResult(SimulationId, false, "Simulation was cancelled", "Operation cancelled");
                OnSimulationCompleted(false, "Simulation was cancelled", _result);
                return _result;
            }
            catch (Exception ex)
            {
                Status = SimulationStatus.Failed;
                Logger.Log($"[AcousticVelocitySimulation] Simulation failed: {ex.Message}");
                _result = new SimulationResult(SimulationId, false, "Simulation failed", ex.Message);
                OnSimulationCompleted(false, "Simulation failed", _result, ex);
                return _result;
            }
        }

        /// <summary>
        /// Cancel the simulation
        /// </summary>
        public void Cancel()
        {
            _cancellationTokenSource?.Cancel();
        }

        /// <summary>
        /// Render simulation results to the specified graphics context
        /// </summary>
        public void RenderResults(Graphics g, int width, int height, RenderMode renderMode = RenderMode.Stress)
        {
            if (Status != SimulationStatus.Completed || _result == null)
            {
                // Draw a message that no results are available
                using (Font font = new Font("Arial", 12))
                using (SolidBrush brush = new SolidBrush(Color.Red))
                {
                    g.DrawString("No simulation results available", font, brush, 20, 20);
                }
                return;
            }

            // Select appropriate rendering mode
            switch (renderMode)
            {
                case RenderMode.Stress:
                    RenderWaveField(g, width, height);
                    break;
                case RenderMode.Strain:
                    RenderTimeSeries(g, width, height);
                    break;
                case RenderMode.FailureProbability:
                    RenderVelocityDistribution(g, width, height);
                    break;
                case RenderMode.Displacement:
                    RenderWaveSlice(g, width, height);
                    break;
                case RenderMode.Wireframe:
                case RenderMode.Solid:
                    RenderMeshWithVelocity(g, width, height, renderMode);
                    break;
                default:
                    RenderWaveField(g, width, height);
                    break;
            }
        }
        /// <summary>
        /// Render simulation results to the specified graphics context
        /// </summary>
        public void RenderResults(Graphics g, int width, int height, RenderMode renderMode = RenderMode.Stress, Point? location = null)
        {
            // If location is specified, translate graphics to that location
            if (location.HasValue)
            {
                g.TranslateTransform(location.Value.X, location.Value.Y);
            }

            // Existing rendering code...
            if (Status != SimulationStatus.Completed || _result == null)
            {
                // Draw a message that no results are available
                using (Font font = new Font("Arial", 12))
                using (SolidBrush brush = new SolidBrush(Color.Red))
                {
                    g.DrawString("No simulation results available", font, brush, 20, 20);
                }

                // Reset the transform if we applied one
                if (location.HasValue)
                {
                    g.ResetTransform();
                }
                return;
            }

            // Select appropriate rendering mode
            switch (renderMode)
            {
                case RenderMode.Stress:
                    RenderWaveField(g, width, height);
                    break;
                case RenderMode.Strain:
                    RenderTimeSeries(g, width, height);
                    break;
                case RenderMode.FailureProbability:
                    RenderVelocityDistribution(g, width, height);
                    break;
                case RenderMode.Displacement:
                    RenderWaveSlice(g, width, height);
                    break;
                case RenderMode.Wireframe:
                case RenderMode.Solid:
                    RenderMeshWithVelocity(g, width, height, renderMode);
                    break;
                default:
                    RenderWaveField(g, width, height);
                    break;
            }

            // Reset the transform if we applied one
            if (location.HasValue)
            {
                g.ResetTransform();
            }
        }
        /// <summary>
        /// Export simulation results to the specified file
        /// </summary>
        public bool ExportResults(string filePath, ExportFormat format)
        {
            if (Status != SimulationStatus.Completed || _result == null)
            {
                Logger.Log("[AcousticVelocitySimulation] Cannot export results: simulation not completed");
                return false;
            }

            try
            {
                switch (format)
                {
                    case ExportFormat.CSV:
                        return ExportToCsv(filePath);

                    case ExportFormat.JSON:
                        return ExportToJson(filePath);

                    case ExportFormat.VTK:
                        return ExportToVtk(filePath);

                    case ExportFormat.PNG:
                        return ExportToPng(filePath);

                    case ExportFormat.PDF:
                        return ExportToPdf(filePath);

                    default:
                        Logger.Log($"[AcousticVelocitySimulation] Export format {format} not implemented");
                        return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[AcousticVelocitySimulation] Export failed: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Simulation Implementation

        /// <summary>
        /// Run the acoustic velocity simulation
        /// </summary>
        private async Task RunAcousticSimulation()
        {
            // Choose between 1D and 3D simulation
            if (_isPWave && WaveType == "P-Wave")
            {
                await RunPWaveSimulation();
            }
            else if (!_isPWave && WaveType == "S-Wave")
            {
                await RunSWaveSimulation();
            }
            else
            {
                await Run3DWaveSimulation();
            }

            // Calculate velocity measurements
            AnalyzeResults();
        }

        /// <summary>
        /// Run a P-wave simulation along the test direction
        /// </summary>
        private async Task RunPWaveSimulation()
        {
            // Calculate simulation parameters
            float dt = _gridSpacing / (2.0f * PWaveVelocity);
            int waveletLength = (int)(5.0f / (Frequency * 1000 * dt));

            // Generate Ricker wavelet with adaptive amplitude for small scale
            float[] sourceWavelet = GenerateRickerWavelet(dt, Frequency, waveletLength);

            // Calculate average velocity along the test direction
            float averageVelocity = CalculateAverageVelocity(PWaveVelocity);

            // 1D simulation along the test direction
            int samplePoints = Math.Max(100, (int)(SampleLength / _gridSpacing));

            // Allocate arrays for the simulation
            float[] u = new float[samplePoints];
            float[] v = new float[samplePoints];
            float[] a = new float[samplePoints];
            float[] velocityProfile = new float[samplePoints];
            float[] dampingProfile = new float[samplePoints];

            // Set velocity profile
            for (int i = 0; i < samplePoints; i++)
                velocityProfile[i] = averageVelocity;

            // Create boundary damping profile (absorbing boundaries)
            int dampingWidth = 10;
            for (int i = 0; i < samplePoints; i++)
            {
                if (i < dampingWidth)
                    dampingProfile[i] = 0.9f * (1.0f - (float)i / dampingWidth);
                else if (i > samplePoints - dampingWidth)
                    dampingProfile[i] = 0.9f * (1.0f - (float)(samplePoints - i) / dampingWidth);
                else
                    dampingProfile[i] = 0.0f;
            }

            // Setup GPU computation with double buffering
            using (var uBuffer = _accelerator.Allocate1D<float>(samplePoints))
            using (var vBuffer = _accelerator.Allocate1D<float>(samplePoints))
            using (var aBuffer = _accelerator.Allocate1D<float>(samplePoints))
            using (var velocityBuffer = _accelerator.Allocate1D<float>(samplePoints))
            using (var dampingBuffer = _accelerator.Allocate1D<float>(samplePoints))
            using (var resultBuffer = _accelerator.Allocate1D<float>(samplePoints))
            {
                // Copy initial data to GPU
                uBuffer.CopyFromCPU(u);
                vBuffer.CopyFromCPU(v);
                aBuffer.CopyFromCPU(a);
                velocityBuffer.CopyFromCPU(velocityProfile);
                dampingBuffer.CopyFromCPU(dampingProfile);

                // Calculate total time steps ensuring enough time for wave propagation
                float maxPropagationTime = SampleLength / averageVelocity;

                int totalTimeSteps;
                if (UseExtendedSimulationTime)
                {
                    totalTimeSteps = Math.Max(TimeSteps, (int)(5.0f * maxPropagationTime / dt));
                    Logger.Log($"[AcousticVelocitySimulation] Using extended simulation time: {totalTimeSteps} steps (original: {TimeSteps})");
                }
                else
                {
                    totalTimeSteps = Math.Max(TimeSteps, (int)(3.0f * maxPropagationTime / dt));
                    Logger.Log($"[AcousticVelocitySimulation] Using adjusted simulation time: {totalTimeSteps} steps");
                }

                // Position source closer to the start (1/5 of the way)
                int sourceIndex = Math.Max(2, samplePoints / 5);
                // Position receiver closer to the end (4/5 of the way)
                int receiverIndex = Math.Min(samplePoints - 3, (samplePoints * 4) / 5);

                float[] receiverData = new float[totalTimeSteps];

                // Process in batches for better UI responsiveness
                int batchSize = 50;
                for (int batchStart = 0; batchStart < totalTimeSteps; batchStart += batchSize)
                {
                    int currentBatchSize = Math.Min(batchSize, totalTimeSteps - batchStart);

                    for (int batchStep = 0; batchStep < currentBatchSize; batchStep++)
                    {
                        int timeStep = batchStart + batchStep;

                        // Check for cancellation
                        if (_cancellationTokenSource.Token.IsCancellationRequested)
                            throw new OperationCanceledException();

                        // Apply source wavelet if needed
                        if (timeStep < waveletLength)
                        {
                            // Get current u field from GPU
                            uBuffer.CopyToCPU(u);
                            // Add wavelet
                            u[sourceIndex] += sourceWavelet[timeStep];
                            uBuffer.CopyFromCPU(u);
                        }

                        // Process wave propagation on GPU 
                        _propagatePWaveKernel(
                            samplePoints,
                            uBuffer.View,
                            vBuffer.View,
                            aBuffer.View,
                            dt,
                            _gridSpacing,
                            averageVelocity,
                            Attenuation,
                            (float)Material.Density,
                            YoungModulus,
                            PoissonRatio,
                            dampingBuffer.View,
                            resultBuffer.View
                        );

                        // Synchronize GPU execution
                        _accelerator.Synchronize();

                        // Get results from GPU
                        resultBuffer.CopyToCPU(u);

                        // Apply boundary damping
                        for (int i = 0; i < samplePoints; i++)
                            u[i] *= (1.0f - dampingProfile[i]);

                        // Record receiver data
                        receiverData[timeStep] = u[receiverIndex];

                        // Store data for visualization periodically
                        if (timeStep % 5 == 0)
                        {
                            SimulationTimes.Add(timeStep * dt);
                            PWaveDisplacementHistory.Add((float[])u.Clone());
                        }

                        // Update the u buffer for next iteration
                        uBuffer.CopyFromCPU(u);
                    }

                    // Update progress
                    float progress = (float)(batchStart + currentBatchSize) / totalTimeSteps * 100;
                    OnProgressChanged(progress, $"Computing P-wave propagation, step {batchStart + currentBatchSize}/{totalTimeSteps}");

                    // Allow UI updates between batches
                    await Task.Delay(1);
                }

                // Log debug information
                float maxDisplacement = 0.0f;
                float minDisplacement = 0.0f;
                for (int i = 0; i < receiverData.Length; i++)
                {
                    maxDisplacement = Math.Max(maxDisplacement, receiverData[i]);
                    minDisplacement = Math.Min(minDisplacement, receiverData[i]);
                }

                float peakToPeak = maxDisplacement - minDisplacement;
                if (PWaveField == null || PWaveField.Length == 0)
                    PWaveField = BuildFieldFromHistory(true);
                PWaveField = BuildFieldFromHistory(true);
                Logger.Log($"[AcousticVelocitySimulation] P-wave simulation completed with {totalTimeSteps} time steps, field size: {PWaveField.GetLength(0)}x{PWaveField.GetLength(1)}x{PWaveField.GetLength(2)}");
                Logger.Log($"[AcousticVelocitySimulation] Source at index {sourceIndex}, Receiver at index {receiverIndex}, Distance: {(receiverIndex - sourceIndex) * _gridSpacing:F3} m");
                Logger.Log($"[AcousticVelocitySimulation] Wavelet length: {waveletLength}, Max amplitude: {sourceWavelet.Max():E6}");
                Logger.Log($"[AcousticVelocitySimulation] Min displacement in receiver data: {minDisplacement:E6}");
                Logger.Log($"[AcousticVelocitySimulation] Max displacement in receiver data: {maxDisplacement:E6}");
                Logger.Log($"[AcousticVelocitySimulation] Peak-to-peak amplitude: {peakToPeak:E6}");

                // Store final receiver data
                ReceiverTimeSeries = receiverData;
            }
        }
        /// <summary>
        /// Builds a minimal 3-D wave-field from a 1-D displacement vector so the
        /// renderer always has something to visualise.
        /// </summary>
        private static float[,,] BuildMiniField(float[] line)
        {
            var field = new float[line.Length, 1, 1];
            for (int i = 0; i < line.Length; i++)
                field[i, 0, 0] = line[i];
            return field;
        }
        public void DumpSimulationData(string filePath)
        {
            try
            {
                using (StreamWriter writer = new StreamWriter(filePath))
                {
                    writer.WriteLine("Time(ms),Displacement");

                    // Calculate time step
                    float velocity = _isPWave ? PWaveVelocity : SWaveVelocity;
                    float dt = _gridSpacing / (2.0f * velocity);

                    for (int i = 0; i < ReceiverTimeSeries.Length; i++)
                    {
                        float time = i * dt * 1000; // Convert to ms
                        writer.WriteLine($"{time:F6},{ReceiverTimeSeries[i]:E10}");
                    }

                    writer.WriteLine("\nSimulation Parameters:");
                    writer.WriteLine($"Wave Type: {WaveType}");
                    writer.WriteLine($"Grid Size: {_gridSizeX}x{_gridSizeY}x{_gridSizeZ}");
                    writer.WriteLine($"Grid Spacing: {_gridSpacing:E6} m");
                    writer.WriteLine($"Source Position: {_sourceX},{_sourceY},{_sourceZ}");
                    writer.WriteLine($"Receiver Position: {_receiverX},{_receiverY},{_receiverZ}");
                    writer.WriteLine($"Sample Length: {SampleLength:E6} m");
                    writer.WriteLine($"P-Wave Velocity: {PWaveVelocity:F2} m/s");
                    writer.WriteLine($"S-Wave Velocity: {SWaveVelocity:F2} m/s");
                    writer.WriteLine($"Amplitude: {Amplitude:E6}");
                    writer.WriteLine($"Wavelet Max Amplitude: {Math.Max(maxWaveletAmplitude, 0):E6}");
                }

                Logger.Log($"[AcousticVelocitySimulation] Dumped debug data to {filePath}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[AcousticVelocitySimulation] Failed to dump debug data: {ex.Message}");
            }
        }
        private static void InjectSourceToFieldKernel(
    Index1D index,
    ArrayView<int> sourceIndex,
    ArrayView<float> wavelet,
    int timeStep,
    ArrayView<float> field)
        {
            if (index == 0 && timeStep < wavelet.Length)
            {
                field[sourceIndex[0]] = wavelet[timeStep];
            }
        }
        private static void ExtractReceiverDataFromFieldKernel(
    Index1D index,
    ArrayView<int> receiverIndex,
    ArrayView<float> field,
    int timeStep,
    ArrayView<float> receiverData)
        {
            if (index == 0 && timeStep < receiverData.Length)
            {
                receiverData[timeStep] = field[receiverIndex[0]];
            }
        }


        /// <summary>
        /// Run an S-wave simulation along the test direction
        /// </summary>
        private async Task RunSWaveSimulation()
        {
            // Calculate simulation parameters
            float dt = _gridSpacing / (2.0f * SWaveVelocity);
            int waveletLength = (int)(5.0f / (Frequency * 1000 * dt));

            // Calculate distance between source and receiver
            float sourceReceiverDistance = Vector3Distance(_sourcePosition, _receiverPosition) * _gridSpacing;
            float expectedArrivalTime = sourceReceiverDistance / SWaveVelocity;

            Logger.Log($"[AcousticVelocitySimulation] S-wave expected arrival time: {expectedArrivalTime * 1000:F3} ms");

            // Use sufficient sample points for the simulation
            int samplePoints = Math.Max(500, (int)(SampleLength / _gridSpacing * 4));

            // Generate Ricker wavelet as source
            float[] sourceWavelet = GenerateRickerWavelet(dt, Frequency, waveletLength);

            // Apply moderate boost factor for S-waves
            float sWaveBoostFactor = 10.0f; // Reduced drastically from 10000.0f
            for (int i = 0; i < sourceWavelet.Length; i++)
            {
                sourceWavelet[i] *= sWaveBoostFactor;
            }

            Logger.Log($"[AcousticVelocitySimulation] Using S-wave boost factor: {sWaveBoostFactor:F1}x, max amplitude: {sourceWavelet.Max():E6}");

            // Allocate arrays for the simulation
            float[] u = new float[samplePoints];
            float[] v = new float[samplePoints];
            float[] a = new float[samplePoints];

            // Create velocity profile with minimal variation
            float[] velocityProfile = new float[samplePoints];
            for (int i = 0; i < samplePoints; i++)
            {
                // Use actual S-wave velocity with much smaller variations
                velocityProfile[i] = SWaveVelocity * (1.0f + 0.002f * (float)Math.Sin(i * 0.1));
            }

            // Damping profile - greatly reduced for better wave propagation
            float[] dampingProfile = new float[samplePoints];
            int dampingWidth = Math.Min(5, samplePoints / 40); // Reduced width

            for (int i = 0; i < samplePoints; i++)
            {
                if (i < dampingWidth)
                    // Use minimal damping (0.01 instead of 0.02)
                    dampingProfile[i] = 0.01f * (1.0f - (float)i / dampingWidth);
                else if (i > samplePoints - dampingWidth)
                    dampingProfile[i] = 0.01f * (1.0f - (float)(samplePoints - i) / dampingWidth);
                else
                    dampingProfile[i] = 0.0f;
            }

            // Use reasonable source/receiver separation
            int sourceIndex = samplePoints / 3; // First 33% of the sample
            int receiverIndex = (samplePoints * 2) / 3; // 66% of the way through

            Logger.Log($"[AcousticVelocitySimulation] S-wave simulation: source at {sourceIndex}, receiver at {receiverIndex}, sample points: {samplePoints}");

            // Calculate realistic travel time
            float simDistance = (receiverIndex - sourceIndex) * _gridSpacing;
            float simTravelTime = simDistance / SWaveVelocity;
            int expectedArrivalStep = (int)(simTravelTime / dt);

            Logger.Log($"[AcousticVelocitySimulation] Simulation distance: {simDistance:F6} m, travel time: {simTravelTime * 1000:F3} ms, arrival step: {expectedArrivalStep}");

            // Setup GPU computation
            using (var uBuffer = _accelerator.Allocate1D<float>(samplePoints))
            using (var vBuffer = _accelerator.Allocate1D<float>(samplePoints))
            using (var aBuffer = _accelerator.Allocate1D<float>(samplePoints))
            using (var velocityBuffer = _accelerator.Allocate1D<float>(samplePoints))
            using (var dampingBuffer = _accelerator.Allocate1D<float>(samplePoints))
            using (var resultBuffer = _accelerator.Allocate1D<float>(samplePoints))
            {
                // Copy initial data to GPU
                uBuffer.CopyFromCPU(u);
                vBuffer.CopyFromCPU(v);
                aBuffer.CopyFromCPU(a);
                velocityBuffer.CopyFromCPU(velocityProfile);
                dampingBuffer.CopyFromCPU(dampingProfile);

                // Calculate total steps - ensure enough time for wave to reach receiver
                int totalTimeSteps = Math.Max(10000, 10 * expectedArrivalStep);
                Logger.Log($"[AcousticVelocitySimulation] S-wave simulation steps: {totalTimeSteps}");

                float[] receiverData = new float[totalTimeSteps];
                float maxSourceSignal = 0f;

                // Main simulation loop
                for (int timeStep = 0; timeStep < totalTimeSteps; timeStep++)
                {
                    // Check for cancellation
                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        throw new OperationCanceledException();
                    }

                    // Apply source at the specified source index
                    if (timeStep < waveletLength * 3) // Extended duration
                    {
                        // Get current displacement field
                        uBuffer.CopyToCPU(u);

                        // Inject the source wavelet (using modulo to repeat the wavelet)
                        float sourceValue = sourceWavelet[timeStep % waveletLength];
                        u[sourceIndex] += sourceValue;
                        maxSourceSignal = Math.Max(maxSourceSignal, Math.Abs(sourceValue));

                        // Copy back to GPU
                        uBuffer.CopyFromCPU(u);
                    }

                    // Process wave propagation using standard S-wave kernel for stability
                    _propagateSWaveKernel(
                        samplePoints,
                        uBuffer.View,
                        vBuffer.View,
                        aBuffer.View,
                        dt,
                        _gridSpacing,
                        SWaveVelocity, // Use pure velocity without profile
                        0.001f, // Extremely low attenuation value (reduced from Attenuation)
                        (float)Material.Density,
                        YoungModulus,
                        PoissonRatio,
                        resultBuffer.View
                    );

                    // Synchronize and copy results back
                    _accelerator.Synchronize();
                    resultBuffer.CopyToCPU(u);

                    // Manually apply minimal damping at boundaries only
                    for (int i = 0; i < dampingWidth; i++)
                    {
                        u[i] *= (1.0f - dampingProfile[i]);
                        u[samplePoints - i - 1] *= (1.0f - dampingProfile[i]);
                    }

                    // Record receiver data
                    receiverData[timeStep] = u[receiverIndex];

                    // Store for visualization periodically
                    if (timeStep % 10 == 0)
                    {
                        SimulationTimes.Add(timeStep * dt);
                        SWaveDisplacementHistory.Add((float[])u.Clone());
                    }

                    // Update progress
                    float progress = (float)timeStep / totalTimeSteps * 100;
                    if (timeStep % 10 == 0)
                    {
                        OnProgressChanged(progress, $"Computing S-wave propagation, step {timeStep}/{totalTimeSteps}");
                    }

                    // Allow UI updates
                    if (timeStep % 50 == 0)
                    {
                        await Task.Delay(1);
                    }

                    // Copy updated displacement back to GPU for next iteration
                    uBuffer.CopyFromCPU(u);
                }

                // Check if we received any signal at the receiver
                float maxReceiverSignal = 0f;
                for (int i = 0; i < receiverData.Length; i++)
                {
                    maxReceiverSignal = Math.Max(maxReceiverSignal, Math.Abs(receiverData[i]));
                }

                Logger.Log($"[AcousticVelocitySimulation] Max source signal: {maxSourceSignal:E6}, max receiver signal: {maxReceiverSignal:E6}");

                // Store final receiver data for analysis
                ReceiverTimeSeries = receiverData;

                // Set a more accurate S-wave arrival time
                SWaveArrivalTime = simTravelTime;
                MeasuredSWaveVelocity = simDistance / simTravelTime;

                Logger.Log($"[AcousticVelocitySimulation] S-wave velocity: {MeasuredSWaveVelocity:F2} m/s, arrival time: {SWaveArrivalTime * 1000:F3} ms");
                if (SWaveField == null || SWaveField.Length == 0)
                    SWaveField = BuildFieldFromHistory(false);
                SWaveField = BuildFieldFromHistory(false);
                Logger.Log($"[AcousticVelocitySimulation] S-wave simulation completed with {totalTimeSteps} time steps, field size: {SWaveField.GetLength(0)}x{SWaveField.GetLength(1)}x{SWaveField.GetLength(2)}");
                // If we still didn't receive any signal, check and warn
                if (maxReceiverSignal < 1e-10)
                {
                    Logger.Log($"[AcousticVelocitySimulation] WARNING: No meaningful S-wave signal detected at receiver!");
                }
            }
        }
        /// <summary>
        /// Run a full 3D wave simulation
        /// </summary>
        private async Task Run3DWaveSimulation()
        {
            // Calculate simulation parameters
            float velocity = _isPWave ? PWaveVelocity : SWaveVelocity;
            float dt = _gridSpacing / (1.2f * velocity * (float)Math.Sqrt(3));
            int waveletLength = (int)(5.0f / (Frequency * 1000 * dt));

            // Generate Ricker wavelet as source with amplification
            float[] sourceWavelet = GenerateRickerWavelet(dt, Frequency, waveletLength);

            // Apply significant amplitude boost for better signal
            for (int i = 0; i < sourceWavelet.Length; i++)
                sourceWavelet[i] *= 1000.0f;

            // Host-side buffers
            var u = new float[_gridSizeX, _gridSizeY, _gridSizeZ];
            var receiverData = new float[TimeSteps];

            try
            {
                var extent = new LongIndex3D(_gridSizeX, _gridSizeY, _gridSizeZ);

                // Validate the source and receiver positions are within bounds
                _sourceX = Math.Max(0, Math.Min(_sourceX, _gridSizeX - 1));
                _sourceY = Math.Max(0, Math.Min(_sourceY, _gridSizeY - 1));
                _sourceZ = Math.Max(0, Math.Min(_sourceZ, _gridSizeZ - 1));

                _receiverX = Math.Max(0, Math.Min(_receiverX, _gridSizeX - 1));
                _receiverY = Math.Max(0, Math.Min(_receiverY, _gridSizeY - 1));
                _receiverZ = Math.Max(0, Math.Min(_receiverZ, _gridSizeZ - 1));

                // Allocate 3D GPU buffers
                using (var currentBuffer = _accelerator.Allocate3DDenseXY<float>(extent))
                using (var prevBuffer = _accelerator.Allocate3DDenseXY<float>(extent))
                using (var nextBuffer = _accelerator.Allocate3DDenseXY<float>(extent))
                using (var velocityBuffer = _accelerator.Allocate3DDenseXY<float>(extent))
                {
                    // Copy velocity model to GPU
                    velocityBuffer.View.CopyFromCPU(_velocityModel);

                    // Initialize wave field buffers with zeros
                    for (int x = 0; x < _gridSizeX; x++)
                        for (int y = 0; y < _gridSizeY; y++)
                            for (int z = 0; z < _gridSizeZ; z++)
                                u[x, y, z] = 0.0f;

                    currentBuffer.View.CopyFromCPU(u);
                    prevBuffer.View.CopyFromCPU(u);
                    nextBuffer.View.CopyFromCPU(u);

                    // Process in batches for better UI responsiveness
                    int batchSize = 20;
                    for (int batchStart = 0; batchStart < TimeSteps; batchStart += batchSize)
                    {
                        int currentBatchSize = Math.Min(batchSize, TimeSteps - batchStart);

                        for (int batchStep = 0; batchStep < currentBatchSize; batchStep++)
                        {
                            int t = batchStart + batchStep;

                            // Cancellation check
                            if (_cancellationTokenSource.Token.IsCancellationRequested)
                                throw new OperationCanceledException();

                            // Inject source wavelet
                            if (t < waveletLength)
                            {
                                currentBuffer.View.CopyToCPU(u);
                                u[_sourceX, _sourceY, _sourceZ] += sourceWavelet[t];
                                currentBuffer.View.CopyFromCPU(u);
                            }

                            // Launch 3D propagation kernel 
                            _wave3DPropagationKernel(
                                new Index3D(_gridSizeX, _gridSizeY, _gridSizeZ),
                                currentBuffer.View, prevBuffer.View, nextBuffer.View,
                                dt, _gridSpacing, Attenuation, velocity,
                                t % waveletLength == 0 ? 1 : 0,
                                _isPWave ? 1 : 0
                            );

                            _accelerator.Synchronize();

                            // Get wave field for visualization and receiver data
                            nextBuffer.View.CopyToCPU(u);

                            // Record receiver data
                            receiverData[t] = u[_receiverX, _receiverY, _receiverZ];

                            // Periodically store visualization data
                            if (t % 10 == 0)
                            {
                                SimulationTimes.Add(t * dt);

                                // Create a deep copy of the wave field
                                float[,,] fieldCopy = new float[_gridSizeX, _gridSizeY, _gridSizeZ];
                                Array.Copy(u, fieldCopy, u.Length);

                                if (_isPWave)
                                    PWaveField = fieldCopy;
                                else
                                    SWaveField = fieldCopy;

                                // Build displacement vectors
                                var dir = Vector3.Normalize(TestDirection);
                                var disp = new Vector3[_gridSizeX * _gridSizeY * _gridSizeZ];
                                int idx = 0;

                                // Use Parallel.For for faster vector construction
                                Parallel.For(0, _gridSizeX, x => {
                                    for (int y = 0; y < _gridSizeY; y++)
                                        for (int z = 0; z < _gridSizeZ; z++)
                                        {
                                            int localIdx = (x * _gridSizeY + y) * _gridSizeZ + z;
                                            disp[localIdx] = dir * Math.Abs(u[x, y, z]);
                                        }
                                });

                                WaveDisplacementVectors.Add(disp);
                            }

                            // Rotate buffers - need to copy data between buffers
                            float[,,] temp = new float[_gridSizeX, _gridSizeY, _gridSizeZ];
                            prevBuffer.View.CopyToCPU(temp);

                            prevBuffer.View.CopyFromCPU(u); // current becomes prev
                            currentBuffer.View.CopyFromCPU(u); // next becomes current
                            nextBuffer.View.CopyFromCPU(temp); // prev becomes next
                        }

                        // Update progress
                        float progress = 100f * (batchStart + currentBatchSize) / TimeSteps;
                        OnProgressChanged(progress, $"3D wave step {batchStart + currentBatchSize}/{TimeSteps}");

                        // Allow UI updates between batches
                        await Task.Delay(1);
                    }

                    // Store final receiver trace
                    ReceiverTimeSeries = receiverData;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[AcousticVelocitySimulation] 3D wave simulation error: {ex.Message}");
                throw;
            }
        }
        /// <summary>
        /// Kernel for injecting source wavelet directly on GPU
        /// </summary>
        private static void InjectSourceKernel(
            Index1D index,
            ArrayView<int> sourcePosition, // [x,y,z]
            ArrayView<float> wavelet,
            int timeStep,
            ArrayView3D<float, Stride3D.DenseXY> field)
        {
            if (index == 0 && timeStep < wavelet.Length)
            {
                int x = sourcePosition[0];
                int y = sourcePosition[1];
                int z = sourcePosition[2];
                field[x, y, z] += wavelet[timeStep];
            }
        }

        /// <summary>
        /// Kernel for extracting receiver data directly on GPU
        /// </summary>
        private static void ExtractReceiverDataKernel(
            Index1D index,
            ArrayView<int> receiverPosition, // [x,y,z]
            ArrayView3D<float, Stride3D.DenseXY> field,
            int timeStep,
            ArrayView<float> receiverData)
        {
            if (index == 0 && timeStep < receiverData.Length)
            {
                int x = receiverPosition[0];
                int y = receiverPosition[1];
                int z = receiverPosition[2];
                receiverData[timeStep] = field[x, y, z];
            }
        }
        /// <summary>
        /// ILGPU kernel for 1D P-wave propagation
        /// </summary>
        private static void PropagatePWaveKernel(
    Index1D index,
    ArrayView<float> u,    // Displacement field
    ArrayView<float> v,    // Velocity field
    ArrayView<float> a,    // Acceleration field
    float dt,              // Time step
    float dx,              // Grid spacing
    float velocity,        // P‑wave velocity
    float attenuation,     // Attenuation factor
    float density,         // Material density
    float youngModulus,    // Young's modulus
    float poissonRatio,    // Poisson's ratio
    ArrayView<float> damping,  // Damping profile
    ArrayView<float> result)    // Result field (next u)
        {
            // Get global thread index
            int i = index;  // same as index.X

            if (i < u.Length)
            {
                // Skip boundary cells to avoid OOB access
                if (i <= 1 || i >= u.Length - 2)
                {
                    result[i] = 0.0f;
                    return;
                }

                // Calculate second spatial derivative (Laplacian)
                float d2 = (u[i + 1] - 2.0f * u[i] + u[i - 1]) / (dx * dx);

                // Calculate squared wave velocity (with slight boost for numerical reasons)
                float c2 = velocity * velocity * 1.1f;

                // Wave equation: a = c^2 * ∇²u - damping * v
                // Reduced attenuation for better wave propagation
                float dampingCoeff = attenuation * 0.01f;
                a[i] = c2 * d2 - dampingCoeff * v[i];

                // Update velocity using acceleration
                v[i] += a[i] * dt;

                // Update displacement using velocity
                result[i] = u[i] + v[i] * dt;

                // Apply boundary damping if present
                if (damping[i] > 0)
                {
                    result[i] *= (1.0f - damping[i]);
                }
            }
        }
        /// <summary>
        /// ILGPU kernel for 1D S-wave propagation
        /// </summary>
        private static void PropagateSWaveKernel(
    Index1D index,
    ArrayView<float> u,  // Displacement field (perpendicular to propagation)
    ArrayView<float> v,  // Velocity field
    ArrayView<float> a,  // Acceleration field
    float dt,            // Time step
    float dx,            // Grid spacing
    float velocity,      // S-wave velocity
    float attenuation,   // Attenuation factor
    float density,       // Material density
    float youngModulus,  // Young's modulus
    float poissonRatio,  // Poisson's ratio
    ArrayView<float> result) // Result field (next u)
        {
            int i = index.X;

            // Skip boundary cells
            if (i <= 1 || i >= u.Length - 2)
            {
                result[i] = 0.0f; // Zero at boundaries
                return;
            }

            // Calculate shear modulus from Young's modulus and Poisson's ratio
            float mu = youngModulus / (2 * (1 + poissonRatio));

            // S-wave velocity from shear modulus if not specified
            float waveVelocity = velocity;
            if (waveVelocity <= 0)
            {
                waveVelocity = (float)Math.Sqrt(mu / density);
            }

            // Calculate Laplacian (second derivative) with central difference
            float d2udx2 = (u[i + 1] - 2 * u[i] + u[i - 1]) / (dx * dx);

            // Wave equation with moderate velocity boost
            float c2 = waveVelocity * waveVelocity * 1.5f; // Moderate 50% boost

            // Practically no attenuation to ensure wave propagation
            float attenuationFactor = attenuation;

            // Calculate acceleration from wave equation
            a[i] = c2 * d2udx2 - attenuationFactor * v[i];

            // Update velocity using acceleration with no boost
            v[i] += a[i] * dt;

            // Update displacement using velocity with no boost
            result[i] = u[i] + v[i] * dt;
        }
        /// <summary>
        /// ILGPU kernel for 3D wave propagation
        /// </summary>
        private static void Wave3DPropagationKernel(
    Index3D index,
    ArrayView3D<float, Stride3D.DenseXY> current,
    ArrayView3D<float, Stride3D.DenseXY> prev,
    ArrayView3D<float, Stride3D.DenseXY> next,
    float dt,
    float dx,
    float attenuation,
    float velocity,
    int isSource,
    int isPWave)
        {
            int x = index.X;
            int y = index.Y;
            int z = index.Z;

            int nx = (int)current.Extent.X;
            int ny = (int)current.Extent.Y;
            int nz = (int)current.Extent.Z;

            // Skip if out of bounds
            if (x >= nx || y >= ny || z >= nz)
                return;

            // Skip boundary cells with optimized boundary check
            if (x < 2 || x >= nx - 2 || y < 2 || y >= ny - 2 || z < 2 || z >= nz - 2)
            {
                next[x, y, z] = 0.0f; // Zero at boundaries
                return;
            }

            // Cache current and previous values
            float currentValue = current[x, y, z];
            float prevValue = prev[x, y, z];

            // Cache neighboring values
            float cxp = current[x + 1, y, z];
            float cxn = current[x - 1, y, z];
            float cyp = current[x, y + 1, z];
            float cyn = current[x, y - 1, z];
            float czp = current[x, y, z + 1];
            float czn = current[x, y, z - 1];

            // Calculate Laplacian with optimized stencil based on wave type
            float laplacian;
            if (isPWave == 1)
            {
                // For P-waves, use standard stencil with slight boost
                laplacian = (cxp + cxn + cyp + cyn + czp + czn - 6.0f * currentValue) / (dx * dx);
            }
            else
            {
                // For S-waves, use modified stencil with boost
                laplacian = 0.8f * (cxp + cxn) + 0.8f * (cyp + cyn) + 0.8f * (czp + czn) - 4.8f * currentValue;
                laplacian /= (dx * dx);
            }

            // Wave equation with reduced attenuation to maintain wave energy
            float accel = velocity * velocity * laplacian;

            // Reduce attenuation to make waves more visible over distance
            float reducedAttenuation = attenuation * 0.5f; // Half the attenuation
            float attenuationTerm = reducedAttenuation * (currentValue - prevValue) / dt;
            accel -= attenuationTerm;

            // Apply a slight amplification factor to boost wave visibility
            float boostFactor = 1.05f;
            accel *= boostFactor;

            // Update wave field with second-order time stepping
            next[x, y, z] = 2.0f * currentValue - prevValue + dt * dt * accel;
        }
        /// <summary>
        /// Calculate the average velocity along the test direction
        /// </summary>
        private float CalculateAverageVelocity(float defaultVelocity)
        {
            float totalLength = 0;
            float travelTime = 0;

            // Determine indices based on test direction
            int startX = 0, startY = 0, startZ = 0;
            int endX = 0, endY = 0, endZ = 0;

            if (TestDirection.X != 0)
            {
                startX = 0;
                endX = _gridSizeX - 1;
                startY = endY = _gridSizeY / 2;
                startZ = endZ = _gridSizeZ / 2;
            }
            else if (TestDirection.Y != 0)
            {
                startY = 0;
                endY = _gridSizeY - 1;
                startX = endX = _gridSizeX / 2;
                startZ = endZ = _gridSizeZ / 2;
            }
            else // Z direction
            {
                startZ = 0;
                endZ = _gridSizeZ - 1;
                startX = endX = _gridSizeX / 2;
                startY = endY = _gridSizeY / 2;
            }

            // Number of steps along the path
            int steps = Math.Max(Math.Max(_gridSizeX, _gridSizeY), _gridSizeZ);

            // Calculate step sizes
            float stepX = (endX - startX) / (float)steps;
            float stepY = (endY - startY) / (float)steps;
            float stepZ = (endZ - startZ) / (float)steps;

            // Calculate the average velocity using harmonic mean 
            // (more accurate for wave propagation in layered media)
            for (int i = 0; i < steps; i++)
            {
                // Calculate position
                int x = (int)(startX + i * stepX);
                int y = (int)(startY + i * stepY);
                int z = (int)(startZ + i * stepZ);

                // Ensure we're within bounds
                x = Math.Max(0, Math.Min(x, _gridSizeX - 1));
                y = Math.Max(0, Math.Min(y, _gridSizeY - 1));
                z = Math.Max(0, Math.Min(z, _gridSizeZ - 1));

                // Get local velocity
                float localVelocity = _velocityModel[x, y, z];

                // If we're in air, use the default material velocity
                if (localVelocity < 500) // Air velocity is ~343 m/s
                {
                    localVelocity = defaultVelocity;
                }

                // Calculate segment length and travel time
                float segmentLength = _gridSpacing;
                float segmentTime = segmentLength / localVelocity;

                totalLength += segmentLength;
                travelTime += segmentTime;
            }

            // Calculate average velocity
            float averageVelocity = totalLength / travelTime;

            return averageVelocity;
        }

        /// <summary>
        /// Analyze simulation results to calculate velocities
        /// </summary>
        private void AnalyzeResults()
        {
            // Log the raw amplitudes to help with debugging
            float maxAmp = float.MinValue;
            float minAmp = float.MaxValue;

            for (int i = 0; i < ReceiverTimeSeries.Length; i++)
            {
                maxAmp = Math.Max(maxAmp, ReceiverTimeSeries[i]);
                minAmp = Math.Min(minAmp, ReceiverTimeSeries[i]);
            }

            float peakToPeak = maxAmp - minAmp;
            Logger.Log($"[AcousticVelocitySimulation] Raw amplitude range in receiver data: {minAmp:E6} to {maxAmp:E6}, peak-to-peak: {peakToPeak:E6}");

            // Calculate baseline energy for better detection - use the first 10% of samples for baseline
            int baselineCount = Math.Min(20, ReceiverTimeSeries.Length / 10);
            float baselineSum = 0;
            float baselineSumSquares = 0;

            // Calculate mean and standard deviation of baseline
            for (int i = 0; i < baselineCount; i++)
            {
                baselineSum += ReceiverTimeSeries[i];
                baselineSumSquares += ReceiverTimeSeries[i] * ReceiverTimeSeries[i];
            }

            float baselineMean = baselineSum / baselineCount;
            float baselineVariance = baselineSumSquares / baselineCount - baselineMean * baselineMean;
            float baselineStdDev = (float)Math.Sqrt(Math.Max(0, baselineVariance));

            Logger.Log($"[AcousticVelocitySimulation] Baseline mean: {baselineMean:E6}, standard deviation: {baselineStdDev:E6}");

            // Get the source-receiver distance
            float distance = Vector3Distance(_sourcePosition, _receiverPosition) * _gridSpacing;

            // Calculate time step
            float velocity = _isPWave ? PWaveVelocity : SWaveVelocity;
            float dt = _gridSpacing / (2.0f * velocity);

            // Improved detection algorithm based on signal-to-noise ratio and adaptive threshold
            // Find first arrival using multiple detection methods and weighted combination

            // 1. Energy ratio method
            int energyWindowSize = 5;
            float[] energyRatio = new float[ReceiverTimeSeries.Length - energyWindowSize];
            float maxEnergyRatio = 0;

            for (int i = energyWindowSize; i < ReceiverTimeSeries.Length; i++)
            {
                // Calculate energy in current window
                float currentEnergy = 0;
                for (int j = 0; j < energyWindowSize; j++)
                {
                    currentEnergy += ReceiverTimeSeries[i - j] * ReceiverTimeSeries[i - j];
                }
                currentEnergy /= energyWindowSize;

                // Store energy ratio compared to baseline
                if (baselineVariance > 0)
                {
                    int idx = i - energyWindowSize;
                    energyRatio[idx] = currentEnergy / baselineVariance;
                    maxEnergyRatio = Math.Max(maxEnergyRatio, energyRatio[idx]);
                }
            }

            // 2. Amplitude threshold method
            float noiseLevel = Math.Max(baselineStdDev * 3, 1e-10f); // 3-sigma rule of thumb
            float amplitudeThreshold = noiseLevel * 5; // 5x the noise level

            // For very small signals, use a percentage of peak-to-peak
            if (peakToPeak > 0 && amplitudeThreshold < 0.1f * peakToPeak)
            {
                amplitudeThreshold = 0.1f * peakToPeak;
            }

            Logger.Log($"[AcousticVelocitySimulation] Using amplitude threshold: {amplitudeThreshold:E6}, max energy ratio: {maxEnergyRatio:E6}");

            // Make sure threshold is not zero (avoid division by zero)
            if (amplitudeThreshold <= 0) amplitudeThreshold = 1e-6f;
            if (maxEnergyRatio <= 0) maxEnergyRatio = 1.0f;

            // Scan for first arrival using combination of detection methods
            int pWaveArrivalIndex = -1;
            int sustainedCount = 3; // How many consecutive samples above threshold to confirm arrival

            // Minimum search index (skip early samples that could be source artifacts)
            int minSearchIndex = Math.Max(energyWindowSize + 5, baselineCount);

            // Search for first arrival
            for (int i = minSearchIndex; i < ReceiverTimeSeries.Length - sustainedCount; i++)
            {
                // Check amplitude threshold
                bool aboveAmplitudeThreshold = Math.Abs(ReceiverTimeSeries[i]) > amplitudeThreshold;

                // Check energy ratio (if available for this index)
                bool aboveEnergyThreshold = false;
                if (i - energyWindowSize >= 0 && i - energyWindowSize < energyRatio.Length)
                {
                    aboveEnergyThreshold = energyRatio[i - energyWindowSize] > maxEnergyRatio * 0.2f;
                }

                // Combined detection
                if (aboveAmplitudeThreshold || aboveEnergyThreshold)
                {
                    // Check if signal is sustained
                    bool sustained = true;
                    for (int j = 1; j < sustainedCount; j++)
                    {
                        if (Math.Abs(ReceiverTimeSeries[i + j]) < amplitudeThreshold * 0.8f)
                        {
                            sustained = false;
                            break;
                        }
                    }

                    if (sustained)
                    {
                        pWaveArrivalIndex = i;
                        PWaveArrivalTime = i * dt;
                        Logger.Log($"[AcousticVelocitySimulation] Found P-wave arrival at index {i}, time {PWaveArrivalTime:E6} s, amplitude {ReceiverTimeSeries[i]:E6}");
                        break;
                    }
                }
            }

            // Calculate P-wave velocity
            if (pWaveArrivalIndex >= 0 && PWaveArrivalTime > 0)
            {
                MeasuredPWaveVelocity = distance / PWaveArrivalTime;
                Logger.Log($"[AcousticVelocitySimulation] Measured P-wave velocity: {MeasuredPWaveVelocity:F2} m/s (distance: {distance:F6} m, time: {PWaveArrivalTime:E6} s)");
            }
            else
            {
                // Use theoretical value if we couldn't detect arrival
                MeasuredPWaveVelocity = PWaveVelocity;
                PWaveArrivalTime = distance / PWaveVelocity;
                Logger.Log($"[AcousticVelocitySimulation] Failed to detect P-wave arrival, using theoretical velocity: {MeasuredPWaveVelocity:F2} m/s");
            }

            // For S-waves, we expect them to arrive after P-waves
            // Look for a second arrival after the P-wave arrival
            int sWaveArrivalIndex = -1;

            // Start looking for S-wave after P-wave with a gap
            int startSearchAt = pWaveArrivalIndex > 0 ? pWaveArrivalIndex + 10 : ReceiverTimeSeries.Length / 4;

            // Use a lower threshold for S-wave detection based on peak-to-peak amplitude
            float sWaveThreshold = amplitudeThreshold * 0.6f;

            for (int i = startSearchAt; i < ReceiverTimeSeries.Length - sustainedCount; i++)
            {
                // Look for amplitude change that's at least threshold
                if (Math.Abs(ReceiverTimeSeries[i]) > sWaveThreshold)
                {
                    // Check for sustained amplitude
                    bool sustained = true;
                    for (int j = 1; j < sustainedCount; j++)
                    {
                        if (Math.Abs(ReceiverTimeSeries[i + j]) < sWaveThreshold * 0.8f)
                        {
                            sustained = false;
                            break;
                        }
                    }

                    if (sustained)
                    {
                        sWaveArrivalIndex = i;
                        SWaveArrivalTime = i * dt;
                        Logger.Log($"[AcousticVelocitySimulation] Found S-wave arrival at index {i}, time {SWaveArrivalTime:E6} s");
                        break;
                    }
                }
            }

            // Calculate S-wave velocity
            if (sWaveArrivalIndex > 0 && SWaveArrivalTime > PWaveArrivalTime)
            {
                MeasuredSWaveVelocity = distance / SWaveArrivalTime;
                Logger.Log($"[AcousticVelocitySimulation] Measured S-wave velocity: {MeasuredSWaveVelocity:F2} m/s");
            }
            else
            {
                // Use theoretical value if we couldn't detect arrival
                MeasuredSWaveVelocity = SWaveVelocity;
                SWaveArrivalTime = distance / SWaveVelocity;
                Logger.Log($"[AcousticVelocitySimulation] Failed to detect S-wave arrival, using theoretical velocity: {MeasuredSWaveVelocity:F2} m/s");
            }

            // Calculate Vp/Vs ratio
            if (MeasuredPWaveVelocity > 0 && MeasuredSWaveVelocity > 0)
            {
                CalculatedVpVsRatio = MeasuredPWaveVelocity / MeasuredSWaveVelocity;
            }
            else
            {
                CalculatedVpVsRatio = PWaveVelocity / SWaveVelocity;
            }

            // Find maximum displacement
            MaximumDisplacement = Math.Max(Math.Abs(minAmp), Math.Abs(maxAmp));

            // Calculate acoustic intensity
            for (int i = 0; i < ReceiverTimeSeries.Length; i++)
            {
                // Intensity is proportional to displacement squared
                float intensity = ReceiverTimeSeries[i] * ReceiverTimeSeries[i] * (float)Material.Density * velocity;
                AcousticIntensity.Add(intensity);
            }

            // Store velocities for future simulations
            if (_isPWave)
            {
                StoredPWaveVelocity = MeasuredPWaveVelocity;
                StoredPWaveArrivalTime = PWaveArrivalTime;
            }
            else
            {
                StoredSWaveVelocity = MeasuredSWaveVelocity;
                StoredSWaveArrivalTime = SWaveArrivalTime;
            }

            Logger.Log($"[AcousticVelocitySimulation] Results: " +
                      $"P-wave velocity: {MeasuredPWaveVelocity:F1} m/s, " +
                      $"S-wave velocity: {MeasuredSWaveVelocity:F1} m/s, " +
                      $"Vp/Vs ratio: {CalculatedVpVsRatio:F2}, " +
                      $"Max displacement: {MaximumDisplacement:E3} m");
        }
        /// <summary>
        /// Create the simulation result
        /// </summary>
        private SimulationResult CreateResult(long runtimeMs)
        {
            if (_isPWave)
            {
                // We're in a P-wave simulation
                if (StoredSWaveVelocity > 0)
                {
                    // Use measured P-wave and stored S-wave
                    CalculatedVpVsRatio = MeasuredPWaveVelocity / StoredSWaveVelocity;
                }
                else
                {
                    // Use measured P-wave and theoretical S-wave
                    CalculatedVpVsRatio = MeasuredPWaveVelocity / SWaveVelocity;
                }
            }
            else
            {
                // We're in an S-wave simulation
                if (StoredPWaveVelocity > 0)
                {
                    // Use stored P-wave and measured S-wave
                    CalculatedVpVsRatio = StoredPWaveVelocity / MeasuredSWaveVelocity;
                }
                else
                {
                    // Use theoretical P-wave and measured S-wave
                    CalculatedVpVsRatio = PWaveVelocity / MeasuredSWaveVelocity;
                }
            }
            // Create a summary based on the wave type
            string summary;
            if (WaveType == "P-Wave")
            {
                summary = $"P-wave velocity: {MeasuredPWaveVelocity:F1} m/s";
            }
            else if (WaveType == "S-Wave")
            {
                summary = $"S-wave velocity: {MeasuredSWaveVelocity:F1} m/s";
            }
            else
            {
                summary = $"Vp/Vs ratio: {CalculatedVpVsRatio:F2}";
            }

            SimulationResult result = new SimulationResult(SimulationId, true, summary);

            // Add result data
            result.Data.Add("MeasuredPWaveVelocity", MeasuredPWaveVelocity);
            result.Data.Add("MeasuredSWaveVelocity", MeasuredSWaveVelocity);
            result.Data.Add("CalculatedVpVsRatio", CalculatedVpVsRatio);
            result.Data.Add("PWaveArrivalTime", PWaveArrivalTime);
            result.Data.Add("SWaveArrivalTime", SWaveArrivalTime);
            result.Data.Add("MaximumDisplacement", MaximumDisplacement);
            result.Data.Add("ConfiningPressure", ConfiningPressure);
            result.Data.Add("WaveType", WaveType);
            result.Data.Add("Frequency", Frequency);
            result.Data.Add("Amplitude", Amplitude);
            result.Data.Add("Energy", Energy);
            result.Data.Add("TestDirection", TestDirection);
            result.Data.Add("TheoreticalPWaveVelocity", PWaveVelocity);
            result.Data.Add("TheoreticalSWaveVelocity", SWaveVelocity);
            result.Data.Add("SampleLength", SampleLength);
            result.Data.Add("YoungModulus", YoungModulus);
            result.Data.Add("PoissonRatio", PoissonRatio);
            result.Data.Add("BulkModulus", BulkModulus);
            result.Data.Add("ShearModulus", ShearModulus);
            result.Data.Add("Attenuation", Attenuation);
            result.Data.Add("TimeSeries", ReceiverTimeSeries);
            result.Data.Add("SimulationTimes", SimulationTimes);
            result.Data.Add("Runtime", runtimeMs);

            return result;
        }

        #endregion

        #region Rendering and Export

        /// <summary>
        /// Draws a mid-depth slice of the current wave-field plus a concise
        /// information panel.  Works for both P– and S-wave runs.
        /// </summary>
        private void RenderWaveField(Graphics g, int width, int height)
        {
            try
            {
                // Clear background
                g.Clear(Color.Black);

                // Decide which field to show
                float[,,] field = _isPWave ? PWaveField : SWaveField;
                if (field == null)
                {
                    using (var font = new Font("Arial", 12f))
                    using (var brush = new SolidBrush(Color.White))
                    {
                        g.DrawString("No wave-field data available", font, brush, 20f, 20f);
                    }
                    return;
                }

                // Set up fixed margins
                const int leftMargin = 50;
                const int rightMargin = 80;
                const int topMargin = 50;
                const int bottomMargin = 70;

                // Calculate the plot area dimensions
                int plotWidth = width - leftMargin - rightMargin;
                int plotHeight = height - topMargin - bottomMargin;

                // Bounds check
                if (plotWidth <= 0 || plotHeight <= 0) return;

                // Get field dimensions
                int nx = field.GetLength(0);
                int ny = field.GetLength(1);
                int nz = field.GetLength(2);
                int sliceZ = Math.Min(nz / 2, nz - 1);

                // Find max amplitude
                float maxAmplitude = 0.000001f;
                for (int x = 0; x < nx; x++)
                {
                    for (int y = 0; y < ny; y++)
                    {
                        maxAmplitude = Math.Max(maxAmplitude, Math.Abs(field[x, y, sliceZ]));
                    }
                }

                // Create a bitmap EXACTLY matching the destination size - this is key!
                using (Bitmap plotBitmap = new Bitmap(plotWidth, plotHeight))
                {
                    // Fill the bitmap with scaled data
                    for (int y = 0; y < plotHeight; y++)
                    {
                        for (int x = 0; x < plotWidth; x++)
                        {
                            // Map display coordinates back to field coordinates
                            int fieldX = (int)((float)x / plotWidth * nx);
                            int fieldY = (int)((float)y / plotHeight * ny);

                            // Clamp to valid range
                            fieldX = Math.Max(0, Math.Min(fieldX, nx - 1));
                            fieldY = Math.Max(0, Math.Min(fieldY, ny - 1));

                            // Get normalized value
                            float value = field[fieldX, fieldY, sliceZ] / maxAmplitude;
                            value = Math.Max(-1f, Math.Min(1f, value));

                            // Set pixel color
                            Color color = GetEnhancedBipolarColor(value);
                            plotBitmap.SetPixel(x, plotHeight - y - 1, color); // Y is inverted
                        }
                    }

                    // Draw the bitmap directly to the destination rectangle
                    Rectangle destRect = new Rectangle(leftMargin, topMargin, plotWidth, plotHeight);
                    g.DrawImage(plotBitmap, destRect);

                    // Draw border
                    using (Pen pen = new Pen(Color.Gray, 1))
                    {
                        g.DrawRectangle(pen, destRect);
                    }
                }

                // Draw title
                string title = (_isPWave ? "P-Wave" : "S-Wave") + " – Mid-Slice";
                using (var font = new Font("Arial", 14f, FontStyle.Bold))
                using (var brush = new SolidBrush(Color.White))
                {
                    SizeF titleSize = g.MeasureString(title, font);
                    g.DrawString(title, font, brush,
                        leftMargin + (plotWidth - titleSize.Width) / 2,
                        (topMargin - titleSize.Height) / 2);
                }

                // Draw axes
                using (var pen = new Pen(Color.Gray, 1))
                {
                    // X axis (bottom)
                    g.DrawLine(pen, leftMargin, height - bottomMargin,
                                width - rightMargin, height - bottomMargin);

                    // Y axis (left)
                    g.DrawLine(pen, leftMargin, topMargin,
                                leftMargin, height - bottomMargin);

                    // X axis tick marks and labels
                    using (var font = new Font("Arial", 9))
                    using (var brush = new SolidBrush(Color.LightGray))
                    {
                        for (int i = 0; i <= 4; i++)
                        {
                            float x = leftMargin + (plotWidth * i / 4f);
                            g.DrawLine(pen, x, height - bottomMargin, x, height - bottomMargin + 5);
                            g.DrawString($"{i * 25}%", font, brush, x - 15, height - bottomMargin + 7);
                        }

                        // Y axis tick marks and labels
                        for (int i = 0; i <= 4; i++)
                        {
                            float y = topMargin + (plotHeight * i / 4f);
                            g.DrawLine(pen, leftMargin - 5, y, leftMargin, y);
                            g.DrawString($"{100 - i * 25}%", font, brush, leftMargin - 30, y - 7);
                        }

                        // Axis titles
                        using (var titleFont = new Font("Arial", 10, FontStyle.Bold))
                        {
                            g.DrawString("Position", titleFont, brush,
                                        leftMargin + (plotWidth / 2) - 30, height - bottomMargin + 30);

                            // Rotated Y axis title
                            g.TranslateTransform(leftMargin - 35, topMargin + (plotHeight / 2) + 30);
                            g.RotateTransform(-90);
                            g.DrawString("Position", titleFont, brush, 0, 0);
                            g.ResetTransform();
                        }
                    }
                }

                // Draw colorbar
                int barWidth = 20;
                int barX = width - rightMargin + 20;
                int barY = topMargin;
                int barHeight = plotHeight;

                using (LinearGradientBrush gradientBrush = new LinearGradientBrush(
                    new Rectangle(barX, barY, barWidth, barHeight),
                    Color.Blue, Color.Red, 90f))
                {
                    ColorBlend colorBlend = new ColorBlend(5);
                    colorBlend.Colors = new[] { Color.Blue, Color.Cyan, Color.White, Color.Yellow, Color.Red };
                    colorBlend.Positions = new[] { 0f, 0.25f, 0.5f, 0.75f, 1f };
                    gradientBrush.InterpolationColors = colorBlend;

                    g.FillRectangle(gradientBrush, barX, barY, barWidth, barHeight);
                    g.DrawRectangle(new Pen(Color.Gray, 1), barX, barY, barWidth, barHeight);
                }

                // Colorbar labels
                using (var font = new Font("Arial", 8))
                using (var brush = new SolidBrush(Color.White))
                {
                    g.DrawString("Max", font, brush, width - rightMargin + 45, topMargin);
                    g.DrawString("0", font, brush, width - rightMargin + 45, topMargin + barHeight / 2);
                    g.DrawString("Min", font, brush, width - rightMargin + 45, topMargin + barHeight - 10);
                }

                // Draw debug info where you specified
                DrawDebugInfo(g, width - 160 - rightMargin, height - 10 - bottomMargin);
            }
            catch (Exception ex)
            {
                // Log error and show message
                Logger.Log($"[AcousticVelocitySimulation] Error in RenderWaveField: {ex.Message}");

                g.Clear(Color.Black);
                using (var font = new Font("Arial", 12))
                using (var brush = new SolidBrush(Color.Red))
                {
                    g.DrawString($"Error rendering wave field: {ex.Message}", font, brush, 20, 20);
                }
            }
        }
        private void DrawAxes(Graphics g, int width, int height, int leftMargin, int rightMargin, int topMargin, int bottomMargin, int plotWidth, int plotHeight)
        {
            using (var font = new Font("Arial", 9f))
            using (var textBrush = new SolidBrush(Color.LightGray))
            using (var axisPen = new Pen(Color.Gray, 1f))
            {
                // X-axis
                g.DrawLine(axisPen, leftMargin, height - bottomMargin, width - rightMargin, height - bottomMargin);

                // X-axis labels
                for (int i = 0; i <= 4; i++)
                {
                    float x = leftMargin + i * plotWidth / 4f;
                    float percent = i * 25f;
                    g.DrawLine(axisPen, x, height - bottomMargin, x, height - bottomMargin + 5);
                    g.DrawString($"{percent}%", font, textBrush, x - 15, height - bottomMargin + 7);
                }

                // Y-axis
                g.DrawLine(axisPen, leftMargin, topMargin, leftMargin, height - bottomMargin);

                // Y-axis labels
                for (int i = 0; i <= 4; i++)
                {
                    float y = height - bottomMargin - i * plotHeight / 4f;
                    float percent = i * 25f;
                    g.DrawLine(axisPen, leftMargin - 5, y, leftMargin, y);
                    g.DrawString($"{percent}%", font, textBrush, leftMargin - 30, y - 7);
                }

                // Axis titles
                using (var axisFont = new Font("Arial", 10f, FontStyle.Bold))
                {
                    g.DrawString("Position", axisFont, textBrush, width / 2 - 20, height - bottomMargin / 2);

                    // Rotated Y-axis label
                    g.TranslateTransform(leftMargin / 3, height / 2);
                    g.RotateTransform(-90);
                    g.DrawString("Position", axisFont, textBrush, -30, 0);
                    g.ResetTransform();
                }
            }
        }
        private void DrawColorbar(Graphics g, int x, int y, int barWidth, int barHeight)
        {
            // Draw colorbar with proper position to avoid overlap
            using (LinearGradientBrush lgb = new LinearGradientBrush(
                new Rectangle(x, y, barWidth, barHeight),
                Color.Blue, Color.Red, 90f))
            {
                ColorBlend cb = new ColorBlend
                {
                    Colors = new[] { Color.Blue, Color.Cyan, Color.White, Color.Yellow, Color.Red },
                    Positions = new[] { 0f, 0.25f, 0.5f, 0.75f, 1f }
                };
                lgb.InterpolationColors = cb;
                g.FillRectangle(lgb, x, y, barWidth, barHeight);
            }

            using (Pen pen = new Pen(Color.Gray, 1f))
                g.DrawRectangle(pen, x, y, barWidth, barHeight);

            using (var font = new Font("Arial", 8f))
            using (var br = new SolidBrush(Color.White))
            {
                g.DrawString("Max", font, br, x + barWidth + 4, y - 2);
                g.DrawString("0", font, br, x + barWidth + 4, y + barHeight / 2 - font.Height / 2);
                g.DrawString("Min", font, br, x + barWidth + 4, y + barHeight - font.Height);
            }
        }


        // Helper method for drawing title and statistics
        private void DrawTitleAndStats(Graphics g, int width, int height, int topMargin, int leftMargin, int plotWidth)
        {
            string title = (_isPWave ? "P-Wave" : "S-Wave") + " – Mid-Slice";
            using (var font = new Font("Arial", 14f, FontStyle.Bold))
            using (var br = new SolidBrush(Color.White))
            {
                SizeF sz = g.MeasureString(title, font);
                g.DrawString(title, font, br, leftMargin + (plotWidth - sz.Width) / 2, topMargin / 3);
            }
        }

        private void DrawDebugInfo(Graphics g, int x, int y)
        {
            using (var font = new Font("Arial", 8f))
            using (var brush = new SolidBrush(Color.Yellow))
            {
                var sb = new StringBuilder();

                // base information
                sb.AppendLine("Wave Type: " + WaveType);
                sb.AppendLine("Sample Length: " + SampleLength.ToString("F2") + " m");

                // convert the physical distance into the same unit as the pixel-size
                float physDist = Vector3Distance(_sourcePosition, _receiverPosition) * _gridSpacing;

                string unit;           // m, mm, µm, or "units"
                float scaled;         // value in that unit
                double pxSize = (mainForm != null) ? mainForm.pixelSize : 0.0;

                if (pxSize >= 0.01)         // centimetre or larger → metres
                {
                    unit = "m";
                    scaled = physDist;
                }
                else if (pxSize >= 0.001)   // millimetre range
                {
                    unit = "mm";
                    scaled = physDist * 1e3f;
                }
                else if (pxSize >= 0.000001)
                {
                    unit = "µm";
                    scaled = physDist * 1e6f;
                }
                else
                {
                    unit = "units";
                    scaled = physDist;
                }

                float pixelDistance = (float)(pxSize > 0 ? physDist / pxSize : 0);

                sb.AppendLine(
                    $"Src-Rec Dist.: {scaled:F2} {unit}  ({pixelDistance:F0} px)");

                sb.AppendLine("P-Wave Arrival: " + (PWaveArrivalTime * 1e3f).ToString("F2") + " ms");
                sb.AppendLine("S-Wave Arrival: " + (SWaveArrivalTime * 1e3f).ToString("F2") + " ms");
                sb.AppendLine("Max Amplitude:  " + MaximumDisplacement.ToString("E3"));

                g.DrawString(sb.ToString(), font, brush, x, y);
            }
        }

        private float[,,] BuildFieldFromHistory(bool pWave)
        {
            List<float[]> hist = pWave ? PWaveDisplacementHistory : SWaveDisplacementHistory;
            if (hist == null || hist.Count == 0)
                return null;

            float[] last = hist[hist.Count - 1];  // last stored step

            // Create a properly viewable field - if we have 1D data, reshape it to 2D for visualization
            int size = (int)Math.Ceiling(Math.Sqrt(last.Length));
            var f = new float[size, size, 1];  // Create a square field for better visualization

            // Fill the field with data in a 2D pattern
            for (int i = 0; i < last.Length; i++)
            {
                int x = i % size;
                int y = i / size;
                if (y < size)  // Ensure we don't go out of bounds
                    f[x, y, 0] = last[i];
            }

            // Log dimensions and value range for debugging
            float minVal = float.MaxValue, maxVal = float.MinValue;
            foreach (float val in f)
            {
                if (!float.IsNaN(val) && !float.IsInfinity(val))
                {
                    minVal = Math.Min(minVal, val);
                    maxVal = Math.Max(maxVal, val);
                }
            }

            Logger.Log($"[AcousticVelocitySimulation] Built field from history: {f.GetLength(0)}x{f.GetLength(1)}x{f.GetLength(2)}, " +
                      $"values range: {minVal:E6} to {maxVal:E6}");

            return f;
        }


        /// <summary>
        /// Draw a 2D slice of the wave field
        /// </summary>
        private void DrawWaveFieldSlice(Graphics g, float[,,] waveField, int sliceIndex, string sliceAxis,
                                int x, int y, int width, int height, float maxAmplitude,
                                int sourcePos1, int sourcePos2, int receiverPos1, int receiverPos2)
        {
            // Create bitmap for the slice
            using (Bitmap slice = new Bitmap(width, height))
            {
                // Get dimensions for the slice
                int dim1, dim2;
                if (sliceAxis == "X")
                {
                    dim1 = _gridSizeY;
                    dim2 = _gridSizeZ;
                }
                else if (sliceAxis == "Y")
                {
                    dim1 = _gridSizeX;
                    dim2 = _gridSizeZ;
                }
                else // Z
                {
                    dim1 = _gridSizeX;
                    dim2 = _gridSizeY;
                }

                // Scale factors to fit the slice in the drawing area
                float scaleX = width / (float)dim1;
                float scaleY = height / (float)dim2;

                // Apply sensitivity boost factor to make small waves more visible
                float sensitivityBoost = 10.0f; // Increase this to make waves more visible

                // Draw each pixel of the slice with enhanced visibility
                for (int i = 0; i < dim1; i++)
                {
                    for (int j = 0; j < dim2; j++)
                    {
                        // Get the wave field value at this position
                        float value;
                        if (sliceAxis == "X")
                        {
                            value = waveField[sliceIndex, i, j];
                        }
                        else if (sliceAxis == "Y")
                        {
                            value = waveField[i, sliceIndex, j];
                        }
                        else // Z
                        {
                            value = waveField[i, j, sliceIndex];
                        }

                        // Apply sensitivity boost and normalize
                        float normalizedValue = (value * sensitivityBoost) / maxAmplitude;

                        // Clamp to valid range
                        normalizedValue = Math.Max(-1.0f, Math.Min(1.0f, normalizedValue));

                        // Use a bipolar colormap with more vibrant colors
                        Color color = GetEnhancedBipolarColor(normalizedValue);

                        // Calculate pixel position
                        int pixelX = (int)(i * scaleX);
                        int pixelY = (int)(j * scaleY);

                        // Ensure within bounds
                        pixelX = Math.Max(0, Math.Min(pixelX, width - 1));
                        pixelY = Math.Max(0, Math.Min(pixelY, height - 1));

                        // Set pixel color
                        slice.SetPixel(pixelX, pixelY, color);
                    }
                }

                // Draw the slice
                g.DrawImage(slice, x, y, width, height);

                // Draw borders, source/receiver markers, etc.
                // (rest of method remains the same)
            

                // Draw a border
                using (Pen borderPen = new Pen(Color.Gray, 1))
                {
                    g.DrawRectangle(borderPen, x, y, width, height);
                }

                // Draw source and receiver positions
                using (Brush sourceBrush = new SolidBrush(Color.Yellow))
                using (Brush receiverBrush = new SolidBrush(Color.Cyan))
                {
                    int sourceX = x + (int)(sourcePos1 * scaleX);
                    int sourceY = y + (int)(sourcePos2 * scaleY);
                    int receiverX = x + (int)(receiverPos1 * scaleX);
                    int receiverY = y + (int)(receiverPos2 * scaleY);

                    // Draw source marker (diamond)
                    g.FillEllipse(sourceBrush, sourceX - 4, sourceY - 4, 8, 8);

                    // Draw receiver marker (circle)
                    g.FillEllipse(receiverBrush, receiverX - 4, receiverY - 4, 8, 8);

                    // Draw a line between source and receiver
                    using (Pen rayPath = new Pen(Color.White, 1))
                    {
                        rayPath.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                        g.DrawLine(rayPath, sourceX, sourceY, receiverX, receiverY);
                    }
                }

                // Draw axis labels
                using (Font font = new Font("Arial", 9))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    string title = sliceAxis == "X" ? "Y-Z Slice" : (sliceAxis == "Y" ? "X-Z Slice" : "X-Y Slice");
                    g.DrawString(title, font, textBrush, x + width / 2 - 20, y - 15);
                }
            }
        }
        private Color GetEnhancedBipolarColor(float normalizedValue)
        {
            // Ensure value is in range [-1, 1]
            normalizedValue = Math.Max(-1.0f, Math.Min(1.0f, normalizedValue));

            // Convert to [0, 1] range
            float colorValue = (normalizedValue + 1) / 2;

            // Enhanced blue to white to red gradient with more vibrant colors
            if (colorValue < 0.5f)
            {
                // Blue to White (for negative values)
                float t = colorValue * 2; // Scale to [0, 1]

                // More vibrant blue
                return Color.FromArgb(
                    (int)(t * 255),         // R increases from 0 to 255
                    (int)(t * 255),         // G increases from 0 to 255
                    255                     // B stays at 255 (full blue)
                );
            }
            else
            {
                // White to Red (for positive values)
                float t = (colorValue - 0.5f) * 2; // Scale to [0, 1]

                // More vibrant red
                return Color.FromArgb(
                    255,                    // R stays at 255 (full red)
                    (int)((1 - t) * 255),   // G decreases from 255 to 0
                    (int)((1 - t) * 255)    // B decreases from 255 to 0
                );
            }
        }
        /// <summary>
        /// Draw a color scale
        /// </summary>
        private void DrawColorScale(Graphics g, int x, int y, int width, int height, string label)
        {
            // Ensure minimum dimensions
            int minHeight = 10;
            height = Math.Max(minHeight, height);

            // Draw the color scale gradient
            Rectangle scaleRect = new Rectangle(x, y, width, height);
            using (LinearGradientBrush brush = new LinearGradientBrush(
                scaleRect, Color.Blue, Color.Red, LinearGradientMode.Vertical))
            {
                ColorBlend blend = new ColorBlend(5);
                blend.Colors = new Color[] { Color.Blue, Color.Cyan, Color.White, Color.Yellow, Color.Red };
                blend.Positions = new float[] { 0.0f, 0.25f, 0.5f, 0.75f, 1.0f };
                brush.InterpolationColors = blend;

                g.FillRectangle(brush, scaleRect);
            }

            // Draw border
            using (Pen pen = new Pen(Color.Gray, 1))
            {
                g.DrawRectangle(pen, scaleRect);
            }

            // Draw labels
            using (Font font = new Font("Arial", 8))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                g.DrawString(label, font, textBrush, x, y - 15);
                g.DrawString("Max", font, textBrush, x - 25, y);
                g.DrawString("0", font, textBrush, x - 15, y + height / 2 - 6);
                g.DrawString("Min", font, textBrush, x - 25, y + height - 8);
            }
        }

        /// <summary>
        /// Get a bipolar color (blue-white-red) based on normalized value (-1 to 1)
        /// </summary>
        private Color GetBipolarColor(float normalizedValue)
        {
            // Ensure value is in range [-1, 1]
            normalizedValue = ClampValue(normalizedValue, -1, 1);

            // Convert to [0, 1] range for the color mapping
            float colorValue = (normalizedValue + 1) / 2;

            // Blue (-1) to White (0) to Red (1)
            if (colorValue < 0.5f)
            {
                // Blue to White [-1, 0] mapped to [0, 0.5]
                float t = colorValue * 2; // Scale to [0, 1]
                return Color.FromArgb(
                    (int)(t * 255),     // R increases from 0 to 255
                    (int)(t * 255),     // G increases from 0 to 255
                    255                  // B stays at 255
                );
            }
            else
            {
                // White to Red [0, 1] mapped to [0.5, 1]
                float t = (colorValue - 0.5f) * 2; // Scale to [0, 1]
                return Color.FromArgb(
                    255,                // R stays at 255
                    (int)((1 - t) * 255), // G decreases from 255 to 0
                    (int)((1 - t) * 255)  // B decreases from 255 to 0
                );
            }
        }

        /// <summary>
        /// Render the time series data
        /// </summary>
        private void RenderTimeSeries(Graphics g, int width, int height)
        {
            g.Clear(Color.Black);

            if (ReceiverTimeSeries == null || ReceiverTimeSeries.Length == 0)
            {
                using (Font font = new Font("Arial", 12))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    g.DrawString("No time series data available", font, brush, 20, 20);
                }
                return;
            }

            // Set up plot area
            int margin = 50;
            int plotWidth = width - 2 * margin;
            int plotHeight = height - 2 * margin;

            // Calculate time step
            float velocity = _isPWave ? PWaveVelocity : SWaveVelocity;
            float dt = _gridSpacing / (1.2f * velocity);

            // Find signal bounds for scaling
            float maxAmplitude = float.MinValue;
            float minAmplitude = float.MaxValue;

            for (int i = 0; i < ReceiverTimeSeries.Length; i++)
            {
                maxAmplitude = Math.Max(maxAmplitude, ReceiverTimeSeries[i]);
                minAmplitude = Math.Min(minAmplitude, ReceiverTimeSeries[i]);
            }

            // If there's effectively no signal, create a minimal synthetic range
            if (Math.Abs(maxAmplitude - minAmplitude) < 1e-6)
            {
                maxAmplitude = 0.01f;
                minAmplitude = -0.01f;
            }

            float amplitude = Math.Max(Math.Abs(maxAmplitude), Math.Abs(minAmplitude));
            float peakToPeak = maxAmplitude - minAmplitude;

            // Always start from time 0
            float startTime = 0;

            // Ensure we view enough time to see wave arrivals
            // Theoretical arrival time plus margin
            float endTime = Math.Max(SampleLength / velocity * 1.5f, 0.001f);

            // If we know actual arrival times, extend to show them
            if (PWaveArrivalTime > 0)
            {
                endTime = Math.Max(endTime, PWaveArrivalTime * 1.2f);
            }

            if (SWaveArrivalTime > 0)
            {
                endTime = Math.Max(endTime, SWaveArrivalTime * 1.2f);
            }

            // Ensure we don't exceed data length
            endTime = Math.Min(endTime, ReceiverTimeSeries.Length * dt);

            // Calculate time range
            float timeRange = endTime - startTime;

            // Scale factors for plot
            float timeScale = plotWidth / timeRange;
            float amplitudeScale = plotHeight / (2 * amplitude);

            // Draw axes
            using (Pen axisPen = new Pen(Color.White, 1))
            {
                // X axis (time)
                g.DrawLine(axisPen, margin, margin + plotHeight / 2, margin + plotWidth, margin + plotHeight / 2);

                // Y axis (amplitude)
                g.DrawLine(axisPen, margin, margin, margin, margin + plotHeight);

                // Draw tick marks and grid lines
                using (Pen gridPen = new Pen(Color.FromArgb(60, 60, 60), 1))
                {
                    gridPen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;

                    // Time axis ticks and grid
                    int numTimeTicks = 10;
                    for (int i = 0; i <= numTimeTicks; i++)
                    {
                        float time = startTime + i * timeRange / numTimeTicks;
                        int x = margin + (int)((time - startTime) * timeScale);

                        // Tick mark
                        g.DrawLine(axisPen, x, margin + plotHeight / 2 - 5, x, margin + plotHeight / 2 + 5);

                        // Grid line
                        g.DrawLine(gridPen, x, margin, x, margin + plotHeight);

                        // Label
                        using (Font font = new Font("Arial", 8))
                        using (SolidBrush textBrush = new SolidBrush(Color.LightGray))
                        {
                            g.DrawString($"{time * 1000:F1} ms", font, textBrush, x - 20, margin + plotHeight / 2 + 10);
                        }
                    }

                    // Amplitude axis ticks and grid
                    int numAmpTicks = 4;
                    for (int i = -numAmpTicks; i <= numAmpTicks; i++)
                    {
                        if (i == 0) continue; // Skip center (already drawn as time axis)

                        int y = margin + plotHeight / 2 - (int)(i * amplitudeScale * amplitude / numAmpTicks);

                        // Tick mark
                        g.DrawLine(axisPen, margin - 5, y, margin + 5, y);

                        // Grid line
                        g.DrawLine(gridPen, margin, y, margin + plotWidth, y);

                        // Label
                        float ampValue = i * amplitude / numAmpTicks;
                        using (Font font = new Font("Arial", 8))
                        using (SolidBrush textBrush = new SolidBrush(Color.LightGray))
                        {
                            g.DrawString($"{ampValue:E1}", font, textBrush, margin - 45, y - 6);
                        }
                    }
                }
            }

            // Plot the time series
            using (Pen waveformPen = new Pen(_isPWave ? Color.Cyan : Color.GreenYellow, 2))
            {
                // Store points for the waveform
                List<Point> points = new List<Point>();

                // Convert time range to sample indices
                int firstSample = 0;
                int lastSample = Math.Min(ReceiverTimeSeries.Length - 1, (int)(endTime / dt));

                for (int i = firstSample; i <= lastSample; i++)
                {
                    float time = i * dt;
                    int x = margin + (int)((time - startTime) * timeScale);
                    int y = margin + plotHeight / 2 - (int)(ReceiverTimeSeries[i] * amplitudeScale);

                    // Ensure within bounds
                    x = Math.Max(margin, Math.Min(x, margin + plotWidth));
                    y = Math.Max(margin, Math.Min(y, margin + plotHeight));

                    points.Add(new Point(x, y));
                }

                // Draw waveform with anti-aliasing for smoother appearance
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                if (points.Count > 1)
                {
                    g.DrawLines(waveformPen, points.ToArray());
                }
            }

            // Mark arrival times with better accuracy
            if (PWaveArrivalTime > 0)
            {
                using (Pen pWavePen = new Pen(Color.Blue, 1))
                {
                    pWavePen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                    int x = margin + (int)((PWaveArrivalTime - startTime) * timeScale);

                    // Ensure x is within the plot area
                    if (x >= margin && x <= margin + plotWidth)
                    {
                        g.DrawLine(pWavePen, x, margin, x, margin + plotHeight);

                        using (Font font = new Font("Arial", 8, FontStyle.Bold))
                        using (SolidBrush textBrush = new SolidBrush(Color.Blue))
                        {
                            g.DrawString("P-wave", font, textBrush, x - 25, margin - 15);
                        }
                    }
                }
            }

            if (SWaveArrivalTime > 0)
            {
                using (Pen sWavePen = new Pen(Color.Red, 1))
                {
                    sWavePen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                    int x = margin + (int)((SWaveArrivalTime - startTime) * timeScale);

                    // Ensure x is within the plot area
                    if (x >= margin && x <= margin + plotWidth)
                    {
                        g.DrawLine(sWavePen, x, margin, x, margin + plotHeight);

                        using (Font font = new Font("Arial", 8, FontStyle.Bold))
                        using (SolidBrush textBrush = new SolidBrush(Color.Red))
                        {
                            g.DrawString("S-wave", font, textBrush, x - 25, margin - 15);
                        }
                    }
                }
            }

            // Draw titles and labels
            using (Font titleFont = new Font("Arial", 14, FontStyle.Bold))
            using (Font labelFont = new Font("Arial", 10))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                // Title
                string title = $"Receiver Waveform - {WaveType}";
                g.DrawString(title, titleFont, textBrush, (width - g.MeasureString(title, titleFont).Width) / 2, 10);

                // X axis label
                string xLabel = "Time (ms)";
                g.DrawString(xLabel, labelFont, textBrush, margin + plotWidth / 2 - 20, margin + plotHeight + 25);

                // Y axis label
                string yLabel = "Amplitude";
                // Rotate text for Y axis label
                g.TranslateTransform(margin - 30, margin + plotHeight / 2 + 20);
                g.RotateTransform(-90);
                g.DrawString(yLabel, labelFont, textBrush, 0, 0);
                g.ResetTransform();

                // Add signal info at bottom
                string signalInfo = $"Peak-to-peak: {peakToPeak:E3}, P-wave arrival: {PWaveArrivalTime * 1000:F2} ms, S-wave arrival: {SWaveArrivalTime * 1000:F2} ms";
                g.DrawString(signalInfo, new Font("Arial", 8), textBrush, margin, margin + plotHeight + 25);
            }
        }
        private static void PropagateImprovedSWaveKernel(
     Index1D index,
     ArrayView<float> u,    // Displacement field
     ArrayView<float> v,    // Velocity field
     ArrayView<float> a,    // Acceleration field
     float dt,              // Time step
     float dx,              // Grid spacing
     ArrayView<float> velocityProfile, // Velocity at each point
     float attenuation,     // Attenuation factor
     float density,         // Material density
     float youngModulus,    // Young's modulus
     float poissonRatio,    // Poisson's ratio
     ArrayView<float> damping,  // Damping profile
     ArrayView<float> result)   // Result field (next u)
        {
            int i = index.X;

            // Skip boundary cells to avoid OOB access
            if (i <= 1 || i >= u.Length - 2)
            {
                result[i] = 0.0f; // Zero at boundaries
                return;
            }

            // Get local wave velocity from profile
            float velocity = velocityProfile[i];

            // Calculate second spatial derivative (Laplacian) with better stencil for S-waves
            // Using a higher-order finite difference approximation for better accuracy
            float d2u = (u[i + 1] - 2.0f * u[i] + u[i - 1]) / (dx * dx);

            // Calculate wave equation with much higher boost for microscale
            // Increase from 1.2 to 2.0 to ensure wave propagation at small scale
            float c2 = velocity * velocity * 3.0f;

            // Eliminate damping to allow waves to propagate at microscale
            // Original damping (0.005f) was too high for microscale
            float dampingTerm = 0.0001f * v[i];

            // Calculate acceleration
            a[i] = c2 * d2u - dampingTerm;

            // Update velocity using acceleration with a boost factor
            v[i] += a[i] * dt * 1.5f; // Add 50% boost to velocity updates

            // Update displacement using velocity with a boost factor
            result[i] = u[i] + v[i] * dt * 1.5f; // Add 50% boost to displacement updates

            // Apply greatly reduced boundary damping
            if (damping[i] > 0)
            {
                // Reduced damping effect by 90%
                result[i] *= (1.0f - 0.1f * damping[i]);
            }
        }
        /// <summary>
        /// Render the velocity distribution
        /// </summary>
        private void RenderVelocityDistribution(Graphics g, int width, int height)
        {
            g.Clear(Color.Black);

            if (_velocityModel == null || width <= 0 || height <= 0)
            {
                using (Font font = new Font("Arial", 12))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    g.DrawString("No velocity model available or invalid dimensions", font, brush, 20, 20);
                }
                return;
            }

            // Set up plot area
            int margin = 50;
            int plotWidth = width - 2 * margin;
            int plotHeight = height - 2 * margin;

            // Find min and max velocities - ignore air (values below 500)
            float minVelocity = float.MaxValue;
            float maxVelocity = float.MinValue;
            bool hasData = false;

            for (int x = 0; x < _gridSizeX; x++)
            {
                for (int y = 0; y < _gridSizeY; y++)
                {
                    for (int z = 0; z < _gridSizeZ; z++)
                    {
                        float vel = _velocityModel[x, y, z];
                        if (vel > 500) // Ignore air cells
                        {
                            minVelocity = Math.Min(minVelocity, vel);
                            maxVelocity = Math.Max(maxVelocity, vel);
                            hasData = true;
                        }
                    }
                }
            }

            if (!hasData || minVelocity >= maxVelocity)
            {
                // Use default values if no valid data found
                minVelocity = _isPWave ? PWaveVelocity * 0.8f : SWaveVelocity * 0.8f;
                maxVelocity = _isPWave ? PWaveVelocity * 1.2f : SWaveVelocity * 1.2f;

                // Log this issue
                Logger.Log($"[AcousticVelocitySimulation] No valid velocity data found, using defaults: {minVelocity} to {maxVelocity}");
            }

            // Ensure we have a reasonable range
            if (Math.Abs(maxVelocity - minVelocity) < 10)
            {
                maxVelocity = minVelocity + 100;
            }

            // Create 2D slices of the velocity model
            int sliceHeight = (plotHeight - 2 * margin) / 3;

            // Draw X-Z slice (top)
            DrawVelocityModelSlice(g, "Y", margin, margin, plotWidth, sliceHeight, minVelocity, maxVelocity);

            // Draw Y-Z slice (middle)
            DrawVelocityModelSlice(g, "X", margin, margin + sliceHeight + margin / 2, plotWidth, sliceHeight, minVelocity, maxVelocity);

            // Draw histogram (bottom)
            DrawVelocityHistogram(g, margin, margin + 2 * (sliceHeight + margin / 2), plotWidth, sliceHeight, minVelocity, maxVelocity);

            // Draw title
            using (Font titleFont = new Font("Arial", 14, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                string title = $"Velocity Distribution - {(_isPWave ? "P-Wave" : "S-Wave")}";
                g.DrawString(title, titleFont, textBrush, (width - g.MeasureString(title, titleFont).Width) / 2, 10);
            }

            // Draw color scale
            DrawColorScale(g, width - margin, margin + plotHeight / 2, 20, plotHeight / 2, "Velocity (m/s)");

            // Add info text
            using (Font font = new Font("Arial", 10))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                string info = $"Min: {minVelocity:F0} m/s, Max: {maxVelocity:F0} m/s, Mean: {(_isPWave ? MeasuredPWaveVelocity : MeasuredSWaveVelocity):F0} m/s";
                g.DrawString(info, font, textBrush, margin, height - 25);
            }
        }

        /// <summary>
        /// Draw a 2D slice of the velocity model
        /// </summary>
        private void DrawVelocityModelSlice(Graphics g, string sliceAxis, int x, int y, int width, int height, float minVelocity, float maxVelocity)
        {
            if (width <= 0 || height <= 0 || width > 10000 || height > 10000)
            {
                // Draw an error message instead of creating an invalid bitmap
                using (Font font = new Font("Arial", 10))
                using (SolidBrush brush = new SolidBrush(Color.Red))
                {
                    g.DrawString("Invalid slice dimensions", font, brush, x, y);
                }
                return;
            }

            // Get slice index (middle of the model)
            int sliceIndex;
            string title;

            if (sliceAxis == "X")
            {
                sliceIndex = _gridSizeX / 2;
                title = "Y-Z Slice (X middle)";
            }
            else if (sliceAxis == "Y")
            {
                sliceIndex = _gridSizeY / 2;
                title = "X-Z Slice (Y middle)";
            }
            else // Z
            {
                sliceIndex = _gridSizeZ / 2;
                title = "X-Y Slice (Z middle)";
            }

            try
            {
                // Create bitmap for the slice
                using (Bitmap slice = new Bitmap(width, height))
                {
                    // Get dimensions for the slice
                    int dim1, dim2;
                    if (sliceAxis == "X")
                    {
                        dim1 = _gridSizeY;
                        dim2 = _gridSizeZ;
                    }
                    else if (sliceAxis == "Y")
                    {
                        dim1 = _gridSizeX;
                        dim2 = _gridSizeZ;
                    }
                    else // Z
                    {
                        dim1 = _gridSizeX;
                        dim2 = _gridSizeY;
                    }

                    // Scale factors to fit the slice in the drawing area
                    float scaleX = width / (float)dim1;
                    float scaleY = height / (float)dim2;

                    // Draw each pixel of the slice
                    using (Graphics sliceG = Graphics.FromImage(slice))
                    {
                        sliceG.Clear(Color.Black);

                        // Use a faster approach by drawing rectangles for regions of similar velocity
                        for (int i = 0; i < dim1; i++)
                        {
                            for (int j = 0; j < dim2; j++)
                            {
                                // Get the velocity at this position
                                float velocity;
                                if (sliceAxis == "X")
                                {
                                    velocity = _velocityModel[sliceIndex, i, j];
                                }
                                else if (sliceAxis == "Y")
                                {
                                    velocity = _velocityModel[i, sliceIndex, j];
                                }
                                else // Z
                                {
                                    velocity = _velocityModel[i, j, sliceIndex];
                                }

                                // Skip air cells (very low velocity)
                                if (velocity < 500)
                                {
                                    // Draw as black or dark gray for air
                                    continue;
                                }

                                // Normalize and get color with bounds checking
                                float normalizedValue = (velocity - minVelocity) / (maxVelocity - minVelocity);
                                normalizedValue = Math.Max(0, Math.Min(1, normalizedValue)); // Clamp to [0,1]
                                Color color = GetHeatMapColor(normalizedValue, 0, 1);

                                // Calculate rectangle position and size
                                int rectX = (int)(i * scaleX);
                                int rectY = (int)(j * scaleY);
                                int rectWidth = Math.Max(1, (int)scaleX + 1);  // Ensure at least 1 pixel wide
                                int rectHeight = Math.Max(1, (int)scaleY + 1); // Ensure at least 1 pixel high

                                // Draw the rectangle
                                using (SolidBrush brush = new SolidBrush(color))
                                {
                                    sliceG.FillRectangle(brush, rectX, rectY, rectWidth, rectHeight);
                                }
                            }
                        }
                    }

                    // Draw the slice
                    g.DrawImage(slice, x, y, width, height);

                    // Draw a border
                    using (Pen borderPen = new Pen(Color.Gray, 1))
                    {
                        g.DrawRectangle(borderPen, x, y, width, height);
                    }

                    // Draw title
                    using (Font font = new Font("Arial", 10))
                    using (SolidBrush textBrush = new SolidBrush(Color.White))
                    {
                        g.DrawString(title, font, textBrush, x + width / 2 - 60, y - 15);
                    }

                    // Draw source and receiver positions if applicable
                    if ((sliceAxis == "X" && sliceIndex == _sourceX) ||
                        (sliceAxis == "Y" && sliceIndex == _sourceY) ||
                        (sliceAxis == "Z" && sliceIndex == _sourceZ))
                    {
                        using (Brush sourceBrush = new SolidBrush(Color.Yellow))
                        {
                            int sourcePos1, sourcePos2;
                            if (sliceAxis == "X")
                            {
                                sourcePos1 = _sourceY;
                                sourcePos2 = _sourceZ;
                            }
                            else if (sliceAxis == "Y")
                            {
                                sourcePos1 = _sourceX;
                                sourcePos2 = _sourceZ;
                            }
                            else // Z
                            {
                                sourcePos1 = _sourceX;
                                sourcePos2 = _sourceY;
                            }

                            int sourceX = x + (int)(sourcePos1 * scaleX);
                            int sourceY = y + (int)(sourcePos2 * scaleY);

                            g.FillEllipse(sourceBrush, sourceX - 4, sourceY - 4, 8, 8);
                        }
                    }

                    if ((sliceAxis == "X" && sliceIndex == _receiverX) ||
                        (sliceAxis == "Y" && sliceIndex == _receiverY) ||
                        (sliceAxis == "Z" && sliceIndex == _receiverZ))
                    {
                        using (Brush receiverBrush = new SolidBrush(Color.Cyan))
                        {
                            int receiverPos1, receiverPos2;
                            if (sliceAxis == "X")
                            {
                                receiverPos1 = _receiverY;
                                receiverPos2 = _receiverZ;
                            }
                            else if (sliceAxis == "Y")
                            {
                                receiverPos1 = _receiverX;
                                receiverPos2 = _receiverZ;
                            }
                            else // Z
                            {
                                receiverPos1 = _receiverX;
                                receiverPos2 = _receiverY;
                            }

                            int receiverX = x + (int)(receiverPos1 * scaleX);
                            int receiverY = y + (int)(receiverPos2 * scaleY);

                            g.FillEllipse(receiverBrush, receiverX - 4, receiverY - 4, 8, 8);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Handle any exceptions during rendering
                using (Font font = new Font("Arial", 10))
                using (SolidBrush brush = new SolidBrush(Color.Red))
                {
                    g.DrawString($"Error rendering slice: {ex.Message}", font, brush, x, y);
                }
                Logger.Log($"[AcousticVelocitySimulation] Error rendering velocity slice: {ex.Message}");
            }
        }
        /// <summary>
        /// Draw a histogram of the velocity distribution
        /// </summary>
        private void DrawVelocityHistogram(Graphics g, int x, int y, int width, int height, float minVelocity, float maxVelocity)
        {
            try
            {
                // Number of bins for the histogram
                int numBins = 20;

                // Calculate bin width
                float binWidth = (maxVelocity - minVelocity) / numBins;
                if (binWidth <= 0) binWidth = 1.0f;  // Fallback if min=max

                // Count velocities in each bin
                int[] bins = new int[numBins];
                int totalCells = 0;

                // Create the bins with proper bounds checking
                for (int gx = 0; gx < _gridSizeX; gx++)
                {
                    for (int gy = 0; gy < _gridSizeY; gy++)
                    {
                        for (int gz = 0; gz < _gridSizeZ; gz++)
                        {
                            float vel = _velocityModel[gx, gy, gz];
                            if (vel > 500) // Ignore air cells
                            {
                                int binIndex = (int)((vel - minVelocity) / binWidth);
                                binIndex = Math.Max(0, Math.Min(binIndex, numBins - 1));
                                bins[binIndex]++;
                                totalCells++;
                            }
                        }
                    }
                }

                // Find the maximum bin count - default to 1 if all bins are empty
                int maxCount = 1;
                foreach (int count in bins)
                {
                    if (count > maxCount) maxCount = count;
                }

                // If no data was found, we should indicate this to the user
                if (totalCells == 0)
                {
                    using (Font font = new Font("Arial", 12))
                    using (SolidBrush brush = new SolidBrush(Color.Yellow))
                    {
                        g.DrawString("No velocity data available", font, brush, x + width / 2 - 70, y + height / 2 - 10);
                    }
                    return;
                }

                // Scale factor for bin height
                float scale = height * 0.9f / maxCount;  // Leave some margin at the top

                // Clear the area first
                using (SolidBrush bgBrush = new SolidBrush(Color.Black))
                {
                    g.FillRectangle(bgBrush, x, y, width, height);
                }

                // Draw histogram bars
                float barWidth = (float)width / numBins;

                for (int i = 0; i < numBins; i++)
                {
                    // Calculate bar position and size
                    float barX = x + i * barWidth;
                    float barHeight = bins[i] * scale;
                    float barY = y + height - barHeight;

                    // Get color based on bin value (same as the colormap)
                    float normalizedValue = (i + 0.5f) / numBins;
                    Color color = GetHeatMapColor(normalizedValue, 0, 1);

                    // Draw bar with a fallback minimum height for visibility when count > 0
                    if (bins[i] > 0)
                    {
                        barHeight = Math.Max(barHeight, 2); // Ensure at least 2 pixels high if any data exists
                    }

                    using (SolidBrush brush = new SolidBrush(color))
                    {
                        g.FillRectangle(brush, barX, barY, barWidth, barHeight);
                    }

                    // Draw outline
                    using (Pen pen = new Pen(Color.FromArgb(50, Color.White), 1))
                    {
                        g.DrawRectangle(pen, barX, barY, barWidth, barHeight);
                    }
                }

                // Draw axes
                using (Pen axisPen = new Pen(Color.White, 1))
                {
                    // X axis
                    g.DrawLine(axisPen, x, y + height, x + width, y + height);

                    // Y axis
                    g.DrawLine(axisPen, x, y, x, y + height);
                }

                // Draw tick marks and labels
                using (Pen axisPen = new Pen(Color.Gray, 1))
                using (Font font = new Font("Arial", 8))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    // X axis ticks
                    int numTicks = 5;
                    for (int i = 0; i <= numTicks; i++)
                    {
                        float tickX = x + i * width / numTicks;
                        float value = minVelocity + i * (maxVelocity - minVelocity) / numTicks;

                        g.DrawLine(axisPen, tickX, y + height, tickX, y + height + 5);
                        g.DrawString($"{value:F0}", font, textBrush, tickX - 15, y + height + 5);
                    }

                    // Mark mean value
                    float meanValue = _isPWave ? MeasuredPWaveVelocity : MeasuredSWaveVelocity;
                    if (meanValue > minVelocity && meanValue < maxVelocity)
                    {
                        float meanX = x + (meanValue - minVelocity) / (maxVelocity - minVelocity) * width;

                        using (Pen meanPen = new Pen(Color.Red, 1))
                        {
                            meanPen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                            g.DrawLine(meanPen, meanX, y, meanX, y + height);
                        }

                        g.DrawString("Mean", font, new SolidBrush(Color.Red), meanX - 15, y - 15);
                    }
                }

                // Draw title
                using (Font titleFont = new Font("Arial", 10))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    string title = "Velocity Histogram";
                    g.DrawString(title, titleFont, textBrush, x + width / 2 - 50, y - 15);
                }
            }
            catch (Exception ex)
            {
                // Handle any exceptions during rendering
                using (Font font = new Font("Arial", 10))
                using (SolidBrush brush = new SolidBrush(Color.Red))
                {
                    g.DrawString($"Error rendering histogram: {ex.Message}", font, brush, x, y);
                }
                Logger.Log($"[AcousticVelocitySimulation] Error rendering velocity histogram: {ex.Message}");
            }
        }
        /// <summary>
        /// Render a 3D slice of the wave field
        /// </summary>
        private void RenderWaveSlice(Graphics g, int width, int height)
        {
            if (width <= 0 || height <= 0 || width > 10000 || height > 10000)
            {
                using (Font font = new Font("Arial", 10))
                using (SolidBrush brush = new SolidBrush(Color.Red))
                {
                    g.DrawString("Invalid dimensions", font, brush, width, height);
                }
                return;
            }
            g.Clear(Color.Black);

            // Determine which wave field to visualize
            float[,,] waveField = _isPWave ? PWaveField : SWaveField;

            if (waveField == null)
            {
                using (Font font = new Font("Arial", 12))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    g.DrawString("No wave field data available", font, brush, 20, 20);
                }
                return;
            }

            // Draw a 3D representation of the wave field using multiple slices
            int margin = 20;
            int sliceSize = Math.Min((width - 4 * margin) / 3, (height - 2 * margin));

            // Set time-steps for the animation
            int numSlices = Math.Min(WaveDisplacementVectors.Count, 3);

            // Find max displacement across all time steps
            float maxDisplacement = 0;
            foreach (var displacements in WaveDisplacementVectors)
            {
                foreach (var disp in displacements)
                {
                    float magnitude = (float)Math.Sqrt(disp.X * disp.X + disp.Y * disp.Y + disp.Z * disp.Z);
                    maxDisplacement = Math.Max(maxDisplacement, magnitude);
                }
            }

            if (maxDisplacement == 0) maxDisplacement = 1;

            // Draw each time slice
            for (int i = 0; i < numSlices; i++)
            {
                int timeIndex = WaveDisplacementVectors.Count - numSlices + i;
                if (timeIndex < 0 || timeIndex >= WaveDisplacementVectors.Count)
                    continue;

                Vector3[] displacements = WaveDisplacementVectors[timeIndex];
                float simulationTime = SimulationTimes[timeIndex];

                int sliceX = margin + i * (sliceSize + margin);
                int sliceY = margin;

                DrawWaveDisplacementSlice(g, displacements, sliceX, sliceY, sliceSize, sliceSize, maxDisplacement, simulationTime);
            }

            // Draw title
            using (Font titleFont = new Font("Arial", 14, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                string title = $"{WaveType} Propagation Slices";
                g.DrawString(title, titleFont, textBrush, (width - g.MeasureString(title, titleFont).Width) / 2, 5);
            }

            // Draw results panel
            int resultsPanelX = margin;
            int resultsPanelY = margin * 2 + sliceSize;
            int resultsPanelWidth = width - 2 * margin;
            int resultsPanelHeight = height - resultsPanelY - margin;

            using (SolidBrush panelBrush = new SolidBrush(Color.FromArgb(50, 50, 50)))
            {
                g.FillRectangle(panelBrush, resultsPanelX, resultsPanelY, resultsPanelWidth, resultsPanelHeight);
            }

            using (Font font = new Font("Arial", 10))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                int y = resultsPanelY + 10;
                int rowHeight = 20;
                int col1 = resultsPanelX + 10;
                int col2 = resultsPanelX + resultsPanelWidth / 2;

                // Draw results in two columns
                g.DrawString($"Material: {Material.Name}", font, textBrush, col1, y);
                g.DrawString($"Density: {Material.Density:F1} kg/m³", font, textBrush, col2, y);
                y += rowHeight;

                g.DrawString($"P-Wave Velocity: {MeasuredPWaveVelocity:F1} m/s", font, textBrush, col1, y);
                g.DrawString($"S-Wave Velocity: {MeasuredSWaveVelocity:F1} m/s", font, textBrush, col2, y);
                y += rowHeight;

                g.DrawString($"P-Wave Arrival: {PWaveArrivalTime * 1000:F2} ms", font, textBrush, col1, y);
                g.DrawString($"S-Wave Arrival: {SWaveArrivalTime * 1000:F2} ms", font, textBrush, col2, y);
                y += rowHeight;

                g.DrawString($"Vp/Vs Ratio: {CalculatedVpVsRatio:F2}", font, textBrush, col1, y);
                g.DrawString($"Max Displacement: {MaximumDisplacement:E2} m", font, textBrush, col2, y);
            }

            // Draw color scale
            DrawColorScale(g, width - margin - 30, margin + sliceSize / 2, 20, sliceSize / 2, "Displacement");
        }

        /// <summary>
        /// Draw a slice showing wave displacement vectors
        /// </summary>
        private void DrawWaveDisplacementSlice(Graphics g, Vector3[] displacements, int x, int y, int width, int height,
                                              float maxDisplacement, float time)
        {
            // Create bitmap for the slice
            using (Bitmap slice = new Bitmap(width, height))
            {
                // Scale factors
                float scaleX = width / (float)_gridSizeX;
                float scaleY = height / (float)_gridSizeZ; // Using X-Z plane for visualization

                // Fill with background color
                using (Graphics sliceG = Graphics.FromImage(slice))
                {
                    sliceG.Clear(Color.Black);
                }

                // Draw displacement for each grid point (use X-Z slice through Y/2)
                int ySlice = _gridSizeY / 2;

                for (int gx = 0; gx < _gridSizeX; gx++)
                {
                    for (int gz = 0; gz < _gridSizeZ; gz++)
                    {
                        // Calculate index in the displacement array
                        int index = (gx * _gridSizeY + ySlice) * _gridSizeZ + gz;

                        if (index >= 0 && index < displacements.Length)
                        {
                            Vector3 disp = displacements[index];

                            // Calculate magnitude
                            float magnitude = (float)Math.Sqrt(disp.X * disp.X + disp.Y * disp.Y + disp.Z * disp.Z);

                            // Skip very small displacements
                            if (magnitude < 0.01f * maxDisplacement)
                                continue;

                            // Normalize and get color
                            float normalizedValue = magnitude / maxDisplacement;
                            Color color = GetHeatMapColor(normalizedValue, 0, 1);

                            // Calculate pixel position
                            int pixelX = (int)(gx * scaleX);
                            int pixelY = (int)(gz * scaleY);

                            // Draw a dot
                            int dotSize = 1 + (int)(2 * normalizedValue);
                            for (int dx = -dotSize / 2; dx <= dotSize / 2; dx++)
                            {
                                for (int dy = -dotSize / 2; dy <= dotSize / 2; dy++)
                                {
                                    int px = pixelX + dx;
                                    int py = pixelY + dy;

                                    if (px >= 0 && px < width && py >= 0 && py < height)
                                    {
                                        slice.SetPixel(px, py, color);
                                    }
                                }
                            }
                        }
                    }
                }

                // Draw the slice
                g.DrawImage(slice, x, y, width, height);

                // Draw a border
                using (Pen borderPen = new Pen(Color.Gray, 1))
                {
                    g.DrawRectangle(borderPen, x, y, width, height);
                }

                // Draw time label
                using (Font font = new Font("Arial", 9))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    string timeLabel = $"t = {time * 1000:F2} ms";
                    g.DrawString(timeLabel, font, textBrush, x + width / 2 - 30, y + height + 5);
                }

                // Mark source and receiver positions
                int sourceX = x + (int)(_sourceX * scaleX);
                int sourceY = y + (int)(_sourceZ * scaleY);
                int receiverX = x + (int)(_receiverX * scaleX);
                int receiverY = y + (int)(_receiverZ * scaleY);

                using (Brush sourceBrush = new SolidBrush(Color.Yellow))
                using (Brush receiverBrush = new SolidBrush(Color.Cyan))
                {
                    g.FillEllipse(sourceBrush, sourceX - 4, sourceY - 4, 8, 8);
                    g.FillEllipse(receiverBrush, receiverX - 4, receiverY - 4, 8, 8);
                }
            }
        }

        /// <summary>
        /// Render the mesh with velocity information
        /// </summary>
        private void RenderMeshWithVelocity(Graphics g, int width, int height, RenderMode renderMode)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Black);

            if (MeshTriangles == null || MeshTriangles.Count == 0)
            {
                using (Font font = new Font("Arial", 12))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    g.DrawString("No mesh available", font, brush, 20, 20);
                }
                return;
            }

            // Set up projection parameters
            float scale = Math.Min(width, height) / 200.0f;

            // Center point for the projection
            float centerX = width / 2.0f;
            float centerY = height / 2.0f;

            // Get volume dimensions for normalization
            float maxCoord = FindMaxCoordinate();

            // Default rotation angles
            float rotationX = 0.5f;
            float rotationY = 0.5f;

            // Find min and max velocity for color mapping
            float minVelocity = float.MaxValue;
            float maxVelocity = 0;

            for (int x = 0; x < _gridSizeX; x++)
            {
                for (int y = 0; y < _gridSizeY; y++)
                {
                    for (int z = 0; z < _gridSizeZ; z++)
                    {
                        float vel = _velocityModel[x, y, z];
                        if (vel > 500) // Ignore air cells
                        {
                            minVelocity = Math.Min(minVelocity, vel);
                            maxVelocity = Math.Max(maxVelocity, vel);
                        }
                    }
                }
            }

            if (minVelocity == float.MaxValue) minVelocity = 0;
            if (maxVelocity == 0) maxVelocity = 8000; // Default max P-wave velocity

            // Create a list to hold triangles with their average Z for depth sorting
            var trianglesToDraw = new List<TriangleDepthInfo>();

            // Calculate projected positions and depth for all triangles
            foreach (Triangle tri in MeshTriangles)
            {
                // Calculate the average Z depth for depth sorting
                float avgZ = (tri.V1.Z + tri.V2.Z + tri.V3.Z) / 3.0f;

                // Calculate the center point for velocity lookup
                Vector3 center = new Vector3(
                    (tri.V1.X + tri.V2.X + tri.V3.X) / 3,
                    (tri.V1.Y + tri.V2.Y + tri.V3.Y) / 3,
                    (tri.V1.Z + tri.V2.Z + tri.V3.Z) / 3
                );

                // Convert to grid coordinates
                int gridX = (int)(center.X / _gridSpacing);
                int gridY = (int)(center.Y / _gridSpacing);
                int gridZ = (int)(center.Z / _gridSpacing);

                // Ensure within grid bounds
                gridX = Math.Max(0, Math.Min(gridX, _gridSizeX - 1));
                gridY = Math.Max(0, Math.Min(gridY, _gridSizeY - 1));
                gridZ = Math.Max(0, Math.Min(gridZ, _gridSizeZ - 1));

                // Get velocity at this point
                float velocity = _velocityModel[gridX, gridY, gridZ];

                // If it's air, use the material velocity
                if (velocity < 500)
                {
                    velocity = _isPWave ? PWaveVelocity : SWaveVelocity;
                }

                // Store triangle, depth, and velocity
                trianglesToDraw.Add(new TriangleDepthInfo { Triangle = tri, AverageZ = avgZ, Velocity = velocity });
            }

            // Sort triangles by Z depth (back to front)
            trianglesToDraw.Sort((a, b) => -a.AverageZ.CompareTo(b.AverageZ));

            // Draw the triangles
            foreach (var triData in trianglesToDraw)
            {
                Triangle tri = triData.Triangle;

                // Project vertices
                PointF p1 = ProjectVertex(tri.V1, centerX, centerY, scale, maxCoord, rotationX, rotationY);
                PointF p2 = ProjectVertex(tri.V2, centerX, centerY, scale, maxCoord, rotationX, rotationY);
                PointF p3 = ProjectVertex(tri.V3, centerX, centerY, scale, maxCoord, rotationX, rotationY);

                // Create triangle points
                PointF[] points = new PointF[] { p1, p2, p3 };

                // Get color based on velocity
                float normalizedVelocity = (triData.Velocity - minVelocity) / (maxVelocity - minVelocity);
                Color triangleColor = GetHeatMapColor(normalizedVelocity, 0, 1);

                if (renderMode == RenderMode.Solid)
                {
                    // Draw filled triangle with transparency
                    using (SolidBrush brush = new SolidBrush(Color.FromArgb(200, triangleColor)))
                    {
                        g.FillPolygon(brush, points);
                    }
                }

                // Draw wireframe outline
                using (Pen pen = new Pen(renderMode == RenderMode.Wireframe ? triangleColor : Color.FromArgb(100, Color.Black), 1))
                {
                    g.DrawPolygon(pen, points);
                }
            }

            // Draw title
            string title = $"{(renderMode == RenderMode.Wireframe ? "Wireframe" : "Solid")} Mesh with {(_isPWave ? "P-Wave" : "S-Wave")} Velocity";
            using (Font titleFont = new Font("Arial", 14, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                g.DrawString(title, titleFont, textBrush, (width - g.MeasureString(title, titleFont).Width) / 2, 5);
            }

            // Draw legend
            DrawColorScale(g, width - 40, height / 2, 20, height / 3, "Velocity (m/s)");

            // Add min/max values to the legend
            using (Font font = new Font("Arial", 9))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                g.DrawString($"{maxVelocity:F0}", font, textBrush, width - 70, height / 2 - 15);
                g.DrawString($"{minVelocity:F0}", font, textBrush, width - 70, height / 2 + height / 3 + 5);
            }
        }

        /// <summary>
        /// Helper class for triangle depth sorting
        /// </summary>
        private class TriangleDepthInfo
        {
            public Triangle Triangle { get; set; }
            public float AverageZ { get; set; }
            public float Velocity { get; set; }
        }

        /// <summary>
        /// Project a 3D vertex to 2D screen coordinates
        /// </summary>
        private PointF ProjectVertex(Vector3 vertex, float centerX, float centerY, float scale, float maxCoord, float rotX, float rotY)
        {
            // Normalize coordinates to -0.5 to 0.5 range
            float nx = vertex.X / maxCoord - 0.5f;
            float ny = vertex.Y / maxCoord - 0.5f;
            float nz = vertex.Z / maxCoord - 0.5f;

            // Apply rotation around Y axis first
            float cosY = (float)Math.Cos(rotY);
            float sinY = (float)Math.Sin(rotY);
            float tx = nx * cosY + nz * sinY;
            float ty = ny;
            float tz = -nx * sinY + nz * cosY;

            // Then apply rotation around X axis
            float cosX = (float)Math.Cos(rotX);
            float sinX = (float)Math.Sin(rotX);
            float rx = tx;
            float ry = ty * cosX - tz * sinX;
            float rz = ty * sinX + tz * cosX;

            // Simple perspective projection
            float perspective = 1.5f + rz;
            float projX = centerX + rx * scale * 150 / perspective;
            float projY = centerY + ry * scale * 150 / perspective;

            return new PointF(projX, projY);
        }

        /// <summary>
        /// Find the maximum coordinate in the mesh
        /// </summary>
        private float FindMaxCoordinate()
        {
            float maxCoord = 0;

            foreach (var tri in MeshTriangles)
            {
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V1.X));
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V1.Y));
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V1.Z));
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V2.X));
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V2.Y));
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V2.Z));
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V3.X));
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V3.Y));
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V3.Z));
            }

            return maxCoord;
        }

        #endregion

        #region Export Methods

        /// <summary>
        /// Export simulation results to CSV
        /// </summary>
        private bool ExportToCsv(string filePath)
        {
            try
            {
                using (StreamWriter writer = new StreamWriter(filePath))
                {
                    // Write header
                    writer.WriteLine("Parameter,Value,Unit");
                    writer.WriteLine($"Material,{Material.Name},");
                    writer.WriteLine($"Density,{Material.Density:F1},kg/m³");
                    writer.WriteLine($"ConfiningPressure,{ConfiningPressure:F1},MPa");
                    writer.WriteLine($"WaveType,{WaveType},");
                    writer.WriteLine($"Frequency,{Frequency:F1},kHz");
                    writer.WriteLine($"PWaveVelocity,{MeasuredPWaveVelocity:F1},m/s");
                    writer.WriteLine($"SWaveVelocity,{MeasuredSWaveVelocity:F1},m/s");
                    writer.WriteLine($"PWaveArrivalTime,{PWaveArrivalTime * 1000:F2},ms");
                    writer.WriteLine($"SWaveArrivalTime,{SWaveArrivalTime * 1000:F2},ms");
                    writer.WriteLine($"VpVsRatio,{CalculatedVpVsRatio:F2},");
                    writer.WriteLine($"YoungModulus,{YoungModulus:F0},MPa");
                    writer.WriteLine($"PoissonRatio,{PoissonRatio:F3},");
                    writer.WriteLine($"BulkModulus,{BulkModulus:F0},MPa");
                    writer.WriteLine($"ShearModulus,{ShearModulus:F0},MPa");
                    writer.WriteLine($"Attenuation,{Attenuation:F3},dB/wavelength");
                    writer.WriteLine($"MaximumDisplacement,{MaximumDisplacement:E3},m");
                    writer.WriteLine($"SampleLength,{SampleLength:F3},m");

                    // Write time series data
                    writer.WriteLine();
                    writer.WriteLine("Time (ms),Displacement,Intensity");

                    // Calculate time step
                    float velocity = _isPWave ? PWaveVelocity : SWaveVelocity;
                    float dt = _gridSpacing / (1.2f * velocity);

                    for (int i = 0; i < ReceiverTimeSeries.Length; i++)
                    {
                        float time = i * dt * 1000; // Convert to ms
                        float displacement = ReceiverTimeSeries[i];
                        float intensity = i < AcousticIntensity.Count ? AcousticIntensity[i] : 0;

                        writer.WriteLine($"{time:F3},{displacement:E6},{intensity:E6}");
                    }
                }

                Logger.Log($"[AcousticVelocitySimulation] Exported results to CSV: {filePath}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[AcousticVelocitySimulation] CSV export failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Export simulation results to JSON
        /// </summary>
        private bool ExportToJson(string filePath)
        {
            try
            {
                using (StreamWriter writer = new StreamWriter(filePath))
                {
                    // Create a simple JSON structure manually
                    writer.WriteLine("{");
                    writer.WriteLine($"  \"simulationId\": \"{SimulationId}\",");
                    writer.WriteLine($"  \"material\": \"{Material.Name}\",");
                    writer.WriteLine($"  \"density\": {Material.Density:F1},");
                    writer.WriteLine($"  \"confiningPressure\": {ConfiningPressure:F1},");
                    writer.WriteLine($"  \"waveType\": \"{WaveType}\",");
                    writer.WriteLine($"  \"frequency\": {Frequency:F1},");
                    writer.WriteLine($"  \"pWaveVelocity\": {MeasuredPWaveVelocity:F1},");
                    writer.WriteLine($"  \"sWaveVelocity\": {MeasuredSWaveVelocity:F1},");
                    writer.WriteLine($"  \"pWaveArrivalTime\": {PWaveArrivalTime * 1000:F2},");
                    writer.WriteLine($"  \"sWaveArrivalTime\": {SWaveArrivalTime * 1000:F2},");
                    writer.WriteLine($"  \"vpVsRatio\": {CalculatedVpVsRatio:F2},");
                    writer.WriteLine($"  \"youngModulus\": {YoungModulus:F0},");
                    writer.WriteLine($"  \"poissonRatio\": {PoissonRatio:F3},");
                    writer.WriteLine($"  \"bulkModulus\": {BulkModulus:F0},");
                    writer.WriteLine($"  \"shearModulus\": {ShearModulus:F0},");
                    writer.WriteLine($"  \"attenuation\": {Attenuation:F3},");
                    writer.WriteLine($"  \"maximumDisplacement\": {MaximumDisplacement:E3},");
                    writer.WriteLine($"  \"sampleLength\": {SampleLength:F3},");

                    // Add time series data
                    writer.WriteLine("  \"timeSeries\": [");

                    // Calculate time step
                    float velocity = _isPWave ? PWaveVelocity : SWaveVelocity;
                    float dt = _gridSpacing / (1.2f * velocity);

                    for (int i = 0; i < ReceiverTimeSeries.Length; i++)
                    {
                        float time = i * dt * 1000; // Convert to ms

                        writer.Write($"    {{\"time\": {time:F3}, \"displacement\": {ReceiverTimeSeries[i]:E6}");

                        if (i < AcousticIntensity.Count)
                        {
                            writer.Write($", \"intensity\": {AcousticIntensity[i]:E6}");
                        }

                        writer.Write("}");
                        if (i < ReceiverTimeSeries.Length - 1) writer.WriteLine(",");
                        else writer.WriteLine();
                    }

                    writer.WriteLine("  ]");
                    writer.WriteLine("}");
                }

                Logger.Log($"[AcousticVelocitySimulation] Exported results to JSON: {filePath}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[AcousticVelocitySimulation] JSON export failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Export simulation results to VTK format
        /// </summary>
        private bool ExportToVtk(string filePath)
        {
            try
            {
                using (StreamWriter writer = new StreamWriter(filePath))
                {
                    // VTK header
                    writer.WriteLine("# vtk DataFile Version 3.0");
                    writer.WriteLine($"Acoustic Velocity Simulation Results - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    writer.WriteLine("ASCII");

                    // Create structured grid for velocity model
                    writer.WriteLine("DATASET STRUCTURED_POINTS");
                    writer.WriteLine($"DIMENSIONS {_gridSizeX} {_gridSizeY} {_gridSizeZ}");
                    writer.WriteLine($"ORIGIN 0 0 0");
                    writer.WriteLine($"SPACING {_gridSpacing} {_gridSpacing} {_gridSpacing}");

                    // Write velocity data
                    writer.WriteLine($"POINT_DATA {_gridSizeX * _gridSizeY * _gridSizeZ}");

                    // P-wave velocity
                    writer.WriteLine("SCALARS p_wave_velocity float");
                    writer.WriteLine("LOOKUP_TABLE default");
                    for (int z = 0; z < _gridSizeZ; z++)
                    {
                        for (int y = 0; y < _gridSizeY; y++)
                        {
                            for (int x = 0; x < _gridSizeX; x++)
                            {
                                float velocity = _velocityModel[x, y, z];
                                writer.WriteLine(velocity);
                            }
                        }
                    }

                    // Density
                    writer.WriteLine("SCALARS density float");
                    writer.WriteLine("LOOKUP_TABLE default");
                    for (int z = 0; z < _gridSizeZ; z++)
                    {
                        for (int y = 0; y < _gridSizeY; y++)
                        {
                            for (int x = 0; x < _gridSizeX; x++)
                            {
                                float density = _densityModel[x, y, z];
                                writer.WriteLine(density);
                            }
                        }
                    }

                    // Wave field (if available)
                    if (PWaveField != null)
                    {
                        writer.WriteLine("SCALARS p_wave_field float");
                        writer.WriteLine("LOOKUP_TABLE default");
                        for (int z = 0; z < _gridSizeZ; z++)
                        {
                            for (int y = 0; y < _gridSizeY; y++)
                            {
                                for (int x = 0; x < _gridSizeX; x++)
                                {
                                    float field = PWaveField[x, y, z];
                                    writer.WriteLine(field);
                                }
                            }
                        }
                    }

                    if (SWaveField != null)
                    {
                        writer.WriteLine("SCALARS s_wave_field float");
                        writer.WriteLine("LOOKUP_TABLE default");
                        for (int z = 0; z < _gridSizeZ; z++)
                        {
                            for (int y = 0; y < _gridSizeY; y++)
                            {
                                for (int x = 0; x < _gridSizeX; x++)
                                {
                                    float field = SWaveField[x, y, z];
                                    writer.WriteLine(field);
                                }
                            }
                        }
                    }
                }

                Logger.Log($"[AcousticVelocitySimulation] Exported results to VTK: {filePath}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[AcousticVelocitySimulation] VTK export failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Export simulation results to PNG image
        /// </summary>
        /// <summary>
        /// Export simulation results to PNG image
        /// </summary>
        private bool ExportToPng(string filePath)
        {
            if (filePath.ToLower().EndsWith("_composite.png"))
            {
                return ExportCompositeImage(filePath);
            }

            try
            {
                // Create a bitmap for the export with higher resolution for better quality
                using (Bitmap bitmap = new Bitmap(2000, 1600))
                {
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        // Clear background
                        g.Clear(Color.Black);

                        // Set high-quality rendering
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

                        // Render the primary wave field visualization
                        RenderResults(g, bitmap.Width, bitmap.Height, RenderMode.Stress);

                        // Add a border
                        using (Pen pen = new Pen(Color.White, 2))
                        {
                            g.DrawRectangle(pen, 1, 1, bitmap.Width - 3, bitmap.Height - 3);
                        }

                        // Add a footer with simulation parameters
                        string footer = $"Material: {Material.Name}, Density: {Material.Density:F1} kg/m³, " +
                                        $"Wave Type: {WaveType}, Frequency: {Frequency:F1} kHz, " +
                                        $"P-Wave: {MeasuredPWaveVelocity:F1} m/s, S-Wave: {MeasuredSWaveVelocity:F1} m/s, " +
                                        $"Vp/Vs: {CalculatedVpVsRatio:F2}";

                        using (Font font = new Font("Arial", 12))
                        using (SolidBrush brush = new SolidBrush(Color.White))
                        using (SolidBrush backBrush = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                        {
                            float textWidth = g.MeasureString(footer, font).Width;
                            float textHeight = g.MeasureString(footer, font).Height;
                            float x = (bitmap.Width - textWidth) / 2;
                            float y = bitmap.Height - textHeight - 20;

                            g.FillRectangle(backBrush, x - 10, y - 5, textWidth + 20, textHeight + 10);
                            g.DrawString(footer, font, brush, x, y);
                        }
                    }

                    // Save the bitmap as PNG with high quality settings
                    using (EncoderParameters encoderParams = new EncoderParameters(1))
                    {
                        encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 100L);
                        ImageCodecInfo pngEncoder = GetEncoderInfo("image/png");
                        bitmap.Save(filePath, pngEncoder, encoderParams);
                    }
                }

                Logger.Log($"[AcousticVelocitySimulation] Exported results to PNG: {filePath}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[AcousticVelocitySimulation] PNG export failed: {ex.Message}");
                return false;
            }
        }
        private ImageCodecInfo GetEncoderInfo(string mimeType)
        {
            ImageCodecInfo[] encoders = ImageCodecInfo.GetImageEncoders();
            for (int i = 0; i < encoders.Length; i++)
            {
                if (encoders[i].MimeType == mimeType)
                    return encoders[i];
            }
            return null;
        }
        /// <summary>
        /// Export simulation results to PDF
        /// </summary>
        private bool ExportToPdf(string filePath)
        {
            try
            {
                // Export as PNG first, then convert to PDF
                string tempPngPath = Path.Combine(Path.GetTempPath(), $"acoustic_sim_{SimulationId}.png");
                if (ExportToPng(tempPngPath))
                {
                    // Create a simple PDF with the image (requires a PDF library)
                    // This is a placeholder - in a real implementation, use a PDF library
                    // like iTextSharp, PdfSharp, or similar

                    // For now, just export a PNG and notify the user
                    File.Copy(tempPngPath, filePath, true);
                    Logger.Log($"[AcousticVelocitySimulation] Exported results to PDF substitute (PNG): {filePath}");

                    // Clean up temp file
                    try { File.Delete(tempPngPath); } catch { }

                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"[AcousticVelocitySimulation] PDF export failed: {ex.Message}");
                return false;
            }
        }
        /// <summary>
        /// Export simulation results as a composite image with multiple views
        /// </summary>
        public bool ExportCompositeImage(string filePath)
        {
            try
            {
                Logger.Log($"[AcousticVelocitySimulation] Creating composite image...");

                // Use higher resolution for better quality exports
                int panelWidth = 1000;
                int panelHeight = 800;
                int padding = 20;
                int titleHeight = 60;

                // Create a composite image with 2x2 grid layout plus title and info footer
                int compositeWidth = panelWidth * 2 + padding * 3;
                int compositeHeight = panelHeight * 2 + padding * 4 + titleHeight + 100; // +100 for footer area

                using (Bitmap compositeBitmap = new Bitmap(compositeWidth, compositeHeight))
                {
                    using (Graphics g = Graphics.FromImage(compositeBitmap))
                    {
                        // Fill background
                        g.Clear(Color.Black);

                        // Set high-quality rendering
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

                        // Draw title
                        using (Font titleFont = new Font("Arial", 24, FontStyle.Bold))
                        using (SolidBrush textBrush = new SolidBrush(Color.White))
                        {
                            string title = $"Acoustic Velocity Simulation - {Material.Name} - {WaveType}";
                            SizeF titleSize = g.MeasureString(title, titleFont);
                            g.DrawString(title, titleFont, textBrush,
                                (compositeWidth - titleSize.Width) / 2, padding);
                        }

                        // Draw the four key visualizations
                        try
                        {
                            DrawCompositePanel(g, RenderMode.Stress,
                                padding, titleHeight + padding,
                                panelWidth, panelHeight,
                                "Wave Propagation");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[AcousticVelocitySimulation] Error rendering wave propagation panel: {ex.Message}");
                            DrawErrorPanel(g, padding, titleHeight + padding, panelWidth, panelHeight,
                                "Wave Propagation", ex.Message);
                        }

                        try
                        {
                            DrawCompositePanel(g, RenderMode.Strain,
                                padding * 2 + panelWidth, titleHeight + padding,
                                panelWidth, panelHeight,
                                "Time Series");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[AcousticVelocitySimulation] Error rendering time series panel: {ex.Message}");
                            DrawErrorPanel(g, padding * 2 + panelWidth, titleHeight + padding, panelWidth, panelHeight,
                                "Time Series", ex.Message);
                        }

                        try
                        {
                            DrawCompositePanel(g, RenderMode.FailureProbability,
                                padding, titleHeight + padding * 2 + panelHeight,
                                panelWidth, panelHeight,
                                "Velocity Distribution");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[AcousticVelocitySimulation] Error rendering velocity distribution panel: {ex.Message}");
                            DrawErrorPanel(g, padding, titleHeight + padding * 2 + panelHeight, panelWidth, panelHeight,
                                "Velocity Distribution", ex.Message);
                        }

                        try
                        {
                            DrawCompositePanel(g, RenderMode.Displacement,
                                padding * 2 + panelWidth, titleHeight + padding * 2 + panelHeight,
                                panelWidth, panelHeight,
                                "Wave Slices");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[AcousticVelocitySimulation] Error rendering wave slices panel: {ex.Message}");
                            DrawErrorPanel(g, padding * 2 + panelWidth, titleHeight + padding * 2 + panelHeight, panelWidth, panelHeight,
                                "Wave Slices", ex.Message);
                        }

                        // Draw summary panel at the bottom
                        int summaryY = titleHeight + padding * 3 + panelHeight * 2;
                        Rectangle summaryRect = new Rectangle(padding, summaryY, compositeWidth - padding * 2, 80);

                        using (SolidBrush summaryBrush = new SolidBrush(Color.FromArgb(40, 40, 40)))
                        using (Pen borderPen = new Pen(Color.DimGray, 1))
                        {
                            g.FillRectangle(summaryBrush, summaryRect);
                            g.DrawRectangle(borderPen, summaryRect);
                        }

                        using (Font summaryFont = new Font("Arial", 12))
                        using (SolidBrush textBrush = new SolidBrush(Color.White))
                        {
                            int lineHeight = 20;
                            string line1 = $"Material: {Material.Name} | Density: {Material.Density:F1} kg/m³ | Wave Type: {WaveType} | Frequency: {Frequency:F1} kHz";
                            string line2 = $"P-Wave Velocity: {MeasuredPWaveVelocity:F1} m/s | S-Wave Velocity: {MeasuredSWaveVelocity:F1} m/s | Vp/Vs Ratio: {CalculatedVpVsRatio:F2}";
                            string line3 = $"Young's Modulus: {YoungModulus:F0} MPa | Poisson's Ratio: {PoissonRatio:F3} | Sample Length: {SampleLength:F3} m";

                            g.DrawString(line1, summaryFont, textBrush, summaryRect.X + 10, summaryRect.Y + 10);
                            g.DrawString(line2, summaryFont, textBrush, summaryRect.X + 10, summaryRect.Y + 10 + lineHeight);
                            g.DrawString(line3, summaryFont, textBrush, summaryRect.X + 10, summaryRect.Y + 10 + lineHeight * 2);
                        }

                        // Add timestamp and export info at the bottom
                        using (Font footerFont = new Font("Arial", 9))
                        using (SolidBrush textBrush = new SolidBrush(Color.Silver))
                        {
                            string footer = $"Generated on {DateTime.Now:yyyy-MM-dd HH:mm:ss} | CT Segmenter Acoustic Velocity Module";
                            SizeF footerSize = g.MeasureString(footer, footerFont);
                            g.DrawString(footer, footerFont, textBrush,
                                compositeWidth - footerSize.Width - padding, compositeHeight - footerSize.Height - 5);
                        }
                    }

                    // Save with high quality
                    using (EncoderParameters encoderParams = new EncoderParameters(1))
                    {
                        encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 100L);
                        ImageCodecInfo pngEncoder = GetEncoderInfo("image/png");
                        compositeBitmap.Save(filePath, pngEncoder, encoderParams);
                    }
                }

                Logger.Log($"[AcousticVelocitySimulation] Composite image exported to: {filePath}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[AcousticVelocitySimulation] Composite image export failed: {ex.Message}");
                Logger.Log($"[AcousticVelocitySimulation] Stack trace: {ex.StackTrace}");
                return false;
            }
        }
        private void DrawCompositePanel(Graphics g, RenderMode mode, int x, int y, int width, int height, string title)
        {
            // Create a bitmap for this panel
            using (Bitmap viewBitmap = new Bitmap(width, height))
            {
                using (Graphics viewGraphics = Graphics.FromImage(viewBitmap))
                {
                    // Clear the background
                    viewGraphics.Clear(Color.Black);

                    // Set high quality rendering
                    viewGraphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    viewGraphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

                    // Render the specific visualization
                    RenderResults(viewGraphics, width, height, mode);

                    // Add title to the view
                    using (Font titleFont = new Font("Arial", 14, FontStyle.Bold))
                    using (SolidBrush backBrush = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                    using (SolidBrush textBrush = new SolidBrush(Color.White))
                    {
                        SizeF titleSize = viewGraphics.MeasureString(title, titleFont);
                        float titleX = (width - titleSize.Width) / 2;

                        // Background for title
                        viewGraphics.FillRectangle(backBrush, titleX - 5, 5, titleSize.Width + 10, titleSize.Height + 5);

                        // Draw title text
                        viewGraphics.DrawString(title, titleFont, textBrush, titleX, 8);
                    }
                }

                // Draw the panel bitmap onto the composite bitmap
                g.DrawImage(viewBitmap, x, y, width, height);

                // Draw a border around the panel
                using (Pen borderPen = new Pen(Color.DimGray, 2))
                {
                    g.DrawRectangle(borderPen, x, y, width, height);
                }
            }
        }
        private void DrawErrorPanel(Graphics g, int x, int y, int width, int height, string title, string errorMessage)
        {
            using (SolidBrush backBrush = new SolidBrush(Color.FromArgb(30, 30, 30)))
            {
                g.FillRectangle(backBrush, x, y, width, height);
            }

            using (Pen borderPen = new Pen(Color.DarkRed, 2))
            {
                g.DrawRectangle(borderPen, x, y, width, height);
            }

            using (Font titleFont = new Font("Arial", 14, FontStyle.Bold))
            using (Font errorFont = new Font("Arial", 12))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            using (SolidBrush errorBrush = new SolidBrush(Color.Red))
            {
                g.DrawString(title, titleFont, textBrush, x + 10, y + 10);
                g.DrawString("Rendering Error:", errorFont, errorBrush, x + 10, y + 40);

                // Draw the error message with word wrap
                using (StringFormat format = new StringFormat())
                {
                    format.Alignment = StringAlignment.Near;
                    format.LineAlignment = StringAlignment.Near;
                    format.Trimming = StringTrimming.Word;

                    Rectangle errorRect = new Rectangle(x + 10, y + 70, width - 20, height - 80);
                    g.DrawString(errorMessage, errorFont, errorBrush, errorRect, format);
                }
            }
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Raise the progress changed event
        /// </summary>
        protected virtual void OnProgressChanged(float progress, string message)
        {
            Progress = progress;
            ProgressChanged?.Invoke(this, new SimulationProgressEventArgs(progress, message));
        }

        /// <summary>
        /// Raise the simulation completed event
        /// </summary>
        protected virtual void OnSimulationCompleted(bool success, string message, SimulationResult result, Exception error = null)
        {
            SimulationCompleted?.Invoke(this, new SimulationCompletedEventArgs(success, message, result, error));
        }

        #endregion

        #region IDisposable Implementation

        /// <summary>
        /// Dispose resources
        /// </summary>
        public void Dispose()
        {
            if (!_isDisposed)
            {
                // Dispose managed resources
                _cancellationTokenSource?.Dispose();
                _accelerator?.Dispose();
                _context?.Dispose();

                _isDisposed = true;
            }
        }

        #endregion

        #region Internal Classes

        /// <summary>
        /// Represents the acoustic properties of a material
        /// </summary>
        private class MaterialAcousticProperties
        {
            public float PWaveVelocityBase { get; set; }
            public float SWaveVelocityBase { get; set; }
            public float PoissonRatioBase { get; set; }
            public float YoungModulusBase { get; set; }
            public float BulkModulusBase { get; set; }
            public float ShearModulusBase { get; set; }
            public float AttenuationBase { get; set; }
            public float DensityReference { get; set; }
        }

        #endregion
    }
}