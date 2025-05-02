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
        public bool DebugMode; 
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

            for (int z = 0; z < _depth; z++)
            {
                for (int y = 0; y < _height; y++)
                {
                    for (int x = 0; x < _width; x++)
                    {
                        if (labels[x, y, z] == matID && density[x, y, z] > 0)
                        {
                            sum += density[x, y, z];
                            count++;
                        }
                    }
                }
            }

            return count > 0 ? (float)(sum / count) : 2500.0f; // Default to typical rock density
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

                    // Apply pressure at boundary
                    ApplyBoundaryPressure(targetAxialPa);

                    // Run time steps for this increment
                    RunTimeSteps(inc, targetAxialMPa);

                    // Calculate current strain and stress
                    UpdateCurrentStrainAndStress(targetAxialMPa);

                    // Record current stress-strain state
                    _strainHistory.Add(_currentStrain);
                    _stressHistory.Add(_currentStress);

                    // Report progress
                    int percentComplete = (int)((double)inc / _pressureIncrements * 100.0);
                    ProgressUpdated?.Invoke(this, new TriaxialSimulationProgressEventArgs(
                        percentComplete, inc, $"Loading: {targetAxialMPa:F2} MPa"));
                }

                // Complete the simulation
                FinalizeSimulation();
            }
            catch (OperationCanceledException)
            {
                Logger.Log("[GPUTriaxialSimulator] Simulation cancelled via exception");
                ProgressUpdated?.Invoke(this, new TriaxialSimulationProgressEventArgs(0, 0, "Cancelled"));
            }
            catch (Exception ex)
            {
                Logger.Log($"[GPUTriaxialSimulator] Error during simulation: {ex.Message}");
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
                Damping = 0.05,
                StressAxis = (int)_pressureAxis
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
            // Set all stress components to confining pressure
            double confiningPa = _confiningPressure * 1e6; // Negative for compression

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
        }

        private void ApplyBoundaryPressure(double targetPressurePa)
        {
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
        }

        private void RunTimeSteps(int incrementNumber, double targetPressureMPa)
        {
            // Run time steps for this increment to equilibrate stresses
            for (int step = 0; step < _stepsPerIncrement; step++)
            {
                if (_simulationCancelled || _cts.Token.IsCancellationRequested)
                    break;

                // Wait while paused
                while (_simulationPaused && !_simulationCancelled && !_cts.Token.IsCancellationRequested)
                {
                    Thread.Sleep(100);
                }

                if (_simulationCancelled || _cts.Token.IsCancellationRequested)
                    break;

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

                // Progress update (every 10 steps)
                if (step % 10 == 0)
                {
                    UpdateCurrentStrainAndStress(targetPressureMPa);
                    int percent = (int)((double)incrementNumber / _pressureIncrements * 100.0);
                    string status = $"Loading: {targetPressureMPa:F2} MPa, Step {step + 1}/{_stepsPerIncrement}";
                    ProgressUpdated?.Invoke(this, new TriaxialSimulationProgressEventArgs(percent, incrementNumber, status));
                }

                // Check for failure every few steps
                if (step % 5 == 0 && !_failureDetected)
                {
                    // UpdateHostArraysFromDevice is now called inside CheckForFailure
                    if (CheckForFailure())
                    {
                        _failureDetected = true;
                        _failureStep = incrementNumber;

                        // Add more detailed logging
                        Logger.Log($"[GPUTriaxialSimulator] FAILURE DETECTED at step {incrementNumber}, stress={_currentStress:F2} MPa, strain={_currentStrain:F4}");

                        // Notify failure detected
                        var failureArgs = new FailureDetectedEventArgs(_currentStress, _currentStrain, incrementNumber, _pressureIncrements);
                        FailureDetected?.Invoke(this, failureArgs);

                        // Pause simulation to let user decide whether to continue
                        _simulationPaused = true;
                    }
                }
            }
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
            float baseCrit = _debugMode ? 0.15f : 0.75f;

            // Add null safety check and logging
            if (_damageHost == null || _labelsHost == null || _densityHost == null)
            {
                Logger.Log("[GPUTriaxialSimulator] ERROR: Host arrays are null in failure check!");
                return false;
            }

            float rho0 = _refRho > 0 ? _refRho : 2500.0f; // Safety check
            int voxelN = _damageHost.Length;

            float maxDamage = 0.0f;
            for (int i = 0; i < voxelN; i++)
            {
                if (_labelsHost[i] != _selectedMaterialID) continue;

                float rho = Math.Max(100.0f, _densityHost[i]);
                float crit = baseCrit * (rho / rho0);

                maxDamage = Math.Max(maxDamage, _damageHost[i]);

                if (_damageHost[i] > crit)
                {
                    Logger.Log($"[GPUTriaxialSimulator] {(_debugMode ? "DEBUG MODE" : "Normal mode")} " +
                              $"Failure detected! Damage={_damageHost[i]}, Threshold={crit}");
                    return true;
                }
            }

            Logger.Log($"[GPUTriaxialSimulator] {(_debugMode ? "DEBUG MODE" : "Normal mode")} " +
                      $"No failure yet. Max damage={maxDamage}");
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

        private void UpdateCurrentStrainAndStress(double targetPressureMPa)
        {
            // Copy displacement and stress fields back to CPU to compute average values
            _dDispX.CopyToCPU(_dispX);
            _dDispY.CopyToCPU(_dispY);
            _dDispZ.CopyToCPU(_dispZ);

            switch (_pressureAxis)
            {
                case StressAxis.X:
                    _dSxx.CopyToCPU(_sxx);
                    break;
                case StressAxis.Y:
                    _dSyy.CopyToCPU(_syy);
                    break;
                case StressAxis.Z:
                    _dSzz.CopyToCPU(_szz);
                    break;
            }

            // Calculate average strain along primary axis
            double avgDisp = 0.0;
            int count = 0;

            switch (_pressureAxis)
            {
                case StressAxis.X:
                    // Measure X displacement at max boundary
                    for (int z = 0; z < _depth; z++)
                    {
                        for (int y = 0; y < _height; y++)
                        {
                            if (_labels[_maxPos, y, z] == _matID)
                            {
                                avgDisp += _dispX[_maxPos, y, z];
                                count++;
                            }
                        }
                    }
                    break;

                case StressAxis.Y:
                    // Measure Y displacement at max boundary
                    for (int z = 0; z < _depth; z++)
                    {
                        for (int x = 0; x < _width; x++)
                        {
                            if (_labels[x, _maxPos, z] == _matID)
                            {
                                avgDisp += _dispY[x, _maxPos, z];
                                count++;
                            }
                        }
                    }
                    break;

                case StressAxis.Z:
                default:
                    // Measure Z displacement at max boundary
                    for (int y = 0; y < _height; y++)
                    {
                        for (int x = 0; x < _width; x++)
                        {
                            if (_labels[x, y, _maxPos] == _matID)
                            {
                                avgDisp += _dispZ[x, y, _maxPos];
                                count++;
                            }
                        }
                    }
                    break;
            }

            if (count > 0) avgDisp /= count;

            // Compressive strain is positive in geomechanics convention
            _currentStrain = -avgDisp / _initialSampleSize;


            // Compute average axial stress on the loaded boundary
            double sig = 0; int n = 0;
            for (int y = 0; y < _height; ++y)
                for (int x = 0; x < _width; ++x)
                    if (_labels[x, y, _maxPos] == _matID) { sig += _szz[x, y, _maxPos]; n++; }
            _currentStress = -(sig / n) / 1e6;   // MPa, compression positive
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
                // shorthand
                var sp = a.SimParams;
                var mp = a.MatParams;

                int x = tid.X + 1;
                int y = tid.Y + 1;
                int z = tid.Z + 1;

                if (x >= sp.Width - 1 || y >= sp.Height - 1 || z >= sp.Depth - 1)
                    return;
                if (a.Labels[x, y, z] != sp.MaterialID) return;

                //------------------------------------------------------------------ density
                float rho = XMath.Max(100.0f, a.Density[x, y, z]);
                float r = rho / (float)mp.Cohesion;   // store ρ₀ in Cohesion for now
                                                      // (keeps MaterialParams unchanged)

                float D = (float)a.Damage[x, y, z];
                float lam = (float)((1.0 - D) * mp.Lambda * r);
                float mu = (float)((1.0 - D) * mp.Mu * r);

                //------------------------------------------------------------------ grads
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

                //------------------------------------------------------------------ elastic
                float dt = (float)sp.TimeStep;

                float SxxN = (float)(a.Sxx[x, y, z] + dt * ((lam + 2 * mu) * dvx_dx + lam * (dvy_dy + dvz_dz)));
                float SyyN = (float)(a.Syy[x, y, z] + dt * ((lam + 2 * mu) * dvy_dy + lam * (dvx_dx + dvz_dz)));
                float SzzN = (float)(a.Szz[x, y, z] + dt * ((lam + 2 * mu) * dvz_dz + lam * (dvx_dx + dvy_dy)));

                float SxyN = (float)(a.Sxy[x, y, z] + dt * (mu * (dvy_dx + dvx_dy)));
                float SxzN = (float)(a.Sxz[x, y, z] + dt * (mu * (dvz_dx + dvx_dz)));
                float SyzN = (float)(a.Syz[x, y, z] + dt * (mu * (dvz_dy + dvy_dz)));

                //------------------------------------------------------------------ MC plastic
                if (a.UsePlastic != 0)
                {
                    float mean = (SxxN + SyyN + SzzN) * 0.3333333f;
                    float dxx = SxxN - mean;
                    float dyy = SyyN - mean;
                    float dzz = SzzN - mean;

                    float J2 = 0.5f * (dxx * dxx + dyy * dyy + dzz * dzz) + (SxyN * SxyN + SxzN * SxzN + SyzN * SyzN);
                    float tau = XMath.Sqrt(XMath.Max(J2, 0.0f));

                    float p = -mean;
                    float coh = (float)(mp.Cohesion * r);

                    float yield = tau + p * (float)mp.FrictionSinPhi - coh * (float)mp.FrictionCosPhi;

                    if (yield > 0)
                    {
                        float fac = (tau > 1e-7f) ? (tau - (coh * (float)mp.FrictionCosPhi - p * (float)mp.FrictionSinPhi)) / tau : 0f;
                        fac = XMath.Min(fac, 0.95f);

                        dxx *= 1 - fac; dyy *= 1 - fac; dzz *= 1 - fac;
                        SxyN *= 1 - fac; SxzN *= 1 - fac; SyzN *= 1 - fac;

                        SxxN = dxx + mean; SyyN = dyy + mean; SzzN = dzz + mean;
                    }
                }

                //------------------------------------------------------------------ tensile
                if (a.UseBrittle != 0)
                {
                    float sigmaMax = XMath.Max(SxxN, XMath.Max(SyyN, SzzN));
                    float sigT = (float)(mp.TensileStrength * r);

                    if (sigmaMax > sigT && D < 0.99f)
                    {
                        float over = (sigmaMax - sigT) / sigT;

                        // Adjust damage increment based on debug mode
                        float dD;
                        if (sp.DebugMode)
                        {
                            // Debug mode: Much faster damage accumulation
                            dD = XMath.Min(over * 0.5f, 0.05f);
                        }
                        else
                        {
                            // Normal mode: Realistic slow damage
                            dD = XMath.Min(over * 0.01f, 0.001f);
                        }

                        D = XMath.Min(D + dD, 0.99f);
                        a.Damage[x, y, z] = D;

                        float soften = 1.0f - dD;
                        SxxN *= soften; SyyN *= soften; SzzN *= soften;
                        SxyN *= soften; SxzN *= soften; SyzN *= soften;
                    }
                }

                //------------------------------------------------------------------ store
                a.Sxx[x, y, z] = SxxN; a.Syy[x, y, z] = SyyN; a.Szz[x, y, z] = SzzN;
                a.Sxy[x, y, z] = SxyN; a.Sxz[x, y, z] = SxzN; a.Syz[x, y, z] = SyzN;
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

            public static void UpdateDisplacement(Index3D idx, DisplacementArgs a)
            {
                int x = idx.X, y = idx.Y, z = idx.Z;
                var sp = a.SimParams;
                if (x <= 0 || x >= sp.Width - 1 ||
                    y <= 0 || y >= sp.Height - 1 ||
                    z <= 0 || z >= sp.Depth - 1) return;
                if (a.Labels[x, y, z] != sp.MaterialID) return;

                double dt = sp.TimeStep;
                a.DispX[x, y, z] += a.Vx[x, y, z] * dt;
                a.DispY[x, y, z] += a.Vy[x, y, z] * dt;
                a.DispZ[x, y, z] += a.Vz[x, y, z] * dt;
            }

            public static void ApplyBoundaryConditions(Index3D idx, BoundaryArgs a)
            {
                int x = idx.X, y = idx.Y, z = idx.Z;

                // Apply stress based on axis
                switch (a.StressAxis)
                {
                    case 0: // X-axis
                        if ((x == a.MaxBoundary || x == a.MinBoundary) && a.Labels[x, y, z] == a.MaterialID)
                        {
                            a.Sxx[x, y, z] = -a.AxialPressure;
                        }
                        else if (x == a.MinBoundary && a.Labels[x, y, z] == a.MaterialID)
                        {
                            a.Sxx[x, y, z] = -a.ConfiningPressure;
                        }
                        break;

                    case 1: // Y-axis
                        if ((y == a.MaxBoundary || x == a.MinBoundary) && a.Labels[x, y, z] == a.MaterialID)
                        {
                            a.Syy[x, y, z] = -a.AxialPressure;
                        }
                        else if (y == a.MinBoundary && a.Labels[x, y, z] == a.MaterialID)
                        {
                            a.Syy[x, y, z] = -a.ConfiningPressure;
                        }
                        break;

                    case 2: // Z-axis (default)
                    default:
                        if ((z == a.MaxBoundary || x == a.MinBoundary) && a.Labels[x, y, z] == a.MaterialID)
                        {
                            a.Szz[x, y, z] = -a.AxialPressure;
                        }
                        else if (z == a.MinBoundary && a.Labels[x, y, z] == a.MaterialID)
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
            Logger.Log("[GPUTriaxialSimulator] Disposing");

            // Cancel any running simulation
            _simulationCancelled = true;
            _cts?.Cancel();

            // Free GPU resources
            _dLabels?.Dispose();
            _dDensity?.Dispose();
            _dVx?.Dispose();
            _dVy?.Dispose();
            _dVz?.Dispose();
            _dSxx?.Dispose();
            _dSyy?.Dispose();
            _dSzz?.Dispose();
            _dSxy?.Dispose();
            _dSxz?.Dispose();
            _dSyz?.Dispose();
            _dDamage?.Dispose();
            _dDispX?.Dispose();
            _dDispY?.Dispose();
            _dDispZ?.Dispose();

            _accelerator?.Dispose();
            _context?.Dispose();

            // Clear event handlers
            ProgressUpdated = null;
            FailureDetected = null;
            SimulationCompleted = null;
        }
        #endregion
    }
}