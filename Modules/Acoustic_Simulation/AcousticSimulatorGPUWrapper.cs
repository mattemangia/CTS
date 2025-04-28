using System;
using System.Threading;
using System.Threading.Tasks;
using CTSegmenter.Modules.Acoustic_Simulation;

namespace CTSegmenter
{
    /// <summary>
    /// Wrapper class that exposes the same public surface of <see cref="AcousticSimulator"/>
    /// ma delega la numerica alla versione GPU (AcousticSimulatorGPU) basata su ILGPU 1.5.2.
    /// L’interfaccia, gli eventi e il costruttore coincidono 1-a-1 con la controparte CPU
    /// in modo che il codice UI resti invariato.
    /// </summary>
    public sealed class AcousticSimulatorGPUWrapper : IDisposable
    {
        #region eventi (uguali a CPU)
        public event EventHandler<AcousticSimulationProgressEventArgs> ProgressUpdated;
        public event EventHandler<AcousticSimulationCompleteEventArgs> SimulationCompleted;
        #endregion

        #region parametri cache
        private readonly int width, height, depth, timeSteps;
        private readonly float pixelSize;
        private readonly byte[,,] volumeLabels;
        private readonly float[,,] densityVolume;
        private readonly byte selectedMaterialID;
        private readonly string axis, waveType;
        private readonly double confiningMPa, tensileMPa, failureAngleDeg, cohesionMPa;
        private readonly double energyJ, frequencyKHz;
        private readonly int amplitude;
        private readonly bool useElastic, usePlastic, useBrittle;
        private readonly double youngMPa, poissonRatio;
        #endregion

        // GPU core
        private AcousticSimulatorGPU gpu;
        

        // posizione TX / RX pre-calcolate per comodità del wrapper (usate nel wavelet)
        private readonly int tx, ty, tz, rx, ry, rz;

        public AcousticSimulatorGPUWrapper(
    int width, int height, int depth, float pixelSize,
    byte[,,] volumeLabels, float[,,] densityVolume, byte selectedMaterialID,
    string axis, string waveType,
    double confiningPressure, double tensileStrength, double failureAngle, double cohesion,
    double energy, double frequency, int amplitude, int timeSteps,
    bool useElasticModel, bool usePlasticModel, bool useBrittleModel,
    double youngsModulus, double poissonRatio)
        {
            // memorizza per ricostruzioni future / re-start
            this.width = width; this.height = height; this.depth = depth; this.pixelSize = pixelSize;
            this.volumeLabels = volumeLabels; this.densityVolume = densityVolume; this.selectedMaterialID = selectedMaterialID;
            this.axis = axis; this.waveType = waveType;
            this.confiningMPa = confiningPressure; this.tensileMPa = tensileStrength; this.failureAngleDeg = failureAngle; this.cohesionMPa = cohesion;
            this.energyJ = energy; this.frequencyKHz = frequency; this.amplitude = amplitude; this.timeSteps = timeSteps;
            this.useElastic = useElasticModel; this.usePlastic = usePlasticModel; this.useBrittle = useBrittleModel;
            this.youngMPa = youngsModulus; this.poissonRatio = poissonRatio;

            // trasduttori
            switch (axis.ToUpperInvariant())
            {
                case "X": tx = 0; ty = height / 2; tz = depth / 2; rx = width - 1; ry = height / 2; rz = depth / 2; break;
                case "Y": tx = width / 2; ty = 0; tz = depth / 2; rx = width / 2; ry = height - 1; rz = depth / 2; break;
                default: tx = width / 2; ty = height / 2; tz = 0; rx = width / 2; ry = height / 2; rz = depth - 1; break;
            }

            BuildSimulator();
        }

        /// <summary>
        /// Crea e configura la nuova istanza GPU; chiamato dal ctor e dopo <see cref="CancelSimulation"/>
        /// per eventuali ri-avvii.
        /// </summary>
        private void BuildSimulator()
        {
            gpu?.Dispose();
            gpu = new AcousticSimulatorGPU(
                width, height, depth, pixelSize,
                volumeLabels, densityVolume, selectedMaterialID,
                axis, waveType,
                confiningMPa, tensileMPa, failureAngleDeg, cohesionMPa,
                energyJ, frequencyKHz, amplitude, timeSteps,
                useElastic, usePlastic, useBrittle,
                youngMPa, poissonRatio);

            // forwarding eventi
            gpu.ProgressUpdated += (s, e) => ProgressUpdated?.Invoke(this, e);
            gpu.SimulationCompleted += (s, e) => SimulationCompleted?.Invoke(this, e);

            // criteri auto-stop secondo logica UI (soglia 1 %, check ogni 5 step, min = 10 % timeSteps)
            gpu.ConfigureAutoStop(rx, ry, rz, true, 0.01, 5, Math.Max(50, timeSteps / 10));
            gpu.SetTotalSteps(timeSteps);
        }

        #region API pubblica coerente con versione CPU

        public void StartSimulation()
        {
            // GPU already runs in its own task – just reset & fire
            gpu.Reset();
            ApplySourceWavelet();
            gpu.StartSimulation();
        }

        public void CancelSimulation() => gpu.CancelSimulation();
        public (double[,,] vx, double[,,] vy, double[,,] vz) GetWaveFieldSnapshot()
            => (Rebuild3D(gpu.GetVelocityX()), Rebuild3D(gpu.GetVelocityY()), Rebuild3D(gpu.GetVelocityZ()));
        #endregion

        #region helper private
        private void ApplySourceWavelet()
        {
            // calcola dt locale come fa la GPU (identico algoritmo)
            double E = youngMPa * 1e6; double mu = E / (2 * (1 + poissonRatio)); double lam = E * poissonRatio / ((1 + poissonRatio) * (1 - 2 * poissonRatio));
            double rhoMin = double.MaxValue; foreach (var v in densityVolume) if (v > 0 && v < rhoMin) rhoMin = v;
            double vpMax = Math.Sqrt((lam + 2 * mu) / rhoMin);
            double dt = Math.Min(0.4 * pixelSize / vpMax, 1.0 / (20 * frequencyKHz * 1000.0));
            int len = Math.Max(100, (int)(10.0 / (frequencyKHz * 1000.0 * dt)));
            var w = new double[len]; double f0 = frequencyKHz * 1000.0;
            for (int i = 0; i < len; ++i)
            {
                double t = i * dt; double t0 = 1.5 / f0; double a = Math.PI * f0 * (t - t0);
                double a2 = a * a; w[i] = amplitude * (1.0 - 2.0 * a2) * Math.Exp(-a2);
            }
            gpu.ApplySource(w, tx, ty, tz);
        }

        private double[,,] Rebuild3D(double[] flat)
        {
            var arr = new double[width, height, depth];
            int idx = 0;
            for (int z = 0; z < depth; z++)
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        if (idx < flat.Length) arr[x, y, z] = flat[idx++];
            return arr;
        }
        #endregion

        public void Dispose()
        {
            
            gpu?.Dispose();
        }
    }
}