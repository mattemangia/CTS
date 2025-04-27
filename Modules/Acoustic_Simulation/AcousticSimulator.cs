using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CTSegmenter
{
    /// <summary>
    /// Full‑physics acoustic / elastodynamic simulator with linear‑elastic, perfectly‑plastic (Mohr–Coulomb)
    /// and tensile‑brittle damage response. Written for C# 7.3 compatibility – no target‑typed «new» or
    /// nullable reference‑type syntax, and no reliance on Math.Cbrt.
    /// </summary>
    public class AcousticSimulator : IDisposable
    {
        #region configuration -----------------------------------------------------------
        private readonly int width, height, depth;
        private readonly float pixelSize;
        private readonly byte[,,] volumeLabels;
        private readonly float[,,] densityVolume;
        private readonly byte selectedMaterialID;
        // progress ----------------------------------------------------
        private int expectedTotalSteps;
        private readonly double confiningPressureMPa;
        private readonly double tensileStrengthMPa;
        private readonly double failureAngleDeg;
        private readonly double cohesionMPa;
        private readonly double sourceEnergyJ;
        private readonly double sourceFrequencyKHz;
        private readonly int sourceAmplitude;
        private readonly int totalTimeSteps;
        private readonly bool useElasticModel;
        private readonly bool usePlasticModel;
        private readonly bool useBrittleModel;
        private readonly double youngsModulusMPa;
        private readonly double poissonRatio;

        private readonly double lambda0, mu0;   // Pa

        // state arrays
        private readonly double[,,] vx, vy, vz;
        private readonly double[,,] sxx, syy, szz, sxy, sxz, syz;
        private readonly double[,,] damage;

        // TX / RX
        private readonly int tx, ty, tz;
        private readonly int rx, ry, rz;

        // time stepping
        private double dt;
        private int stepCount;
        private const double SafetyCourant = 0.4;

        // termination
        private readonly int minRequiredSteps;
        private const int checkInterval = 10;
        private bool receiverTouched;
        private int touchStep;
        private double maxReceiverEnergy;
        private bool energyPeaked;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();

        // events
        public event EventHandler<AcousticSimulationProgressEventArgs> ProgressUpdated;
        public event EventHandler<AcousticSimulationCompleteEventArgs> SimulationCompleted;

        #endregion

        #region constructor -------------------------------------------------------------
        public AcousticSimulator(
            int width, int height, int depth, float pixelSize,
            byte[,,] volumeLabels, float[,,] densityVolume, byte selectedMaterialID,
            string axis, string waveType,
            double confiningPressure, double tensileStrength, double failureAngle, double cohesion,
            double energy, double frequency, int amplitude, int timeSteps,
            bool useElasticModel, bool usePlasticModel, bool useBrittleModel,
            double youngsModulus, double poissonRatio)
        {
            // grid & material
            this.width = width; this.height = height; this.depth = depth;
            this.pixelSize = pixelSize;
            this.volumeLabels = volumeLabels;
            this.densityVolume = densityVolume;
            this.selectedMaterialID = selectedMaterialID;

            // physics
            confiningPressureMPa = confiningPressure;
            tensileStrengthMPa = tensileStrength;
            failureAngleDeg = failureAngle;
            cohesionMPa = cohesion;
            sourceEnergyJ = energy;
            sourceFrequencyKHz = frequency;
            sourceAmplitude = amplitude;
            totalTimeSteps = timeSteps;
            this.useElasticModel = useElasticModel;
            this.usePlasticModel = usePlasticModel;
            this.useBrittleModel = useBrittleModel;
            youngsModulusMPa = youngsModulus;
            this.poissonRatio = poissonRatio;

            // Lamé constants (Pa)
            double E = youngsModulusMPa * 1e6;
            mu0 = E / (2.0 * (1.0 + poissonRatio));
            lambda0 = E * poissonRatio / ((1 + poissonRatio) * (1 - 2 * poissonRatio));

            // allocate arrays
            vx = new double[width, height, depth]; vy = new double[width, height, depth]; vz = new double[width, height, depth];
            sxx = new double[width, height, depth]; syy = new double[width, height, depth]; szz = new double[width, height, depth];
            sxy = new double[width, height, depth]; sxz = new double[width, height, depth]; syz = new double[width, height, depth];
            damage = new double[width, height, depth];

            // transducer assignment
            switch (axis.ToUpperInvariant())
            {
                case "X": tx = 0; ty = height / 2; tz = depth / 2; rx = width - 1; ry = height / 2; rz = depth / 2; break;
                case "Y": tx = width / 2; ty = 0; tz = depth / 2; rx = width / 2; ry = height - 1; rz = depth / 2; break;
                default: tx = width / 2; ty = height / 2; tz = 0; rx = width / 2; ry = height / 2; rz = depth - 1; break;
            }

            minRequiredSteps = Math.Max(50, timeSteps / 10);
        }
        #endregion

        #region public API --------------------------------------------------------------
        public void StartSimulation() { Task.Run(() => Run(cts.Token)); }
        public void CancelSimulation() { cts.Cancel(); }
        public (double[,,] vx, double[,,] vy, double[,,] vz) GetWaveFieldSnapshot() => (vx, vy, vz);
        #endregion

        #region core loop ---------------------------------------------------------------
        private void Run(CancellationToken token)
        {
            ComputeStableTimeStep();                 // sets dt
            ClearFields();

            // ----- progress -----------------------------------------------------------
            double dist = Math.Sqrt((tx - rx) * (tx - rx) +
                                         (ty - ry) * (ty - ry) +
                                         (tz - rz) * (tz - rz)) * pixelSize;
            double rhoAvg = densityVolume.Cast<float>().Average();
            double vpEst = Math.Sqrt((lambda0 + 2 * mu0) / rhoAvg);
            expectedTotalSteps = (int)Math.Ceiling(dist / (vpEst * dt)) + totalTimeSteps;
            // --------------------------------------------------------------------------

            stepCount = 0;
            receiverTouched = false;
            touchStep = -1;                   // not reached yet
            int prolongSteps = totalTimeSteps;       // GUI “Time steps” = extra steps

            while (!token.IsCancellationRequested)
            {
                UpdateStress();
                UpdateVelocity();
                stepCount++;

                if (!receiverTouched && CheckReceiverTouch())
                {
                    receiverTouched = true;
                    touchStep = stepCount;
                    Logger.Log($"[AcousticSimulator] RX touched at step {touchStep}");
                }

                if (receiverTouched && stepCount - touchStep >= prolongSteps)
                    break;                           // natural stop

                if (stepCount % 10 == 0)
                    ReportProgress();
            }

            if (token.IsCancellationRequested)       // user abort
            {
                Logger.Log("[AcousticSimulator] Simulation cancelled by user");
                ProgressUpdated?.Invoke(
                    this,
                    new AcousticSimulationProgressEventArgs(
                        0, stepCount, "Cancelled", null, null));
                return;
            }

            ReportProgress("Finalising", 99);
            FinaliseAndRaiseEvent();                 // will use measured arrival
        }
        #endregion

        #region helpers -----------------------------------------------------------------
        private void ComputeStableTimeStep()
        {
            double rhoMin = densityVolume.Cast<float>().Where(d => d > 0).Min();
            double vpMax = Math.Sqrt((lambda0 + 2 * mu0) / rhoMin);
            double f = sourceFrequencyKHz > 0 ? sourceFrequencyKHz * 1e3 : 1e5;
            double dtFreq = 1.0 / (20.0 * f);
            dt = Math.Min(SafetyCourant * pixelSize / vpMax, dtFreq);
        }

        private void ClearFields()
        {
            Array.Clear(vx, 0, vx.Length); Array.Clear(vy, 0, vy.Length); Array.Clear(vz, 0, vz.Length);
            Array.Clear(sxx, 0, sxx.Length); Array.Clear(syy, 0, syy.Length); Array.Clear(szz, 0, szz.Length);
            Array.Clear(sxy, 0, sxy.Length); Array.Clear(sxz, 0, sxz.Length); Array.Clear(syz, 0, syz.Length);
            Array.Clear(damage, 0, damage.Length);

            double pulse = sourceAmplitude * Math.Sqrt(sourceEnergyJ);
            sxx[tx, ty, tz] = syy[tx, ty, tz] = szz[tx, ty, tz] = pulse;
        }
        #endregion

        #region update ------------------------------------------------------------------
        private static double CubeRoot(double x) { return x >= 0 ? Math.Pow(x, 1.0 / 3.0) : -Math.Pow(-x, 1.0 / 3.0); }

        private void UpdateStress()
        {
            Parallel.For(1, depth - 1, z =>
            {
                double sinPhi = Math.Sin(failureAngleDeg * Math.PI / 180.0);
                double cosPhi = Math.Cos(failureAngleDeg * Math.PI / 180.0);
                double cohesionPa = cohesionMPa * 1e6;

                for (int y = 1; y < height - 1; y++)
                    for (int x = 1; x < width - 1; x++)
                    {
                        if (volumeLabels[x, y, z] != selectedMaterialID) continue;

                        double D = useBrittleModel ? damage[x, y, z] : 0.0;
                        double lambda = (1 - D) * lambda0;
                        double mu = (1 - D) * mu0;

                        // velocity gradients
                        double dvx_dx = (vx[x + 1, y, z] - vx[x - 1, y, z]) / (2 * pixelSize);
                        double dvy_dy = (vy[x, y + 1, z] - vy[x, y - 1, z]) / (2 * pixelSize);
                        double dvz_dz = (vz[x, y, z + 1] - vz[x, y, z - 1]) / (2 * pixelSize);
                        double dvx_dy = (vx[x, y + 1, z] - vx[x, y - 1, z]) / (2 * pixelSize);
                        double dvx_dz = (vx[x, y, z + 1] - vx[x, y, z - 1]) / (2 * pixelSize);
                        double dvy_dx = (vy[x + 1, y, z] - vy[x - 1, y, z]) / (2 * pixelSize);
                        double dvy_dz = (vy[x, y, z + 1] - vy[x, y, z - 1]) / (2 * pixelSize);
                        double dvz_dx = (vz[x + 1, y, z] - vz[x - 1, y, z]) / (2 * pixelSize);
                        double dvz_dy = (vz[x, y + 1, z] - vz[x, y - 1, z]) / (2 * pixelSize);

                        double volumetricStrainRate = dvx_dx + dvy_dy + dvz_dz;

                        // elastic predictor
                        double dsxx = dt * (lambda * volumetricStrainRate + 2 * mu * dvx_dx);
                        double dsyy = dt * (lambda * volumetricStrainRate + 2 * mu * dvy_dy);
                        double dszz = dt * (lambda * volumetricStrainRate + 2 * mu * dvz_dz);
                        double dsxy = dt * (mu * (dvx_dy + dvy_dx));
                        double dsxz = dt * (mu * (dvx_dz + dvz_dx));
                        double dsyz = dt * (mu * (dvy_dz + dvz_dy));

                        double sxxN = sxx[x, y, z] + dsxx;
                        double syyN = syy[x, y, z] + dsyy;
                        double szzN = szz[x, y, z] + dszz;
                        double sxyN = sxy[x, y, z] + dsxy;
                        double sxzN = sxz[x, y, z] + dsxz;
                        double syzN = syz[x, y, z] + dsyz;

                        // plastic correction (Mohr‑Coulomb)
                        if (usePlasticModel)
                        {
                            double mean = (sxxN + syyN + szzN) / 3.0;
                            double dev_xx = sxxN - mean;
                            double dev_yy = syyN - mean;
                            double dev_zz = szzN - mean;
                            double J2 = 0.5 * (dev_xx * dev_xx + dev_yy * dev_yy + dev_zz * dev_zz) + (sxyN * sxyN + sxzN * sxzN + syzN * syzN);
                            double tau = Math.Sqrt(J2);
                            double p = -mean;
                            double yield = tau + p * sinPhi - cohesionPa * cosPhi;
                            if (yield > 0)
                            {
                                double scale = (tau - (cohesionPa * cosPhi - p * sinPhi)) / tau;
                                dev_xx *= 1 - scale; dev_yy *= 1 - scale; dev_zz *= 1 - scale;
                                sxyN *= 1 - scale; sxzN *= 1 - scale; syzN *= 1 - scale;
                                sxxN = dev_xx + mean; syyN = dev_yy + mean; szzN = dev_zz + mean;
                            }
                        }

                        // brittle damage (max tensile principal stress)
                        if (useBrittleModel)
                        {
                            // invariants
                            double I1 = sxxN + syyN + szzN;
                            double I2 = sxxN * syyN + syyN * szzN + szzN * sxxN - sxyN * sxyN - sxzN * sxzN - syzN * syzN;
                            double I3 = sxxN * (syyN * szzN - syzN * syzN) - sxyN * (sxyN * szzN - syzN * sxzN) + sxzN * (sxyN * syzN - syyN * sxzN);
                            double a = -I1; double b = I2; double c = -I3;
                            double q = (3 * b - a * a) / 9.0;
                            double r = (9 * a * b - 27 * c - 2 * a * a * a) / 54.0;
                            double disc = q * q * q + r * r;
                            double sigmaMax;
                            if (disc >= 0)
                            {
                                double sqrtDisc = Math.Sqrt(disc);
                                double s1 = CubeRoot(r + sqrtDisc);
                                double s2 = CubeRoot(r - sqrtDisc);
                                sigmaMax = -a / 3.0 + s1 + s2;
                            }
                            else
                            {
                                double thetaAcos = Math.Acos(r / Math.Sqrt(-q * q * q));
                                sigmaMax = 2.0 * Math.Sqrt(-q) * Math.Cos(thetaAcos / 3.0) - a / 3.0;
                            }

                            double tensilePa = tensileStrengthMPa * 1e6;
                            if (sigmaMax > tensilePa && D < 1.0)
                            {
                                double incr = (sigmaMax - tensilePa) / tensilePa;
                                damage[x, y, z] = Math.Min(1.0, D + incr * 0.01);
                                double factor = 1.0 - damage[x, y, z];
                                sxxN *= factor; syyN *= factor; szzN *= factor;
                                sxyN *= factor; sxzN *= factor; syzN *= factor;
                            }
                        }

                        // commit
                        sxx[x, y, z] = sxxN; syy[x, y, z] = syyN; szz[x, y, z] = szzN;
                        sxy[x, y, z] = sxyN; sxz[x, y, z] = sxzN; syz[x, y, z] = syzN;
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
        #endregion

        #region termination -------------------------------------------------------------
        private bool CheckReceiverTouch()
        {
            return Math.Abs(vx[rx, ry, rz]) > 1e-6 || Math.Abs(vy[rx, ry, rz]) > 1e-6 || Math.Abs(vz[rx, ry, rz]) > 1e-6;
        }

        private bool CheckEnergyStopping()
        {
            double e = CalcEnergyAtPoint(rx, ry, rz);
            if (e > maxReceiverEnergy) { maxReceiverEnergy = e; return false; }
            if (!energyPeaked && e < 0.5 * maxReceiverEnergy) energyPeaked = true;
            if (energyPeaked && e < 0.01 * maxReceiverEnergy) return true;
            return false;
        }

        private double CalcEnergyAtPoint(int x, int y, int z)
        {
            double rho = densityVolume[x, y, z];
            double ke = 0.5 * rho * (vx[x, y, z] * vx[x, y, z] + vy[x, y, z] * vy[x, y, z] + vz[x, y, z] * vz[x, y, z]);
            double D = useBrittleModel ? damage[x, y, z] : 0.0;
            double mu = (1 - D) * mu0; double lambda = (1 - D) * lambda0;
            double mean = (sxx[x, y, z] + syy[x, y, z] + szz[x, y, z]) / 3.0;
            double se = 0.5 / (2 * mu) * ((sxx[x, y, z] - mean) * (sxx[x, y, z] - mean) + (syy[x, y, z] - mean) * (syy[x, y, z] - mean) + (szz[x, y, z] - mean) * (szz[x, y, z] - mean))
                        + (sxy[x, y, z] * sxy[x, y, z] + sxz[x, y, z] * sxz[x, y, z] + syz[x, y, z] * syz[x, y, z]) / (2 * mu);
            return ke + se;
        }
        #endregion

        #region progress & completion ---------------------------------------------------
        private void ReportProgress(string text = "Simulating", int? force = null)
        {
            int percent = force ?? (int)(stepCount * 100.0 / expectedTotalSteps);
            if (percent > 99) percent = 99;                // keep 100 % for Finish()
            ProgressUpdated?.Invoke(
                this,
                new AcousticSimulationProgressEventArgs(
                    percent, stepCount, text, null, null));
            Logger.Log("[AcousticSimulator] CPU Simulation Progress: " + percent + " Step: " + stepCount + "/" + expectedTotalSteps);
        }

        private static float[,,] ConvertToFloat(double[,,] src)
        {
            int w = src.GetLength(0), h = src.GetLength(1), d = src.GetLength(2);
            float[,,] dst = new float[w, h, d];
            for (int z = 0; z < d; z++)
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++) dst[x, y, z] = (float)src[x, y, z];
            return dst;
        }

        private void FinaliseAndRaiseEvent()
        {
            double dist = Math.Sqrt((tx - rx) * (tx - rx) +
                                    (ty - ry) * (ty - ry) +
                                    (tz - rz) * (tz - rz)) * pixelSize;

            double vp = dist / (touchStep * dt);              // measured
            int pSteps = touchStep;

            double vs = dist / ((stepCount - touchStep) * dt);
            int sSteps = stepCount - touchStep;

            SimulationCompleted?.Invoke(
                this,
                new AcousticSimulationCompleteEventArgs(
                    vp, vs, vp / vs, pSteps, sSteps, stepCount));
        }
        #endregion

        #region IDisposable -------------------------------------------------------------
        public void Dispose()
        {
            cts.Cancel();
            GC.SuppressFinalize(this);
        }
        #endregion
    }

    #region event args -----------------------------------------------------------------
    public class AcousticSimulationProgressEventArgs : EventArgs
    {
        public int ProgressPercent { get; }
        public int TimeStep { get; }
        public string StatusText { get; }
        public float[,,] PWaveField { get; }
        public float[,,] SWaveField { get; }
        public AcousticSimulationProgressEventArgs(int percent, int step, string text, float[,,] p, float[,,] s)
        { ProgressPercent = percent; TimeStep = step; StatusText = text; PWaveField = p; SWaveField = s; }
    }

    public class AcousticSimulationCompleteEventArgs : EventArgs
    {
        public double PWaveVelocity { get; }
        public double SWaveVelocity { get; }
        public double VpVsRatio { get; }
        public int PWaveTravelTime { get; }
        public int SWaveTravelTime { get; }
        public int TotalTimeSteps { get; }
        public AcousticSimulationCompleteEventArgs(double vp, double vs, double ratio, int pTime, int sTime, int total)
        { PWaveVelocity = vp; SWaveVelocity = vs; VpVsRatio = ratio; PWaveTravelTime = pTime; SWaveTravelTime = sTime; TotalTimeSteps = total; }
    }
    #endregion
}
