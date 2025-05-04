using System;
using System.Threading;
using System.Threading.Tasks;


namespace CTS
{
    /// <summary>
    /// Wrapper class that provides the same interface as AcousticSimulator
    /// but uses the GPU-accelerated implementation internally. This ensures
    /// seamless integration with existing code.
    /// </summary>
    public sealed class AcousticSimulatorGPUWrapper : IDisposable
    {
        #region Events
        public event EventHandler<AcousticSimulationProgressEventArgs> ProgressUpdated;
        public event EventHandler<AcousticSimulationCompleteEventArgs> SimulationCompleted;
        #endregion

        #region Fields
        private AcousticSimulatorGPU gpuSimulator;

        // Cache original parameters for possible reset/restart
        private readonly int width, height, depth;
        private readonly float pixelSize;
        private readonly byte[,,] volumeLabels;
        private readonly float[,,] densityVolume;
        private readonly byte selectedMaterialID;
        private readonly string axis, waveType;
        private readonly double confiningPressureMPa, tensileStrengthMPa, failureAngleDeg, cohesionMPa;
        private readonly double sourceEnergyJ, sourceFrequencyKHz;
        private readonly int sourceAmplitude, totalTimeSteps;
        private readonly bool useElasticModel, usePlasticModel, useBrittleModel;
        private readonly double youngsModulusMPa, poissonRatio;
        private readonly int tx, ty, tz, rx, ry, rz;
        #endregion

        #region Constructor
        public AcousticSimulatorGPUWrapper() { }
        public AcousticSimulatorGPUWrapper(
            int width, int height, int depth, float pixelSize,
            byte[,,] volumeLabels, float[,,] densityVolume, byte selectedMaterialID,
            string axis, string waveType,
            double confiningPressure, double tensileStrength, double failureAngle, double cohesion,
            double energy, double frequency, int amplitude, int timeSteps,
            bool useElasticModel, bool usePlasticModel, bool useBrittleModel,
            double youngsModulus, double poissonRatio,
            int tx = -1, int ty = -1, int tz = -1, int rx = -1, int ry = -1, int rz = -1)
        {
            // Cache all parameters for possible reuse
            this.width = width;
            this.height = height;
            this.depth = depth;
            this.pixelSize = pixelSize;
            this.volumeLabels = volumeLabels;
            this.densityVolume = densityVolume;
            this.selectedMaterialID = selectedMaterialID;
            this.axis = axis;
            this.waveType = waveType;
            this.confiningPressureMPa = confiningPressure;
            this.tensileStrengthMPa = tensileStrength;
            this.failureAngleDeg = failureAngle;
            this.cohesionMPa = cohesion;
            this.sourceEnergyJ = energy;
            this.sourceFrequencyKHz = frequency;
            this.sourceAmplitude = amplitude;
            this.totalTimeSteps = timeSteps;
            this.useElasticModel = useElasticModel;
            this.usePlasticModel = usePlasticModel;
            this.useBrittleModel = useBrittleModel;
            this.youngsModulusMPa = youngsModulus;
            this.poissonRatio = poissonRatio;

            // Set transducer positions
            if (tx >= 0 && ty >= 0 && tz >= 0 && rx >= 0 && ry >= 0 && rz >= 0)
            {
                this.tx = tx;
                this.ty = ty;
                this.tz = tz;
                this.rx = rx;
                this.ry = ry;
                this.rz = rz;
            }
            else
            {
                // Default positions based on axis
                switch (axis.ToUpperInvariant())
                {
                    case "X":
                        this.tx = 1; this.ty = height / 2; this.tz = depth / 2;
                        this.rx = width - 2; this.ry = height / 2; this.rz = depth / 2;
                        break;
                    case "Y":
                        this.tx = width / 2; this.ty = 1; this.tz = depth / 2;
                        this.rx = width / 2; this.ry = height - 2; this.rz = depth / 2;
                        break;
                    default: // Z axis
                        this.tx = width / 2; this.ty = height / 2; this.tz = 1;
                        this.rx = width / 2; this.ry = height / 2; this.rz = depth - 2;
                        break;
                }
            }

            // Create the GPU simulator
            InitializeGPUSimulator();
        }
        #endregion

        #region Private methods
        private void InitializeGPUSimulator()
        {
            // Create and configure the GPU simulator
            gpuSimulator = new AcousticSimulatorGPU(
                width, height, depth, pixelSize,
                volumeLabels, densityVolume, selectedMaterialID,
                axis, waveType,
                confiningPressureMPa, tensileStrengthMPa, failureAngleDeg, cohesionMPa,
                sourceEnergyJ, sourceFrequencyKHz, sourceAmplitude, totalTimeSteps,
                useElasticModel, usePlasticModel, useBrittleModel,
                youngsModulusMPa, poissonRatio);

            // Connect event handlers
            gpuSimulator.ProgressUpdated += (sender, args) =>
                ProgressUpdated?.Invoke(this, args);

            gpuSimulator.SimulationCompleted += (sender, args) =>
                SimulationCompleted?.Invoke(this, args);

            Logger.Log("[AcousticSimulatorGPUWrapper] GPU simulator initialized");
        }
        #endregion

        #region Public API (matches CPU version)
        public void StartSimulation()
        {
            // Ensure we have a fresh GPU simulator instance
            if (gpuSimulator == null)
            {
                InitializeGPUSimulator();
            }

            // Start the simulation
            gpuSimulator.StartSimulation();
            Logger.Log("[AcousticSimulatorGPUWrapper] Simulation started");
        }

        public void CancelSimulation()
        {
            gpuSimulator?.CancelSimulation();
            Logger.Log("[AcousticSimulatorGPUWrapper] Simulation cancelled");
        }

        public (double[,,] vx, double[,,] vy, double[,,] vz) GetWaveFieldSnapshot()
        {
            if (gpuSimulator == null)
                return (new double[width, height, depth],
                        new double[width, height, depth],
                        new double[width, height, depth]);

            return gpuSimulator.GetWaveFieldSnapshot();
        }
        #endregion

        #region IDisposable
        public void Dispose()
        {
            gpuSimulator?.Dispose();
            gpuSimulator = null;
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}