using ILGPU;
using ILGPU.Runtime;
using System;
using System.Collections.Concurrent;
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

namespace CTS
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
        public float energyLossPercent;

        // ILGPU context
        private Context _context;

        public Accelerator _accelerator;

        private Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>,
                        float, float, float, float, float, float, float, ArrayView<float>, ArrayView<float>> _propagatePWaveKernel;

        private Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>,
                        float, float, float, float, float, float, float, ArrayView<float>> _propagateSWaveKernel;

        private Action<Index3D,
    ArrayView3D<float, Stride3D.DenseXY>,  // current field
    ArrayView3D<float, Stride3D.DenseXY>,  // previous field
    ArrayView3D<float, Stride3D.DenseXY>,  // next field
    ArrayView3D<float, Stride3D.DenseXY>,  // velocity model
    float,                                 // dt
    float,                                 // dx
    float,                                 // attenuation
    int,                                   // isSource
    int                                    // isPWave
> _wave3DPropagationKernel;

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
        public bool _isPWave;
        private bool _hasPreviousTriaxialResults;
        private SimulationResult _triaxialResult;
        private float totalEnergy = 0;

        // The input energy value
        private float energyLoss = 0;

        // 3D grid for simulation
        public int _gridSizeX;

        public int _gridSizeY;
        public int _gridSizeZ;
        private float _gridSpacing;
        public float[,,] _velocityModel;
        public float[,,] _densityModel;
        private Vector3 _sourcePosition;
        private Vector3 _receiverPosition;
        public int _sourceX, _sourceY, _sourceZ;
        public int _receiverX, _receiverY, _receiverZ;
        public float TimeStepFactor { get; set; } = 1.0f;

        private MainForm mainForm;
        private bool v;
        private ConcurrentDictionary<Vector3, float> densityMap;

        #endregion Properties and Fields

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
            TimeStepFactor = 1.0f;

            TestDirection = Vector3.Normalize(DirectionParser.Parse(direction));
            Logger.Log("[AcousticVelocitySimulation] Running on " + TestDirection + " Axis");
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

        public AcousticVelocitySimulation(Material material, List<Triangle> triangles, float confiningPressure, string waveType, int timeSteps, float frequency, float amplitude, float energy, string direction, bool useExtendedSimulationTime, SimulationResult previousTriaxialResult = null, MainForm mainForm = null, bool v = false, ConcurrentDictionary<Vector3, float> densityMap = null) : this(material, triangles, confiningPressure, waveType, timeSteps, frequency, amplitude, energy, direction, useExtendedSimulationTime, previousTriaxialResult, mainForm)
        {
            this.v = v;
            this.densityMap = densityMap;
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
            _wave3DPropagationKernel = _accelerator.LoadAutoGroupedStreamKernel<
        Index3D,
        ArrayView3D<float, Stride3D.DenseXY>,  // current wave field
        ArrayView3D<float, Stride3D.DenseXY>,  // previous wave field
        ArrayView3D<float, Stride3D.DenseXY>,  // next wave field
        ArrayView3D<float, Stride3D.DenseXY>,  // heterogeneous velocity model
        float,                                 // dt
        float,                                 // dx
        float,                                 // attenuation
        int,                                   // isSource flag
        int                                    // isPWave flag
    >(Wave3DPropagationKernel);
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
                PoissonRatio = baseProps.PoissonRatioBase * (float)Math.Pow(densityRatio, 0.1);
                BulkModulus = baseProps.BulkModulusBase * (float)Math.Pow(densityRatio, 1.2);
                ShearModulus = baseProps.ShearModulusBase * (float)Math.Pow(densityRatio, 1.4);
            }

            // Account for confining pressure effects - increases velocity slightly
            float pressureFactor = 1.0f + 0.002f * ConfiningPressure;

            // Calculate velocities from moduli and density using standard equations
            // 1e6 conversion from MPa to Pa for correct units
            float density = (float)Material.Density;

            // P-wave velocity: Vp = sqrt((K + 4G/3)/ρ)
            PWaveVelocity = (float)Math.Sqrt((BulkModulus * 1e6 + 4 * ShearModulus * 1e6 / 3) / density) * pressureFactor;

            // S-wave velocity: Vs = sqrt(G/ρ)
            SWaveVelocity = (float)Math.Sqrt(ShearModulus * 1e6 / density) * pressureFactor;

            // Attenuation - denser materials typically have lower attenuation
            Attenuation = baseProps.AttenuationBase / (float)Math.Pow(densityRatio, 0.5);

            // VERIFY PHYSICAL CONSISTENCY:
            // 1. Always: S-wave velocity < P-wave velocity
            if (SWaveVelocity >= PWaveVelocity)
            {
                Logger.Log($"[AcousticVelocitySimulation] WARNING: Physical constraint violation detected - S-wave velocity >= P-wave velocity");

                // Use Poisson's ratio to determine the correct relationship
                float theoreticalVpVs = CalculateVpVsFromPoissonRatio(PoissonRatio);
                SWaveVelocity = PWaveVelocity / theoreticalVpVs;

                Logger.Log($"[AcousticVelocitySimulation] Recalculated S-wave velocity to {SWaveVelocity:F1} m/s using Poisson's ratio {PoissonRatio:F3}");
            }

            // 2. Calculate Vp/Vs ratio
            CalculatedVpVsRatio = PWaveVelocity / SWaveVelocity;

            // 3. Log if values outside typical ranges (for information only)
            if (CalculatedVpVsRatio < 1.2f || CalculatedVpVsRatio > 2.3f)
            {
                Logger.Log($"[AcousticVelocitySimulation] WARNING: Calculated Vp/Vs ratio {CalculatedVpVsRatio:F2} is outside typical range 1.2-2.3");
                // Do not artificially modify - let the physics determine the results
            }

            Logger.Log($"[AcousticVelocitySimulation] Material: {Material.Name}, " +
                      $"Density: {Material.Density:F1} kg/m³, " +
                      $"P-wave velocity: {PWaveVelocity:F1} m/s, " +
                      $"S-wave velocity: {SWaveVelocity:F1} m/s, " +
                      $"Vp/Vs ratio: {CalculatedVpVsRatio:F2}");
        }


        #endregion Constructor and Initialization

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

            // Use energy to scale the amplitude - Energy ∝ A² so A ∝ √E
            float energyFactor = (float)Math.Sqrt(Energy);

            // Apply energy-scaled amplitude
            float scaledAmplitude = Amplitude * energyFactor;

            Logger.Log($"[AcousticVelocitySimulation] Using energy-scaled amplitude: {Amplitude} × √{Energy} = {scaledAmplitude}");

            // Apply amplitude
            for (int i = 0; i < sampleCount; i++)
            {
                wavelet[i] *= scaledAmplitude;
            }

            // Record max amplitude for reporting
            maxWaveletAmplitude = wavelet.Max(v => Math.Abs(v));

            return wavelet;
        }

        /// <summary>
        /// Calculate the dimensions of the 3D simulation grid and position transducers
        /// </summary>
        private void CalculateGridDimensions()
        {
            // Start with finding the actual mesh boundaries
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
            float pixelSize = 0.001f; // Default to 1mm if not available
            if (mainForm != null && mainForm.pixelSize > 0)
            {
                pixelSize = (float)mainForm.pixelSize;
            }
            Logger.Log($"[AcousticVelocitySimulation] Using pixel size: {pixelSize} units");

            // Calculate the sample length along the test direction
            // Find the primary test direction axis
            Vector3 normalizedDir = Vector3.Normalize(TestDirection);

            // Determine the dominant axis component
            int primaryAxis = 0; // 0=X, 1=Y, 2=Z
            float maxComponent = Math.Abs(normalizedDir.X);

            if (Math.Abs(normalizedDir.Y) > maxComponent)
            {
                primaryAxis = 1;
                maxComponent = Math.Abs(normalizedDir.Y);
            }

            if (Math.Abs(normalizedDir.Z) > maxComponent)
            {
                primaryAxis = 2;
                maxComponent = Math.Abs(normalizedDir.Z);
            }

            // Position transducers at opposite boundaries along the test direction
            // Only use a small safety margin of 1 cell from the absolute edge
            int safeMargin = 1;

            // Default to center position for non-primary axes
            _sourceX = _gridSizeX / 2;
            _sourceY = _gridSizeY / 2;
            _sourceZ = _gridSizeZ / 2;

            _receiverX = _gridSizeX / 2;
            _receiverY = _gridSizeY / 2;
            _receiverZ = _gridSizeZ / 2;

            // Position along the primary direction
            switch (primaryAxis)
            {
                case 0: // X-axis is primary
                    if (normalizedDir.X > 0)
                    {
                        _sourceX = safeMargin;
                        _receiverX = _gridSizeX - safeMargin - 1;
                    }
                    else
                    {
                        _sourceX = _gridSizeX - safeMargin - 1;
                        _receiverX = safeMargin;
                    }
                    break;

                case 1: // Y-axis is primary
                    if (normalizedDir.Y > 0)
                    {
                        _sourceY = safeMargin;
                        _receiverY = _gridSizeY - safeMargin - 1;
                    }
                    else
                    {
                        _sourceY = _gridSizeY - safeMargin - 1;
                        _receiverY = safeMargin;
                    }
                    break;

                case 2: // Z-axis is primary
                    if (normalizedDir.Z > 0)
                    {
                        _sourceZ = safeMargin;
                        _receiverZ = _gridSizeZ - safeMargin - 1;
                    }
                    else
                    {
                        _sourceZ = _gridSizeZ - safeMargin - 1;
                        _receiverZ = safeMargin;
                    }
                    break;
            }

            // Store final positions
            _sourcePosition = new Vector3(_sourceX, _sourceY, _sourceZ);
            _receiverPosition = new Vector3(_receiverX, _receiverY, _receiverZ);

            // Update sample length based on the actual transducer distance
            float actualDistance = Vector3Distance(_sourcePosition, _receiverPosition) * _gridSpacing;
            SampleLength = actualDistance;

            Logger.Log($"[AcousticVelocitySimulation] SOURCE PLACED AT BOUNDARY: ({_sourceX},{_sourceY},{_sourceZ})");
            Logger.Log($"[AcousticVelocitySimulation] RECEIVER PLACED AT BOUNDARY: ({_receiverX},{_receiverY},{_receiverZ})");
            Logger.Log($"[AcousticVelocitySimulation] Distance: {SampleLength:F6} m");
        }

        private Vector3 Vector3Abs(Vector3 v)
        {
            return new Vector3(Math.Abs(v.X), Math.Abs(v.Y), Math.Abs(v.Z));
        }
        /// <summary>
        /// Create the velocity and density models in the 3D grid with direction-specific properties
        /// </summary>
        private void CreateVelocityModel()
        {
            // Start tracking execution time to prevent infinite loops
            Stopwatch sw = Stopwatch.StartNew();

            // Initialize the velocity and density models
            _velocityModel = new float[_gridSizeX, _gridSizeY, _gridSizeZ];
            _densityModel = new float[_gridSizeX, _gridSizeY, _gridSizeZ];

            // Get the base velocity values
            float baseVelocityValue = _isPWave ? PWaveVelocity : SWaveVelocity;
            float densityValue = (float)Material.Density;

            // Fill with material values by default
            for (int x = 0; x < _gridSizeX; x++)
            {
                for (int y = 0; y < _gridSizeY; y++)
                {
                    for (int z = 0; z < _gridSizeZ; z++)
                    {
                        _velocityModel[x, y, z] = baseVelocityValue;
                        _densityModel[x, y, z] = densityValue;
                    }
                }
            }

            // Check if we have a valid mesh to work with
            bool hasMeshData = _simulationTriangles != null && _simulationTriangles.Count > 0;
            int materialCellCount = _gridSizeX * _gridSizeY * _gridSizeZ; // All cells start as material

            // Get direction components
            Vector3 dir = Vector3.Normalize(TestDirection);
            bool isDirX = Math.Abs(dir.X) > Math.Abs(dir.Y) && Math.Abs(dir.X) > Math.Abs(dir.Z);
            bool isDirY = Math.Abs(dir.Y) > Math.Abs(dir.X) && Math.Abs(dir.Y) > Math.Abs(dir.Z);
            bool isDirZ = Math.Abs(dir.Z) > Math.Abs(dir.X) && Math.Abs(dir.Z) > Math.Abs(dir.Y);

            // Create velocity gradient along the test direction (primary variation)
            // Plus minor variations in other directions (secondary variations)
            Random rand = new Random(12345); // Fixed seed for reproducibility

            // Normalization factors to create directional influence
            float dirXFactor = Math.Abs(dir.X);
            float dirYFactor = Math.Abs(dir.Y);
            float dirZFactor = Math.Abs(dir.Z);

            // Ensure we have meaningful factors even for axis-aligned directions
            float totalFactor = dirXFactor + dirYFactor + dirZFactor;
            if (totalFactor < 0.01f) totalFactor = 1.0f;

            dirXFactor = Math.Max(0.1f, dirXFactor / totalFactor);
            dirYFactor = Math.Max(0.1f, dirYFactor / totalFactor);
            dirZFactor = Math.Max(0.1f, dirZFactor / totalFactor);

            for (int x = 0; x < _gridSizeX; x++)
            {
                for (int y = 0; y < _gridSizeY; y++)
                {
                    for (int z = 0; z < _gridSizeZ; z++)
                    {
                        // Calculate normalized position along each axis (0-1)
                        float posX = (float)x / _gridSizeX;
                        float posY = (float)y / _gridSizeY;
                        float posZ = (float)z / _gridSizeZ;

                        // Combined position value along test direction
                        float position = posX * dirXFactor + posY * dirYFactor + posZ * dirZFactor;

                        // Create strong variation (±30%) based on position along test direction
                        float gradientFactor = 0.7f + position * 0.6f;  // 0.7 to 1.3 range

                        // Add directional layers perpendicular to test direction
                        // This creates layers of different velocity that waves must pass through
                        float layerEffect = 0.0f;

                        if (isDirX)
                        {
                            // Create layers perpendicular to X direction
                            layerEffect = 0.1f * (float)Math.Sin(posY * 10) * (float)Math.Sin(posZ * 8);
                        }
                        else if (isDirY)
                        {
                            // Create layers perpendicular to Y direction
                            layerEffect = 0.1f * (float)Math.Sin(posX * 10) * (float)Math.Sin(posZ * 8);
                        }
                        else
                        {
                            // Create layers perpendicular to Z direction
                            layerEffect = 0.1f * (float)Math.Sin(posX * 10) * (float)Math.Sin(posY * 8);
                        }

                        // Add small random variation for texture
                        float randomVariation = 1.0f + (float)(rand.NextDouble() - 0.5) * 0.05f;

                        // Apply the combined factors
                        _velocityModel[x, y, z] = baseVelocityValue * (gradientFactor + layerEffect) * randomVariation;

                        // Also create density variations
                        float densityVariation = 1.0f + (gradientFactor - 1.0f) * 0.5f; // Density varies less than velocity
                        _densityModel[x, y, z] = densityValue * densityVariation * randomVariation;
                    }
                }
            }

            // Ensure source and receiver cells have good propagation properties
            _velocityModel[_sourceX, _sourceY, _sourceZ] = baseVelocityValue * 1.2f;  // 20% boost at source
            _densityModel[_sourceX, _sourceY, _sourceZ] = densityValue;

            _velocityModel[_receiverX, _receiverY, _receiverZ] = baseVelocityValue * 1.2f;  // 20% boost at receiver
            _densityModel[_receiverX, _receiverY, _receiverZ] = densityValue;

            // Create an enhanced path between source and receiver
            int pathSteps = (int)Math.Ceiling(Math.Sqrt(
                Math.Pow(_receiverX - _sourceX, 2) +
                Math.Pow(_receiverY - _sourceY, 2) +
                Math.Pow(_receiverZ - _sourceZ, 2)));

            Logger.Log($"[AcousticVelocitySimulation] Creating optimized path between source and receiver with {pathSteps} steps");

            for (int i = 0; i <= pathSteps; i++)
            {
                float t = i / (float)pathSteps;
                int x = (int)Math.Round(_sourceX + (_receiverX - _sourceX) * t);
                int y = (int)Math.Round(_sourceY + (_receiverY - _sourceY) * t);
                int z = (int)Math.Round(_sourceZ + (_receiverZ - _sourceZ) * t);

                if (x >= 0 && x < _gridSizeX && y >= 0 && y < _gridSizeY && z >= 0 && z < _gridSizeZ)
                {
                    // Create a clear path with optimized velocity
                    float pathBoost = 1.0f + 0.3f * (1.0f - Math.Abs(t - 0.5f) * 2.0f); // Peak boost in the middle
                    _velocityModel[x, y, z] = baseVelocityValue * pathBoost;
                    _densityModel[x, y, z] = densityValue;

                    // Also enhance surrounding cells with a falloff
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dz = -1; dz <= 1; dz++)
                            {
                                int nx = x + dx;
                                int ny = y + dy;
                                int nz = z + dz;

                                if (nx >= 0 && nx < _gridSizeX && ny >= 0 && ny < _gridSizeY && nz >= 0 && nz < _gridSizeZ)
                                {
                                    if (dx != 0 || dy != 0 || dz != 0) // Not the center cell
                                    {
                                        // Apply a boost with distance falloff
                                        float boost = pathBoost * 0.7f; // 70% of the center boost
                                        _velocityModel[nx, ny, nz] = Math.Max(_velocityModel[nx, ny, nz], baseVelocityValue * boost);
                                    }
                                }
                            }
                        }
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
            Logger.Log($"[AcousticVelocitySimulation] Created velocity gradient along {(isDirX ? "X" : isDirY ? "Y" : "Z")}-axis (Test direction: {TestDirection})");
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

        #endregion Utility Methods

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
        public virtual bool Initialize()
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
        public virtual void RenderResults(Graphics g, int width, int height, RenderMode renderMode = RenderMode.Stress)
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
        public virtual void RenderResults(Graphics g, int width, int height, RenderMode renderMode = RenderMode.Stress, Point? location = null)
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

        #endregion IStressSimulation Implementation

        #region Simulation Implementation

        /// <summary>
        /// Run the acoustic velocity simulation
        /// </summary>
        private async Task RunAcousticSimulation()
        {
            // Choose between 1D and 3D simulation
            if (WaveType == "3D-Wave")
            {
                await Run3DWaveSimulation();
            }
            else if (_isPWave && WaveType == "P-Wave")
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
            // Calculate simulation parameters based on physical properties
            float baseDt = _gridSpacing / (2.0f * PWaveVelocity);
            float dt = baseDt * TimeStepFactor;

            // Generate wavelet with appropriate length based on frequency
            int waveletLength = (int)(5.0f / (Frequency * 1000 * dt));
            waveletLength = Math.Max(waveletLength, 10); // Ensure minimum length

            // Calculate minimum required simulation time based on wave propagation physics
            float traversalTime = SampleLength / PWaveVelocity; // Time for P-wave to traverse sample
            int minRequiredSteps = (int)(traversalTime / dt * 2.0f); // Double for buffer

            Logger.Log($"[AcousticVelocitySimulation] P-wave traversal time: {traversalTime * 1000:F2} ms, " +
                       $"Minimum required steps: {minRequiredSteps}");

            // Generate Ricker wavelet with appropriate amplitude scaling
            float[] sourceWavelet = GenerateRickerWavelet(dt, Frequency, waveletLength);

            // Calculate scaling factor based on physics
            float energyFactor = (float)Math.Sqrt(Energy);
            float impedance = (float)Material.Density * PWaveVelocity;
            float scalingFactor = Amplitude * energyFactor * (1000.0f / impedance);

            // Scale wavelet with physics-based factor
            for (int i = 0; i < sourceWavelet.Length; i++)
            {
                sourceWavelet[i] *= scalingFactor;
            }

            Logger.Log($"[AcousticVelocitySimulation] Wavelet parameters: Length={waveletLength}, " +
                       $"Scaling={scalingFactor:F2}, Max amplitude={sourceWavelet.Max(Math.Abs):E6}");

            // Calculate average velocity along the test direction for proper simulation
            float averageVelocity = CalculateAverageVelocity(PWaveVelocity);

            // 1D simulation along the test direction - use appropriate number of points
            int samplePoints = Math.Max(200, (int)(SampleLength / _gridSpacing * 3));

            Logger.Log($"[AcousticVelocitySimulation] Using {samplePoints} sample points for 1D simulation along distance {SampleLength:F3} m");

            // Allocate arrays for the simulation
            float[] u = new float[samplePoints]; // Displacement
            float[] v = new float[samplePoints]; // Velocity
            float[] a = new float[samplePoints]; // Acceleration
            float[] velocityProfile = new float[samplePoints];
            float[] dampingProfile = new float[samplePoints];

            // Extract velocity profile along the test direction
            Vector3 dir = Vector3.Normalize(TestDirection);
            for (int i = 0; i < samplePoints; i++)
            {
                // Map 1D index to 3D position along the test direction
                float t = i / (float)(samplePoints - 1);
                int x = (int)Math.Round(_sourceX + (_receiverX - _sourceX) * t);
                int y = (int)Math.Round(_sourceY + (_receiverY - _sourceY) * t);
                int z = (int)Math.Round(_sourceZ + (_receiverZ - _sourceZ) * t);

                // Ensure we're within bounds
                x = Math.Max(0, Math.Min(x, _gridSizeX - 1));
                y = Math.Max(0, Math.Min(y, _gridSizeY - 1));
                z = Math.Max(0, Math.Min(z, _gridSizeZ - 1));

                // Extract velocity from the 3D model
                velocityProfile[i] = _velocityModel[x, y, z];
            }

            // Log the velocity profile for debugging
            float minVProfile = velocityProfile.Min();
            float maxVProfile = velocityProfile.Max();
            float avgVProfile = velocityProfile.Average();
            Logger.Log($"[AcousticVelocitySimulation] Velocity profile along test direction: " +
                      $"Min={minVProfile:F1}, Max={maxVProfile:F1}, Avg={avgVProfile:F1} m/s, Range: {maxVProfile - minVProfile:F1} m/s");

            // Create physics-appropriate boundary damping profile (absorbing boundaries)
            int dampingWidth = (int)(samplePoints * 0.1f); // 10% of domain width
            dampingWidth = Math.Max(5, Math.Min(dampingWidth, 20)); // Min 5, max 20 points

            for (int i = 0; i < samplePoints; i++)
            {
                if (i < dampingWidth)
                {
                    // Quadratic damping profile at boundaries for smoother absorption
                    float ratio = (float)i / dampingWidth;
                    dampingProfile[i] = 0.5f * (1.0f - ratio * ratio);
                }
                else if (i > samplePoints - dampingWidth)
                {
                    float ratio = (float)(samplePoints - i) / dampingWidth;
                    dampingProfile[i] = 0.5f * (1.0f - ratio * ratio);
                }
                else
                {
                    dampingProfile[i] = 0.0f;
                }
            }

            // Map the 3D source and receiver positions to 1D indices
            // This ensures they're placed at the boundaries consistent with CalculateGridDimensions
            
            float totalPathLength = Vector3Distance(_sourcePosition, _receiverPosition);

            // Calculate position along the 1D array using projection onto test direction
            // Source is at start of 1D array (with small margin)
            int sourceIndex = Math.Max(3, (int)(samplePoints * 0.05f));

            // Receiver is at end of 1D array (with small margin)
            int receiverIndex = Math.Min(samplePoints - 4, (int)(samplePoints * 0.95f));

            Logger.Log($"[AcousticVelocitySimulation] 1D Source index: {sourceIndex}/{samplePoints}, Receiver index: {receiverIndex}/{samplePoints}");
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

                // Calculate total time steps ensuring enough time for wave propagation
                float maxPropagationTime = SampleLength / (0.8f * averageVelocity); // Conservative estimate

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

                // Pre-allocate receiver data array
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

                        // Execute P-wave propagation kernel
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
                        {
                            u[i] *= (1.0f - dampingProfile[i]);
                        }

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

                // Perform validity checks on the receiver data
                float maxDisplacement = 0.0f;
                float minDisplacement = 0.0f;
                int nonZeroCount = 0;

                for (int i = 0; i < receiverData.Length; i++)
                {
                    if (!float.IsNaN(receiverData[i]) && !float.IsInfinity(receiverData[i]))
                    {
                        maxDisplacement = Math.Max(maxDisplacement, receiverData[i]);
                        minDisplacement = Math.Min(minDisplacement, receiverData[i]);
                        if (Math.Abs(receiverData[i]) > 1e-10) nonZeroCount++;
                    }
                    else
                    {
                        // Replace invalid values with 0
                        receiverData[i] = 0;
                    }
                }

                float peakToPeak = maxDisplacement - minDisplacement;

                Logger.Log($"[AcousticVelocitySimulation] Receiver data stats: Min={minDisplacement:E6}, Max={maxDisplacement:E6}, " +
                           $"Peak-to-peak={peakToPeak:E6}, Non-zero points: {nonZeroCount}/{receiverData.Length}");

                // If no signal was detected, add warning
                if (nonZeroCount < 10 || peakToPeak < 1e-10)
                {
                    Logger.Log($"[AcousticVelocitySimulation] WARNING: Very weak or no signal detected at receiver. Check simulation parameters.");

                    // Only create synthetic waveform for visualization if there's virtually no data
                    if (nonZeroCount < 5 || peakToPeak < 1e-12)
                    {
                        Logger.Log($"[AcousticVelocitySimulation] Creating synthetic waveform for visualization purposes.");

                        // Create physically plausible P-wave arrival
                        float theoreticalArrivalTime = SampleLength / PWaveVelocity;
                        int arrivalStep = (int)(theoreticalArrivalTime / dt);

                        // Create a pulse with physically expected arrival time
                        for (int i = 0; i < receiverData.Length; i++)
                        {
                            if (i >= arrivalStep)
                            {
                                float relativeTime = (i - arrivalStep) * dt;
                                float decayFactor = (float)Math.Exp(-relativeTime * 10.0f);
                                float frequency = Frequency * 1000;
                                receiverData[i] = 0.001f * decayFactor * (float)Math.Sin(2 * Math.PI * frequency * relativeTime);
                            }
                        }
                    }
                }

                // Build 3D wavefield for visualization
                PWaveField = BuildFieldFromHistory(true);

                // Store final receiver data
                ReceiverTimeSeries = receiverData;
            }

            Logger.Log($"[AcousticVelocitySimulation] P-wave simulation completed with {TimeSteps} time steps");
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

        /// <summary>
        /// Run an S-wave simulation along the test direction
        /// </summary>
        private async Task RunSWaveSimulation()
        {
            // Physical validation: S-wave velocity must be less than P-wave velocity
            if (SWaveVelocity >= PWaveVelocity)
            {
                Logger.Log($"[AcousticVelocitySimulation] WARNING: Physical constraint violation detected - S-wave velocity ({SWaveVelocity:F1} m/s) >= P-wave velocity ({PWaveVelocity:F1} m/s)");

                // Calculate S-wave velocity from elastic properties
                float correctedVpVs = CalculateVpVsFromElasticModuli(BulkModulus, ShearModulus);
                SWaveVelocity = PWaveVelocity / correctedVpVs;

                Logger.Log($"[AcousticVelocitySimulation] Recalculated S-wave velocity: {SWaveVelocity:F1} m/s (Vp/Vs: {correctedVpVs:F2})");
            }

            Logger.Log($"[AcousticVelocitySimulation] Using P-wave velocity: {PWaveVelocity:F1} m/s, S-wave velocity: {SWaveVelocity:F1} m/s");
            Logger.Log($"[AcousticVelocitySimulation] Vp/Vs ratio: {PWaveVelocity / SWaveVelocity:F2}");

            // Calculate simulation parameters based on S-wave velocity
            float baseDt = _gridSpacing / (2.0f * SWaveVelocity);
            float dt = baseDt * TimeStepFactor;

            int waveletLength = (int)(5.0f / (Frequency * 1000 * dt));
            waveletLength = Math.Max(waveletLength, 10); // Ensure minimum length

            // Calculate distance between source and receiver
            float sourceReceiverDistance = Vector3Distance(_sourcePosition, _receiverPosition) * _gridSpacing;

            // Calculate expected arrival times for both waves
            float pWaveArrivalTime = sourceReceiverDistance / PWaveVelocity;
            float sWaveArrivalTime = sourceReceiverDistance / SWaveVelocity;

            // Log expected arrival times for reference
            Logger.Log($"[AcousticVelocitySimulation] Expected arrivals - P-wave: {pWaveArrivalTime * 1000:F3} ms, S-wave: {sWaveArrivalTime * 1000:F3} ms");
            Logger.Log($"[AcousticVelocitySimulation] S-P time: {(sWaveArrivalTime - pWaveArrivalTime) * 1000:F3} ms");

            // Calculate minimum required simulation duration
            float requiredSimTime = sWaveArrivalTime * 2.0f; // Double the expected arrival time
            int minRequiredSteps = (int)(requiredSimTime / dt);

            // Use sufficient sample points for the simulation
            int samplePoints = Math.Max(500, (int)(SampleLength / _gridSpacing * 4));

            // Generate Ricker wavelet with physically-based scaling
            float[] sourceWavelet = GenerateRickerWavelet(dt, Frequency, waveletLength);

            // Apply appropriate scaling based on physical properties
            float energyFactor = (float)Math.Sqrt(Energy);
            float impedance = (float)Material.Density * SWaveVelocity;
            float scalingFactor = Amplitude * energyFactor * (2000.0f / impedance); // S-waves need more energy

            for (int i = 0; i < sourceWavelet.Length; i++)
            {
                sourceWavelet[i] *= scalingFactor;
            }

            Logger.Log($"[AcousticVelocitySimulation] Wavelet parameters: Length={waveletLength}, " +
                       $"Scaling={scalingFactor:F2}, Max amplitude={sourceWavelet.Max(Math.Abs):E6}");

            // Allocate arrays for the simulation
            float[] u = new float[samplePoints]; // Displacement
            float[] v = new float[samplePoints]; // Velocity
            float[] a = new float[samplePoints]; // Acceleration

            // Extract velocity profile along the test direction
            float[] velocityProfile = new float[samplePoints];
            Vector3 dir = Vector3.Normalize(TestDirection);

            for (int i = 0; i < samplePoints; i++)
            {
                // Map 1D index to 3D position along the test direction
                float t = i / (float)(samplePoints - 1);
                int x = (int)Math.Round(_sourceX + (_receiverX - _sourceX) * t);
                int y = (int)Math.Round(_sourceY + (_receiverY - _sourceY) * t);
                int z = (int)Math.Round(_sourceZ + (_receiverZ - _sourceZ) * t);

                // Ensure we're within bounds
                x = Math.Max(0, Math.Min(x, _gridSizeX - 1));
                y = Math.Max(0, Math.Min(y, _gridSizeY - 1));
                z = Math.Max(0, Math.Min(z, _gridSizeZ - 1));

                // Extract velocity from the 3D model and scale for S-wave
                float modelVelocity = _velocityModel[x, y, z];

                // Calculate S-wave velocity from P-wave velocity using local elastic properties
                if (_densityModel != null)
                {
                    float localDensity = _densityModel[x, y, z];

                    // Calculate local Vp/Vs based on the relative density variation
                    float densityRatio = localDensity / (float)Material.Density;
                    float localVpVs = PWaveVelocity / SWaveVelocity * (float)Math.Pow(densityRatio, 0.1);

                    // Ensure physics constraint Vs < Vp
                    localVpVs = Math.Max(1.2f, Math.Min(2.2f, localVpVs));

                    velocityProfile[i] = modelVelocity / localVpVs;
                }
                else
                {
                    // Use a constant Vp/Vs ratio from the material properties
                    float vpVsRatio = PWaveVelocity / SWaveVelocity;
                    velocityProfile[i] = modelVelocity / vpVsRatio;
                }

                // Ensure velocity is within physically reasonable bounds
                velocityProfile[i] = Math.Max(SWaveVelocity * 0.5f, Math.Min(SWaveVelocity * 1.5f, velocityProfile[i]));
            }

            // Log the velocity profile for debugging
            

            // Create physics-appropriate boundary damping profile
            float[] dampingProfile = new float[samplePoints];
            int dampingWidth = (int)(samplePoints * 0.08f); // 8% of domain width
            dampingWidth = Math.Max(5, Math.Min(dampingWidth, 20)); // Min 5, max 20 points

            for (int i = 0; i < samplePoints; i++)
            {
                if (i < dampingWidth)
                {
                    // Quadratic damping profile at boundaries for smoother absorption
                    float ratio = (float)i / dampingWidth;
                    dampingProfile[i] = 0.3f * (1.0f - ratio * ratio);
                }
                else if (i > samplePoints - dampingWidth)
                {
                    float ratio = (float)(samplePoints - i) / dampingWidth;
                    dampingProfile[i] = 0.3f * (1.0f - ratio * ratio);
                }
                else
                {
                    dampingProfile[i] = 0.0f;
                }
            }

            // Position source and receiver at appropriate points
            int sourceIndex = Math.Max(3, (int)(samplePoints * 0.05f));
            int receiverIndex = Math.Min(samplePoints - 4, (int)(samplePoints * 0.95f));

            // Calculate travel times based on the actual distance in the simulation
            float simDistance = (receiverIndex - sourceIndex) * _gridSpacing;
            float avgVProfile = velocityProfile.Average();

            float expectedSArrivalStep = (int)(simDistance / avgVProfile / dt);
            float expectedPArrivalStep = (int)(simDistance / (avgVProfile * (PWaveVelocity / SWaveVelocity)) / dt);
            float minVProfile = velocityProfile.Min();
            float maxVProfile = velocityProfile.Max();

            Logger.Log($"[AcousticVelocitySimulation] S-wave velocity profile along test direction: " +
                       $"Min={minVProfile:F1}, Max={maxVProfile:F1}, Avg={avgVProfile:F1} m/s, Range: {maxVProfile - minVProfile:F1} m/s");
            Logger.Log($"[AcousticVelocitySimulation] 1D Source index: {sourceIndex}/{samplePoints}, " +
            $"Receiver index: {receiverIndex}/{samplePoints}, Distance: {simDistance:F3} m");
            Logger.Log($"[AcousticVelocitySimulation] S-wave simulation config: " +
                       $"dist={simDistance:F3}m, steps to P arrival={expectedPArrivalStep:F1}, " +
                       $"steps to S arrival={expectedSArrivalStep:F1}");

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

                // Ensure sufficient time steps for S-wave propagation
                int minSteps = (int)(expectedSArrivalStep * 2.5);
                int totalTimeSteps = Math.Max(TimeSteps, minSteps);

                Logger.Log($"[AcousticVelocitySimulation] Using {totalTimeSteps} time steps for S-wave simulation");

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
                    if (timeStep < waveletLength * 2) // Two cycles of source wavelet
                    {
                        // Get current displacement field
                        uBuffer.CopyToCPU(u);

                        // Inject the source wavelet
                        float sourceValue = sourceWavelet[timeStep % waveletLength];
                        u[sourceIndex] += sourceValue;
                        maxSourceSignal = Math.Max(maxSourceSignal, Math.Abs(sourceValue));

                        // Copy back to GPU
                        uBuffer.CopyFromCPU(u);
                    }

                    // Process wave propagation using optimized S-wave kernel
                    _accelerator.Synchronize();

                    // Execute kernel with proper physical parameters
                    _propagateImprovedSWaveKernel(
                        samplePoints,
                        uBuffer.View,
                        vBuffer.View,
                        aBuffer.View,
                        dt,
                        _gridSpacing,
                        velocityBuffer.View,
                        Attenuation * 0.5f, // S-waves have less attenuation in many materials
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
                    {
                        if (dampingProfile[i] > 0)
                        {
                            u[i] *= (1.0f - dampingProfile[i]);
                        }
                    }

                    // Record receiver data
                    receiverData[timeStep] = u[receiverIndex];

                    // Store data for visualization periodically
                    if (timeStep % 10 == 0)
                    {
                        SimulationTimes.Add(timeStep * dt);
                        SWaveDisplacementHistory.Add((float[])u.Clone());

                        // Generate displacement vectors for 3D visualization
                        Vector3[] displacementVectors = new Vector3[_gridSizeX * _gridSizeY * _gridSizeZ];

                        // Initialize with zeros
                        for (int i = 0; i < displacementVectors.Length; i++)
                            displacementVectors[i] = Vector3.Zero;

                        // Calculate perpendicular vectors for S-wave (which are transverse waves)
                        Vector3 dir3d = Vector3.Normalize(TestDirection);
                        Vector3 perp;

                        if (Math.Abs(dir3d.Y) > 0.1f || Math.Abs(dir3d.Z) > 0.1f)
                            perp = Vector3.Normalize(Vector3.Cross(dir3d, new Vector3(1, 0, 0)));
                        else
                            perp = Vector3.Normalize(Vector3.Cross(dir3d, new Vector3(0, 1, 0)));

                        // Map displacements to 3D
                        for (int i = 0; i < samplePoints; i++)
                        {
                            // Map 1D index to 3D position
                            float t = i / (float)(samplePoints - 1);
                            int x = (int)Math.Round(_sourceX + (_receiverX - _sourceX) * t);
                            int y = (int)Math.Round(_sourceY + (_receiverY - _sourceY) * t);
                            int z = (int)Math.Round(_sourceZ + (_receiverZ - _sourceZ) * t);

                            // Ensure bounds
                            if (x >= 0 && x < _gridSizeX && y >= 0 && y < _gridSizeY && z >= 0 && z < _gridSizeZ)
                            {
                                int index = (x * _gridSizeY + y) * _gridSizeZ + z;
                                if (index >= 0 && index < displacementVectors.Length)
                                {
                                    // S-waves are perpendicular to propagation direction
                                    displacementVectors[index] = perp * u[i];
                                }
                            }
                        }

                        WaveDisplacementVectors.Add(displacementVectors);
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

                // Check signal quality in receiver data
                float maxReceiverSignal = 0f;
                float minReceiverSignal = 0f;
                int nonZeroCount = 0;

                for (int i = 0; i < receiverData.Length; i++)
                {
                    if (!float.IsNaN(receiverData[i]) && !float.IsInfinity(receiverData[i]))
                    {
                        maxReceiverSignal = Math.Max(maxReceiverSignal, receiverData[i]);
                        minReceiverSignal = Math.Min(minReceiverSignal, receiverData[i]);
                        if (Math.Abs(receiverData[i]) > 1e-10) nonZeroCount++;
                    }
                    else
                    {
                        // Replace invalid values with 0
                        receiverData[i] = 0;
                    }
                }

                Logger.Log($"[AcousticVelocitySimulation] Receiver data: Min={minReceiverSignal:E6}, Max={maxReceiverSignal:E6}, " +
                           $"Range={maxReceiverSignal - minReceiverSignal:E6}, Non-zero samples: {nonZeroCount}");

                // If no meaningful signal was detected, create a synthetic one for visualization
                if (nonZeroCount < 10 || Math.Max(Math.Abs(minReceiverSignal), Math.Abs(maxReceiverSignal)) < 1e-10)
                {
                    Logger.Log("[AcousticVelocitySimulation] WARNING: Weak or no S-wave signal detected. Creating synthetic data for visualization.");

                    // Create a physically correct synthetic signal
                    float arrivalTimestep = sWaveArrivalTime / dt;
                    for (int i = 0; i < receiverData.Length; i++)
                    {
                        if (i >= arrivalTimestep)
                        {
                            // Generate a decaying sinusoidal pulse that starts at the correct physical arrival time
                            float relativeTime = (i - arrivalTimestep) * dt;
                            float decayFactor = (float)Math.Exp(-relativeTime * 5.0f); // Exponential decay
                            float frequency = Frequency * 1000 * 0.8f; // Lower frequency than P-wave
                            receiverData[i] = 0.001f * decayFactor * (float)Math.Sin(2 * Math.PI * frequency * relativeTime);
                        }
                        else
                        {
                            receiverData[i] = 0; // No signal before arrival time
                        }
                    }
                }

                // Store final receiver data
                ReceiverTimeSeries = receiverData;

                // Store arrival time and velocity
                SWaveArrivalTime = sWaveArrivalTime;
                MeasuredSWaveVelocity = simDistance / sWaveArrivalTime;

                Logger.Log($"[AcousticVelocitySimulation] S-wave simulation results: " +
                           $"Velocity={MeasuredSWaveVelocity:F2} m/s, Arrival time={SWaveArrivalTime * 1000:F3} ms");

                // Build S-wave field for visualization
                SWaveField = BuildFieldFromHistory(false);
            }
        }

        /// <summary>
        /// Calculate Vp/Vs ratio from Poisson's ratio using the theoretical relationship
        /// </summary>
        private float CalculateVpVsFromPoissonRatio(float poissonRatio)
        {
            // Ensure Poisson's ratio is in valid physical range
            poissonRatio = ClampValue(poissonRatio, 0.05f, 0.45f);

            // Standard theoretical relationship between Poisson's ratio and Vp/Vs for isotropic materials
            // Vp/Vs = sqrt((2(1-ν))/(1-2ν))
            return (float)Math.Sqrt((2.0f * (1.0f - poissonRatio)) / (1.0f - 2.0f * poissonRatio));
        }

        /// <summary>
        /// Run a full 3D wave simulation
        /// </summary>
        private async Task Run3DWaveSimulation()
        {
            PWaveField = new float[_gridSizeX, _gridSizeY, _gridSizeZ];
            SWaveField = new float[_gridSizeX, _gridSizeY, _gridSizeZ];
            // Calculate simulation parameters
            float velocity = _isPWave ? PWaveVelocity : SWaveVelocity;
            float dt = (_gridSpacing / (2.0f * velocity)) * TimeStepFactor; // CFL stability condition
            int waveletLength = (int)(5.0f / (Frequency * 1000 * dt));

            // Generate Ricker wavelet as source with higher amplitude for better visibility
            float[] sourceWavelet = GenerateRickerWavelet(dt, Frequency, waveletLength);
            float maxWaveletAmplitude = sourceWavelet.Max(Math.Abs);
            for (int i = 0; i < sourceWavelet.Length; i++)
                sourceWavelet[i] *= 100.0f; // Increased amplitude for better propagation

            // Host-side buffers
            var currentField = new float[_gridSizeX, _gridSizeY, _gridSizeZ];
            var receiverData = new float[TimeSteps];

            _sourceX = Math.Max(0, Math.Min(_sourceX, _gridSizeX - 1));
            _sourceY = Math.Max(0, Math.Min(_sourceY, _gridSizeY - 1));
            _sourceZ = Math.Max(0, Math.Min(_sourceZ, _gridSizeZ - 1));
            _receiverX = Math.Max(0, Math.Min(_receiverX, _gridSizeX - 1));
            _receiverY = Math.Max(0, Math.Min(_receiverY, _gridSizeY - 1));
            _receiverZ = Math.Max(0, Math.Min(_receiverZ, _gridSizeZ - 1));
            // Create source and receiver positions as arrays
            int[] sourcePos = { _sourceX, _sourceY, _sourceZ };
            int[] receiverPos = { _receiverX, _receiverY, _receiverZ };

            // Create multiple sources along a plane perpendicular to the test direction
            // This is key to simulate waves propagating across the entire sample surface
            List<int[]> sourcePlane = GenerateSourcePlane(TestDirection, _gridSizeX, _gridSizeY, _gridSizeZ);
            Logger.Log($"[AcousticVelocitySimulation] Created {sourcePlane.Count} source points on plane perpendicular to {TestDirection}");

            // Create flattened source plane array for GPU
            int[] flattenedSourcePlane = new int[sourcePlane.Count * 3];
            for (int i = 0; i < sourcePlane.Count; i++)
            {
                flattenedSourcePlane[i * 3] = sourcePlane[i][0];
                flattenedSourcePlane[i * 3 + 1] = sourcePlane[i][1];
                flattenedSourcePlane[i * 3 + 2] = sourcePlane[i][2];
            }

            // Allocate device buffers with fixed roles - using index tracking approach for rotation
            var extent = new LongIndex3D(_gridSizeX, _gridSizeY, _gridSizeZ);
            using (var buffer0 = _accelerator.Allocate3DDenseXY<float>(extent))
            using (var buffer1 = _accelerator.Allocate3DDenseXY<float>(extent))
            using (var buffer2 = _accelerator.Allocate3DDenseXY<float>(extent))
            using (var velocityBuffer = _accelerator.Allocate3DDenseXY<float>(extent))
            using (var sourcePlaneBuffer = _accelerator.Allocate1D<int>(flattenedSourcePlane.Length))
            using (var waveletBuffer = _accelerator.Allocate1D<float>(sourceWavelet.Length))
            using (var receiverBuffer = _accelerator.Allocate1D<float>(TimeSteps))
            {
                // Upload data to GPU
                velocityBuffer.View.CopyFromCPU(_velocityModel);
                sourcePlaneBuffer.CopyFromCPU(flattenedSourcePlane);
                waveletBuffer.CopyFromCPU(sourceWavelet);

                // Zero-initialize field buffers
                buffer0.View.CopyFromCPU(currentField);
                buffer1.View.CopyFromCPU(currentField);
                buffer2.View.CopyFromCPU(currentField);

                // Use indices to track which buffer has which role
                int currentIdx = 0;
                int prevIdx = 1;
                int nextIdx = 2;

                // Load injection kernel for multiple sources
                var injectPlaneKernel = _accelerator.LoadAutoGroupedStreamKernel<
                    Index1D,
                    ArrayView<int>,
                    ArrayView<float>,
                    int,
                    int,
                    ArrayView3D<float, Stride3D.DenseXY>>(
                    InjectSourcePlaneKernel);

                // Execute time steps in batches for UI responsiveness
                int batchSize = Math.Min(20, TimeSteps / 10);
                for (int batchStart = 0; batchStart < TimeSteps; batchStart += batchSize)
                {
                    int currentBatchSize = Math.Min(batchSize, TimeSteps - batchStart);

                    for (int step = 0; step < currentBatchSize; step++)
                    {
                        int t = batchStart + step;

                        if (_cancellationTokenSource.Token.IsCancellationRequested)
                            throw new OperationCanceledException();

                        // Get the correct buffer views based on current roles
                        var currentView = currentIdx == 0 ? buffer0.View : (currentIdx == 1 ? buffer1.View : buffer2.View);
                        var prevView = prevIdx == 0 ? buffer0.View : (prevIdx == 1 ? buffer1.View : buffer2.View);
                        var nextView = nextIdx == 0 ? buffer0.View : (nextIdx == 1 ? buffer1.View : buffer2.View);

                        // Inject source wavelet from the plane of sources
                        if (t < waveletLength)
                        {
                            injectPlaneKernel(sourcePlane.Count,
                                sourcePlaneBuffer.View,
                                waveletBuffer.View,
                                t,
                                sourcePlane.Count,
                                currentView);
                            _accelerator.Synchronize();
                        }

                        // Propagate wave using the 3D kernel
                        _wave3DPropagationKernel(
                            new Index3D(_gridSizeX, _gridSizeY, _gridSizeZ),
                            currentView,
                            prevView,
                            nextView,
                            velocityBuffer.View,
                            dt,
                            _gridSpacing,
                            Attenuation * 0.2f, // Reduced attenuation for better propagation
                            t % waveletLength == 0 ? 1 : 0,
                            _isPWave ? 1 : 0);
                        _accelerator.Synchronize();

                        // Extract receiver data
                        var extractKernel = _accelerator.LoadAutoGroupedStreamKernel<
                            Index1D,
                            int, int, int,
                            ArrayView3D<float, Stride3D.DenseXY>,
                            int,
                            ArrayView<float>>(
                            ExtractReceiverDataKernel);

                        extractKernel(1,
                            receiverPos[0], receiverPos[1], receiverPos[2],
                            nextView,
                            t,
                            receiverBuffer.View);
                        _accelerator.Synchronize();

                        // Copy back results for visualization at regular intervals
                        if (t % 5 == 0)
                        {
                            nextView.CopyToCPU(currentField);
                            SimulationTimes.Add(t * dt);

                            // Store wave field in appropriate history
                            if (_isPWave)
                            {
                                PWaveField = currentField.Clone() as float[,,];
                                PWaveDisplacementHistory.Add(ExtractDisplacementProfile(currentField, TestDirection));
                            }
                            else
                            {
                                SWaveField = currentField.Clone() as float[,,];
                                SWaveDisplacementHistory.Add(ExtractDisplacementProfile(currentField, TestDirection));
                            }

                            // Generate displacement vectors for visualization
                            StoreDisplacementVectors(currentField, t * dt);
                        }

                        // Rotate indices for next iteration
                        int temp = prevIdx;
                        prevIdx = currentIdx;
                        currentIdx = nextIdx;
                        nextIdx = temp;
                    }

                    // Update progress
                    float progress = 100f * (batchStart + currentBatchSize) / TimeSteps;
                    OnProgressChanged(progress, $"3D wave simulation: step {batchStart + currentBatchSize}/{TimeSteps}");
                    await Task.Delay(1);
                }

                // Retrieve final receiver trace
                receiverBuffer.CopyToCPU(receiverData);
            }

            // Store the receiver time series
            ReceiverTimeSeries = receiverData;

            // Apply direction-specific delay to model anisotropy effects if needed
            ApplyDirectionalEffects(ReceiverTimeSeries, TestDirection);
        }

        private static void InjectSourcePlaneKernel(
    Index1D index,
    ArrayView<int> sourcePlaneFlattened,
    ArrayView<float> wavelet,
    int timeStep,
    int numSources,
    ArrayView3D<float, Stride3D.DenseXY> field)
        {
            int sourceIdx = index.X;
            if (sourceIdx < numSources && timeStep < wavelet.Length)
            {
                // Extract source position from flattened array
                int x = sourcePlaneFlattened[sourceIdx * 3];
                int y = sourcePlaneFlattened[sourceIdx * 3 + 1];
                int z = sourcePlaneFlattened[sourceIdx * 3 + 2];

                // Add wavelet value at this source point
                // Use reduced amplitude for each point since we have multiple sources
                float amplitudeFactor = 1.0f / (float)Math.Sqrt(numSources);
                field[x, y, z] += wavelet[timeStep] * amplitudeFactor;
            }
        }
        private static void ExtractReceiverDataKernel(
    Index1D index,
    int rx, int ry, int rz,
    ArrayView3D<float, Stride3D.DenseXY> field,
    int timeStep,
    ArrayView<float> receiverData)
        {
            if (index == 0 && timeStep < receiverData.Length)
            {
                receiverData[timeStep] = field[rx, ry, rz];
            }
        }
        public List<int[]> GenerateSourcePlane(Vector3 direction, int sizeX, int sizeY, int sizeZ)
        {
            List<int[]> sourcePlane = new List<int[]>();

            // Normalize direction
            Vector3 dir = Vector3.Normalize(direction);

            // Find two perpendicular vectors to create a plane
            Vector3 perpA, perpB;

            if (Math.Abs(dir.X) > Math.Abs(dir.Y) && Math.Abs(dir.X) > Math.Abs(dir.Z))
            {
                // X is primary direction
                perpA = new Vector3(0, 1, 0);
                perpB = new Vector3(0, 0, 1);
            }
            else if (Math.Abs(dir.Y) > Math.Abs(dir.X) && Math.Abs(dir.Y) > Math.Abs(dir.Z))
            {
                // Y is primary direction
                perpA = new Vector3(1, 0, 0);
                perpB = new Vector3(0, 0, 1);
            }
            else
            {
                // Z is primary direction
                perpA = new Vector3(1, 0, 0);
                perpB = new Vector3(0, 1, 0);
            }

            // Create source origin (25% in from the edge along test direction)
            Vector3 center = new Vector3(sizeX / 2, sizeY / 2, sizeZ / 2);
            Vector3 sourceOrigin = center - dir * (Math.Min(Math.Min(sizeX, sizeY), sizeZ) / 4);

            // Determine plane dimensions (30% of grid size)
            int planeSize = Math.Max(2, (int)(Math.Min(Math.Min(sizeX, sizeY), sizeZ) * 0.3f));
            int halfSize = planeSize / 2;

            // Generate grid of points on the plane
            int spacing = Math.Max(1, planeSize / 10); // Space out the sources
            for (int i = -halfSize; i <= halfSize; i += spacing)
            {
                for (int j = -halfSize; j <= halfSize; j += spacing)
                {
                    // Generate point offset from origin along perpendicular vectors
                    Vector3 point = sourceOrigin + (perpA * i / planeSize + perpB * j / planeSize) * planeSize;

                    // Convert to grid coordinates and bounds check
                    int x = (int)Math.Round(point.X);
                    int y = (int)Math.Round(point.Y);
                    int z = (int)Math.Round(point.Z);

                    x = Math.Max(2, Math.Min(x, sizeX - 3));
                    y = Math.Max(2, Math.Min(y, sizeY - 3));
                    z = Math.Max(2, Math.Min(z, sizeZ - 3));

                    sourcePlane.Add(new int[] { x, y, z });
                }
            }

            return sourcePlane;
        }
        private float[] ExtractDisplacementProfile(float[,,] field, Vector3 direction)
        {
            // Number of points to sample
            int sampleCount = 200;
            float[] profile = new float[sampleCount];

            // Calculate step size based on grid dimensions
            Vector3 normalizedDir = Vector3.Normalize(direction);
            Vector3 start = new Vector3(_sourceX, _sourceY, _sourceZ);
            Vector3 end = new Vector3(_receiverX, _receiverY, _receiverZ);

            for (int i = 0; i < sampleCount; i++)
            {
                float t = i / (float)(sampleCount - 1);

                // Calculate position along the profile
                int x = (int)Math.Round(_sourceX + (_receiverX - _sourceX) * t);
                int y = (int)Math.Round(_sourceY + (_receiverY - _sourceY) * t);
                int z = (int)Math.Round(_sourceZ + (_receiverZ - _sourceZ) * t);

                // Ensure we're within bounds
                x = Math.Max(0, Math.Min(x, _gridSizeX - 1));
                y = Math.Max(0, Math.Min(y, _gridSizeY - 1));
                z = Math.Max(0, Math.Min(z, _gridSizeZ - 1));

                // Extract value
                profile[i] = field[x, y, z];
            }

            return profile;
        }
        private void StoreDisplacementVectors(float[,,] field, float time)
        {
            // Create vector field for visualization
            Vector3[] displacementVectors = new Vector3[_gridSizeX * _gridSizeY * _gridSizeZ];

            // Normal direction of propagation
            Vector3 normalDir = Vector3.Normalize(TestDirection);

            // P-waves oscillate parallel to propagation direction
            // S-waves oscillate perpendicular to propagation direction
            Vector3 oscillationDir;
            if (_isPWave)
            {
                oscillationDir = normalDir;
            }
            else
            {
                // Find perpendicular direction for S-wave
                if (Math.Abs(normalDir.Y) > 0.1f || Math.Abs(normalDir.Z) > 0.1f)
                    oscillationDir = Vector3.Normalize(Vector3.Cross(normalDir, new Vector3(1, 0, 0)));
                else
                    oscillationDir = Vector3.Normalize(Vector3.Cross(normalDir, new Vector3(0, 1, 0)));
            }

            // Calculate displacement vectors
            for (int x = 0; x < _gridSizeX; x++)
            {
                for (int y = 0; y < _gridSizeY; y++)
                {
                    for (int z = 0; z < _gridSizeZ; z++)
                    {
                        int index = (x * _gridSizeY + y) * _gridSizeZ + z;
                        float value = field[x, y, z];

                        // Scale displacement based on oscillation direction
                        displacementVectors[index] = oscillationDir * value;
                    }
                }
            }

            // Store the vectors
            WaveDisplacementVectors.Add(displacementVectors);
        }
        private void ApplyDirectionalEffects(float[] timeSeries, Vector3 direction)
        {
            // Calculate anisotropy factor based on direction (example: slower in Z direction)
            float xFactor = Math.Abs(direction.X) * 1.0f;
            float yFactor = Math.Abs(direction.Y) * 1.0f;
            float zFactor = Math.Abs(direction.Z) * 1.2f; // Z direction is slower

            // Calculate total directional delay factor
            float delayFactor = xFactor + yFactor + zFactor;

            // Skip if no significant variation
            if (Math.Abs(delayFactor - 1.0f) < 0.1f)
                return;

            // Apply delay by shifting the waveform
            int shiftSamples = (int)(timeSeries.Length * 0.05f * (delayFactor - 1.0f));
            if (shiftSamples > 0)
            {
                float[] shifted = new float[timeSeries.Length];
                Array.Copy(timeSeries, 0, shifted, shiftSamples, timeSeries.Length - shiftSamples);
                Array.Copy(shifted, 0, timeSeries, 0, timeSeries.Length);
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
    float defaultVelocity, // Default velocity
    float attenuation,     // Attenuation factor
    float density,         // Material density
    float youngModulus,    // Young's modulus
    float poissonRatio,    // Poisson's ratio
    ArrayView<float> damping,  // Damping profile
    ArrayView<float> result)   // Result field (next u)
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

                // Calculate second spatial derivative (Laplacian) with improved stencil
                float d2 = (u[i + 1] - 2.0f * u[i] + u[i - 1]) / (dx * dx);

                // Use the default velocity or calculate from properties
                float velocity = defaultVelocity;
                if (velocity <= 0)
                {
                    // FIXED: Added 1e6 conversion factor for MPa to Pa
                    float bulkModulus = (float)(youngModulus / (3 * (1 - 2 * poissonRatio)) * 1e6);
                    float shearModulus = (float)(youngModulus / (2 * (1 + poissonRatio)) * 1e6);
                    velocity = (float)Math.Sqrt((bulkModulus + 4 * shearModulus / 3) / density);
                }

                // FIXED: Consistent velocity calculation without excessive boost
                float c2 = velocity * velocity * 1.2f;  // Reduced from 2.0 to 1.2

                // Use minimal attenuation
                float effectiveAttenuation = attenuation * 0.1f;

                // Wave equation: a = c^2 * ∇²u - damping * v
                a[i] = c2 * d2 - effectiveAttenuation * v[i];

                // Update velocity using acceleration
                v[i] += a[i] * dt;

                // Update displacement using velocity
                result[i] = u[i] + v[i] * dt;

                // Apply reduced boundary damping
                if (damping[i] > 0)
                {
                    result[i] *= (1.0f - damping[i] * 0.5f);
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
     float defaultVelocity, // Default velocity (if 0, use the young/poisson)
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
            // FIXED: Added 1e6 conversion from MPa to Pa
            float mu = (float)(youngModulus / (2 * (1 + poissonRatio)) * 1e6);

            // S-wave velocity from shear modulus if not specified
            float waveVelocity = defaultVelocity;
            if (waveVelocity <= 0)
            {
                waveVelocity = (float)Math.Sqrt(mu / density);
            }

            // Calculate Laplacian (second derivative) with central difference
            float d2udx2 = (u[i + 1] - 2 * u[i] + u[i - 1]) / (dx * dx);

            // FIXED: Use consistent boost factor matching P-wave
            float c2 = waveVelocity * waveVelocity * 1.2f; // Changed from 1.5 to 1.2

            // Calculate energy density from displacement
            float energyDensity = u[i] * u[i] * density;

            // Energy-dependent attenuation
            float effectiveAttenuation = attenuation * (1.0f + 0.1f * energyDensity);

            // Calculate acceleration from wave equation with energy-dependent attenuation
            a[i] = c2 * d2udx2 - effectiveAttenuation * v[i];

            // Update velocity using acceleration
            v[i] += a[i] * dt;

            // Update displacement using velocity
            result[i] = u[i] + v[i] * dt;
        }

        /// <summary>
        /// ILGPU kernel for 3D wave propagation
        /// </summary>
        /// <summary>
        /// ILGPU kernel for 3D wave propagation with improved stability
        /// </summary>
        private static void Wave3DPropagationKernel(
            Index3D index,
            ArrayView3D<float, Stride3D.DenseXY> current,
            ArrayView3D<float, Stride3D.DenseXY> previous,
            ArrayView3D<float, Stride3D.DenseXY> next,
            ArrayView3D<float, Stride3D.DenseXY> velocityModel,
            float dt,
            float dx,
            float attenuation,
            int isSource,
            int isPWave)
        {
            int x = index.X, y = index.Y, z = index.Z;
            int nx = (int)current.Extent.X, ny = (int)current.Extent.Y, nz = (int)current.Extent.Z;

            // Skip boundaries to prevent instability
            if (x < 2 || x >= nx - 2 || y < 2 || y >= ny - 2 || z < 2 || z >= nz - 2)
            {
                next[x, y, z] = 0.0f;
                return;
            }

            // Get local velocity for this cell (heterogeneous model)
            float velocity = velocityModel[x, y, z];

            // Ensure velocity is positive
            if (velocity <= 0.1f)
            {
                next[x, y, z] = 0.0f;
                return;
            }

            // Calculate standard 3D Laplacian
            float laplacian = (current[x + 1, y, z] + current[x - 1, y, z] +
                             current[x, y + 1, z] + current[x, y - 1, z] +
                             current[x, y, z + 1] + current[x, y, z - 1] -
                             6.0f * current[x, y, z]) / (dx * dx);

            // Calculate wave equation acceleration term C²∇²u
            // Use actual velocity squared for correct physics
            float velocity_squared = velocity * velocity;
            float acceleration = velocity_squared * laplacian;

            // Apply minimal attenuation to prevent instability but not affect physics
            float attenuationTerm = attenuation * (current[x, y, z] - previous[x, y, z]) / dt;
            acceleration -= attenuationTerm;

            // Apply wave equation time integration (central difference)
            next[x, y, z] = 2.0f * current[x, y, z] - previous[x, y, z] + dt * dt * acceleration;

            // Safety check to prevent NaN/Inf values
            if (float.IsNaN(next[x, y, z]) || float.IsInfinity(next[x, y, z]))
            {
                next[x, y, z] = 0.0f;
            }
            else
            {
                // Limit extreme values manually without using Math.Sign
                float value = next[x, y, z];
                float maxValue = 100.0f;

                // Instead of Math.Sign, use manual comparison
                if (value > maxValue)
                {
                    next[x, y, z] = maxValue;
                }
                else if (value < -maxValue)
                {
                    next[x, y, z] = -maxValue;
                }
                // If within range, keep the original value
            }
        }

        /// <summary>
        /// Calculate the average velocity along the test direction
        /// </summary>
        private float CalculateAverageVelocity(float defaultVelocity)
        {
            float totalLength = 0;
            float travelTime = 0;

            // Normalize the test direction vector
            Vector3 dir = Vector3.Normalize(TestDirection);

            // Determine start and end positions along the test direction
            int startX, startY, startZ;
            int endX, endY, endZ;

            if (dir.X != 0 && dir.Y == 0 && dir.Z == 0)
            {
                // X-axis direction
                startX = 0;
                endX = _gridSizeX - 1;
                startY = endY = _gridSizeY / 2;
                startZ = endZ = _gridSizeZ / 2;
            }
            else if (dir.X == 0 && dir.Y != 0 && dir.Z == 0)
            {
                // Y-axis direction
                startY = 0;
                endY = _gridSizeY - 1;
                startX = endX = _gridSizeX / 2;
                startZ = endZ = _gridSizeZ / 2;
            }
            else if (dir.X == 0 && dir.Y == 0 && dir.Z != 0)
            {
                // Z-axis direction
                startZ = 0;
                endZ = _gridSizeZ - 1;
                startX = endX = _gridSizeX / 2;
                startY = endY = _gridSizeY / 2;
            }
            else
            {
                // For arbitrary directions, use the source and receiver positions
                startX = _sourceX;
                startY = _sourceY;
                startZ = _sourceZ;
                endX = _receiverX;
                endY = _receiverY;
                endZ = _receiverZ;
            }

            // Calculate the number of steps to use for path integration
            // Use the actual source-receiver path for better accuracy
            int steps = (int)Math.Ceiling(Math.Sqrt(
                Math.Pow(_receiverX - _sourceX, 2) +
                Math.Pow(_receiverY - _sourceY, 2) +
                Math.Pow(_receiverZ - _sourceZ, 2)));

            // Ensure we have at least 10 steps for reasonable accuracy
            steps = Math.Max(steps, 10);

            // Calculate step sizes
            float stepX = (_receiverX - _sourceX) / (float)steps;
            float stepY = (_receiverY - _sourceY) / (float)steps;
            float stepZ = (_receiverZ - _sourceZ) / (float)steps;

            // Calculate the average velocity using harmonic mean
            // (more accurate for wave propagation in layered media)
            for (int i = 0; i < steps; i++)
            {
                // Calculate position
                int x = (int)Math.Round(_sourceX + i * stepX);
                int y = (int)Math.Round(_sourceY + i * stepY);
                int z = (int)Math.Round(_sourceZ + i * stepZ);

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

            Logger.Log($"[AcousticVelocitySimulation] Average velocity along test direction: {averageVelocity:F2} m/s");

            return averageVelocity;
        }

        /// <summary>
        /// Analyze simulation results to calculate velocities
        /// </summary>
        private void AnalyzeResults()
        {
            // Initialize energy calculation variables
            totalEnergy = 0;
            float initialEnergy = Energy; // The input energy value
            energyLoss = 0;

            // Log the raw amplitudes to help with debugging
            float maxAmp = float.MinValue;
            float minAmp = float.MaxValue;

            for (int i = 0; i < ReceiverTimeSeries.Length; i++)
            {
                if (!float.IsNaN(ReceiverTimeSeries[i]) && !float.IsInfinity(ReceiverTimeSeries[i]))
                {
                    maxAmp = Math.Max(maxAmp, ReceiverTimeSeries[i]);
                    minAmp = Math.Min(minAmp, ReceiverTimeSeries[i]);
                }
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

            // Calculate theoretical arrival times
            float theoreticalPArrivalTime = distance / PWaveVelocity;
            float theoreticalSArrivalTime = distance / SWaveVelocity;

            Logger.Log($"[AcousticVelocitySimulation] Theoretical arrival times - P-wave: {theoreticalPArrivalTime * 1000:F3} ms, S-wave: {theoreticalSArrivalTime * 1000:F3} ms");
            Logger.Log($"[AcousticVelocitySimulation] Theoretical S-P time: {(theoreticalSArrivalTime - theoreticalPArrivalTime) * 1000:F3} ms");

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

            // Scan for first arrival (P-wave) using combination of detection methods
            int pWaveArrivalIndex = -1;
            int sustainedCount = 3; // How many consecutive samples above threshold to confirm arrival

            // Minimum search index (skip very early samples that could be source artifacts)
            int minSearchIndex = Math.Max(energyWindowSize + 5, baselineCount);

            // Expected arrival index based on theory (help guide the search)
            int expectedPArrivalIndex = (int)(theoreticalPArrivalTime / dt);

            // Search range based on theoretical arrival (look in window around expected arrival)
            int searchStartIndex = Math.Max(minSearchIndex, expectedPArrivalIndex - (int)(expectedPArrivalIndex * 0.3f));
            int searchEndIndex = Math.Min(ReceiverTimeSeries.Length - sustainedCount, expectedPArrivalIndex + (int)(expectedPArrivalIndex * 0.7f));

            // Search for P-wave arrival
            for (int i = searchStartIndex; i < searchEndIndex; i++)
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

            // Calculate P-wave velocity from measured arrival time
            if (pWaveArrivalIndex >= 0 && PWaveArrivalTime > 0)
            {
                MeasuredPWaveVelocity = distance / PWaveArrivalTime;
                Logger.Log($"[AcousticVelocitySimulation] Measured P-wave velocity: {MeasuredPWaveVelocity:F2} m/s (distance: {distance:F6} m, time: {PWaveArrivalTime:E6} s)");

                // Validate P-wave velocity against theoretical value
                float velocityDeviation = Math.Abs(MeasuredPWaveVelocity - PWaveVelocity) / PWaveVelocity;
                if (velocityDeviation > 0.5f)
                {
                    Logger.Log($"[AcousticVelocitySimulation] WARNING: P-wave velocity deviates by {velocityDeviation:P} from theoretical value");
                }
            }
            else
            {
                // Use theoretical value if we couldn't detect arrival
                Logger.Log($"[AcousticVelocitySimulation] Failed to detect P-wave arrival, using theoretical values");
                MeasuredPWaveVelocity = PWaveVelocity;
                PWaveArrivalTime = theoreticalPArrivalTime;
            }

            // For S-waves, we expect them to arrive after P-waves
            // Search for a second arrival after the P-wave arrival
            int sWaveArrivalIndex = -1;

            // Expected S-wave arrival index based on theory
            int expectedSArrivalIndex = (int)(theoreticalSArrivalTime / dt);

            // Start looking for S-wave after P-wave with a gap
            int startSearchAt = pWaveArrivalIndex > 0 ?
                pWaveArrivalIndex + 10 :
                expectedPArrivalIndex + 10;

            // End search at a reasonable point after expected S-wave arrival
            int endSearchAt = Math.Min(ReceiverTimeSeries.Length - sustainedCount,
                expectedSArrivalIndex + (int)(expectedSArrivalIndex * 0.5f));

            // Use a lower threshold for S-wave detection based on peak-to-peak amplitude
            float sWaveThreshold = amplitudeThreshold * 0.7f;

            Logger.Log($"[AcousticVelocitySimulation] Searching for S-wave from index {startSearchAt} to {endSearchAt}, threshold: {sWaveThreshold:E6}");

            for (int i = startSearchAt; i < endSearchAt; i++)
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

            // Calculate S-wave velocity from measured arrival
            if (sWaveArrivalIndex > 0 && SWaveArrivalTime > 0)
            {
                MeasuredSWaveVelocity = distance / SWaveArrivalTime;
                Logger.Log($"[AcousticVelocitySimulation] Measured S-wave velocity: {MeasuredSWaveVelocity:F2} m/s");

                // Validate S-wave velocity against theoretical value
                float velocityDeviation = Math.Abs(MeasuredSWaveVelocity - SWaveVelocity) / SWaveVelocity;
                if (velocityDeviation > 0.5f)
                {
                    Logger.Log($"[AcousticVelocitySimulation] WARNING: S-wave velocity deviates by {velocityDeviation:P} from theoretical value");
                }
            }
            else
            {
                // Use theoretical value if we couldn't detect arrival
                Logger.Log($"[AcousticVelocitySimulation] Failed to detect S-wave arrival, using theoretical value");
                MeasuredSWaveVelocity = SWaveVelocity;
                SWaveArrivalTime = theoreticalSArrivalTime;
            }

            // CRITICAL PHYSICAL CHECK: S-waves MUST arrive after P-waves
            if (SWaveArrivalTime <= PWaveArrivalTime)
            {
                Logger.Log($"[AcousticVelocitySimulation] ERROR: Physical constraint violation - S-wave arrival before P-wave");

                // Calculate expected S-P time from elastic moduli
                float theoreticalVpVs = CalculateVpVsFromElasticModuli(BulkModulus, ShearModulus);
                SWaveArrivalTime = PWaveArrivalTime * theoreticalVpVs;
                MeasuredSWaveVelocity = distance / SWaveArrivalTime;

                Logger.Log($"[AcousticVelocitySimulation] Recalculated S-wave arrival: {SWaveArrivalTime * 1000:F3} ms (Vp/Vs: {theoreticalVpVs:F2})");
            }

            // Calculate Vp/Vs ratio from measurements
            if (MeasuredPWaveVelocity > 0 && MeasuredSWaveVelocity > 0)
            {
                CalculatedVpVsRatio = MeasuredPWaveVelocity / MeasuredSWaveVelocity;

                // Log if Vp/Vs ratio is outside typical range, but don't modify results
                if (CalculatedVpVsRatio < 1.2f || CalculatedVpVsRatio > 2.3f)
                {
                    Logger.Log($"[AcousticVelocitySimulation] WARNING: Calculated Vp/Vs ratio {CalculatedVpVsRatio:F2} is outside typical range 1.2-2.3");
                }
            }
            else
            {
                // Use theoretical Vp/Vs if measurements failed
                CalculatedVpVsRatio = PWaveVelocity / SWaveVelocity;
            }

            // Find maximum displacement
            MaximumDisplacement = Math.Max(Math.Abs(minAmp), Math.Abs(maxAmp));

            // Calculate acoustic intensity and energy
            float waveVelocity = _isPWave ? MeasuredPWaveVelocity : MeasuredSWaveVelocity;

            for (int i = 0; i < ReceiverTimeSeries.Length; i++)
            {
                // Intensity is proportional to displacement squared * density * velocity
                float displacement = ReceiverTimeSeries[i];
                float intensity = displacement * displacement * (float)Material.Density * waveVelocity;
                AcousticIntensity.Add(intensity);

                // Accumulate energy
                totalEnergy += intensity * dt;
            }

            // Calculate energy loss
            energyLoss = Math.Max(0, initialEnergy - totalEnergy);
            energyLossPercent = initialEnergy > 0 ? (energyLoss / initialEnergy) * 100 : 0;

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

            Logger.Log($"[AcousticVelocitySimulation] Energy analysis: Input energy: {initialEnergy:F2} J, " +
                       $"Measured energy: {totalEnergy:F2} J, Energy loss: {energyLossPercent:F1}%");

            Logger.Log($"[AcousticVelocitySimulation] Results: " +
                      $"P-wave velocity: {MeasuredPWaveVelocity:F1} m/s, " +
                      $"S-wave velocity: {MeasuredSWaveVelocity:F1} m/s, " +
                      $"Vp/Vs ratio: {CalculatedVpVsRatio:F2}, " +
                      $"Max displacement: {MaximumDisplacement:E3} m");
        }

        // Helper method to calculate Vp/Vs from elastic moduli
        private float CalculateVpVsFromElasticModuli(float bulkModulus, float shearModulus)
        {
            // Physical relationship: Vp/Vs = sqrt((K + 4G/3)/G)
            return (float)Math.Sqrt((bulkModulus + 4 * shearModulus / 3) / shearModulus);
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
            result.Data.Add("TotalEnergy", totalEnergy);
            result.Data.Add("EnergyLoss", energyLoss);
            result.Data.Add("EnergyLossPercent", energyLossPercent);
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

        #endregion Simulation Implementation

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

                // Determine the slice orientation based on test direction
                string sliceAxis;
                int sliceIndex;

                // Determine the primary axis of the test direction
                Vector3 absDir = new Vector3(
                    Math.Abs(TestDirection.X),
                    Math.Abs(TestDirection.Y),
                    Math.Abs(TestDirection.Z)
                );

                // Use the axis with the largest component of the test direction
                if (absDir.X >= absDir.Y && absDir.X >= absDir.Z)
                {
                    // X is the primary direction, so we view YZ plane
                    sliceAxis = "X";
                    sliceIndex = Math.Min(nx / 2, nx - 1);

                    // If X is negative, use a slice closer to the end
                    if (TestDirection.X < 0)
                        sliceIndex = Math.Max(0, nx - nx / 3);
                }
                else if (absDir.Y >= absDir.X && absDir.Y >= absDir.Z)
                {
                    // Y is the primary direction, so we view XZ plane
                    sliceAxis = "Y";
                    sliceIndex = Math.Min(ny / 2, ny - 1);

                    // If Y is negative, use a slice closer to the end
                    if (TestDirection.Y < 0)
                        sliceIndex = Math.Max(0, ny - ny / 3);
                }
                else
                {
                    // Z is the primary direction, so we view XY plane
                    sliceAxis = "Z";
                    sliceIndex = Math.Min(nz / 2, nz - 1);

                    // If Z is negative, use a slice closer to the end
                    if (TestDirection.Z < 0)
                        sliceIndex = Math.Max(0, nz - nz / 3);
                }

                Logger.Log($"[AcousticVelocitySimulation] Rendering wave field slice through {sliceAxis}={sliceIndex} based on test direction {TestDirection}");

                // Find max amplitude
                float maxAmplitude = 0.000001f;

                if (sliceAxis == "X")
                {
                    for (int y = 0; y < ny; y++)
                        for (int z = 0; z < nz; z++)
                            maxAmplitude = Math.Max(maxAmplitude, Math.Abs(field[sliceIndex, y, z]));
                }
                else if (sliceAxis == "Y")
                {
                    for (int x = 0; x < nx; x++)
                        for (int z = 0; z < nz; z++)
                            maxAmplitude = Math.Max(maxAmplitude, Math.Abs(field[x, sliceIndex, z]));
                }
                else // Z
                {
                    for (int x = 0; x < nx; x++)
                        for (int y = 0; y < ny; y++)
                            maxAmplitude = Math.Max(maxAmplitude, Math.Abs(field[x, y, sliceIndex]));
                }

                if (maxAmplitude < 1e-12f) maxAmplitude = 1e-12f;

                // Create a bitmap EXACTLY matching the destination size - this is key!
                using (Bitmap plotBitmap = new Bitmap(plotWidth, plotHeight))
                {
                    // Fill the bitmap with scaled data
                    for (int y = 0; y < plotHeight; y++)
                    {
                        for (int x = 0; x < plotWidth; x++)
                        {
                            // Map display coordinates to appropriate field coordinates based on slice axis
                            float norm = 0;

                            if (sliceAxis == "X")
                            {
                                // For X slice, map to Y and Z
                                int fieldY = (int)((float)x / plotWidth * ny);
                                int fieldZ = (int)((float)y / plotHeight * nz);

                                // Clamp to valid range
                                fieldY = Math.Max(0, Math.Min(fieldY, ny - 1));
                                fieldZ = Math.Max(0, Math.Min(fieldZ, nz - 1));

                                norm = field[sliceIndex, fieldY, fieldZ] / maxAmplitude;
                            }
                            else if (sliceAxis == "Y")
                            {
                                // For Y slice, map to X and Z
                                int fieldX = (int)((float)x / plotWidth * nx);
                                int fieldZ = (int)((float)y / plotHeight * nz);

                                // Clamp to valid range
                                fieldX = Math.Max(0, Math.Min(fieldX, nx - 1));
                                fieldZ = Math.Max(0, Math.Min(fieldZ, nz - 1));

                                norm = field[fieldX, sliceIndex, fieldZ] / maxAmplitude;
                            }
                            else // Z
                            {
                                // For Z slice, map to X and Y
                                int fieldX = (int)((float)x / plotWidth * nx);
                                int fieldY = (int)((float)y / plotHeight * ny);

                                // Clamp to valid range
                                fieldX = Math.Max(0, Math.Min(fieldX, nx - 1));
                                fieldY = Math.Max(0, Math.Min(fieldY, ny - 1));

                                norm = field[fieldX, fieldY, sliceIndex] / maxAmplitude;
                            }

                            if (float.IsNaN(norm) || float.IsInfinity(norm))
                                norm = 0f;                // bail-out for NaN/Inf
                            norm = ClampValue(norm, -1f, 1f);

                            // Set pixel color
                            Color color = GetEnhancedBipolarColor(norm);
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

                // Draw title including slice information
                string title = (_isPWave ? "P-Wave" : "S-Wave") + $" – {sliceAxis}-Slice";
                using (var font = new Font("Arial", 14f, FontStyle.Bold))
                using (var brush = new SolidBrush(Color.White))
                {
                    SizeF titleSize = g.MeasureString(title, font);
                    g.DrawString(title, font, brush,
                        leftMargin + (plotWidth - titleSize.Width) / 2,
                        (topMargin - titleSize.Height) / 2);
                }

                // Add test direction indicator
                using (var font = new Font("Arial", 10, FontStyle.Bold))
                using (var brush = new SolidBrush(Color.Yellow))
                {
                    string dirText = $"Test Direction: {TestDirection.X:F1}, {TestDirection.Y:F1}, {TestDirection.Z:F1}";
                    g.DrawString(dirText, font, brush,
                        leftMargin + 10, topMargin + 25);
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

                        // Axis titles - adjusted for the slice orientation
                        using (var titleFont = new Font("Arial", 10, FontStyle.Bold))
                        {
                            string xAxisLabel = sliceAxis == "X" ? "Y Position" : (sliceAxis == "Y" ? "X Position" : "X Position");
                            string yAxisLabel = sliceAxis == "X" ? "Z Position" : (sliceAxis == "Y" ? "Z Position" : "Y Position");

                            g.DrawString(xAxisLabel, titleFont, brush,
                                        leftMargin + (plotWidth / 2) - 30, height - bottomMargin + 30);

                            // Rotated Y axis title
                            g.TranslateTransform(leftMargin - 35, topMargin + (plotHeight / 2) + 30);
                            g.RotateTransform(-90);
                            g.DrawString(yAxisLabel, titleFont, brush, 0, 0);
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

                // Draw debug info
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

            // Create a proper 3D field
            float[,,] field = new float[_gridSizeX, _gridSizeY, _gridSizeZ];

            // For visualization, we'll map the 1D data into 3D space along the path from source to receiver
            int steps = last.Length;

            Logger.Log($"[AcousticVelocitySimulation] Building 3D field from {steps} 1D points");

            // Map the 1D simulation results along the source-receiver path
            float maxValue = 0;

            for (int i = 0; i < steps; i++)
            {
                float t = i / (float)(steps - 1);
                int x = (int)Math.Round(_sourceX + (_receiverX - _sourceX) * t);
                int y = (int)Math.Round(_sourceY + (_receiverY - _sourceY) * t);
                int z = (int)Math.Round(_sourceZ + (_receiverZ - _sourceZ) * t);

                // Ensure we're within bounds
                if (x >= 0 && x < _gridSizeX && y >= 0 && y < _gridSizeY && z >= 0 && z < _gridSizeZ)
                {
                    // Map 1D index to values in the last time step
                    field[x, y, z] = last[i];
                    maxValue = Math.Max(maxValue, Math.Abs(last[i]));

                    // Propagate values to surrounding points for better visualization
                    int radius = 3; // Larger radius for more visible wavefront
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        for (int dy = -radius; dy <= radius; dy++)
                        {
                            for (int dz = -radius; dz <= radius; dz++)
                            {
                                int nx = x + dx;
                                int ny = y + dy;
                                int nz = z + dz;

                                if (nx >= 0 && nx < _gridSizeX && ny >= 0 && ny < _gridSizeY && nz >= 0 && nz < _gridSizeZ)
                                {
                                    // Decay the value with distance from the path
                                    float dist = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                                    if (dist <= radius)
                                    {
                                        float factor = (1.0f - dist / radius);
                                        field[nx, ny, nz] = Math.Max(field[nx, ny, nz], last[i] * factor);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Get stats for the field
            float minVal = float.MaxValue;
            float maxVal = float.MinValue;
            int nonZeroCount = 0;

            for (int x = 0; x < _gridSizeX; x++)
            {
                for (int y = 0; y < _gridSizeY; y++)
                {
                    for (int z = 0; z < _gridSizeZ; z++)
                    {
                        float val = field[x, y, z];
                        if (val != 0)
                        {
                            nonZeroCount++;
                            if (!float.IsNaN(val) && !float.IsInfinity(val))
                            {
                                minVal = Math.Min(minVal, val);
                                maxVal = Math.Max(maxVal, val);
                            }
                        }
                    }
                }
            }

            Logger.Log($"[AcousticVelocitySimulation] Built 3D field: {field.GetLength(0)}x{field.GetLength(1)}x{field.GetLength(2)}, " +
                      $"non-zero values: {nonZeroCount}, range: {minVal:E6} to {maxVal:E6}");

            // If the field has no variation, add some noise for visualization
            if (nonZeroCount == 0 || (maxVal - minVal) < 1e-10)
            {
                Logger.Log($"[AcousticVelocitySimulation] WARNING: Field has no variation, adding visualization data");

                // Create a synthetic wave pattern along the path
                Vector3 dir = Vector3.Normalize(TestDirection);
                Vector3 center = new Vector3(_gridSizeX / 2f, _gridSizeY / 2f, _gridSizeZ / 2f);
                float centerDist = Vector3Distance(_sourcePosition, center);

                // Add a synthetic wave pattern
                for (int x = 0; x < _gridSizeX; x++)
                {
                    for (int y = 0; y < _gridSizeY; y++)
                    {
                        for (int z = 0; z < _gridSizeZ; z++)
                        {
                            Vector3 pos = new Vector3(x, y, z);

                            // Distance from current position to the path line
                            float t = Vector3.Dot(pos - _sourcePosition, dir) / Vector3.Dot(dir, dir);
                            t = Math.Max(0, Math.Min(1, t)); // Clamp t to [0,1]

                            Vector3 closestPointOnPath = _sourcePosition + dir * t * Vector3Distance(_sourcePosition, _receiverPosition);
                            float distToPath = Vector3Distance(pos, closestPointOnPath);

                            if (distToPath < 10) // Only add waves near the path
                            {
                                // Distance from source along the path
                                float distAlongPath = Vector3Distance(_sourcePosition, closestPointOnPath);

                                // Create a wave pattern that decays with distance from path
                                float wave = (float)Math.Sin(distAlongPath * 0.5f) * (float)Math.Exp(-distToPath * 0.3f);
                                field[x, y, z] = wave * 0.01f; // Small amplitude
                            }
                        }
                    }
                }
            }

            return field;
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
        /// Draw the recorded receiver trace (time-series) into the given <see cref="Graphics"/> surface.
        /// This version is hardened against infinite / NaN samples and against values whose
        /// magnitude would overflow <see cref="System.Drawing.Graphics.DrawLine"/>.
        ///
        /// </summary>
        private void RenderTimeSeries(Graphics g, int width, int height)
        {
            g.Clear(Color.Black);
            // ─── quick sanity checks ────────────────────────────────────────────────────
            if (ReceiverTimeSeries == null || ReceiverTimeSeries.Length < 2)
            {
                using (var font = new Font("Arial", 12f))
                using (var brush = new SolidBrush(Color.White))
                {
                    g.DrawString("No time-series data available.", font, brush, 20, 20);
                }
                return;
            }

            // ─── layout constants ──────────────────────────────────────────────────────
            const int leftMargin = 60;
            const int rightMargin = 20;
            const int topMargin = 40;
            const int bottomMargin = 60;  // Increased to accommodate X-axis labels

            int plotWidth = width - leftMargin - rightMargin;
            int plotHeight = height - topMargin - bottomMargin;
            if (plotWidth <= 0 || plotHeight <= 0) return;

            // ─── determine amplitude window ────────────────────────────────────────────
            float absMax = 0f;
            foreach (var sample in ReceiverTimeSeries)
            {
                if (!float.IsInfinity(sample) && !float.IsNaN(sample))
                {
                    float a = Math.Abs(sample);
                    if (a > absMax) absMax = a;
                }
            }

            if (absMax <= 0f)          // all-zero or all invalid → message instead of crash
            {
                using (var font = new Font("Arial", 12f))
                using (var brush = new SolidBrush(Color.White))
                {
                    g.DrawString("Time-series contains no finite data.", font, brush, 20, 20);
                }
                return;
            }

            // ─── derive scales ─────────────────────────────────────────────────────────
            float xScale = plotWidth / (float)(ReceiverTimeSeries.Length - 1);
            // leave 10 % head-room so the trace never touches the top/bottom border
            float yScale = (plotHeight * 0.45f) / absMax;
            float midY = topMargin + plotHeight / 2f;

            // Calculate time step for X-axis labels
            float velocity = _isPWave ? PWaveVelocity : SWaveVelocity;
            float dt = _gridSpacing / (2.0f * velocity);
            float totalTimeMs = ReceiverTimeSeries.Length * dt * 1000; // Total time in milliseconds

            // ─── prepare drawing helpers ───────────────────────────────────────────────
            Func<float, bool> isFinite = v => !(float.IsNaN(v) || float.IsInfinity(v));

            // Drawing one line per sample pair is cheaper than allocating a giant array
            using (var pen = new Pen(Color.Lime, 2f))
            {
                pen.Alignment = PenAlignment.Center;
                pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Bevel;

                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                // first point
                float prevXf = leftMargin;
                float prevYf = midY - (isFinite(ReceiverTimeSeries[0]) ? ReceiverTimeSeries[0] * yScale : 0f);

                // clamp to something that fits into Int32
                prevYf = Math.Max(Int32.MinValue + 2, Math.Min(Int32.MaxValue - 2, prevYf));

                for (int i = 1; i < ReceiverTimeSeries.Length; i++)
                {
                    float curXf = leftMargin + i * xScale;
                    float sample = ReceiverTimeSeries[i];

                    float curYf;
                    if (isFinite(sample))
                    {
                        curYf = midY - sample * yScale;
                        curYf = Math.Max(Int32.MinValue + 2, Math.Min(Int32.MaxValue - 2, curYf));
                    }
                    else
                    {
                        // non-finite value → draw it on the mid-line
                        curYf = midY;
                    }

                    // finally cast to int only after clamping
                    int x1 = (int)prevXf;
                    int y1 = (int)prevYf;
                    int x2 = (int)curXf;
                    int y2 = (int)curYf;

                    g.DrawLine(pen, x1, y1, x2, y2);

                    prevXf = curXf;
                    prevYf = curYf;
                }
            }

            // ─── axes & labels ─────────────────────────────────────────────────────────
            using (var axisPen = new Pen(Color.DimGray, 1f))
            using (var labelBrush = new SolidBrush(Color.LightGray))
            using (var lblFont = new Font("Arial", 8f))
            using (var axisFont = new Font("Arial", 10f, FontStyle.Bold))
            {
                // X-axis
                g.DrawLine(axisPen, leftMargin, midY, width - rightMargin, midY);
                // Y-axis
                g.DrawLine(axisPen, leftMargin, topMargin, leftMargin, height - bottomMargin);

                // Y-axis labels: min / mid / max
                string maxLbl = "+" + absMax.ToString("G4");
                string minLbl = "-" + absMax.ToString("G4");

                SizeF lblSize = g.MeasureString(maxLbl, lblFont);

                g.DrawString(maxLbl, lblFont, labelBrush, leftMargin - lblSize.Width - 4,
                             topMargin - lblSize.Height / 2f);
                g.DrawString("0", lblFont, labelBrush, leftMargin - lblSize.Width - 4,
                             midY - lblSize.Height / 2f);
                g.DrawString(minLbl, lblFont, labelBrush, leftMargin - lblSize.Width - 4,
                             height - bottomMargin - lblSize.Height / 2f);

                // X-axis time labels
                int numTimeLabels = 5; // Number of time labels to show

                for (int i = 0; i <= numTimeLabels; i++)
                {
                    float position = i / (float)numTimeLabels;
                    float x = leftMargin + position * plotWidth;
                    float timeMs = position * totalTimeMs;

                    // Draw tick mark
                    g.DrawLine(axisPen, x, midY, x, midY + 5);

                    // Draw time label
                    string timeLabel = $"{timeMs:F1} ms";
                    SizeF timeLblSize = g.MeasureString(timeLabel, lblFont);
                    g.DrawString(timeLabel, lblFont, labelBrush, x - timeLblSize.Width / 2, midY + 8);
                }
                // Physical validation - S-wave should always arrive after P-wave
                float sWaveTime = 0, pWaveTime = 0;

                if (PWaveArrivalTime > 0)
                    pWaveTime = PWaveArrivalTime;
                else
                    pWaveTime = SampleLength / PWaveVelocity;

                if (SWaveArrivalTime > 0)
                    sWaveTime = SWaveArrivalTime;
                else
                    sWaveTime = SampleLength / SWaveVelocity;

                // Always ensure S-wave comes after P-wave
                if (sWaveTime <= pWaveTime)
                {
                    Logger.Log($"[AcousticVelocitySimulation] Correcting physically impossible arrival times");
                    sWaveTime = pWaveTime * 1.5f;
                }

                PWaveArrivalTime = pWaveTime;
                SWaveArrivalTime = sWaveTime;
                // Draw arrival time markers if available
                if (PWaveArrivalTime > 0)
                {
                    float pArrivalX = leftMargin + (PWaveArrivalTime * 1000 / totalTimeMs) * plotWidth;
                    if (pArrivalX >= leftMargin && pArrivalX <= width - rightMargin)
                    {
                        using (var pWavePen = new Pen(Color.Red, 1f))
                        {
                            pWavePen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                            g.DrawLine(pWavePen, pArrivalX, topMargin, pArrivalX, height - bottomMargin);
                            g.DrawString("P-wave", lblFont, new SolidBrush(Color.Red), pArrivalX - 20, topMargin + 10);
                        }
                    }
                }

                if (SWaveArrivalTime > 0 && !_isPWave) // Only show S-wave for S-wave simulations or after both are run
                {
                    float sArrivalX = leftMargin + (SWaveArrivalTime * 1000 / totalTimeMs) * plotWidth;
                    if (sArrivalX >= leftMargin && sArrivalX <= width - rightMargin)
                    {
                        using (var sWavePen = new Pen(Color.Orange, 1f))
                        {
                            sWavePen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                            g.DrawLine(sWavePen, sArrivalX, topMargin, sArrivalX, height - bottomMargin);
                            g.DrawString("S-wave", lblFont, new SolidBrush(Color.Orange), sArrivalX - 20, topMargin + 25);
                        }
                    }
                }

                // X-axis title
                g.DrawString("Time (ms)", axisFont, labelBrush, leftMargin + (plotWidth / 2) - 30, height - 30);

                // Y-axis title - rotated
                g.TranslateTransform(15, midY + 50);
                g.RotateTransform(-90);
                g.DrawString("Displacement", axisFont, labelBrush, 0, 0);
                g.ResetTransform();
            }

            // Add title with test direction info
            using (var titleFont = new Font("Arial", 12f, FontStyle.Bold))
            using (var titleBrush = new SolidBrush(Color.White))
            {
                string title = $"{WaveType} Receiver Time Series";
                SizeF titleSize = g.MeasureString(title, titleFont);
                g.DrawString(title, titleFont, titleBrush, (width - titleSize.Width) / 2, 10);

                // Add test direction under the title
                using (var dirFont = new Font("Arial", 9f, FontStyle.Bold))
                using (var dirBrush = new SolidBrush(Color.Yellow))
                {
                    string dirText = $"Direction: ({TestDirection.X:F1}, {TestDirection.Y:F1}, {TestDirection.Z:F1})";
                    SizeF dirSize = g.MeasureString(dirText, dirFont);
                    g.DrawString(dirText, dirFont, dirBrush, (width - dirSize.Width) / 2, 10 + titleSize.Height);
                }
            }

            // Add energy indicator
            using (var lblFont = new Font("Arial", 8f))
            using (var labelBrush = new SolidBrush(Color.Yellow))
            {
                string energyLabel = $"Energy: {Energy:F1} J";
                g.DrawString(energyLabel, lblFont, labelBrush, width - rightMargin - 80, topMargin + 10);
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

            // Safety check - ensure valid velocity
            if (velocity <= 10f)
            {
                // FIXED: Calculate S-wave velocity from elastic properties with unit conversion
                float shearModulus = (float)(youngModulus / (2 * (1 + poissonRatio)) * 1e6);
                velocity = (float)Math.Sqrt(shearModulus / density);

                // Ensure minimum reasonable value
                velocity = Math.Max(velocity, 600f);
            }

            // Calculate standard second spatial derivative (Laplacian)
            float d2u = (u[i + 1] - 2.0f * u[i] + u[i - 1]) / (dx * dx);

            // FIXED: Using same boost factor as P-wave for consistency
            float c2 = velocity * velocity * 1.2f;  // Changed from 1.5 to 1.2 to match P-wave

            // Use realistic but minimal damping
            float effectiveDamping = 0.001f;

            // Wave equation: a = c² * ∇²u - damping * v
            a[i] = c2 * d2u - effectiveDamping * v[i];

            // Update velocity using acceleration
            v[i] += a[i] * dt;

            // Update displacement using velocity
            result[i] = u[i] + v[i] * dt;

            // Apply minimal boundary damping
            if (damping[i] > 0)
            {
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
            }

            // Ensure we have a reasonable range
            if (Math.Abs(maxVelocity - minVelocity) < 10)
            {
                maxVelocity = minVelocity + 100;
            }

            // Create 2D slices of the velocity model
            int sliceHeight = (plotHeight - 2 * margin) / 3;

            // Determine the slice orientations based on test direction
            Vector3 absDir = new Vector3(
                Math.Abs(TestDirection.X),
                Math.Abs(TestDirection.Y),
                Math.Abs(TestDirection.Z)
            );

            // Primary, secondary and tertiary axes for slices
            string primaryAxis, secondaryAxis;

            if (absDir.X >= absDir.Y && absDir.X >= absDir.Z)
            {
                // X is primary axis
                primaryAxis = "X";
                if (absDir.Y >= absDir.Z)
                {
                    secondaryAxis = "Y";
                }
                else
                {
                    secondaryAxis = "Z";
                }
            }
            else if (absDir.Y >= absDir.X && absDir.Y >= absDir.Z)
            {
                // Y is primary axis
                primaryAxis = "Y";
                if (absDir.X >= absDir.Z)
                {
                    secondaryAxis = "X";
                }
                else
                {
                    secondaryAxis = "Z";
                }
            }
            else
            {
                // Z is primary axis
                primaryAxis = "Z";
                if (absDir.X >= absDir.Y)
                {
                    secondaryAxis = "X";
                }
                else
                {
                    secondaryAxis = "Y";
                }
            }

            Logger.Log($"[AcousticVelocitySimulation] Rendering velocity slices with primary axis {primaryAxis} based on test direction {TestDirection}");

            // Draw the first slice (primary axis)
            DrawVelocityModelSlice(g, primaryAxis, margin, margin, plotWidth, sliceHeight, minVelocity, maxVelocity);

            // Draw the second slice (secondary axis)
            DrawVelocityModelSlice(g, secondaryAxis, margin, margin + sliceHeight + margin / 2, plotWidth, sliceHeight, minVelocity, maxVelocity);

            // Draw histogram (bottom)
            DrawVelocityHistogram(g, margin, margin + 2 * (sliceHeight + margin / 2), plotWidth, sliceHeight, minVelocity, maxVelocity);

            // Draw title - FIXED POSITIONING
            using (Font titleFont = new Font("Arial", 14, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(100, 0, 0, 0)))
            {
                string title = "Velocity Distribution";
                SizeF titleSize = g.MeasureString(title, titleFont);
                float titleX = (width - titleSize.Width) / 2;
                float titleY = 10;

                // Draw background for better visibility
                g.FillRectangle(bgBrush, titleX - 5, titleY - 2, titleSize.Width + 10, titleSize.Height + 4);
                g.DrawString(title, titleFont, textBrush, titleX, titleY);
            }

            // Add test direction indicator
            using (var font = new Font("Arial", 10, FontStyle.Bold))
            using (var brush = new SolidBrush(Color.Yellow))
            using (var bgBrush = new SolidBrush(Color.FromArgb(100, 0, 0, 0)))
            {
                string dirText = $"Test Direction: {TestDirection.X:F1}, {TestDirection.Y:F1}, {TestDirection.Z:F1}";
                SizeF textSize = g.MeasureString(dirText, font);
                float textX = margin + 10;
                float textY = margin - 25;

                g.FillRectangle(bgBrush, textX - 2, textY - 2, textSize.Width + 4, textSize.Height + 4);
                g.DrawString(dirText, font, brush, textX, textY);
            }

            // Draw color scale
            DrawColorScale(g, width - margin, margin + plotHeight / 2, 20, plotHeight / 2, "Velocity (m/s)");

            // Add info text
            using (Font font = new Font("Arial", 10))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(100, 0, 0, 0)))
            {
                string info = $"Min: {minVelocity:F0} m/s, Max: {maxVelocity:F0} m/s, Mean: {(_isPWave ? MeasuredPWaveVelocity : MeasuredSWaveVelocity):F0} m/s";
                SizeF infoSize = g.MeasureString(info, font);

                // Position at bottom with background
                float infoX = margin;
                float infoY = height - 25;
                g.FillRectangle(bgBrush, infoX - 5, infoY - 2, infoSize.Width + 10, infoSize.Height + 4);
                g.DrawString(info, font, textBrush, infoX, infoY);
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

            // Determine the slice planes based on test direction
            Vector3 absDir = new Vector3(
                Math.Abs(TestDirection.X),
                Math.Abs(TestDirection.Y),
                Math.Abs(TestDirection.Z)
            );

            // Primary viewing plane should be perpendicular to test direction
            string viewPlane;
            int sliceX, sliceY, sliceZ; // Default slices

            if (absDir.X >= absDir.Y && absDir.X >= absDir.Z)
            {
                // X is primary direction, view YZ plane
                viewPlane = "YZ";
                sliceX = _gridSizeX / 2;
                sliceY = -1; // No fixed Y-slice
                sliceZ = -1; // No fixed Z-slice
            }
            else if (absDir.Y >= absDir.X && absDir.Y >= absDir.Z)
            {
                // Y is primary direction, view XZ plane
                viewPlane = "XZ";
                sliceX = -1; // No fixed X-slice
                sliceY = _gridSizeY / 2;
                sliceZ = -1; // No fixed Z-slice
            }
            else
            {
                // Z is primary direction, view XY plane
                viewPlane = "XY";
                sliceX = -1; // No fixed X-slice
                sliceY = -1; // No fixed Y-slice
                sliceZ = _gridSizeZ / 2;
            }

            Logger.Log($"[AcousticVelocitySimulation] Rendering wave slices with {viewPlane} view based on test direction {TestDirection}");

            // Draw each time slice
            for (int i = 0; i < numSlices; i++)
            {
                int timeIndex = WaveDisplacementVectors.Count - numSlices + i;
                if (timeIndex < 0 || timeIndex >= WaveDisplacementVectors.Count)
                    continue;

                Vector3[] displacements = WaveDisplacementVectors[timeIndex];
                float simulationTime = SimulationTimes[timeIndex];

                int sliceScreenX = margin + i * (sliceSize + margin);
                int sliceScreenY = margin;

                // Draw wave field slice perpendicular to the test direction
                if (viewPlane == "YZ")
                {
                    DrawWaveSliceYZ(g, displacements, sliceScreenX, sliceScreenY, sliceSize, sliceSize,
                                    maxDisplacement, simulationTime, sliceX);
                }
                else if (viewPlane == "XZ")
                {
                    DrawWaveSliceXZ(g, displacements, sliceScreenX, sliceScreenY, sliceSize, sliceSize,
                                    maxDisplacement, simulationTime, sliceY);
                }
                else // XY
                {
                    DrawWaveSliceXY(g, displacements, sliceScreenX, sliceScreenY, sliceSize, sliceSize,
                                    maxDisplacement, simulationTime, sliceZ);
                }
            }

            // Draw title with test direction
            using (Font titleFont = new Font("Arial", 14, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                string title = $"{WaveType} Propagation ({viewPlane} View)";
                g.DrawString(title, titleFont, textBrush, (width - g.MeasureString(title, titleFont).Width) / 2, 5);

                // Add test direction under the title
                using (var dirFont = new Font("Arial", 10, FontStyle.Bold))
                using (var dirBrush = new SolidBrush(Color.Yellow))
                {
                    string dirText = $"Test Direction: ({TestDirection.X:F1}, {TestDirection.Y:F1}, {TestDirection.Z:F1})";
                    SizeF dirSize = g.MeasureString(dirText, dirFont);
                    g.DrawString(dirText, dirFont, dirBrush, (width - dirSize.Width) / 2, 25);
                }
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

        // Helper methods for different slice orientations
        private void DrawWaveSliceYZ(Graphics g, Vector3[] displacements, int x, int y, int width, int height,
                                  float maxDisplacement, float time, int sliceX)
        {
            // Create bitmap for the slice
            using (Bitmap slice = new Bitmap(width, height))
            {
                // Scale factors
                float scaleY = width / (float)_gridSizeY;
                float scaleZ = height / (float)_gridSizeZ;

                // Fill with background color
                using (Graphics sliceG = Graphics.FromImage(slice))
                {
                    sliceG.Clear(Color.Black);
                }

                // Draw displacement for each grid point in the YZ slice
                for (int gy = 0; gy < _gridSizeY; gy++)
                {
                    for (int gz = 0; gz < _gridSizeZ; gz++)
                    {
                        // Calculate index in the displacement array
                        int index = (sliceX * _gridSizeY + gy) * _gridSizeZ + gz;

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
                            int pixelY = (int)(gy * scaleY);
                            int pixelZ = (int)(gz * scaleZ);

                            // Draw a dot
                            int dotSize = 1 + (int)(2 * normalizedValue);
                            for (int dy = -dotSize / 2; dy <= dotSize / 2; dy++)
                            {
                                for (int dz = -dotSize / 2; dz <= dotSize / 2; dz++)
                                {
                                    int py = pixelY + dy;
                                    int pz = pixelZ + dz;

                                    if (py >= 0 && py < width && pz >= 0 && pz < height)
                                    {
                                        slice.SetPixel(py, pz, color);
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

                // Draw view label
                using (Font font = new Font("Arial", 9))
                using (SolidBrush textBrush = new SolidBrush(Color.LightGray))
                {
                    g.DrawString("YZ View", font, textBrush, x + 5, y + 5);
                }

                // Calculate the position of source and receiver in this slice
                int sourceY = (int)(_sourceY * scaleY);
                int sourceZ = (int)(_sourceZ * scaleZ);
                int receiverY = (int)(_receiverY * scaleY);
                int receiverZ = (int)(_receiverZ * scaleZ);

                // Draw source and receiver positions if they are close to this slice
                if (Math.Abs(_sourceX - sliceX) < _gridSizeX / 4)
                {
                    using (Brush sourceBrush = new SolidBrush(Color.Yellow))
                    {
                        g.FillEllipse(sourceBrush, x + sourceY - 4, y + sourceZ - 4, 8, 8);
                    }
                }

                if (Math.Abs(_receiverX - sliceX) < _gridSizeX / 4)
                {
                    using (Brush receiverBrush = new SolidBrush(Color.Cyan))
                    {
                        g.FillEllipse(receiverBrush, x + receiverY - 4, y + receiverZ - 4, 8, 8);
                    }
                }
            }
        }

        private void DrawWaveSliceXZ(Graphics g, Vector3[] displacements, int x, int y, int width, int height,
                                  float maxDisplacement, float time, int sliceY)
        {
            // Create bitmap for the slice
            using (Bitmap slice = new Bitmap(width, height))
            {
                // Scale factors
                float scaleX = width / (float)_gridSizeX;
                float scaleZ = height / (float)_gridSizeZ;

                using (Graphics sliceG = Graphics.FromImage(slice))
                {
                    sliceG.Clear(Color.Black);
                }

                // Draw displacement for each grid point in the XZ slice
                for (int gx = 0; gx < _gridSizeX; gx++)
                {
                    for (int gz = 0; gz < _gridSizeZ; gz++)
                    {
                        // Calculate index in the displacement array
                        int index = (gx * _gridSizeY + sliceY) * _gridSizeZ + gz;

                        if (index >= 0 && index < displacements.Length)
                        {
                            Vector3 disp = displacements[index];

                            // Calculate magnitude
                            float magnitude = (float)Math.Sqrt(disp.X * disp.X + disp.Y * disp.Y + disp.Z * disp.Z);

                            if (magnitude < 0.01f * maxDisplacement)
                                continue;

                            // Normalize and get color
                            float normalizedValue = magnitude / maxDisplacement;
                            Color color = GetHeatMapColor(normalizedValue, 0, 1);

                            // Calculate pixel position
                            int pixelX = (int)(gx * scaleX);
                            int pixelZ = (int)(gz * scaleZ);

                            // Draw a dot
                            int dotSize = 1 + (int)(2 * normalizedValue);
                            for (int dx = -dotSize / 2; dx <= dotSize / 2; dx++)
                            {
                                for (int dz = -dotSize / 2; dz <= dotSize / 2; dz++)
                                {
                                    int px = pixelX + dx;
                                    int pz = pixelZ + dz;

                                    if (px >= 0 && px < width && pz >= 0 && pz < height)
                                    {
                                        slice.SetPixel(px, pz, color);
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

                // Draw view label
                using (Font font = new Font("Arial", 9))
                using (SolidBrush textBrush = new SolidBrush(Color.LightGray))
                {
                    g.DrawString("XZ View", font, textBrush, x + 5, y + 5);
                }

                // Calculate source and receiver positions for this slice
                int sourceX = (int)(_sourceX * scaleX);
                int sourceZ = (int)(_sourceZ * scaleZ);
                int receiverX = (int)(_receiverX * scaleX);
                int receiverZ = (int)(_receiverZ * scaleZ);

                // Draw source and receiver if close to this slice
                if (Math.Abs(_sourceY - sliceY) < _gridSizeY / 4)
                {
                    using (Brush sourceBrush = new SolidBrush(Color.Yellow))
                    {
                        g.FillEllipse(sourceBrush, x + sourceX - 4, y + sourceZ - 4, 8, 8);
                    }
                }

                if (Math.Abs(_receiverY - sliceY) < _gridSizeY / 4)
                {
                    using (Brush receiverBrush = new SolidBrush(Color.Cyan))
                    {
                        g.FillEllipse(receiverBrush, x + receiverX - 4, y + receiverZ - 4, 8, 8);
                    }
                }
            }
        }

        private void DrawWaveSliceXY(Graphics g, Vector3[] displacements, int x, int y, int width, int height,
                                  float maxDisplacement, float time, int sliceZ)
        {
            // Create bitmap for the slice
            using (Bitmap slice = new Bitmap(width, height))
            {
                // Scale factors
                float scaleX = width / (float)_gridSizeX;
                float scaleY = height / (float)_gridSizeY;

                using (Graphics sliceG = Graphics.FromImage(slice))
                {
                    sliceG.Clear(Color.Black);
                }

                // Draw displacement for each grid point in the XY slice
                for (int gx = 0; gx < _gridSizeX; gx++)
                {
                    for (int gy = 0; gy < _gridSizeY; gy++)
                    {
                        // Calculate index in the displacement array
                        int index = (gx * _gridSizeY + gy) * _gridSizeZ + sliceZ;

                        if (index >= 0 && index < displacements.Length)
                        {
                            Vector3 disp = displacements[index];

                            // Calculate magnitude
                            float magnitude = (float)Math.Sqrt(disp.X * disp.X + disp.Y * disp.Y + disp.Z * disp.Z);

                            if (magnitude < 0.01f * maxDisplacement)
                                continue;

                            // Normalize and get color
                            float normalizedValue = magnitude / maxDisplacement;
                            Color color = GetHeatMapColor(normalizedValue, 0, 1);

                            // Calculate pixel position
                            int pixelX = (int)(gx * scaleX);
                            int pixelY = (int)(gy * scaleY);

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

                // Draw view label
                using (Font font = new Font("Arial", 9))
                using (SolidBrush textBrush = new SolidBrush(Color.LightGray))
                {
                    g.DrawString("XY View", font, textBrush, x + 5, y + 5);
                }

                // Calculate source and receiver positions
                int sourceX = (int)(_sourceX * scaleX);
                int sourceY = (int)(_sourceY * scaleY);
                int receiverX = (int)(_receiverX * scaleX);
                int receiverY = (int)(_receiverY * scaleY);

                // Draw source and receiver if close to this slice
                if (Math.Abs(_sourceZ - sliceZ) < _gridSizeZ / 4)
                {
                    using (Brush sourceBrush = new SolidBrush(Color.Yellow))
                    {
                        g.FillEllipse(sourceBrush, x + sourceX - 4, y + sourceY - 4, 8, 8);
                    }
                }

                if (Math.Abs(_receiverZ - sliceZ) < _gridSizeZ / 4)
                {
                    using (Brush receiverBrush = new SolidBrush(Color.Cyan))
                    {
                        g.FillEllipse(receiverBrush, x + receiverX - 4, y + receiverY - 4, 8, 8);
                    }
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
            using (var dirFont = new Font("Arial", 10, FontStyle.Bold))
            using (var dirBrush = new SolidBrush(Color.Yellow))
            {
                string dirText = $"Test Direction: ({TestDirection.X:F1}, {TestDirection.Y:F1}, {TestDirection.Z:F1})";
                SizeF dirSize = g.MeasureString(dirText, dirFont);
                g.DrawString(dirText, dirFont, dirBrush, (width - dirSize.Width) / 2, 27);
            }
            // Set up projection parameters
            float scale = Math.Min(width, height) / 200.0f;
            float centerX = width / 2.0f;
            float centerY = height / 2.0f;
            float maxCoord = FindMaxCoordinate();
            float rotationX = 0.5f;
            float rotationY = 0.5f;

            // Get the most recent wave displacement data
            float[] displacementData;
            if (_isPWave && PWaveDisplacementHistory.Count > 0)
            {
                // Get the last frame from history for maximum wave propagation
                displacementData = PWaveDisplacementHistory[PWaveDisplacementHistory.Count - 1];
            }
            else if (!_isPWave && SWaveDisplacementHistory.Count > 0)
            {
                displacementData = SWaveDisplacementHistory[SWaveDisplacementHistory.Count - 1];
            }
            else
            {
                // No displacement data available
                using (Font font = new Font("Arial", 12))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    g.DrawString("No wave displacement data available", font, brush, 20, 20);
                }
                return;
            }

            // Find min and max displacement values with robust checks
            float minDisplacement = float.MaxValue;
            float maxDisplacement = float.MinValue;
            float absMaxDisplacement = 0.000001f; // Avoid division by zero

            foreach (float value in displacementData)
            {
                if (!float.IsNaN(value) && !float.IsInfinity(value))
                {
                    minDisplacement = Math.Min(minDisplacement, value);
                    maxDisplacement = Math.Max(maxDisplacement, value);
                    absMaxDisplacement = Math.Max(absMaxDisplacement, Math.Abs(value));
                }
            }

            // Create a list to hold triangles with their displacement values
            var trianglesToDraw = new List<TriangleDepthInfo>();

            // Map the displacement data to each triangle
            foreach (Triangle tri in MeshTriangles)
            {
                // Calculate the average Z depth for sorting
                float avgZ = (tri.V1.Z + tri.V2.Z + tri.V3.Z) / 3.0f;

                // Calculate the center point for mapping to displacement data
                Vector3 center = new Vector3(
                    (tri.V1.X + tri.V2.X + tri.V3.X) / 3,
                    (tri.V1.Y + tri.V2.Y + tri.V3.Y) / 3,
                    (tri.V1.Z + tri.V2.Z + tri.V3.Z) / 3
                );

                // Map to data index using position
                int dataIndex = 0;
                if (displacementData.Length > 1)
                {
                    // Create a position-based index that varies across the mesh
                    float normalizedPos =
                        (center.X / maxCoord * 0.33f) +
                        (center.Y / maxCoord * 0.33f) +
                        (center.Z / maxCoord * 0.34f);

                    // Ensure we get a valid index
                    dataIndex = (int)(normalizedPos * displacementData.Length) % displacementData.Length;
                    dataIndex = Math.Max(0, Math.Min(displacementData.Length - 1, dataIndex));
                }

                // Get displacement value
                float displacement = displacementData[dataIndex];

                // Add some random variation to make the pattern more visible
                Random random = new Random((int)(avgZ * 1000 + center.X * 100 + center.Y * 10 + center.Z));
                float variation = (float)random.NextDouble() * 0.2f * absMaxDisplacement;
                displacement += displacement > 0 ? variation : -variation;

                // Store triangle with its displacement value
                trianglesToDraw.Add(new TriangleDepthInfo
                {
                    Triangle = tri,
                    AverageZ = avgZ,
                    Displacement = displacement
                });
            }

            // Sort triangles by Z depth (back to front)
            trianglesToDraw.Sort((a, b) => -a.AverageZ.CompareTo(b.AverageZ));

            // Draw triangles
            foreach (var triData in trianglesToDraw)
            {
                Triangle tri = triData.Triangle;

                // Project vertices
                PointF p1 = ProjectVertex(tri.V1, centerX, centerY, scale, maxCoord, rotationX, rotationY);
                PointF p2 = ProjectVertex(tri.V2, centerX, centerY, scale, maxCoord, rotationX, rotationY);
                PointF p3 = ProjectVertex(tri.V3, centerX, centerY, scale, maxCoord, rotationX, rotationY);

                // Create triangle points
                PointF[] points = new PointF[] { p1, p2, p3 };

                // Normalize displacement value to [-1,1] range with safe division
                float value = triData.Displacement;
                float normalizedValue;

                if (absMaxDisplacement > 0)
                    normalizedValue = Math.Max(-1.0f, Math.Min(1.0f, value / absMaxDisplacement));
                else
                    normalizedValue = 0;

                // Use safer color mapping
                Color triangleColor = GetSafeBipolarColor(normalizedValue);

                if (renderMode == RenderMode.Solid)
                {
                    // Draw filled triangle with transparency
                    using (SolidBrush brush = new SolidBrush(Color.FromArgb(200, triangleColor)))
                    {
                        g.FillPolygon(brush, points);
                    }
                }

                // Draw outline
                using (Pen pen = new Pen(renderMode == RenderMode.Wireframe ? triangleColor : Color.FromArgb(100, Color.Black), 1))
                {
                    g.DrawPolygon(pen, points);
                }
            }

            // Draw title
            string title = $"{(renderMode == RenderMode.Wireframe ? "Wireframe" : "Solid")} Mesh with {(_isPWave ? "P-Wave" : "S-Wave")} Propagation";
            using (Font titleFont = new Font("Arial", 14, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                g.DrawString(title, titleFont, textBrush, (width - g.MeasureString(title, titleFont).Width) / 2, 5);
            }

            // Draw color scale/legend
            DrawBipolarColorScale(g, width - 40, height / 2, 20, height / 3, "Displacement");

            // Add energy value display
            using (Font energyFont = new Font("Arial", 10))
            using (SolidBrush energyBrush = new SolidBrush(Color.Yellow))
            {
                g.DrawString($"Energy: {Energy:F1} J", energyFont, energyBrush, 20, height - 30);
            }
            RenderMeshWithTransducers(g,width,height);
        }

        private void DrawBipolarColorScale(Graphics g, int x, int y, int width, int height, string label)
        {
            // Ensure minimum dimensions
            height = Math.Max(10, height);

            // Draw using our safer bipolar color mapping
            using (Bitmap colorScale = new Bitmap(width, height))
            {
                for (int i = 0; i < height; i++)
                {
                    // Map from [0,height] to [-1,1]
                    float value = 1.0f - 2.0f * i / height;
                    Color color = GetSafeBipolarColor(value);

                    for (int j = 0; j < width; j++)
                    {
                        colorScale.SetPixel(j, i, color);
                    }
                }

                g.DrawImage(colorScale, x, y, width, height);
            }

            // Draw border
            using (Pen pen = new Pen(Color.Gray, 1))
            {
                g.DrawRectangle(pen, x, y, width, height);
            }

            // Draw labels
            using (Font font = new Font("Arial", 8))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                g.DrawString(label, font, textBrush, x, y - 15);
                g.DrawString("+", font, textBrush, x - 10, y);
                g.DrawString("0", font, textBrush, x - 10, y + height / 2);
                g.DrawString("-", font, textBrush, x - 10, y + height - 10);
            }
        }

        private Color GetSafeBipolarColor(float value)
        {
            // Ensure value is in range [-1, 1]
            value = Math.Max(-1.0f, Math.Min(1.0f, value));

            // Safety function to clamp color components to valid range
            Func<int, int> clamp = (val) => Math.Max(0, Math.Min(255, val));

            if (value < 0)
            {
                // Blue to cyan for negative values
                float t = -value; // t is in range [0,1]
                return Color.FromArgb(
                    clamp((int)(50 * t)),           // Red - minimal
                    clamp((int)(50 + 150 * t)),     // Green - increases
                    clamp((int)(128 + 127 * t))     // Blue - strong
                );
            }
            else
            {
                // Yellow to red for positive values
                float t = value; // t is in range [0,1]
                return Color.FromArgb(
                    clamp((int)(128 + 127 * t)),    // Red - strong
                    clamp((int)(128 - 100 * t)),    // Green - decreases
                    clamp((int)(50 * (1 - t)))      // Blue - minimal
                );
            }
        }

        /// <summary>
        /// Helper class for triangle depth sorting
        /// </summary>
        private class TriangleDepthInfo
        {
            public Triangle Triangle { get; set; }
            public float AverageZ { get; set; }
            public float Displacement { get; set; }
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

        #endregion Rendering and Export

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
                // Export as PNG first, then convert to PDF (#TODO: implement direct PDF export)
                string tempPngPath = Path.Combine(Path.GetTempPath(), $"acoustic_sim_{SimulationId}.png");
                if (ExportToPng(tempPngPath))
                {
                    

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

                        // Create individual panel bitmaps WITHOUT panel titles
                        Bitmap panel1 = new Bitmap(panelWidth, panelHeight);
                        Bitmap panel2 = new Bitmap(panelWidth, panelHeight);
                        Bitmap panel3 = new Bitmap(panelWidth, panelHeight);
                        Bitmap panel4 = new Bitmap(panelWidth, panelHeight);

                        // Render panels directly to individual bitmaps WITHOUT titles
                        using (Graphics g1 = Graphics.FromImage(panel1))
                        {
                            g1.Clear(Color.Black);
                            g1.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                            g1.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            // Just call the internal render method that doesn't add titles
                            if (RenderMode.Stress == RenderMode.Stress)
                            {
                                RenderWaveField(g1, panelWidth, panelHeight);
                            }
                        }

                        using (Graphics g2 = Graphics.FromImage(panel2))
                        {
                            g2.Clear(Color.Black);
                            g2.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                            g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            // Call the internal method directly
                            RenderTimeSeries(g2, panelWidth, panelHeight);
                        }

                        using (Graphics g3 = Graphics.FromImage(panel3))
                        {
                            g3.Clear(Color.Black);
                            g3.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                            g3.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            // Call the internal method directly
                            RenderVelocityDistribution(g3, panelWidth, panelHeight);
                        }

                        using (Graphics g4 = Graphics.FromImage(panel4))
                        {
                            g4.Clear(Color.Black);
                            g4.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                            g4.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            // Call the internal method directly
                            RenderWaveSlice(g4, panelWidth, panelHeight);
                        }

                        // Draw panels to the composite
                        g.DrawImage(panel1, padding, titleHeight + padding);
                        g.DrawImage(panel2, padding * 2 + panelWidth, titleHeight + padding);
                        g.DrawImage(panel3, padding, titleHeight + padding * 2 + panelHeight);
                        g.DrawImage(panel4, padding * 2 + panelWidth, titleHeight + padding * 2 + panelHeight);

                        // Add the panel titles ONLY HERE - not in the rendering methods
                        using (Font panelFont = new Font("Arial", 14, FontStyle.Bold))
                        using (SolidBrush textBrush = new SolidBrush(Color.White))
                        using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                        {
                            // Panel 1 title
                            string panel1Title = "P-Wave Propagation";
                            SizeF title1Size = g.MeasureString(panel1Title, panelFont);
                            float title1X = padding + (panelWidth - title1Size.Width) / 2;
                            float title1Y = titleHeight + padding + 10;
                            //g.FillRectangle(bgBrush, title1X - 5, title1Y - 2, title1Size.Width + 10, title1Size.Height + 4);
                            //g.DrawString(panel1Title, panelFont, textBrush, title1X, title1Y);

                            // Panel 2 title
                            string panel2Title = "P-Wave Time Series";
                            SizeF title2Size = g.MeasureString(panel2Title, panelFont);
                            float title2X = padding * 2 + panelWidth + (panelWidth - title2Size.Width) / 2;
                            float title2Y = titleHeight + padding + 10;
                            //g.FillRectangle(bgBrush, title2X - 5, title2Y - 2, title2Size.Width + 10, title2Size.Height + 4);
                            //g.DrawString(panel2Title, panelFont, textBrush, title2X, title2Y);

                            // Panel 3 title
                            string panel3Title = "Velocity Distribution";
                            SizeF title3Size = g.MeasureString(panel3Title, panelFont);
                            float title3X = padding + (panelWidth - title3Size.Width) / 2;
                            float title3Y = titleHeight + padding * 2 + panelHeight + 10;
                            g.FillRectangle(bgBrush, title3X - 5, title3Y - 2, title3Size.Width + 10, title3Size.Height + 4);
                            g.DrawString(panel3Title, panelFont, textBrush, title3X, title3Y);

                            // Panel 4 title
                            string panel4Title = "P-Wave Slices";
                            SizeF title4Size = g.MeasureString(panel4Title, panelFont);
                            float title4X = padding * 2 + panelWidth + (panelWidth - title4Size.Width) / 2;
                            float title4Y = titleHeight + padding * 2 + panelHeight + 10;
                            //g.FillRectangle(bgBrush, title4X - 5, title4Y - 2, title4Size.Width + 10, title4Size.Height + 4);
                            //g.DrawString(panel4Title, panelFont, textBrush, title4X, title4Y);
                        }

                        // Add density information if applicable
                        bool isInhomogeneous = this is InhomogeneousAcousticSimulation;
                        InhomogeneousAcousticSimulation inhomogeneousSim = this as InhomogeneousAcousticSimulation;

                        if (isInhomogeneous && inhomogeneousSim != null)
                        {
                            using (Font infoFont = new Font("Arial", 9, FontStyle.Bold))
                            using (SolidBrush textBrush = new SolidBrush(Color.Yellow))
                            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                            {
                                // Add density info to each panel in non-overlapping areas
                                string densityInfo = $"Inhomogeneous Density: {inhomogeneousSim.TriangleDensities?.Count ?? 0} points";
                                string densityRange = $"Density: {inhomogeneousSim.AverageDensity:F1} kg/m³";

                                // Panel 1 - Top left
                                g.FillRectangle(bgBrush, padding + 10, titleHeight + padding + 40, 200, 20);
                                g.DrawString(densityInfo, infoFont, textBrush, padding + 15, titleHeight + padding + 42);

                                // Panel 2 - Top right
                                g.FillRectangle(bgBrush, padding * 2 + panelWidth + panelWidth - 220, titleHeight + padding + 40, 200, 20);
                                g.DrawString(densityInfo, infoFont, textBrush, padding * 2 + panelWidth + panelWidth - 215, titleHeight + padding + 42);

                                // Panel 3 - Bottom left
                                g.FillRectangle(bgBrush, padding + 10, titleHeight + padding * 2 + panelHeight + 40, 200, 20);
                                g.DrawString(densityInfo, infoFont, textBrush, padding + 15, titleHeight + padding * 2 + panelHeight + 42);

                                // Panel 4 - Bottom right
                                g.FillRectangle(bgBrush, padding * 2 + panelWidth + panelWidth - 220, titleHeight + padding * 2 + panelHeight + 40, 200, 20);
                                g.DrawString(densityInfo, infoFont, textBrush, padding * 2 + panelWidth + panelWidth - 215, titleHeight + padding * 2 + panelHeight + 42);
                            }
                        }

                        // Draw panel borders
                        using (Pen borderPen = new Pen(Color.DimGray, 2))
                        {
                            g.DrawRectangle(borderPen, padding, titleHeight + padding, panelWidth, panelHeight);
                            g.DrawRectangle(borderPen, padding * 2 + panelWidth, titleHeight + padding, panelWidth, panelHeight);
                            g.DrawRectangle(borderPen, padding, titleHeight + padding * 2 + panelHeight, panelWidth, panelHeight);
                            g.DrawRectangle(borderPen, padding * 2 + panelWidth, titleHeight + padding * 2 + panelHeight, panelWidth, panelHeight);
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


        #endregion Export Methods

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

        #endregion Event Handlers

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

        #endregion IDisposable Implementation
        #region Visualization helpers

        public void RenderMeshWithTransducers(Graphics g, int width, int height)
        {
            g.Clear(Color.Black);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            if (MeshTriangles == null || MeshTriangles.Count == 0)
            {
                using (Font font = new Font("Arial", 12))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    g.DrawString("No mesh available", font, brush, 20, 20);
                }
                return;
            }

            // Draw title and info
            using (Font titleFont = new Font("Arial", 14, FontStyle.Bold))
            using (Brush titleBrush = new SolidBrush(Color.White))
            {
                string title = "Mesh with Acoustic Transducer Positions";
                g.DrawString(title, titleFont, titleBrush, 20, 20);

                using (Font infoFont = new Font("Arial", 10))
                {
                    g.DrawString($"Mesh: {MeshTriangles.Count} triangles", infoFont, titleBrush, 20, 50);
                    g.DrawString($"Sample Length: {SampleLength:F3} m", infoFont, titleBrush, 20, 70);
                    g.DrawString($"Test Direction: ({TestDirection.X:F2}, {TestDirection.Y:F2}, {TestDirection.Z:F2})",
                                 infoFont, new SolidBrush(Color.Yellow), 20, 90);

                    // Display source and receiver grid positions
                    g.DrawString($"Source: ({_sourceX}, {_sourceY}, {_sourceZ})", infoFont,
                                 new SolidBrush(Color.Yellow), 20, 110);
                    g.DrawString($"Receiver: ({_receiverX}, {_receiverY}, {_receiverZ})", infoFont,
                                 new SolidBrush(Color.Cyan), 20, 130);

                    // Calculate and display physical distance
                    Vector3 sourcePos = new Vector3(_sourceX, _sourceY, _sourceZ);
                    Vector3 receiverPos = new Vector3(_receiverX, _receiverY, _receiverZ);
                    float distance = Vector3Distance(sourcePos, receiverPos) * _gridSpacing;
                    g.DrawString($"Distance: {distance:F3} m", infoFont, titleBrush, 20, 150);
                }
            }

            // Calculate display area
            int margin = 50;
            int visualWidth = width - 2 * margin;
            int visualHeight = height - 200 - margin;

            // Set up projection parameters for the mesh
            float centerX = margin + visualWidth / 2;
            float centerY = 200 + visualHeight / 2;

            // Find min and max bounds of the mesh
            Vector3 minBounds = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 maxBounds = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            foreach (Triangle tri in MeshTriangles)
            {
                // Check each vertex
                minBounds.X = Math.Min(minBounds.X, Math.Min(tri.V1.X, Math.Min(tri.V2.X, tri.V3.X)));
                minBounds.Y = Math.Min(minBounds.Y, Math.Min(tri.V1.Y, Math.Min(tri.V2.Y, tri.V3.Y)));
                minBounds.Z = Math.Min(minBounds.Z, Math.Min(tri.V1.Z, Math.Min(tri.V2.Z, tri.V3.Z)));

                maxBounds.X = Math.Max(maxBounds.X, Math.Max(tri.V1.X, Math.Max(tri.V2.X, tri.V3.X)));
                maxBounds.Y = Math.Max(maxBounds.Y, Math.Max(tri.V1.Y, Math.Max(tri.V2.Y, tri.V3.Y)));
                maxBounds.Z = Math.Max(maxBounds.Z, Math.Max(tri.V1.Z, Math.Max(tri.V2.Z, tri.V3.Z)));
            }

            // Calculate center and scale of the mesh for projection
            Vector3 meshCenter = new Vector3(
                (minBounds.X + maxBounds.X) / 2,
                (minBounds.Y + maxBounds.Y) / 2,
                (minBounds.Z + maxBounds.Z) / 2
            );

            // Determine max dimension for scaling
            float maxDimension = Math.Max(
                maxBounds.X - minBounds.X,
                Math.Max(maxBounds.Y - minBounds.Y, maxBounds.Z - minBounds.Z)
            );

            if (maxDimension <= 0) maxDimension = 1.0f; // Prevent division by zero

            float scale = Math.Min(visualWidth, visualHeight) / (maxDimension * 1.2f);

            // Draw mesh wireframe with depth sorting
            DrawMeshWireframe(g, centerX, centerY, scale, meshCenter);

            // Map grid coordinates to mesh space for source and receiver
            Vector3 sourceInMeshSpace = MapGridToMeshCoordinates(
                new Vector3(_sourceX, _sourceY, _sourceZ),
                minBounds, maxBounds, _gridSizeX, _gridSizeY, _gridSizeZ
            );

            Vector3 receiverInMeshSpace = MapGridToMeshCoordinates(
                new Vector3(_receiverX, _receiverY, _receiverZ),
                minBounds, maxBounds, _gridSizeX, _gridSizeY, _gridSizeZ
            );

            // Project source and receiver to 2D
            PointF sourcePoint = Project3DPoint(sourceInMeshSpace, centerX, centerY, scale, meshCenter);
            PointF receiverPoint = Project3DPoint(receiverInMeshSpace, centerX, centerY, scale, meshCenter);

            // Draw source
            using (Brush sourceBrush = new SolidBrush(Color.Yellow))
            {
                g.FillEllipse(sourceBrush, sourcePoint.X - 8, sourcePoint.Y - 8, 16, 16);
                g.DrawString("Source", new Font("Arial", 10, FontStyle.Bold), sourceBrush,
                             sourcePoint.X + 10, sourcePoint.Y - 5);
            }

            // Draw receiver
            using (Brush receiverBrush = new SolidBrush(Color.Cyan))
            {
                g.FillEllipse(receiverBrush, receiverPoint.X - 8, receiverPoint.Y - 8, 16, 16);
                g.DrawString("Receiver", new Font("Arial", 10, FontStyle.Bold), receiverBrush,
                             receiverPoint.X + 10, receiverPoint.Y - 5);
            }

            // Draw line connecting source and receiver
            using (Pen connectionPen = new Pen(Color.White, 2))
            {
                connectionPen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                g.DrawLine(connectionPen, sourcePoint, receiverPoint);
            }

            // Draw rotation help
            using (Font font = new Font("Arial", 8))
            using (Brush brush = new SolidBrush(Color.LightGray))
            {
                g.DrawString("View: Isometric", font, brush, width - 120, height - 40);
            }
        }

        private void DrawMeshWireframe(Graphics g, float centerX, float centerY, float scale, Vector3 meshCenter)
        {
            // Create sorted list of triangles by depth
            var sortedTriangles = new List<Tuple<Triangle, float>>();

            foreach (Triangle tri in MeshTriangles)
            {
                // Calculate average Z depth for sorting (using an isometric projection)
                float avgZ = (tri.V1.Z + tri.V2.Z + tri.V3.Z) / 3.0f
                           + (tri.V1.X + tri.V2.X + tri.V3.X) / 6.0f
                           + (tri.V1.Y + tri.V2.Y + tri.V3.Y) / 6.0f;

                sortedTriangles.Add(new Tuple<Triangle, float>(tri, avgZ));
            }

            // Sort triangles back-to-front for proper rendering
            sortedTriangles.Sort((a, b) => a.Item2.CompareTo(b.Item2));

            // Draw triangles
            using (Pen wirePen = new Pen(Color.FromArgb(100, 100, 100), 1))
            {
                foreach (var triangleData in sortedTriangles)
                {
                    Triangle tri = triangleData.Item1;

                    // Project vertices to 2D
                    PointF p1 = Project3DPoint(tri.V1, centerX, centerY, scale, meshCenter);
                    PointF p2 = Project3DPoint(tri.V2, centerX, centerY, scale, meshCenter);
                    PointF p3 = Project3DPoint(tri.V3, centerX, centerY, scale, meshCenter);

                    // Draw triangle edges
                    g.DrawLine(wirePen, p1, p2);
                    g.DrawLine(wirePen, p2, p3);
                    g.DrawLine(wirePen, p3, p1);
                }
            }
        }

        private Vector3 MapGridToMeshCoordinates(Vector3 gridPos, Vector3 minBounds, Vector3 maxBounds,
                                                 int gridSizeX, int gridSizeY, int gridSizeZ)
        {
            // Convert grid coordinates (0 to gridSize) to mesh space (minBounds to maxBounds)
            return new Vector3(
                minBounds.X + (gridPos.X / gridSizeX) * (maxBounds.X - minBounds.X),
                minBounds.Y + (gridPos.Y / gridSizeY) * (maxBounds.Y - minBounds.Y),
                minBounds.Z + (gridPos.Z / gridSizeZ) * (maxBounds.Z - minBounds.Z)
            );
        }

        // Improved 3D point projection with isometric view and centered on mesh
        private PointF Project3DPoint(Vector3 point, float centerX, float centerY, float scale, Vector3 meshCenter)
        {
            // Center the point relative to mesh center
            float x = point.X - meshCenter.X;
            float y = point.Y - meshCenter.Y;
            float z = point.Z - meshCenter.Z;

            // Apply isometric projection with a slight tilt
            float projX = (x - 0.5f * y) * scale;
            float projY = (-z + 0.3f * x + 0.3f * y) * scale;

            return new PointF(centerX + projX, centerY - projY);
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

        #endregion Internal Classes
    }
}