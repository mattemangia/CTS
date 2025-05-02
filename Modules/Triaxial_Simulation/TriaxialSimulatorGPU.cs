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
    }

    public class TriaxialSimulatorGPU : IDisposable
    {
        #region Fields and Properties
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
            double tensileMPa, double frictionAngleDeg, double cohesionMPa)
        {
            Logger.Log("[GPUTriaxialSimulator] Constructor start");
            _width = width;
            _height = height;
            _depth = depth;
            _pixelSize = pixelSize;
            _labels = labels;
            _density = density;
            _matID = materialID;

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

            _cts = new CancellationTokenSource();
            InitGpu();
            Logger.Log("[GPUTriaxialSimulator] Constructor end");
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
                if (step % 5 == 0 && (_matParams.UseModelFlags & 4) != 0 && !_failureDetected)
                {
                    if (CheckForFailure())
                    {
                        _failureDetected = true;
                        _failureStep = incrementNumber;

                        // Notify failure detected
                        var failureArgs = new FailureDetectedEventArgs(_currentStress, _currentStrain, incrementNumber, _pressureIncrements);
                        FailureDetected?.Invoke(this, failureArgs);

                        // Pause simulation to let user decide whether to continue
                        _simulationPaused = true;
                    }
                }
            }
        }

        private bool CheckForFailure()
        {
            // Copy damage field back to CPU for analysis
            _dDamage.CopyToCPU(_damage);

            // Count severely damaged elements
            int failedCount = 0;
            int totalCount = 0;
            double maxDamage = 0.0;

            for (int z = 0; z < _depth; z++)
            {
                for (int y = 0; y < _height; y++)
                {
                    for (int x = 0; x < _width; x++)
                    {
                        if (_labels[x, y, z] == _matID)
                        {
                            totalCount++;
                            double damage = _damage[x, y, z];
                            if (damage > _simParams.FailureThreshold)
                            {
                                failedCount++;
                            }
                            maxDamage = Math.Max(maxDamage, damage);
                        }
                    }
                }
            }

            // Consider failure if more than 10% of voxels are severely damaged
            double failureRatio = (double)failedCount / totalCount;
            return failureRatio > 0.1 || maxDamage > 0.9;
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

            // Use target pressure for stress for now (could measure actual stress instead)
            _currentStress = targetPressureMPa;
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
        #endregion

        #region Kernel Classes
        private static class Kernels
        {
            public struct StressArgs
            {
                public ArrayView3D<byte, Stride3D.DenseXY> Labels;
                public ArrayView3D<float, Stride3D.DenseXY> Density;
                public ArrayView3D<double, Stride3D.DenseXY> Vx, Vy, Vz;
                public ArrayView3D<double, Stride3D.DenseXY> Sxx, Syy, Szz, Sxy, Sxz, Syz;
                public ArrayView3D<double, Stride3D.DenseXY> Damage;
                public MaterialParams MatParams;
                public SimulationParams SimParams;
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

            public static void UpdateStress(Index3D idx, StressArgs a)
            {
                int x = idx.X, y = idx.Y, z = idx.Z;
                var sp = a.SimParams;
                if (x <= 0 || x >= sp.Width - 1 ||
                    y <= 0 || y >= sp.Height - 1 ||
                    z <= 0 || z >= sp.Depth - 1) return;
                if (a.Labels[x, y, z] != sp.MaterialID) return;

                double dx = a.SimParams.PixelSize;
                double inv2dx = 1.0 / (2 * dx);

                // strain rates (central differences)
                double exx = (a.Vx[x + 1, y, z] - a.Vx[x - 1, y, z]) * inv2dx;
                double eyy = (a.Vy[x, y + 1, z] - a.Vy[x, y - 1, z]) * inv2dx;
                double ezz = (a.Vz[x, y, z + 1] - a.Vz[x, y, z - 1]) * inv2dx;
                double exy = 0.5 * ((a.Vx[x, y + 1, z] - a.Vx[x, y - 1, z])
                                  + (a.Vy[x + 1, y, z] - a.Vy[x - 1, y, z])) * inv2dx;
                double exz = 0.5 * ((a.Vx[x, y, z + 1] - a.Vx[x, y, z - 1])
                                  + (a.Vz[x + 1, y, z] - a.Vz[x - 1, y, z])) * inv2dx;
                double eyz = 0.5 * ((a.Vy[x, y, z + 1] - a.Vy[x, y, z - 1])
                                  + (a.Vz[x, y + 1, z] - a.Vz[x, y - 1, z])) * inv2dx;

                var mp = a.MatParams;
                double dt = sp.TimeStep;
                double D = a.Damage[x, y, z];
                double lambda = (1.0 - D) * mp.Lambda;
                double mu = (1.0 - D) * mp.Mu;

                // elastic update
                if ((mp.UseModelFlags & 1) != 0)
                {
                    double trace = exx + eyy + ezz;
                    a.Sxx[x, y, z] += dt * (lambda * trace + 2 * mu * exx);
                    a.Syy[x, y, z] += dt * (lambda * trace + 2 * mu * eyy);
                    a.Szz[x, y, z] += dt * (lambda * trace + 2 * mu * ezz);
                    a.Sxy[x, y, z] += dt * (2 * mu * exy);
                    a.Sxz[x, y, z] += dt * (2 * mu * exz);
                    a.Syz[x, y, z] += dt * (2 * mu * eyz);
                }

                // plastic Mohr-Coulomb
                if ((mp.UseModelFlags & 2) != 0)
                {
                    double mean = (a.Sxx[x, y, z] + a.Syy[x, y, z] + a.Szz[x, y, z]) / 3.0;
                    double dev_xx = a.Sxx[x, y, z] - mean;
                    double dev_yy = a.Syy[x, y, z] - mean;
                    double dev_zz = a.Szz[x, y, z] - mean;
                    double J2 = 0.5 * (dev_xx * dev_xx + dev_yy * dev_yy + dev_zz * dev_zz)
                              + (a.Sxy[x, y, z] * a.Sxy[x, y, z] + a.Sxz[x, y, z] * a.Sxz[x, y, z] + a.Syz[x, y, z] * a.Syz[x, y, z]);
                    double tau = Math.Sqrt(Math.Max(J2, 0.0));
                    double p = -mean + mp.ConfiningPressure;
                    double yield = tau + p * mp.FrictionSinPhi - mp.Cohesion * mp.FrictionCosPhi;

                    if (yield > 0.0)
                    {
                        double safeTau = (tau > 1e-10) ? tau : 1e-10;
                        double scale = (tau - (mp.Cohesion * mp.FrictionCosPhi - p * mp.FrictionSinPhi)) / safeTau;
                        scale = Math.Min(scale, 0.95);

                        // Reduce deviatoric stress components
                        dev_xx *= (1 - scale);
                        dev_yy *= (1 - scale);
                        dev_zz *= (1 - scale);
                        a.Sxy[x, y, z] *= (1 - scale);
                        a.Sxz[x, y, z] *= (1 - scale);
                        a.Syz[x, y, z] *= (1 - scale);

                        // Recombine with mean stress
                        a.Sxx[x, y, z] = dev_xx + mean;
                        a.Syy[x, y, z] = dev_yy + mean;
                        a.Szz[x, y, z] = dev_zz + mean;
                    }
                }

                // brittle tensile failure
                if ((mp.UseModelFlags & 4) != 0)
                {
                    // Calculate principal stresses via characteristic equation
                    double I1 = a.Sxx[x, y, z] + a.Syy[x, y, z] + a.Szz[x, y, z];
                    double I2 = a.Sxx[x, y, z] * a.Syy[x, y, z] + a.Syy[x, y, z] * a.Szz[x, y, z] + a.Szz[x, y, z] * a.Sxx[x, y, z]
                              - (a.Sxy[x, y, z] * a.Sxy[x, y, z] + a.Sxz[x, y, z] * a.Sxz[x, y, z] + a.Syz[x, y, z] * a.Syz[x, y, z]);
                    double I3 = a.Sxx[x, y, z] * (a.Syy[x, y, z] * a.Szz[x, y, z] - a.Syz[x, y, z] * a.Syz[x, y, z])
                              - a.Sxy[x, y, z] * (a.Sxy[x, y, z] * a.Szz[x, y, z] - a.Syz[x, y, z] * a.Sxz[x, y, z])
                              + a.Sxz[x, y, z] * (a.Sxy[x, y, z] * a.Syz[x, y, z] - a.Syy[x, y, z] * a.Sxz[x, y, z]);

                    // Solve cubic equation for principal stresses
                    double eqnA = -I1;  // Renamed from 'a' to avoid collision with parameter
                    double eqnB = I2;   // Renamed from 'b'
                    double eqnC = -I3;  // Renamed from 'c'
                    double q = (3 * eqnB - eqnA * eqnA) / 9.0;
                    double r = (9 * eqnA * eqnB - 27 * eqnC - 2 * eqnA * eqnA * eqnA) / 54.0;
                    double disc = q * q * q + r * r;

                    double sigmaMax;
                    if (disc >= 0)
                    {
                        double sqrtDisc = Math.Sqrt(disc);
                        double s1 = (r + sqrtDisc) >= 0 ? Math.Pow(r + sqrtDisc, 1.0 / 3.0) : -Math.Pow(-r - sqrtDisc, 1.0 / 3.0);
                        double s2 = (r - sqrtDisc) >= 0 ? Math.Pow(r - sqrtDisc, 1.0 / 3.0) : -Math.Pow(-r + sqrtDisc, 1.0 / 3.0);
                        sigmaMax = -eqnA / 3.0 + s1 + s2;
                    }
                    else
                    {
                        double theta = Math.Acos(r / Math.Sqrt(-q * q * q));
                        sigmaMax = 2.0 * Math.Sqrt(-q) * Math.Cos(theta / 3.0) - eqnA / 3.0;
                    }

                    // Apply tensile damage if max principal stress exceeds tensile strength
                    if (sigmaMax > mp.TensileStrength && D < 1.0)
                    {
                        double incr = (sigmaMax - mp.TensileStrength) / mp.TensileStrength;
                        incr = Math.Min(incr, 0.1); // limit increment for stability
                        double newD = Math.Min(0.95, D + incr * 0.01);
                        a.Damage[x, y, z] = newD;
                        double factor = 1.0 - newD;
                        a.Sxx[x, y, z] *= factor;
                        a.Syy[x, y, z] *= factor;
                        a.Szz[x, y, z] *= factor;
                        a.Sxy[x, y, z] *= factor;
                        a.Sxz[x, y, z] *= factor;
                        a.Syz[x, y, z] *= factor;
                    }
                }
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
                        if (x == a.MaxBoundary && a.Labels[x, y, z] == a.MaterialID)
                        {
                            a.Sxx[x, y, z] = -a.AxialPressure;
                        }
                        else if (x == a.MinBoundary && a.Labels[x, y, z] == a.MaterialID)
                        {
                            a.Sxx[x, y, z] = -a.ConfiningPressure;
                        }
                        break;

                    case 1: // Y-axis
                        if (y == a.MaxBoundary && a.Labels[x, y, z] == a.MaterialID)
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
                        if (z == a.MaxBoundary && a.Labels[x, y, z] == a.MaterialID)
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