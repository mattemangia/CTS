using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Algorithms;
using ILGPU.Runtime.CPU;
using ILGPU.Runtime.Cuda;
using ILGPU.Runtime.OpenCL;
using Microsoft.Office.Interop.Word;
using Task = System.Threading.Tasks.Task;

namespace CTSegmenter
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MaterialParams
    {
        public double Lambda, Mu, TensileStrength, FrictionSinPhi, FrictionCosPhi, Cohesion, ConfiningPressure;
        public byte UseModelFlags; // bitmask: 1=elastic,2=plastic,4=brittle
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SimulationParams
    {
        public int Width, Height, Depth;
        public float PixelSize;
        public double TimeStep;
        public byte MaterialID;
        public double FailureThreshold, Damping;
        public int StressAxis; // 0=X, 1=Y, 2=Z
        public byte DebugMode;
        public float RefDensity;
    }

    public class TriaxialSimulatorGPU : IDisposable
    {
        #region Fields and Properties
        // ─── CPU-side copies of GPU buffers (updated every N steps) ────────────────
        private float[] _damageHost;     // 1-D flattening of (X*Y*Z)
        private ushort[] _labelsHost;
        private float[] _densityHost;

        // selected material id & reference density (set in ctor)
        private readonly byte _selectedMaterialID;
        private readonly float _refRho;              // ρ₀  (kg m-3)

        private Context _context;
        private Accelerator _accelerator;
        private readonly int _width, _height, _depth;
        private readonly float _pixelSize;
        private readonly byte _matID;
        private MaterialParams _matParams;
        private SimulationParams _simParams;

        // Host arrays
        private readonly byte[,,] _labels;
        private readonly float[,,] _density;
        private readonly double[,,] _vx, _vy, _vz;
        private readonly double[,,] _sxx, _syy, _szz, _sxy, _sxz, _syz;
        private readonly double[,,] _damage, _dispX, _dispY, _dispZ;

        // Simulation state
        private double _currentTime;
        private int _currentStep;
        private int _totalSteps;
        private double _currentStrain;
        private double _currentStress;
        private double _initialSampleSize;
        private int _minPos, _maxPos;
        private List<double> _strainHistory = new List<double>();
        private List<double> _stressHistory = new List<double>();
        private double _confiningPressure;
        private double _initialAxialPressure;
        private double _finalAxialPressure;
        private int _pressureIncrements;
        private StressAxis _pressureAxis;
        private int _stepsPerIncrement;
        private volatile bool _simulationRunning;
        private volatile bool _simulationPaused;
        private volatile bool _simulationCancelled;
        private bool _failureDetected;
        private int _failureStep;
        private Task _simulationTask;
        private CancellationTokenSource _cts;

        // Device buffers
        private MemoryBuffer3D<byte, Stride3D.DenseXY> _dLabels;
        private MemoryBuffer3D<float, Stride3D.DenseXY> _dDensity;
        private MemoryBuffer3D<double, Stride3D.DenseXY> _dVx, _dVy, _dVz;
        private MemoryBuffer3D<double, Stride3D.DenseXY> _dSxx, _dSyy, _dSzz, _dSxy, _dSxz, _dSyz;
        private MemoryBuffer3D<double, Stride3D.DenseXY> _dDamage, _dDispX, _dDispY, _dDispZ;

        // Kernels
        private Action<Index3D, Kernels.StressArgs> _stressKernel;
        private Action<Index3D, Kernels.VelocityArgs> _velocityKernel;
        private Action<Index3D, Kernels.DisplacementArgs> _dispKernel;
        private Action<Index3D, Kernels.BoundaryArgs> _boundaryKernel;
        private Action<Index3D, Kernels.InitializeArgs> _initKernel;

        // Events to match CPU simulator API
        public event EventHandler<TriaxialSimulationProgressEventArgs> ProgressUpdated;
        public event EventHandler<TriaxialSimulationCompleteEventArgs> SimulationCompleted;
        public event EventHandler<FailureDetectedEventArgs> FailureDetected;
        private readonly bool _debugMode;
        // Properties to match CPU simulator API
        public double CurrentStrain => _currentStrain;
        public double CurrentStress => _currentStress;
        #endregion

        #region Constructor and Initialization
        public TriaxialSimulatorGPU(
            int width, int height, int depth,
            float pixelSize,
            byte[,,] labels,
            float[,,] density,
            byte materialID,
            double youngsModulusMPa,
            double poissonRatio,
            bool useElastic, bool usePlastic, bool useBrittle,
            double tensileMPa, double frictionAngleDeg, double cohesionMPa, bool debugMode = false)
        {
            Logger.Log("[GPUTriaxialSimulator] Constructor start");
            _width = width;
            _height = height;
            _depth = depth;
            _pixelSize = pixelSize;
            _labels = labels;
            _density = density;
            _matID = materialID;
            // Initialize host arrays for damage checking
            int totalSize = width * height * depth;
            _damageHost = new float[totalSize];
            _labelsHost = new ushort[totalSize];
            _densityHost = new float[totalSize];

            // Store material ID specifically for failure check
            _selectedMaterialID = materialID;

            // Calculate reference density
            _refRho = CalculateReferenceRho(labels, density, materialID);
            // Pre-alloc host arrays
            _vx = new double[_width, _height, _depth];
            _vy = new double[_width, _height, _depth];
            _vz = new double[_width, _height, _depth];
            _sxx = new double[_width, _height, _depth];
            _syy = new double[_width, _height, _depth];
            _szz = new double[_width, _height, _depth];
            _sxy = new double[_width, _height, _depth];
            _sxz = new double[_width, _height, _depth];
            _syz = new double[_width, _height, _depth];
            _damage = new double[_width, _height, _depth];
            _dispX = new double[_width, _height, _depth];
            _dispY = new double[_width, _height, _depth];
            _dispZ = new double[_width, _height, _depth];

            // Compute Lame parameters
            double E = youngsModulusMPa * 1e6;
            double mu = E / (2 * (1 + poissonRatio));
            double lambda = E * poissonRatio / ((1 + poissonRatio) * (1 - 2 * poissonRatio));

            _matParams = new MaterialParams
            {
                Lambda = lambda,
                Mu = mu,
                TensileStrength = tensileMPa * 1e6,
                FrictionSinPhi = Math.Sin(frictionAngleDeg * Math.PI / 180.0),
                FrictionCosPhi = Math.Cos(frictionAngleDeg * Math.PI / 180.0),
                Cohesion = cohesionMPa * 1e6,
                ConfiningPressure = 0.0,
                UseModelFlags = (byte)((useElastic ? 1 : 0) | (usePlastic ? 2 : 0) | (useBrittle ? 4 : 0))
            };
            _debugMode = debugMode;
            Logger.Log($"[GPUTriaxialSimulator] Debug mode: {debugMode}");
            _cts = new CancellationTokenSource();
            InitGpu();
            Logger.Log("[GPUTriaxialSimulator] Constructor end");
        }
        private float CalculateReferenceRho(byte[,,] labels, float[,,] density, byte matID)
        {
            double sum = 0;
            int count = 0;
            double minDensity = double.MaxValue;
            double maxDensity = double.MinValue;

            // Get density statistics for detailed logging
            for (int z = 0; z < _depth; z++)
            {
                for (int y = 0; y < _height; y++)
                {
                    for (int x = 0; x < _width; x++)
                    {
                        if (labels[x, y, z] == matID && density[x, y, z] > 0)
                        {
                            double value = density[x, y, z];
                            sum += value;
                            count++;
                            minDensity = Math.Min(minDensity, value);
                            maxDensity = Math.Max(maxDensity, value);
                        }
                    }
                }
            }

            float refRho = count > 0 ? (float)(sum / count) : 2500.0f; // Default to typical rock density

            // Log detailed density info to help with debugging
            Logger.Log($"[GPUTriaxialSimulator] Density statistics: min={minDensity:F0}, max={maxDensity:F0}, avg={refRho:F0} kg/m³, samples={count}");

            return refRho;
        }
        private void InitGpu()
        {
            Logger.Log("[GPUTriaxialSimulator] Initializing GPU");
            _context = Context.Create(builder => builder.Cuda().OpenCL().CPU().EnableAlgorithms());
            var device = _context.Devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.Cuda)
                         ?? _context.Devices.First();
            _accelerator = device.CreateAccelerator(_context);
            Logger.Log($"[GPUTriaxialSimulator] Using {_accelerator.AcceleratorType}");

            // Load kernels
            _stressKernel = _accelerator.LoadAutoGroupedStreamKernel<Index3D, Kernels.StressArgs>(Kernels.UpdateStress);
            _velocityKernel = _accelerator.LoadAutoGroupedStreamKernel<Index3D, Kernels.VelocityArgs>(Kernels.UpdateVelocity);
            _dispKernel = _accelerator.LoadAutoGroupedStreamKernel<Index3D, Kernels.DisplacementArgs>(Kernels.UpdateDisplacement);
            _boundaryKernel = _accelerator.LoadAutoGroupedStreamKernel<Index3D, Kernels.BoundaryArgs>(Kernels.ApplyBoundaryConditions);
            _initKernel = _accelerator.LoadAutoGroupedStreamKernel<Index3D, Kernels.InitializeArgs>(Kernels.InitializeFields);
            

            var extent = new Index3D(_width, _height, _depth);
            _dLabels = _accelerator.Allocate3DDenseXY<byte>(extent);
            _dDensity = _accelerator.Allocate3DDenseXY<float>(extent);
            _dVx = _accelerator.Allocate3DDenseXY<double>(extent);
            _dVy = _accelerator.Allocate3DDenseXY<double>(extent);
            _dVz = _accelerator.Allocate3DDenseXY<double>(extent);
            _dSxx = _accelerator.Allocate3DDenseXY<double>(extent);
            _dSyy = _accelerator.Allocate3DDenseXY<double>(extent);
            _dSzz = _accelerator.Allocate3DDenseXY<double>(extent);
            _dSxy = _accelerator.Allocate3DDenseXY<double>(extent);
            _dSxz = _accelerator.Allocate3DDenseXY<double>(extent);
            _dSyz = _accelerator.Allocate3DDenseXY<double>(extent);
            _dDamage = _accelerator.Allocate3DDenseXY<double>(extent);
            _dDispX = _accelerator.Allocate3DDenseXY<double>(extent);
            _dDispY = _accelerator.Allocate3DDenseXY<double>(extent);
            _dDispZ = _accelerator.Allocate3DDenseXY<double>(extent);

            // Copy initial data
            _dLabels.CopyFromCPU(_labels);
            _dDensity.CopyFromCPU(_density);
            Logger.Log("[GPUTriaxialSimulator] GPU initialization complete");
        }
        #endregion

        #region Public API (matching CPU simulator)
        public static bool IsGpuAvailable()
        {
            try
            {
                using (var context = Context.Create(builder => builder.Cuda().OpenCL().CPU()))
                {
                    // Check for CUDA devices first (preferred)
                    var cudaDevice = context.Devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.Cuda);
                    if (cudaDevice != null)
                        return true;

                    // Check for OpenCL devices next
                    var openCLDevice = context.Devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.OpenCL);
                    if (openCLDevice != null)
                        return true;

                    // Fall back to CPU acceleration (still considered "GPU" for UI purpose)
                    return context.Devices.Any(d => d.AcceleratorType == AcceleratorType.CPU);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GPUTriaxialSimulator] Error checking GPU availability: {ex.Message}");
                return false;
            }
        }

        public Task StartSimulationAsync(
            double confiningPressure,
            double initialPressure,
            double finalPressure,
            int pressureIncrements,
            StressAxis axis,
            int stepsPerIncrement,
            CancellationToken cancelToken = default)
        {
            _confiningPressure = confiningPressure;
            _initialAxialPressure = initialPressure;
            _finalAxialPressure = finalPressure;
            _pressureIncrements = pressureIncrements;
            _pressureAxis = axis;
            _stepsPerIncrement = stepsPerIncrement;
            _currentTime = 0;
            _currentStep = 0;
            _totalSteps = pressureIncrements * stepsPerIncrement;
            _simulationRunning = true;
            _simulationPaused = false;
            _simulationCancelled = false;
            _failureDetected = false;
            _failureStep = -1;
            _strainHistory.Clear();
            _stressHistory.Clear();

            // Link external cancellation token
            if (cancelToken != default)
            {
                cancelToken.Register(() => _simulationCancelled = true);
            }

            // Start simulation task
            _simulationTask = Task.Run(() => RunSimulation(), _cts.Token);
            return _simulationTask;
        }

        public void PauseSimulation()
        {
            _simulationPaused = true;
            Logger.Log("[GPUTriaxialSimulator] Simulation paused");
        }

        public void ResumeSimulation()
        {
            _simulationPaused = false;
            Logger.Log("[GPUTriaxialSimulator] Simulation resumed");
        }

        public void CancelSimulation()
        {
            _simulationCancelled = true;
            _cts.Cancel();
            Logger.Log("[GPUTriaxialSimulator] Simulation cancelled");
        }

        public void ContinueAfterFailure()
        {
            _simulationPaused = false;
            Logger.Log("[GPUTriaxialSimulator] Continuing after failure");
        }
        #endregion

        #region Simulation Methods
        /// <summary>
        /// Runs the full GPU‐accelerated triaxial simulation, applying axial and lateral
        /// boundary conditions on the device each increment, then performing time‐integration
        /// and measuring interior response.
        /// </summary>
        private void RunSimulation()
        {
            try
            {
                Logger.Log("[GPUTriaxialSimulator] Starting simulation");

                // Initialize simulation state
                InitializeSimulation();

                // Find material boundaries
                FindMaterialBoundaries();

                // Set initial confining pressure
                _matParams.ConfiningPressure = _confiningPressure * 1e6;

                // Initialize stress state (via kernel)
                InitializeStressState();

                // Record initial stress-strain state
                _strainHistory.Add(0.0);
                _stressHistory.Add(_initialAxialPressure);
                _currentStrain = 0.0;
                _currentStress = _initialAxialPressure;

                // Calculate axial pressure step size
                double axialStepMPa = (_finalAxialPressure - _initialAxialPressure) / _pressureIncrements;

                // Find all specimen bounds for lateral BCs
                int minX = _width, maxX = 0, minY = _height, maxY = 0, minZ = _depth, maxZ = 0;
                for (int z = 0; z < _depth; z++)
                    for (int y = 0; y < _height; y++)
                        for (int x = 0; x < _width; x++)
                            if (_labels[x, y, z] == _matID)
                            {
                                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                                minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
                            }

                // Step through pressure increments
                for (int inc = 1; inc <= _pressureIncrements; inc++)
                {
                    if (_simulationCancelled || _cts.Token.IsCancellationRequested)
                    {
                        Logger.Log("[GPUTriaxialSimulator] Simulation cancelled during pressure increment loop");
                        break;
                    }

                    // Wait while paused
                    while (_simulationPaused && !_simulationCancelled && !_cts.Token.IsCancellationRequested)
                    {
                        Thread.Sleep(100);
                    }

                    double targetAxialMPa = _initialAxialPressure + axialStepMPa * inc;
                    double targetAxialPa = targetAxialMPa * 1e6;
                    double confiningPa = _confiningPressure * 1e6;

                    // Apply axial pressure at boundary
                    ApplyBoundaryPressure(targetAxialPa);

                    // IMPORTANT: Apply confining pressure on lateral boundaries
                    // This was missing in the original code!
                    if (_pressureAxis == StressAxis.X)
                    {
                        ApplyConfiningPressure(confiningPa, StressAxis.Y, minY, maxY);
                        ApplyConfiningPressure(confiningPa, StressAxis.Z, minZ, maxZ);
                    }
                    else if (_pressureAxis == StressAxis.Y)
                    {
                        ApplyConfiningPressure(confiningPa, StressAxis.X, minX, maxX);
                        ApplyConfiningPressure(confiningPa, StressAxis.Z, minZ, maxZ);
                    }
                    else // Z axis
                    {
                        ApplyConfiningPressure(confiningPa, StressAxis.X, minX, maxX);
                        ApplyConfiningPressure(confiningPa, StressAxis.Y, minY, maxY);
                    }

                    // Run time steps for this increment with intermediate data recording
                    int recordInterval = Math.Max(1, _stepsPerIncrement / 10); // Record ~10 points per increment

                    for (int step = 0; step < _stepsPerIncrement; step++)
                    {
                        if (_simulationCancelled || _cts.Token.IsCancellationRequested) break;

                        while (_simulationPaused && !_simulationCancelled && !_cts.Token.IsCancellationRequested)
                            Thread.Sleep(100);

                        if (_simulationCancelled) break;

                        // Update stress field
                        var stressArgs = new Kernels.StressArgs
                        {
                            Labels = _dLabels,
                            Density = _dDensity,
                            Vx = _dVx,
                            Vy = _dVy,
                            Vz = _dVz,
                            Sxx = _dSxx,
                            Syy = _dSyy,
                            Szz = _dSzz,
                            Sxy = _dSxy,
                            Sxz = _dSxz,
                            Syz = _dSyz,
                            Damage = _dDamage,
                            MatParams = _matParams,
                            SimParams = _simParams
                        };
                        _stressKernel(new Index3D(_width, _height, _depth), stressArgs);

                        // Update velocity field
                        var velArgs = new Kernels.VelocityArgs
                        {
                            Labels = _dLabels,
                            Density = _dDensity,
                            Vx = _dVx,
                            Vy = _dVy,
                            Vz = _dVz,
                            Sxx = _dSxx,
                            Syy = _dSyy,
                            Szz = _dSzz,
                            Sxy = _dSxy,
                            Sxz = _dSxz,
                            Syz = _dSyz,
                            SimParams = _simParams
                        };
                        _velocityKernel(new Index3D(_width, _height, _depth), velArgs);

                        // Update displacement field
                        var dispArgs = new Kernels.DisplacementArgs
                        {
                            Labels = _dLabels,
                            Vx = _dVx,
                            Vy = _dVy,
                            Vz = _dVz,
                            DispX = _dDispX,
                            DispY = _dDispY,
                            DispZ = _dDispZ,
                            SimParams = _simParams
                        };
                        _dispKernel(new Index3D(_width, _height, _depth), dispArgs);

                        _accelerator.Synchronize();
                        _currentTime += _simParams.TimeStep;

                        // Record data at intervals to get better curve (not just at the end of increments)
                        if (step % recordInterval == 0 || step == _stepsPerIncrement - 1)
                        {
                            UpdateCurrentStrainAndStress(targetAxialMPa);

                            // Add data point for detailed stress-strain curve
                            if (step > 0 && step < _stepsPerIncrement - 1)
                            {
                                _strainHistory.Add(_currentStrain);
                                _stressHistory.Add(_currentStress);
                            }

                            // Progress update
                            int percentComplete = (int)((double)(inc - 1) / _pressureIncrements * 100.0) +
                                                 (int)((double)step / _stepsPerIncrement * (100.0 / _pressureIncrements));
                            ProgressUpdated?.Invoke(this, new TriaxialSimulationProgressEventArgs(
                                percentComplete, inc, $"Loading: {targetAxialMPa:F2} MPa, Step {step + 1}/{_stepsPerIncrement}"));
                        }

                        // Check for failure more frequently in debug mode
                        int checkInterval = _debugMode ? 2 : 5;
                        if (step % checkInterval == 0 && !_failureDetected)
                        {
                            if (CheckForFailure())
                            {
                                _failureDetected = true;
                                _failureStep = inc;

                                Logger.Log($"[GPUTriaxialSimulator] FAILURE DETECTED at step {inc}, stress={_currentStress:F2} MPa, strain={_currentStrain:F4}");

                                var failureArgs = new FailureDetectedEventArgs(_currentStress, _currentStrain, inc, _pressureIncrements);
                                FailureDetected?.Invoke(this, failureArgs);

                                _simulationPaused = true;
                            }
                        }
                    }

                    // Final strain and stress update for this increment
                    UpdateCurrentStrainAndStress(targetAxialMPa);

                    // Record the final stress-strain state for this increment
                    _strainHistory.Add(_currentStrain);
                    _stressHistory.Add(_currentStress);

                    // Report progress
                    int pctComplete = (int)((double)inc / _pressureIncrements * 100.0);
                    ProgressUpdated?.Invoke(this, new TriaxialSimulationProgressEventArgs(
                        pctComplete, inc, $"Loading: {targetAxialMPa:F2} MPa (complete)"));
                }

                FinalizeSimulation();
            }
            catch (OperationCanceledException)
            {
                Logger.Log("[GPUTriaxialSimulator] Simulation cancelled via exception");
                ProgressUpdated?.Invoke(this, new TriaxialSimulationProgressEventArgs(0, 0, "Cancelled"));
            }
            catch (Exception ex)
            {
                Logger.Log($"[GPUTriaxialSimulator] Error during simulation: {ex.Message}\n{ex.StackTrace}");
                ProgressUpdated?.Invoke(this, new TriaxialSimulationProgressEventArgs(0, 0, $"Error: {ex.Message}"));
            }
            finally
            {
                _simulationRunning = false;
            }
        }

        private void InitializeSimulation()
        {
            Logger.Log("[GPUTriaxialSimulator] Initializing simulation");

            // Compute stable time step
            double dt = ComputeStableTimeStep();

            // Setup simulation parameters
            _simParams = new SimulationParams
            {
                Width = _width,
                Height = _height,
                Depth = _depth,
                PixelSize = _pixelSize,
                TimeStep = dt,
                MaterialID = _matID,
                FailureThreshold = 0.75,
                DebugMode = _debugMode ? (byte)1 : (byte)0,
                Damping = 0.05,
                StressAxis = (int)_pressureAxis,
                RefDensity = _refRho  
            };

            // Initialize fields via kernel
            var initArgs = new Kernels.InitializeArgs
            {
                Labels = _dLabels,
                Vx = _dVx,
                Vy = _dVy,
                Vz = _dVz,
                Sxx = _dSxx,
                Syy = _dSyy,
                Szz = _dSzz,
                Sxy = _dSxy,
                Sxz = _dSxz,
                Syz = _dSyz,
                Damage = _dDamage,
                DispX = _dDispX,
                DispY = _dDispY,
                DispZ = _dDispZ,
                MatParams = _matParams,
                SimParams = _simParams
            };

            _initKernel(new Index3D(_width, _height, _depth), initArgs);
            _accelerator.Synchronize();
        }

        private double ComputeStableTimeStep()
        {
            // Find minimum density in material
            double rhoMin = double.MaxValue;

            for (int z = 0; z < _depth; z++)
            {
                for (int y = 0; y < _height; y++)
                {
                    for (int x = 0; x < _width; x++)
                    {
                        if (_labels[x, y, z] == _matID)
                        {
                            float rho = _density[x, y, z];
                            if (rho > 0 && rho < rhoMin) rhoMin = rho;
                        }
                    }
                }
            }

            if (rhoMin == double.MaxValue || rhoMin <= 0.0) rhoMin = 100.0;

            // P-wave velocity (m/s) = sqrt((lambda + 2*mu)/rho)
            double vpMax = Math.Sqrt((_matParams.Lambda + 2 * _matParams.Mu) / rhoMin);
            vpMax = Math.Min(vpMax, 6000.0);  // cap P-wave velocity

            // CFL condition with safety factor
            double safety = 0.2;
            double dt = safety * _pixelSize / vpMax;

            // Ensure dt is not too small
            if (dt < 1e-8) dt = 1e-8;

            Logger.Log($"[GPUTriaxialSimulator] Stable time step: {dt}");
            return dt;
        }

        private void FindMaterialBoundaries()
        {
            _minPos = 0;
            _maxPos = 0;

            switch (_pressureAxis)
            {
                case StressAxis.X:
                    // Find min and max X containing material
                    _minPos = _width - 1;
                    _maxPos = 0;
                    for (int x = 0; x < _width; x++)
                    {
                        bool containsMaterial = false;
                        for (int z = 0; z < _depth && !containsMaterial; z++)
                        {
                            for (int y = 0; y < _height && !containsMaterial; y++)
                            {
                                if (_labels[x, y, z] == _matID)
                                {
                                    containsMaterial = true;
                                    _minPos = Math.Min(_minPos, x);
                                    _maxPos = Math.Max(_maxPos, x);
                                }
                            }
                        }
                    }
                    break;

                case StressAxis.Y:
                    // Find min and max Y containing material
                    _minPos = _height - 1;
                    _maxPos = 0;
                    for (int y = 0; y < _height; y++)
                    {
                        bool containsMaterial = false;
                        for (int z = 0; z < _depth && !containsMaterial; z++)
                        {
                            for (int x = 0; x < _width && !containsMaterial; x++)
                            {
                                if (_labels[x, y, z] == _matID)
                                {
                                    containsMaterial = true;
                                    _minPos = Math.Min(_minPos, y);
                                    _maxPos = Math.Max(_maxPos, y);
                                }
                            }
                        }
                    }
                    break;

                case StressAxis.Z:
                default:
                    // Find min and max Z containing material
                    _minPos = _depth - 1;
                    _maxPos = 0;
                    for (int z = 0; z < _depth; z++)
                    {
                        bool containsMaterial = false;
                        for (int y = 0; y < _height && !containsMaterial; y++)
                        {
                            for (int x = 0; x < _width && !containsMaterial; x++)
                            {
                                if (_labels[x, y, z] == _matID)
                                {
                                    containsMaterial = true;
                                    _minPos = Math.Min(_minPos, z);
                                    _maxPos = Math.Max(_maxPos, z);
                                }
                            }
                        }
                    }
                    break;
            }

            // Calculate initial sample size for strain calculations
            _initialSampleSize = (_maxPos - _minPos + 1) * _pixelSize;
            Logger.Log($"[GPUTriaxialSimulator] Material bounds: min={_minPos}, max={_maxPos}, size={_initialSampleSize}");
        }

        private void InitializeStressState()
        {
            Logger.Log("[GPUTriaxialSimulator] Initializing stress state");

            // Set all stress components to confining pressure
            double confiningPa = _confiningPressure * 1e6;

            // Update material parameters with current confining pressure
            _matParams.ConfiningPressure = confiningPa;

            // Create initialization kernel arguments
            var initArgs = new Kernels.InitializeArgs
            {
                Labels = _dLabels,
                Vx = _dVx,
                Vy = _dVy,
                Vz = _dVz,
                Sxx = _dSxx,
                Syy = _dSyy,
                Szz = _dSzz,
                Sxy = _dSxy,
                Sxz = _dSxz,
                Syz = _dSyz,
                Damage = _dDamage,
                DispX = _dDispX,
                DispY = _dDispY,
                DispZ = _dDispZ,
                MatParams = _matParams,
                SimParams = _simParams
            };

            _initKernel(new Index3D(_width, _height, _depth), initArgs);
            _accelerator.Synchronize();

            // Apply boundary pressure to establish initial stress gradient
            ApplyBoundaryPressure(_initialAxialPressure * 1e6);
        }

        private void ApplyBoundaryPressure(double targetPressurePa)
        {
            // Update material parameters with confining pressure
            _matParams.ConfiningPressure = _confiningPressure * 1e6;

            // Apply axial pressure at boundary based on primary axis
            var boundaryArgs = new Kernels.BoundaryArgs
            {
                Labels = _dLabels,
                Sxx = _dSxx,
                Syy = _dSyy,
                Szz = _dSzz,
                MaterialID = _matID,
                ConfiningPressure = _confiningPressure * 1e6,
                AxialPressure = targetPressurePa,
                StressAxis = (int)_pressureAxis,
                MinBoundary = _minPos,
                MaxBoundary = _maxPos
            };

            _boundaryKernel(new Index3D(_width, _height, _depth), boundaryArgs);
            _accelerator.Synchronize();

            // Log the applied pressures for debugging
            Logger.Log($"[GPUTriaxialSimulator] Applied axial pressure {targetPressurePa / 1e6:F2} MPa on {_pressureAxis} axis (bounds: {_minPos}-{_maxPos})");
            Logger.Log($"[GPUTriaxialSimulator] Applied confining pressure {_confiningPressure} MPa on lateral axes");

            // Force an immediate measurement of stress-strain state to verify pressure application
            UpdateCurrentStrainAndStress(targetPressurePa / 1e6);
            Logger.Log($"[GPUTriaxialSimulator] After boundary application: strain={_currentStrain:F6}, stress={_currentStress:F2} MPa");

            // Apply force to ALL material voxels to help propagate pressure - simplest solution
            ApplyForceToAllMaterialVoxels(targetPressurePa, _confiningPressure * 1e6);
        }
        
        private void ApplyConfiningPressure(double pressurePa, StressAxis axis, int minBound, int maxBound)
        {
            var boundaryArgs = new Kernels.BoundaryArgs
            {
                Labels = _dLabels,
                Sxx = _dSxx,
                Syy = _dSyy,
                Szz = _dSzz,
                MaterialID = _matID,
                ConfiningPressure = pressurePa,
                AxialPressure = pressurePa, // Use same value for both
                StressAxis = (int)axis,
                MinBoundary = minBound,
                MaxBoundary = maxBound
            };

            _boundaryKernel(new Index3D(_width, _height, _depth), boundaryArgs);
            _accelerator.Synchronize();

            Logger.Log($"[GPUTriaxialSimulator] Applied confining pressure {pressurePa / 1e6:F2}MPa on {axis} axis");
        }
        private void ApplyForceToAllMaterialVoxels(double axialPressurePa, double confiningPressurePa)
        {
            // Create host arrays for direct manipulation
            double[,,] sxxHost = new double[_width, _height, _depth];
            double[,,] syyHost = new double[_width, _height, _depth];
            double[,,] szzHost = new double[_width, _height, _depth];

            // Copy stress fields from device to host
            _dSxx.CopyToCPU(sxxHost);
            _dSyy.CopyToCPU(syyHost);
            _dSzz.CopyToCPU(szzHost);

            // Modify stress fields based on axis
            int count = 0;
            for (int z = 0; z < _depth; z++)
            {
                for (int y = 0; y < _height; y++)
                {
                    for (int x = 0; x < _width; x++)
                    {
                        // Only modify material voxels
                        if (_labels[x, y, z] == _matID)
                        {
                            count++;

                            // Set stress based on axis
                            switch (_pressureAxis)
                            {
                                case StressAxis.X:
                                    sxxHost[x, y, z] = -axialPressurePa;
                                    syyHost[x, y, z] = -confiningPressurePa;
                                    szzHost[x, y, z] = -confiningPressurePa;
                                    break;

                                case StressAxis.Y:
                                    sxxHost[x, y, z] = -confiningPressurePa;
                                    syyHost[x, y, z] = -axialPressurePa;
                                    szzHost[x, y, z] = -confiningPressurePa;
                                    break;

                                default: // Z
                                    sxxHost[x, y, z] = -confiningPressurePa;
                                    syyHost[x, y, z] = -confiningPressurePa;
                                    szzHost[x, y, z] = -axialPressurePa;
                                    break;
                            }
                        }
                    }
                }
            }

            // Copy modified stress fields back to device
            _dSxx.CopyFromCPU(sxxHost);
            _dSyy.CopyFromCPU(syyHost);
            _dSzz.CopyFromCPU(szzHost);
            _accelerator.Synchronize();

            Logger.Log($"[GPUTriaxialSimulator] Applied initial stress state to {count} material voxels");
        }

        // -----------------------------------------------------------------------------
        //  CheckForFailure  (GPU version)
        //  host routine called every few kernel steps after copying the three buffers
        //  back from the device.  Density-weighted threshold:
        //      Dcrit = 0.75 × ρ/ρ₀
        // -----------------------------------------------------------------------------
        private bool CheckForFailure()
        {
            // First update the host arrays
            UpdateHostArraysFromDevice();

            // Use lower threshold in debug mode

            double baseCrit = _debugMode ? 0.5 : 0.75;

            // Add null safety check and logging
            if (_damageHost == null || _labelsHost == null || _densityHost == null)
            {
                Logger.Log("[GPUTriaxialSimulator] ERROR: Host arrays are null in failure check!");
                return false;
            }

            float rho0 = _refRho > 0 ? _refRho : 2500.0f; // Safety check
            int voxelN = _damageHost.Length;

            // Track statistics for logging
            float maxDamage = 0.0f;
            float maxRatio = 0.0f;
            int criticalVoxels = 0;
            float avgDensity = 0.0f;
            int materialVoxels = 0;

            // Find the maximum damage voxel and the voxel with max damage/threshold ratio
            int maxDamageX = -1, maxDamageY = -1, maxDamageZ = -1;

            // Convert flat index to 3D for tracking
            for (int i = 0; i < voxelN; i++)
            {
                if (_labelsHost[i] != _selectedMaterialID) continue;

                materialVoxels++;
                float rho = Math.Max(100.0f, _densityHost[i]);
                avgDensity += rho;

                float crit = (float)(baseCrit * (rho / rho0));
                float damage = _damageHost[i];
                float ratio = damage / crit;

                if (damage > maxDamage)
                {
                    maxDamage = damage;

                    // Convert linear index to 3D coordinates
                    int z = i / (_width * _height);
                    int remainder = i % (_width * _height);
                    int y = remainder / _width;
                    int x = remainder % _width;

                    maxDamageX = x;
                    maxDamageY = y;
                    maxDamageZ = z;
                }

                if (ratio > maxRatio)
                {
                    maxRatio = ratio;
                }

                if (damage > crit)
                {
                    criticalVoxels++;

                    Logger.Log($"[GPUTriaxialSimulator] {(_debugMode ? "DEBUG MODE" : "Normal mode")} " +
                              $"Failure detected at voxel {i}! Damage={damage:F3}, Density={rho:F0}, Threshold={crit:F3}, Ratio={ratio:F3}");
                    return true;
                }
            }

            // Calculate average density of material voxels
            if (materialVoxels > 0)
            {
                avgDensity /= materialVoxels;
            }

            // Detailed logging to help debug issues
            Logger.Log($"[GPUTriaxialSimulator] {(_debugMode ? "DEBUG MODE" : "Normal mode")} " +
                      $"No failure yet. Max damage={maxDamage:F3} at ({maxDamageX},{maxDamageY},{maxDamageZ}), " +
                      $"Max ratio={maxRatio:F3}, Material voxels={materialVoxels}, " +
                      $"Avg density={avgDensity:F0} kg/m³, Ref density={rho0:F0} kg/m³");

            return false;
        }
        private void UpdateHostArraysFromDevice()
        {
            try
            {
                // Copy damage array from GPU to CPU
                _dDamage.CopyToCPU(_damage);

                // Convert 3D arrays to 1D arrays for the failure check
                int index = 0;
                for (int z = 0; z < _depth; z++)
                {
                    for (int y = 0; y < _height; y++)
                    {
                        for (int x = 0; x < _width; x++)
                        {
                            if (index < _damageHost.Length)
                            {
                                _damageHost[index] = (float)_damage[x, y, z];
                                _labelsHost[index] = _labels[x, y, z];
                                _densityHost[index] = _density[x, y, z];
                                index++;
                            }
                        }
                    }
                }

                Logger.Log($"[GPUTriaxialSimulator] Updated host arrays: max damage = {_damageHost.Max()}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[GPUTriaxialSimulator] Error updating host arrays: {ex.Message}");
            }
        }
        /// <summary>
        /// Compute specimen-level axial strain (positive = compression) and stress
        /// after the latest batch of GPU kernels.  Strain is the change in gap
        /// between the *first* and *last* material-bearing slices; stress is the
        /// mean normal stress on those same two faces.
        /// </summary>
        /// <summary>
        /// Copy displacement & stress fields back from the GPU, then compute
        /// axial and two orthogonal radial strains and stresses, finally logging them.
        /// </summary>
        private void UpdateCurrentStrainAndStress(double targetPressureMPa)
        {
            /* ─── bring GPU fields back to the host ─────────────────────────── */
            _dDispX.CopyToCPU(_dispX);
            _dDispY.CopyToCPU(_dispY);
            _dDispZ.CopyToCPU(_dispZ);

            switch (_pressureAxis)
            {
                case StressAxis.X: _dSxx.CopyToCPU(_sxx); break;
                case StressAxis.Y: _dSyy.CopyToCPU(_syy); break;
                default: _dSzz.CopyToCPU(_szz); break;
            }

            /* ─── find global min/max displacement along the loading axis ────── */
            double dispMin = double.MaxValue;
            double dispMax = double.MinValue;

            for (int z = 0; z < _depth; z++)
                for (int y = 0; y < _height; y++)
                    for (int x = 0; x < _width; x++)
                    {
                        if (_labels[x, y, z] != _matID) continue;

                        double d = 0;
                        switch (_pressureAxis)
                        {
                            case StressAxis.X: d = _dispX[x, y, z]; break;
                            case StressAxis.Y: d = _dispY[x, y, z]; break;
                            default: d = _dispZ[x, y, z]; break;
                        }

                        if (d < dispMin) dispMin = d;
                        if (d > dispMax) dispMax = d;
                    }

            // if no voxels found (shouldn't happen), default to zero
            if (dispMin == double.MaxValue || dispMax == double.MinValue)
            {
                dispMin = dispMax = 0;
            }

            /*  Positive strain = compression (geomechanics convention)         */
            _currentStrain = -(dispMax - dispMin) / _initialSampleSize;

            /* ─── MODIFIED: focus on measuring boundary face stresses ─────────── */
            double sigSum = 0;
            int sigN = 0;

            // Only use boundary faces for immediate stress measurement
            switch (_pressureAxis)
            {
                case StressAxis.X:
                    for (int z = 0; z < _depth; z++)
                        for (int y = 0; y < _height; y++)
                        {
                            // Check both min and max boundaries
                            if (_labels[_minPos, y, z] == _matID)
                            {
                                sigSum += -_sxx[_minPos, y, z];
                                sigN++;
                            }
                            if (_labels[_maxPos, y, z] == _matID)
                            {
                                sigSum += -_sxx[_maxPos, y, z];
                                sigN++;
                            }
                        }
                    break;

                case StressAxis.Y:
                    for (int z = 0; z < _depth; z++)
                        for (int x = 0; x < _width; x++)
                        {
                            if (_labels[x, _minPos, z] == _matID)
                            {
                                sigSum += -_syy[x, _minPos, z];
                                sigN++;
                            }
                            if (_labels[x, _maxPos, z] == _matID)
                            {
                                sigSum += -_syy[x, _maxPos, z];
                                sigN++;
                            }
                        }
                    break;

                default: // Z
                    for (int y = 0; y < _height; y++)
                        for (int x = 0; x < _width; x++)
                        {
                            if (_labels[x, y, _minPos] == _matID)
                            {
                                sigSum += -_szz[x, y, _minPos];
                                sigN++;
                            }
                            if (_labels[x, y, _maxPos] == _matID)
                            {
                                sigSum += -_szz[x, y, _maxPos];
                                sigN++;
                            }
                        }
                    break;
            }

            // CRITICAL FIX: If no boundary measurements, use the target pressure
            if (sigN == 0)
            {
                _currentStress = targetPressureMPa;
                Logger.Log($"[GPUTriaxialSimulator] No boundary measurements available, using target pressure: {_currentStress:F2} MPa");
            }
            else
            {
                _currentStress = (sigN > 0 ? sigSum / sigN : 0.0) / 1e6;   // Pa → MPa
            }

            Logger.Log($"[GPUTriaxialSimulator] Current strain={_currentStrain:F4}, " +
                       $"stress={_currentStress:F2} MPa, sampled stress from {sigN} boundary points");
        }
        private void FinalizeSimulation()
        {
            // If cancelled, notify that we're done
            if (_simulationCancelled)
            {
                SimulationCompleted?.Invoke(this, new TriaxialSimulationCompleteEventArgs(
                    _strainHistory.ToArray(), _stressHistory.ToArray(), _failureDetected, _failureStep));
                return;
            }

            // Final progress update
            ProgressUpdated?.Invoke(this, new TriaxialSimulationProgressEventArgs(
                100, _pressureIncrements, "Completed"));

            // Create completion event with stress-strain history
            var resultArgs = new TriaxialSimulationCompleteEventArgs(
                _strainHistory.ToArray(),
                _stressHistory.ToArray(),
                _failureDetected,
                _failureStep);

            // Notify completion
            SimulationCompleted?.Invoke(this, resultArgs);
        }
        /// <summary>
        /// Copy damage data from GPU to CPU for visualization
        /// </summary>
        /// <param name="destArray">Destination array to receive damage data</param>
        public void CopyDamageToCPU(double[,,] destinationArray)
        {
            if (destinationArray == null ||
                destinationArray.GetLength(0) != _width ||
                destinationArray.GetLength(1) != _height ||
                destinationArray.GetLength(2) != _depth)
            {
                throw new ArgumentException("Destination array has incorrect dimensions");
            }

            // Copy damage data from device to host
            _dDamage.CopyToCPU(_damage);

            // Copy from internal array to the provided destination array
            Array.Copy(_damage, destinationArray, _damage.Length);
        }
        /// <summary>
        /// Find the point with maximum damage
        /// </summary>
        /// <returns>Coordinates of the point with maximum damage</returns>
        public (int x, int y, int z) FindMaxDamagePoint()
        {
            // First copy damage data from GPU to CPU
            _dDamage.CopyToCPU(_damage);

            int maxX = 0, maxY = 0, maxZ = 0;
            double maxDamage = 0;

            // Find the maximum damage value and its location
            for (int z = 0; z < _depth; z++)
            {
                for (int y = 0; y < _height; y++)
                {
                    for (int x = 0; x < _width; x++)
                    {
                        // Only consider points that are part of the selected material
                        if (_labels[x, y, z] == _matID && _damage[x, y, z] > maxDamage)
                        {
                            maxDamage = _damage[x, y, z];
                            maxX = x;
                            maxY = y;
                            maxZ = z;
                        }
                    }
                }
            }

            return (maxX, maxY, maxZ);
        }
        #endregion

        #region Kernel Classes
        private static class Kernels
        {
            public struct StressArgs           // ← no readonly
            {
                // 3-D ArrayViews
                public ArrayView3D<byte, Stride3D.DenseXY> Labels;
                public ArrayView3D<float, Stride3D.DenseXY> Density;
                public ArrayView3D<double, Stride3D.DenseXY> Vx, Vy, Vz;
                public ArrayView3D<double, Stride3D.DenseXY> Sxx, Syy, Szz, Sxy, Sxz, Syz;
                public ArrayView3D<double, Stride3D.DenseXY> Damage;

                // parameter blocks
                public MaterialParams MatParams;
                public SimulationParams SimParams;

                // feature switches (keep as bytes to stay blittable)
                public byte UsePlastic;
                public byte UseBrittle;
            }


            public struct VelocityArgs
            {
                public ArrayView3D<byte, Stride3D.DenseXY> Labels;
                public ArrayView3D<float, Stride3D.DenseXY> Density;
                public ArrayView3D<double, Stride3D.DenseXY> Vx, Vy, Vz;
                public ArrayView3D<double, Stride3D.DenseXY> Sxx, Syy, Szz, Sxy, Sxz, Syz;
                public SimulationParams SimParams;
            }

            public struct DisplacementArgs
            {
                public ArrayView3D<byte, Stride3D.DenseXY> Labels;
                public ArrayView3D<double, Stride3D.DenseXY> Vx, Vy, Vz;
                public ArrayView3D<double, Stride3D.DenseXY> DispX, DispY, DispZ;
                public SimulationParams SimParams;
            }

            public struct BoundaryArgs
            {
                public ArrayView3D<byte, Stride3D.DenseXY> Labels;
                public ArrayView3D<double, Stride3D.DenseXY> Sxx, Syy, Szz;
                public byte MaterialID;
                public double ConfiningPressure;
                public double AxialPressure;
                public int StressAxis;
                public int MinBoundary;
                public int MaxBoundary;
            }

            public struct InitializeArgs
            {
                public ArrayView3D<byte, Stride3D.DenseXY> Labels;
                public ArrayView3D<double, Stride3D.DenseXY> Vx, Vy, Vz;
                public ArrayView3D<double, Stride3D.DenseXY> Sxx, Syy, Szz, Sxy, Sxz, Syz;
                public ArrayView3D<double, Stride3D.DenseXY> Damage, DispX, DispY, DispZ;
                public MaterialParams MatParams;
                public SimulationParams SimParams;
            }
            public static void InitializeStressState(Index3D idx, InitializeArgs a)
            {
                int x = idx.X, y = idx.Y, z = idx.Z;

                // Only initialize stress for material voxels
                if (a.Labels[x, y, z] != a.SimParams.MaterialID)
                    return;

                // Get axis for applying different pressures
                int axisDirection = a.SimParams.StressAxis;
                double axialPressure = a.MatParams.ConfiningPressure;
                double confiningPressure = a.MatParams.ConfiningPressure;

                // Set stress components based on axis
                if (axisDirection == 0) // X-axis
                {
                    a.Sxx[x, y, z] = -axialPressure;
                    a.Syy[x, y, z] = -confiningPressure;
                    a.Szz[x, y, z] = -confiningPressure;
                }
                else if (axisDirection == 1) // Y-axis
                {
                    a.Sxx[x, y, z] = -confiningPressure;
                    a.Syy[x, y, z] = -axialPressure;
                    a.Szz[x, y, z] = -confiningPressure;
                }
                else // Z-axis
                {
                    a.Sxx[x, y, z] = -confiningPressure;
                    a.Syy[x, y, z] = -confiningPressure;
                    a.Szz[x, y, z] = -axialPressure;
                }

                // Keep shear stresses at zero initially
                a.Sxy[x, y, z] = 0.0;
                a.Sxz[x, y, z] = 0.0;
                a.Syz[x, y, z] = 0.0;
            }
            // ─────────────────────────────────────────────────────────────────────────────
            //  UpdateStress  (ILGPU kernel)
            //
            //  Works with the original layout you had:
            //
            //  struct StressArgs
            //  {
            //      public ArrayView<float> Sxx,Syy,Szz,Sxy,Sxz,Syz;
            //      public ArrayView<float> Vx,Vy,Vz;
            //      public ArrayView<float> Damage;
            //      public ArrayView<ushort> Labels;
            //      public ArrayView<float>  Density;
            //
            //      public SimParams  Sim;   // dt, dx, NX, NY, NZ
            //      public MatParams  Mat;   // λ0, μ0, coh0, σt0, sinφ, cosφ, ρ₀, matID
            //      public byte UsePlastic;
            //      public byte UseBrittle;
            //  }
            //
            //  (If your field names differ slightly, adjust in THREE places at the
            //   top of the method; nothing else assigns to any field.)
            // ─────────────────────────────────────────────────────────────────────────────
            public static void UpdateStress(Index3D tid, StressArgs a)
            {
                // ───── shorthand handles ────────────────────────────────────────────────
                SimulationParams sp = a.SimParams;
                MaterialParams mp = a.MatParams;

                int x = tid.X + 1;
                int y = tid.Y + 1;
                int z = tid.Z + 1;

                // inner computational domain – we need one-voxel halo for centred stencils
                if (x >= sp.Width - 1 || y >= sp.Height - 1 || z >= sp.Depth - 1)
                    return;

                if (a.Labels[x, y, z] != sp.MaterialID)
                    return;

                // ───── density–scaled Lamé parameters with damage softening ─────────────
                float rho = XMath.Max(100.0f, a.Density[x, y, z]);
                float rhoFact = rho / sp.RefDensity;

                float Dprev = (float)a.Damage[x, y, z];
                float lam = (1.0f - Dprev) * (float)mp.Lambda * rhoFact;
                float mu = (1.0f - Dprev) * (float)mp.Mu * rhoFact;

                // ───── velocity gradients (central differences) ─────────────────────────
                float inv2dx = 0.5f / sp.PixelSize;

                float dvx_dx = (float)((a.Vx[x + 1, y, z] - a.Vx[x - 1, y, z]) * inv2dx);
                float dvy_dy = (float)((a.Vy[x, y + 1, z] - a.Vy[x, y - 1, z]) * inv2dx);
                float dvz_dz = (float)((a.Vz[x, y, z + 1] - a.Vz[x, y, z - 1]) * inv2dx);

                float dvx_dy = (float)((a.Vx[x, y + 1, z] - a.Vx[x, y - 1, z]) * inv2dx);
                float dvx_dz = (float)((a.Vx[x, y, z + 1] - a.Vx[x, y, z - 1]) * inv2dx);
                float dvy_dx = (float)((a.Vy[x + 1, y, z] - a.Vy[x - 1, y, z]) * inv2dx);
                float dvy_dz = (float)((a.Vy[x, y, z + 1] - a.Vy[x, y, z - 1]) * inv2dx);
                float dvz_dx = (float)((a.Vz[x + 1, y, z] - a.Vz[x - 1, y, z]) * inv2dx);
                float dvz_dy = (float)((a.Vz[x, y + 1, z] - a.Vz[x, y - 1, z]) * inv2dx);

                // ───── elastic predictor (linear momentum) ──────────────────────────────
                float dt = (float)sp.TimeStep;

                float SxxN = (float)(a.Sxx[x, y, z] + dt * ((lam + 2f * mu) * dvx_dx + lam * (dvy_dy + dvz_dz)));
                float SyyN = (float)(a.Syy[x, y, z] + dt * ((lam + 2f * mu) * dvy_dy + lam * (dvx_dx + dvz_dz)));
                float SzzN = (float)(a.Szz[x, y, z] + dt * ((lam + 2f * mu) * dvz_dz + lam * (dvx_dx + dvy_dy)));

                float SxyN = (float)(a.Sxy[x, y, z] + dt * (mu * (dvy_dx + dvx_dy)));
                float SxzN = (float)(a.Sxz[x, y, z] + dt * (mu * (dvz_dx + dvx_dz)));
                float SyzN = (float)(a.Syz[x, y, z] + dt * (mu * (dvz_dy + dvy_dz)));

                // ───── Mohr-Coulomb return-mapping (plastic corrector) ──────────────────
                float Dnew = Dprev;                             // we may increase this later
                float mean = (SxxN + SyyN + SzzN) * 0.3333333f; // −p  (geomechanics sign)
                float dxx = SxxN - mean;
                float dyy = SyyN - mean;
                float dzz = SzzN - mean;

                float J2 = 0.5f * (dxx * dxx + dyy * dyy + dzz * dzz) +
                             (SxyN * SxyN + SxzN * SxzN + SyzN * SyzN);
                float tau = XMath.Sqrt(XMath.Max(J2, 0f));
                float p = -mean;

                float coh = (float)mp.Cohesion * rhoFact;
                float sinφ = (float)mp.FrictionSinPhi;
                float cosφ = (float)mp.FrictionCosPhi;

                float yield = tau + p * sinφ - coh * cosφ;

                if (((mp.UseModelFlags & 2) != 0) && yield > 0f)   // plastic model on
                {
                    float fac = (tau > 1e-7f) ? yield / tau : 0f;
                    fac = XMath.Min(fac, 0.95f);

                    dxx *= (1f - fac); dyy *= (1f - fac); dzz *= (1f - fac);
                    SxyN *= (1f - fac); SxzN *= (1f - fac); SyzN *= (1f - fac);

                    SxxN = dxx + mean; SyyN = dyy + mean; SzzN = dzz + mean;

                    // ===== **new** shear-damage coupling
                    if ((mp.UseModelFlags & 4) != 0)
                    {
                        float dD = XMath.Min(fac * 0.02f, 0.005f);    // faster in debug mode
                        if (sp.DebugMode != 0) dD *= 4f;
                        Dnew = XMath.Min(Dprev + dD, 0.99f);
                    }
                }

                // ───── tensile damage (unchanged) ───────────────────────────────────────
                if ((mp.UseModelFlags & 4) != 0)
                {
                    float σmax = XMath.Max(SxxN, XMath.Max(SyyN, SzzN));   // +tension
                    float σT = (float)mp.TensileStrength * rhoFact;

                    if (σmax > σT && Dnew < 0.99f)
                    {
                        float over = (σmax - σT) / σT;
                        float dD = (sp.DebugMode != 0) ? XMath.Min(over * 0.5f, 0.05f)
                                                         : XMath.Min(over * 0.01f, 0.002f);

                        Dnew = XMath.Min(Dnew + dD, 0.99f);
                    }
                }

                // ───── damage softening applied once, using *incremental* dD ────────────
                float soften = (Dnew > Dprev) ? (1f - (Dnew - Dprev)) : 1f;

                SxxN *= soften; SyyN *= soften; SzzN *= soften;
                SxyN *= soften; SxzN *= soften; SyzN *= soften;

                // ───── store back to global memory ──────────────────────────────────────
                a.Sxx[x, y, z] = SxxN; a.Syy[x, y, z] = SyyN; a.Szz[x, y, z] = SzzN;
                a.Sxy[x, y, z] = SxyN; a.Sxz[x, y, z] = SxzN; a.Syz[x, y, z] = SyzN;
                a.Damage[x, y, z] = Dnew;
            }

            public static void UpdateVelocity(Index3D idx, VelocityArgs a)
            {
                int x = idx.X, y = idx.Y, z = idx.Z;
                var sp = a.SimParams;
                if (x <= 0 || x >= sp.Width - 1 ||
                    y <= 0 || y >= sp.Height - 1 ||
                    z <= 0 || z >= sp.Depth - 1) return;
                if (a.Labels[x, y, z] != sp.MaterialID) return;

                double dx = sp.PixelSize;
                double inv2dx = 1.0 / (2 * dx);
                double dt = sp.TimeStep;
                double damp = 1.0 - sp.Damping;

                // compute stress divergence
                double fx = (a.Sxx[x + 1, y, z] - a.Sxx[x - 1, y, z])
                          + (a.Sxy[x, y + 1, z] - a.Sxy[x, y - 1, z])
                          + (a.Sxz[x, y, z + 1] - a.Sxz[x, y, z - 1]);
                double fy = (a.Sxy[x + 1, y, z] - a.Sxy[x - 1, y, z])
                          + (a.Syy[x, y + 1, z] - a.Syy[x, y - 1, z])
                          + (a.Syz[x, y, z + 1] - a.Syz[x, y, z - 1]);
                double fz = (a.Sxz[x + 1, y, z] - a.Sxz[x - 1, y, z])
                          + (a.Syz[x, y + 1, z] - a.Syz[x, y - 1, z])
                          + (a.Szz[x, y, z + 1] - a.Szz[x, y, z - 1]);

                double rho = Math.Max(100.0, a.Density[x, y, z]);
                a.Vx[x, y, z] = damp * (a.Vx[x, y, z] + dt * fx * inv2dx / rho);
                a.Vy[x, y, z] = damp * (a.Vy[x, y, z] + dt * fy * inv2dx / rho);
                a.Vz[x, y, z] = damp * (a.Vz[x, y, z] + dt * fz * inv2dx / rho);
            }

            /// <summary>
            /// Explicit time-integration of the displacement field
            ///     u  ←  u  +  v Δt
            /// for **every material voxel**, including the two boundary faces that carry
            /// the axial load.  We trust the <c>Labels</c> array to exclude ghost cells,
            /// so we no longer hard-code an index guard that froze those faces.
            /// </summary>
            public static void UpdateDisplacement(Index3D idx, DisplacementArgs a)
            {
                int x = idx.X, y = idx.Y, z = idx.Z;

                /* Update **every** voxel that belongs to the specimen.
                   We do NOT exclude the boundary slices: whether a cell may move is
                   decided purely by its material label, not by its index position.   */
                if (a.Labels[x, y, z] != a.SimParams.MaterialID)
                    return;

                double dt = a.SimParams.TimeStep;

                a.DispX[x, y, z] += a.Vx[x, y, z] * dt;
                a.DispY[x, y, z] += a.Vy[x, y, z] * dt;
                a.DispZ[x, y, z] += a.Vz[x, y, z] * dt;
            }


            public static void ApplyBoundaryConditions(Index3D idx, BoundaryArgs a)
            {
                int x = idx.X, y = idx.Y, z = idx.Z;

                // Skip non-material voxels
                if (a.Labels[x, y, z] != a.MaterialID)
                    return;

                // Apply stress based on axis
                switch (a.StressAxis)
                {
                    case 0: // X-axis
                        if (x == a.MaxBoundary || x == a.MinBoundary)
                        {
                            a.Sxx[x, y, z] = -a.AxialPressure;
                        }
                        else
                        {
                            a.Sxx[x, y, z] = -a.ConfiningPressure;
                        }
                        break;

                    case 1: // Y-axis
                        if (y == a.MaxBoundary || y == a.MinBoundary)
                        {
                            a.Syy[x, y, z] = -a.AxialPressure;
                        }
                        else
                        {
                            a.Syy[x, y, z] = -a.ConfiningPressure;
                        }
                        break;

                    case 2: // Z-axis (default)
                    default:
                        if (z == a.MaxBoundary || z == a.MinBoundary)
                        {
                            a.Szz[x, y, z] = -a.AxialPressure;
                        }
                        else
                        {
                            a.Szz[x, y, z] = -a.ConfiningPressure;
                        }
                        break;
                }
            }
            public static Point3D FindMaxDamagePoint(double[,,] damage, int width, int height, int depth, byte materialID, byte[,,] labels)
            {
                // Find the point with maximum damage
                double maxDamage = 0.0;
                int maxX = 0, maxY = 0, maxZ = 0;

                for (int z = 0; z < depth; z++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            if (labels[x, y, z] == materialID && damage[x, y, z] > maxDamage)
                            {
                                maxDamage = damage[x, y, z];
                                maxX = x;
                                maxY = y;
                                maxZ = z;
                            }
                        }
                    }
                }

                return new Point3D(maxX, maxY, maxZ);
            }

            
            public static void InitializeFields(Index3D idx, InitializeArgs a)
            {
                int x = idx.X, y = idx.Y, z = idx.Z;

                // Zero velocities and displacements for all voxels
                a.Vx[x, y, z] = 0.0;
                a.Vy[x, y, z] = 0.0;
                a.Vz[x, y, z] = 0.0;
                a.DispX[x, y, z] = 0.0;
                a.DispY[x, y, z] = 0.0;
                a.DispZ[x, y, z] = 0.0;

                // Initialize stresses for material voxels
                if (a.Labels[x, y, z] == a.SimParams.MaterialID)
                {
                    double confP = a.MatParams.ConfiningPressure;
                    a.Sxx[x, y, z] = -confP;
                    a.Syy[x, y, z] = -confP;
                    a.Szz[x, y, z] = -confP;
                    a.Sxy[x, y, z] = 0.0;
                    a.Sxz[x, y, z] = 0.0;
                    a.Syz[x, y, z] = 0.0;
                    a.Damage[x, y, z] = 0.0;
                }
                else
                {
                    a.Sxx[x, y, z] = 0.0;
                    a.Syy[x, y, z] = 0.0;
                    a.Szz[x, y, z] = 0.0;
                    a.Sxy[x, y, z] = 0.0;
                    a.Sxz[x, y, z] = 0.0;
                    a.Syz[x, y, z] = 0.0;
                    a.Damage[x, y, z] = 0.0;
                }
            }
        }
        // ============================================================================
        //  UpdateStressKernel  (ILGPU, C# 7.3 compatible)
        //  --------------------------------------------------------------------------
        //  * density-scaled elastic predictor
        //  * Mohr–Coulomb projector   (optional)
        //  * tensile damage           (optional)
        //  * damage-softening of λ, μ
        //
        //  Flat 1–D ArrayView buffers, X-major layout:
        //
        //    lin = (z * NY + y) * NX + x
        // ============================================================================

        static void UpdateStressKernel(
            Index3D idx,

            ArrayView<float> Sxx,
            ArrayView<float> Syy,
            ArrayView<float> Szz,
            ArrayView<float> Sxy,
            ArrayView<float> Sxz,
            ArrayView<float> Syz,

            ArrayView<float> Vx,
            ArrayView<float> Vy,
            ArrayView<float> Vz,

            ArrayView<float> Damage,
            ArrayView<ushort> Labels,
            ArrayView<float> Density,

            float lambda0,          // Pa @ ρ = ρ₀, D = 0
            float mu0,              // Pa
            float cohesionPa0,      // Pa
            float tensilePa0,       // Pa
            float sinPhi,
            float cosPhi,
            float dt,               // s
            float dx,               // m
            float refRho,           // ρ₀  (kg m-3)
            byte matID,
            byte usePlastic,       // 0 / 1
            byte useBrittle,       // 0 / 1
            int NX,
            int NY,
            int NZ)
        {
            // -------- bounds & material mask ---------------------------------------
            int x = idx.X + 1;
            int y = idx.Y + 1;
            int z = idx.Z + 1;

            if (x >= NX - 1 || y >= NY - 1 || z >= NZ - 1)
                return;

            int lin = (z * NY + y) * NX + x;
            if (Labels[lin] != matID)
                return;

            // -------- neighbours' linear indices -----------------------------------
            int xm1 = lin - 1; int xp1 = lin + 1;
            int ym1 = lin - NX; int yp1 = lin + NX;
            int zm1 = lin - NX * NY; int zp1 = lin + NX * NY;

            // -------- density ratio & damage-scaled Lamé ---------------------------
            float rho = XMath.Max(100.0f, Density[lin]);
            float r = rho / refRho;

            float D = Damage[lin];
            float lam = (1.0f - D) * lambda0 * r;
            float mu = (1.0f - D) * mu0 * r;

            // -------- velocity gradients (central diff) ----------------------------
            float inv2dx = 0.5f / dx;

            float dvx_dx = (Vx[xp1] - Vx[xm1]) * inv2dx;
            float dvy_dy = (Vy[yp1] - Vy[ym1]) * inv2dx;
            float dvz_dz = (Vz[zp1] - Vz[zm1]) * inv2dx;

            float dvx_dy = (Vx[yp1] - Vx[ym1]) * inv2dx;
            float dvx_dz = (Vx[zp1] - Vx[zm1]) * inv2dx;
            float dvy_dx = (Vy[xp1] - Vy[xm1]) * inv2dx;
            float dvy_dz = (Vy[zp1] - Vy[zm1]) * inv2dx;
            float dvz_dx = (Vz[xp1] - Vz[xm1]) * inv2dx;
            float dvz_dy = (Vz[yp1] - Vz[ym1]) * inv2dx;

            // -------- elastic predictor -------------------------------------------
            float SxxN = Sxx[lin] + dt * ((lam + 2 * mu) * dvx_dx + lam * (dvy_dy + dvz_dz));
            float SyyN = Syy[lin] + dt * ((lam + 2 * mu) * dvy_dy + lam * (dvx_dx + dvz_dz));
            float SzzN = Szz[lin] + dt * ((lam + 2 * mu) * dvz_dz + lam * (dvx_dx + dvy_dy));

            float SxyN = Sxy[lin] + dt * (mu * (dvy_dx + dvx_dy));
            float SxzN = Sxz[lin] + dt * (mu * (dvz_dx + dvx_dz));
            float SyzN = Syz[lin] + dt * (mu * (dvz_dy + dvy_dz));

            // -------- Mohr–Coulomb plasticity --------------------------------------
            if (usePlastic != 0)
            {
                float mean = (SxxN + SyyN + SzzN) * 0.33333333f;
                float dxx = SxxN - mean;
                float dyy = SyyN - mean;
                float dzz = SzzN - mean;

                float J2 = 0.5f * (dxx * dxx + dyy * dyy + dzz * dzz) +
                           (SxyN * SxyN + SxzN * SxzN + SyzN * SyzN);
                float tau = XMath.Sqrt(XMath.Max(J2, 0.0f));

                float p = -mean;
                float cohesion = cohesionPa0 * r;

                float yield = tau + p * sinPhi - cohesion * cosPhi;

                if (yield > 0.0f)
                {
                    float safeTau = tau > 1e-7f ? tau : 1e-7f;
                    float a = (tau - (cohesion * cosPhi - p * sinPhi)) / safeTau;
                    a = XMath.Min(a, 0.95f);

                    dxx *= (1 - a);
                    dyy *= (1 - a);
                    dzz *= (1 - a);
                    SxyN *= (1 - a);
                    SxzN *= (1 - a);
                    SyzN *= (1 - a);

                    SxxN = dxx + mean;
                    SyyN = dyy + mean;
                    SzzN = dzz + mean;
                }
            }

            // -------- tensile damage ----------------------------------------------
            if (useBrittle != 0)
            {
                float sigmaMax = XMath.Max(SxxN, XMath.Max(SyyN, SzzN));
                float tensile = tensilePa0 * r;

                if (sigmaMax > tensile && D < 0.99f)
                {
                    float over = (sigmaMax - tensile) / tensile;
                    float dD = XMath.Min(over * 0.01f, 0.001f);
                    D += dD;
                    D = XMath.Min(D, 0.99f);
                    Damage[lin] = D;

                    float soften = 1.0f - dD;
                    SxxN *= soften; SyyN *= soften; SzzN *= soften;
                    SxyN *= soften; SxzN *= soften; SyzN *= soften;
                }
            }

            // -------- store back ---------------------------------------------------
            Sxx[lin] = SxxN; Syy[lin] = SyyN; Szz[lin] = SzzN;
            Sxy[lin] = SxyN; Sxz[lin] = SxzN; Syz[lin] = SyzN;
        }

        #endregion

        #region IDisposable Support
        public void Dispose()
        {
            Logger.Log("[GPUTriaxialSimulator] Disposing resources");

            try
            {
                // Cancel any running simulation first
                _simulationCancelled = true;
                _cts?.Cancel();

                // Try to join the simulation task to ensure it's complete
                try
                {
                    _simulationTask?.Wait(TimeSpan.FromSeconds(2));
                }
                catch (Exception ex)
                {
                    Logger.Log($"[GPUTriaxialSimulator] Warning: Task join exception: {ex.Message}");
                }

                // Ensure accelerator is synchronized before disposing resources
                try
                {
                    _accelerator?.Synchronize();
                }
                catch (Exception ex)
                {
                    Logger.Log($"[GPUTriaxialSimulator] Warning: Sync exception: {ex.Message}");
                }

                // Dispose GPU resources in reverse order of allocation
                // Device buffers first
                try
                {
                    _dDispZ?.Dispose();
                    _dDispY?.Dispose();
                    _dDispX?.Dispose();
                    _dDamage?.Dispose();
                    _dSyz?.Dispose();
                    _dSxz?.Dispose();
                    _dSxy?.Dispose();
                    _dSzz?.Dispose();
                    _dSyy?.Dispose();
                    _dSxx?.Dispose();
                    _dVz?.Dispose();
                    _dVy?.Dispose();
                    _dVx?.Dispose();
                    _dDensity?.Dispose();
                    _dLabels?.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.Log($"[GPUTriaxialSimulator] Warning: Buffer dispose exception: {ex.Message}");
                }

                // Finally dispose accelerator and context
                try
                {
                    _accelerator?.Dispose();
                    _context?.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.Log($"[GPUTriaxialSimulator] Warning: Accelerator/context dispose exception: {ex.Message}");
                }

                // Clear event handlers
                ProgressUpdated = null;
                FailureDetected = null;
                SimulationCompleted = null;

                Logger.Log("[GPUTriaxialSimulator] Disposal complete");
            }
            catch (Exception ex)
            {
                Logger.Log($"[GPUTriaxialSimulator] Error during disposal: {ex.Message}");
            }
        }
        #endregion
    }
}