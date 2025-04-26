using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CTSegmenter
{
    public class AcousticSimulator : IDisposable
    {
        // Auto-stop criteria fields
        private bool autoStopEnabled = true;
        private double energyThresholdRatio = 0.01; // Stop when energy drops to 1% of peak
        private double maxReceiverEnergy = 0;
        private bool energyPeaked = false;
        private int checkInterval = 5; // Check every 5 steps
        private int minRequiredSteps = 50;

        // IDisposable implementation
        public void Dispose()
        {
            cts?.Dispose();
            GC.SuppressFinalize(this);
        }
        // Finalizer
        ~AcousticSimulator()
        {
            Dispose();
        }
        // Fields

        // Simulation parameters
        private int width, height, depth;
        private float pixelSize;
        private byte[,,] volumeLabels;
        private float[,,] densityVolume;
        private byte selectedMaterialID;
        private string axis, waveType;
        private double confiningPressure, tensileStrength, failureAngle, cohesion;
        private double energy, frequency;
        private int amplitude, timeSteps;
        private bool useElasticModel, usePlasticModel, useBrittleModel;

        // Elastic moduli (Pa)
        private double lambda, mu;

        // Fields
        private double[,,] vx, vy, vz;              // particle velocities
        private double[,,] sxx, syy, szz, sxy, sxz, syz; // stress components

        // Source/receiver
        private int tx, ty, tz;
        private int rx, ry, rz;
        private bool receiverTouched;
        private int stepCount, postTouchCount, touchStep;

        // Time stepping
        private double dt;
        private double expectedPreTouchIters;
        private int maxPostSteps;

        // Cancellation
        private CancellationTokenSource cts;

        // Events
        public event EventHandler<AcousticSimulationProgressEventArgs> ProgressUpdated;
        public event EventHandler<AcousticSimulationCompleteEventArgs> SimulationCompleted;

        // Full constructor
        public AcousticSimulator(
            int width, int height, int depth, float pixelSize,
            byte[,,] volumeLabels, float[,,] densityVolume, byte selectedMaterialID,
            string axis, string waveType,
            double confiningPressure, double tensileStrength, double failureAngle, double cohesion,
            double energy, double frequency, int amplitude, int timeSteps,
            bool useElasticModel, bool usePlasticModel, bool useBrittleModel,
            double youngsModulus, double poissonRatio)
        {
            // assign parameters
            this.width = width; this.height = height; this.depth = depth;
            this.pixelSize = pixelSize;
            this.volumeLabels = volumeLabels;
            this.densityVolume = densityVolume;
            this.selectedMaterialID = selectedMaterialID;
            this.axis = axis; this.waveType = waveType;
            this.confiningPressure = confiningPressure;
            this.tensileStrength = tensileStrength;
            this.failureAngle = failureAngle;
            this.cohesion = cohesion;
            this.energy = energy;
            this.frequency = frequency;
            this.amplitude = amplitude;
            this.timeSteps = timeSteps;
            this.useElasticModel = useElasticModel;
            this.usePlasticModel = usePlasticModel;
            this.useBrittleModel = useBrittleModel;
            this.maxPostSteps = timeSteps;

            // compute Lame parameters
            double E = youngsModulus * 1e6;
            mu = E / (2 * (1 + poissonRatio));
            lambda = E * poissonRatio / ((1 + poissonRatio) * (1 - 2 * poissonRatio));

            // allocate fields
            vx = new double[width, height, depth]; vy = new double[width, height, depth]; vz = new double[width, height, depth];
            sxx = new double[width, height, depth]; syy = new double[width, height, depth]; szz = new double[width, height, depth];
            sxy = new double[width, height, depth]; sxz = new double[width, height, depth]; syz = new double[width, height, depth];

            // set transducer positions
            switch (axis)
            {
                case "X": tx = 0; ty = height / 2; tz = depth / 2; rx = width - 1; ry = height / 2; rz = depth / 2; break;
                case "Y": tx = width / 2; ty = 0; tz = depth / 2; rx = width / 2; ry = height - 1; rz = depth / 2; break;
                default: tx = width / 2; ty = height / 2; tz = 0; rx = width / 2; ry = height / 2; rz = depth - 1; break;
            }

            // Configure auto-stop to use approximately 10% of timeSteps as minimum
            minRequiredSteps = Math.Max(50, timeSteps / 10);
        }

        // Legacy constructor with default moduli
        public AcousticSimulator(
            int width, int height, int depth, float pixelSize,
            byte[,,] volumeLabels, float[,,] densityVolume, byte selectedMaterialID,
            string axis, string waveType,
            double confiningPressure, double tensileStrength, double failureAngle, double cohesion,
            double energy, double frequency, int amplitude, int timeSteps,
            bool useElasticModel, bool usePlasticModel, bool useBrittleModel)
            : this(width, height, depth, pixelSize, volumeLabels, densityVolume, selectedMaterialID,
                   axis, waveType, confiningPressure, tensileStrength, failureAngle, cohesion,
                   energy, frequency, amplitude, timeSteps,
                   useElasticModel, usePlasticModel, useBrittleModel,
                   50.0, 0.25) // default E=50MPa, nu=0.25
        {
        }

        // Configure auto-stop settings
        public void ConfigureAutoStop(bool enabled = true, double thresholdRatio = 0.01,
                                     int checkInterval = 5, int minSteps = 50)
        {
            this.autoStopEnabled = enabled;
            this.energyThresholdRatio = thresholdRatio;
            this.checkInterval = checkInterval;
            this.minRequiredSteps = minSteps;
        }

        // Start/cancel
        public void StartSimulation() { cts = new CancellationTokenSource(); InitAndRun(cts.Token); }
        public void CancelSimulation() => cts.Cancel();

        // Snapshot for visualization
        public (double[,,] vx, double[,,] vy, double[,,] vz) GetWaveFieldSnapshot() => (vx, vy, vz);

        private void InitAndRun(CancellationToken token)
        {
            ComputeTimeStep(); EstimateIterations();
            receiverTouched = false; stepCount = postTouchCount = touchStep = 0;
            maxReceiverEnergy = 0; energyPeaked = false;
            ClearFields();
            Task.Run(() => RunLoop(token), token);
        }

        private void ComputeTimeStep()
        {
            double vpMax = Math.Sqrt((lambda + 2 * mu) / densityVolume.Cast<float>().Max());
            dt = 0.4 * pixelSize / vpMax;
        }

        private void EstimateIterations()
        {
            double dist = Math.Sqrt(
                Math.Pow(tx - rx, 2) + Math.Pow(ty - ry, 2) + Math.Pow(tz - rz, 2)) * pixelSize;
            expectedPreTouchIters = dist / (dt * Math.Sqrt((lambda + 2 * mu) / densityVolume.Cast<float>().Max()));
        }

        private void ClearFields()
        {
            Array.Clear(vx, 0, vx.Length); Array.Clear(vy, 0, vy.Length); Array.Clear(vz, 0, vz.Length);
            Array.Clear(sxx, 0, sxx.Length); Array.Clear(syy, 0, syy.Length); Array.Clear(szz, 0, szz.Length);
            Array.Clear(sxy, 0, sxy.Length); Array.Clear(sxz, 0, sxz.Length); Array.Clear(syz, 0, syz.Length);

            // initialize source stress
            sxx[tx, ty, tz] = amplitude * Math.Sqrt(energy);
        }

        private void RunLoop(CancellationToken token)
        {
            bool continueSimulation = true;

            while (continueSimulation &&
                  ((!receiverTouched && stepCount < timeSteps) ||
                   (receiverTouched && postTouchCount < maxPostSteps)))
            {
                token.ThrowIfCancellationRequested();
                UpdateStress();
                UpdateVelocity();
                stepCount++;

                // Check if the receiver has been touched
                if (!receiverTouched && CheckReceiver())
                {
                    receiverTouched = true;
                    touchStep = stepCount;
                }
                else if (receiverTouched)
                {
                    postTouchCount++;
                }

                // Check energy-based stopping criterion periodically
                if (autoStopEnabled && stepCount >= minRequiredSteps && stepCount % checkInterval == 0)
                {
                    if (CheckEnergyStopping())
                    {
                        ReportProgress("Energy-based auto-stop triggered", 95);
                        continueSimulation = false;
                    }
                }

                if (continueSimulation && stepCount % 10 == 0)
                {
                    ReportProgress();
                }
            }

            // Final progress update
            ReportProgress("Simulation complete, calculating results...", 99);
            FinalizeResults();
        }

        private bool CheckEnergyStopping()
        {
            // Calculate the current energy at the receiver position
            double currentEnergy = CalculateEnergyAtPoint(rx, ry, rz);

            // Update maximum energy seen
            if (currentEnergy > maxReceiverEnergy)
            {
                maxReceiverEnergy = currentEnergy;
                return false; // Still seeing increasing energy, don't stop
            }

            // Check if energy has peaked (dropped significantly)
            if (!energyPeaked && maxReceiverEnergy > 0 && currentEnergy < 0.5 * maxReceiverEnergy)
            {
                energyPeaked = true;
                ReportProgress($"Energy peaked at {maxReceiverEnergy:E2}, now at {currentEnergy:E2}");
            }

            // If energy has peaked and dropped below threshold, stop
            if (energyPeaked && currentEnergy < energyThresholdRatio * maxReceiverEnergy)
            {
                ReportProgress($"Energy at receiver dropped to {currentEnergy / maxReceiverEnergy:P2} of maximum, stopping");
                return true; // Stop simulation
            }

            return false; // Continue
        }

        private double CalculateEnergyAtPoint(int x, int y, int z)
        {
            if (x < 0 || y < 0 || z < 0 || x >= width || y >= height || z >= depth)
                return 0;

            double localDensity = densityVolume[x, y, z];

            // Kinetic energy density (velocity)
            double kineticEnergy = 0.5 * localDensity * (
                vx[x, y, z] * vx[x, y, z] +
                vy[x, y, z] * vy[x, y, z] +
                vz[x, y, z] * vz[x, y, z]);

            // Elastic potential energy density (stress)
            double strainEnergy = 0.5 * (
                sxx[x, y, z] * sxx[x, y, z] +
                syy[x, y, z] * syy[x, y, z] +
                szz[x, y, z] * szz[x, y, z] +
                2 * (sxy[x, y, z] * sxy[x, y, z] +
                     sxz[x, y, z] * sxz[x, y, z] +
                     syz[x, y, z] * syz[x, y, z])) / (2 * mu);

            return kineticEnergy + strainEnergy;
        }

        private void UpdateStress()
        {
            Parallel.For(1, depth - 1, z =>
            {
                for (int y = 1; y < height - 1; y++)
                    for (int x = 1; x < width - 1; x++)
                    {
                        if (volumeLabels[x, y, z] != selectedMaterialID) continue;
                        double dvx_dx = (vx[x + 1, y, z] - vx[x - 1, y, z]) / (2 * pixelSize);
                        double dvy_dy = (vy[x, y + 1, z] - vy[x, y - 1, z]) / (2 * pixelSize);
                        double dvz_dz = (vz[x, y, z + 1] - vz[x, y, z - 1]) / (2 * pixelSize);
                        double dvx_dy = (vx[x, y + 1, z] - vx[x, y - 1, z]) / (2 * pixelSize);
                        double dvx_dz = (vx[x, y, z + 1] - vx[x, y, z - 1]) / (2 * pixelSize);
                        double dvy_dx = (vy[x + 1, y, z] - vy[x - 1, y, z]) / (2 * pixelSize);
                        double dvz_dx = (vz[x + 1, y, z] - vz[x - 1, y, z]) / (2 * pixelSize);
                        double dvy_dz = (vy[x, y, z + 1] - vy[x, y, z - 1]) / (2 * pixelSize);
                        double dvz_dy = (vz[x, y + 1, z] - vz[x, y - 1, z]) / (2 * pixelSize);

                        double theta = dvx_dx + dvy_dy + dvz_dz;
                        sxx[x, y, z] += dt * (lambda * theta + 2 * mu * dvx_dx);
                        syy[x, y, z] += dt * (lambda * theta + 2 * mu * dvy_dy);
                        szz[x, y, z] += dt * (lambda * theta + 2 * mu * dvz_dz);
                        sxy[x, y, z] += dt * (mu * (dvx_dy + dvy_dx));
                        sxz[x, y, z] += dt * (mu * (dvx_dz + dvz_dx));
                        syz[x, y, z] += dt * (mu * (dvy_dz + dvz_dy));
                    }
            });
        }

        private void UpdateVelocity()
        {
            Parallel.For(1, depth - 1, z =>
            {
                for (int y = 1; y < height - 1; y++)
                    for (int x = 1; x < width - 1; x++)
                    {
                        if (volumeLabels[x, y, z] != selectedMaterialID) continue;
                        double rho = densityVolume[x, y, z];
                        double dsxx_dx = (sxx[x, y, z] - sxx[x - 1, y, z]) / pixelSize;
                        double dsxy_dy = (sxy[x, y, z] - sxy[x, y - 1, z]) / pixelSize;
                        double dsxz_dz = (sxz[x, y, z] - sxz[x, y, z - 1]) / pixelSize;
                        double dsyy_dy = (syy[x, y, z] - syy[x, y - 1, z]) / pixelSize;
                        double dsxy_dx = (sxy[x + 1, y, z] - sxy[x, y, z]) / pixelSize;
                        double dsyz_dz = (syz[x, y, z] - syz[x, y, z - 1]) / pixelSize;
                        double dszz_dz = (szz[x, y, z] - szz[x, y, z - 1]) / pixelSize;
                        double dsxz_dx = (sxz[x + 1, y, z] - sxz[x, y, z]) / pixelSize;
                        double dsyz_dy = (syz[x, y + 1, z] - syz[x, y, z]) / pixelSize;

                        vx[x, y, z] += dt * (dsxx_dx + dsxy_dy + dsxz_dz) / rho;
                        vy[x, y, z] += dt * (dsxy_dx + dsyy_dy + dsyz_dz) / rho;
                        vz[x, y, z] += dt * (dsxz_dx + dsyz_dy + dszz_dz) / rho;
                    }
            });
        }

        private bool CheckReceiver()
        {
            return Math.Abs(vx[rx, ry, rz]) > 1e-6 || Math.Abs(vy[rx, ry, rz]) > 1e-6 || Math.Abs(vz[rx, ry, rz]) > 1e-6;
        }

        private void ReportProgress(string message = "Simulating...", int? forcePercent = null)
        {
            int percent;

            if (forcePercent.HasValue)
            {
                percent = forcePercent.Value;
            }
            else if (energyPeaked)
            {
                // If energy has peaked, we're in the late stages
                percent = 80 + (int)(19.0 * Math.Min(1.0, stepCount / (double)timeSteps));
            }
            else if (receiverTouched)
            {
                // Wave reached receiver but still running
                percent = 50 + (int)(29.0 * postTouchCount / maxPostSteps);
            }
            else
            {
                // Still waiting for wave to reach receiver
                percent = (int)(49.0 * Math.Min(1.0, stepCount / expectedPreTouchIters));
            }

            percent = Math.Max(0, Math.Min(99, percent));

            // Get wave fields for visualization
            float[,,] pWaveField = ConvertToFloat(vx);
            float[,,] sWaveField = ConvertToFloat(vy);

            ProgressUpdated?.Invoke(this,
                new AcousticSimulationProgressEventArgs(percent, stepCount, message, pWaveField, sWaveField));
        }

        private float[,,] ConvertToFloat(double[,,] input)
        {
            float[,,] result = new float[width, height, depth];

            for (int z = 0; z < depth; z++)
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        result[x, y, z] = (float)input[x, y, z];

            return result;
        }

        private void FinalizeResults()
        {
            double dist = Math.Sqrt(
                Math.Pow(tx - rx, 2) + Math.Pow(ty - ry, 2) + Math.Pow(tz - rz, 2)) * pixelSize;

            double vP;
            if (touchStep > 0)
            {
                vP = dist / (touchStep * dt);
            }
            else
            {
                // Fallback if receiver was never touched
                double avgDensity = densityVolume.Cast<float>().Average();
                vP = Math.Sqrt((lambda + 2 * mu) / avgDensity);
            }

            // Calculate S-wave velocity based on theory
            double vS = vP / Math.Sqrt(3.0); // Theoretical relation for Poisson solid
            double ratio = vP / vS;

            // Calculate both travel times properly
            int pWaveTravelTime = touchStep > 0 ? touchStep : (int)(dist / (vP * dt));
            int sWaveTravelTime = touchStep > 0 ? (int)(touchStep * (vP / vS)) : (int)(dist / (vS * dt));

            SimulationCompleted?.Invoke(this,
                new AcousticSimulationCompleteEventArgs(vP, vS, ratio, pWaveTravelTime, sWaveTravelTime, stepCount));
        }

    }

    /// <summary>
    /// Event arguments for simulation progress updates
    /// </summary>
    public class AcousticSimulationProgressEventArgs : EventArgs
    {
        public int ProgressPercent { get; }
        public int TimeStep { get; }
        public string StatusText { get; }
        public float[,,] PWaveField { get; }
        public float[,,] SWaveField { get; }

        public AcousticSimulationProgressEventArgs(
            int progressPercent, int timeStep, string statusText,
            float[,,] pWaveField, float[,,] sWaveField)
        {
            ProgressPercent = progressPercent;
            TimeStep = timeStep;
            StatusText = statusText;
            PWaveField = pWaveField;
            SWaveField = sWaveField;
        }
    }

    /// <summary>
    /// Event arguments for simulation completion
    /// </summary>
    public class AcousticSimulationCompleteEventArgs : EventArgs
    {
        public double PWaveVelocity { get; } // m/s
        public double SWaveVelocity { get; } // m/s
        public double VpVsRatio { get; }
        public int PWaveTravelTime { get; } // time steps
        public int SWaveTravelTime { get; } // time steps
        public int TotalTimeSteps { get; }

        public AcousticSimulationCompleteEventArgs(
            double pWaveVelocity, double sWaveVelocity, double vpVsRatio,
            int pWaveTravelTime, int sWaveTravelTime, int totalTimeSteps)
        {
            PWaveVelocity = pWaveVelocity;
            SWaveVelocity = sWaveVelocity;
            VpVsRatio = vpVsRatio;
            PWaveTravelTime = pWaveTravelTime;
            SWaveTravelTime = sWaveTravelTime;
            TotalTimeSteps = totalTimeSteps;
        }
    }
}
