using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CTS
{
    /// <summary>
    /// Full‑physics acoustic / elastodynamic simulator with linear‑elastic, perfectly‑plastic (Mohr–Coulomb)
    /// and tensile‑brittle damage response. Written for C# 7.3 compatibility – no target‑typed «new» or
    /// nullable reference‑type syntax, and no reliance on Math.Cbrt.
    /// </summary>
    /// 
    public class AcousticSimulator : IDisposable
    {
        #region configuration -----------------------------------------------------------
        private const double WAVE_VISUALIZATION_AMPLIFICATION = 1.0e10;
        private readonly int width, height, depth;
        private readonly float pixelSize;
        private readonly byte[,,] volumeLabels;
        private readonly float[,,] densityVolume;
        private readonly byte selectedMaterialID;
        private bool pWaveReceiverTouched;
        private bool sWaveReceiverTouched;
        private int pWaveTouchStep;
        private int sWaveTouchStep;
        private double pWaveMaxAmplitude;
        private double sWaveMaxAmplitude;
        private readonly bool useFullFaceTransducers;

        // progress ----------------------------------------------------
        private int expectedTotalSteps;
        private FrameCacheManager cacheManager;
        private bool enableFrameCaching = true;
        private int cacheInterval = 1; // Save every frame
        private List<float> pWaveHistory = new List<float>();
        private List<float> sWaveHistory = new List<float>();
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
        private int tx, ty, tz;
        private int rx, ry, rz;

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
        public bool EnableFrameCaching
        {
            get => enableFrameCaching;
            set => enableFrameCaching = value;
        }

        public int CacheInterval
        {
            get => cacheInterval;
            set => cacheInterval = Math.Max(1, value);
        }
        #endregion

        #region constructor -------------------------------------------------------------
        public AcousticSimulator(
    int width, int height, int depth, float pixelSize,
    byte[,,] volumeLabels, float[,,] densityVolume, byte selectedMaterialID,
    string axis, string waveType,
    double confiningPressure, double tensileStrength, double failureAngle, double cohesion,
    double energy, double frequency, int amplitude, int timeSteps,
    bool useElasticModel, bool usePlasticModel, bool useBrittleModel,
    double youngsModulus, double poissonRatio,
    int tx, int ty, int tz, int rx, int ry, int rz, bool useFullFaceTransducers = false)
        {
            // Grid & material properties
            this.width = width;
            this.height = height;
            this.depth = depth;
            this.pixelSize = pixelSize;
            this.volumeLabels = volumeLabels;
            this.densityVolume = densityVolume;
            this.selectedMaterialID = selectedMaterialID;

            // Physics parameters
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
            this.useFullFaceTransducers = useFullFaceTransducers;
            // Lamé constants (Pa)
            double E = youngsModulusMPa * 1e6;
            mu0 = E / (2.0 * (1.0 + poissonRatio));
            lambda0 = E * poissonRatio / ((1 + poissonRatio) * (1 - 2 * poissonRatio));
            Logger.Log($"[AcousticSimulator] Full-face transducers: {useFullFaceTransducers}");
            // Allocate arrays for simulation
            vx = new double[width, height, depth];
            vy = new double[width, height, depth];
            vz = new double[width, height, depth];
            sxx = new double[width, height, depth];
            syy = new double[width, height, depth];
            szz = new double[width, height, depth];
            sxy = new double[width, height, depth];
            sxz = new double[width, height, depth];
            syz = new double[width, height, depth];
            damage = new double[width, height, depth];

            // Set transducer positions from parameters
            this.tx = tx;
            this.ty = ty;
            this.tz = tz;
            this.rx = rx;
            this.ry = ry;
            this.rz = rz;

            // Ensure transducers are within the volume boundaries and not on edges
            if (this.tx < 1) this.tx = 1;
            if (this.ty < 1) this.ty = 1;
            if (this.tz < 1) this.tz = 1;
            if (this.rx < 1) this.rx = 1;
            if (this.ry < 1) this.ry = 1;
            if (this.rz < 1) this.rz = 1;
            if (this.tx >= width - 1) this.tx = width - 2;
            if (this.ty >= height - 1) this.ty = height - 2;
            if (this.tz >= depth - 1) this.tz = depth - 2;
            if (this.rx >= width - 1) this.rx = width - 2;
            if (this.ry >= height - 1) this.ry = height - 2;
            if (this.rz >= depth - 1) this.rz = depth - 2;

            Logger.Log($"[AcousticSimulator] Using TX: ({this.tx},{this.ty},{this.tz}), RX: ({this.rx},{this.ry},{this.rz})");

            // Set minimum required steps for simulation to avoid premature termination
            minRequiredSteps = Math.Max(50, timeSteps / 10);
            if (enableFrameCaching)
            {
                string cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AcousticSimulator");
                Directory.CreateDirectory(cacheDir);
                cacheManager = new FrameCacheManager(cacheDir, width, height, depth);
                Logger.Log($"[AcousticSimulator] Frame caching enabled, interval: {cacheInterval}");
            }
        }
        #endregion

        #region public API --------------------------------------------------------------
        public void StartSimulation()
        {
            // Capture the current SynchronizationContext (presumably the UI thread)
            var context = SynchronizationContext.Current;

            Task.Run(() =>
            {
                // Set the context for the worker thread so event marshaling works
                SynchronizationContext.SetSynchronizationContext(context);
                Run(cts.Token);
            });
        }
        public void CancelSimulation() { cts.Cancel(); }
        public (double[,,] vx, double[,,] vy, double[,,] vz) GetWaveFieldSnapshot() => (vx, vy, vz);
        #endregion

        #region core loop ---------------------------------------------------------------
        public AcousticSimulator() { }
        private void Run(CancellationToken token)
        {
            ComputeStableTimeStep();                 // sets dt
            ClearFields();                          // calls ApplySource() for initial pulse

            // ----- progress -----------------------------------------------------------
            double dist = Math.Sqrt((tx - rx) * (tx - rx) +
                                         (ty - ry) * (ty - ry) +
                                         (tz - rz) * (tz - rz)) * pixelSize;
            double rhoAvg = densityVolume.Cast<float>().Average();
            rhoAvg = Math.Max(rhoAvg, 100.0); // Safety minimum
            double vpEst = Math.Sqrt((lambda0 + 2 * mu0) / rhoAvg);
            vpEst = Math.Min(vpEst, 6000.0); // Reasonable maximum
            expectedTotalSteps = (int)Math.Ceiling(dist / (vpEst * dt)) + totalTimeSteps;

            // Add maximum step limit to prevent infinite simulations
            int absoluteMaxSteps = Math.Max(1000, expectedTotalSteps * 2);
            // --------------------------------------------------------------------------

            stepCount = 0;
            pWaveReceiverTouched = false;
            sWaveReceiverTouched = false;
            pWaveTouchStep = -1;
            sWaveTouchStep = -1;
            pWaveMaxAmplitude = 0;
            sWaveMaxAmplitude = 0;
            int prolongSteps = totalTimeSteps;  // GUI "Time steps" = extra steps after both waves arrive

            Logger.Log($"[AcousticSimulator] Starting simulation with prolongSteps: {prolongSteps}");
            Logger.Log($"[AcousticSimulator] Expected total steps: {expectedTotalSteps}, Maximum allowed: {absoluteMaxSteps}");
            Logger.Log($"[AcousticSimulator] Using full-face transducers: {useFullFaceTransducers}");

            // Add variables to detect instability
            bool instabilityDetected = false;
            double previousMaxField = 0;
            int stableCount = 0;
            int instabilityCounter = 0;

            // Set check interval based on transducer type
            int waveCheckInterval = useFullFaceTransducers ? 5 : 1;
            int progressInterval = useFullFaceTransducers ? 10 : 5;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    UpdateStress();
                    UpdateVelocity();
                    stepCount++;

                    // FRAME CACHING - Add this after each simulation step
                    if (cacheManager != null && stepCount % cacheInterval == 0)
                    {
                        SaveFrameToCache();
                    }

                    // Check for numerical instability (less frequently for full-face)
                    if (stepCount % 20 == 0)
                    {
                        double currentMaxField = GetMaxFieldValue();
                        if (double.IsInfinity(currentMaxField) || double.IsNaN(currentMaxField) ||
                            currentMaxField > 1e30 || (currentMaxField > 1e15 && currentMaxField > previousMaxField * 10))
                        {
                            instabilityCounter++;

                            if (instabilityCounter >= 3) // Confirm instability with multiple detections
                            {
                                Logger.Log($"[AcousticSimulator] WARNING: Numerical instability detected at step {stepCount}. Max field value: {currentMaxField:E6}");
                                instabilityDetected = true;

                                // Start checking for wave arrival even with instability
                                if (!pWaveReceiverTouched && stepCount > minRequiredSteps / 2)
                                {
                                    pWaveReceiverTouched = true;
                                    pWaveTouchStep = stepCount;
                                    Logger.Log($"[AcousticSimulator] Using current step {stepCount} as P-Wave arrival due to instability");
                                }
                                else if (pWaveReceiverTouched && !sWaveReceiverTouched && stepCount > pWaveTouchStep + minRequiredSteps / 4)
                                {
                                    sWaveReceiverTouched = true;
                                    sWaveTouchStep = stepCount;
                                    Logger.Log($"[AcousticSimulator] Using current step {stepCount} as S-Wave arrival due to instability");
                                }
                            }
                        }
                        else
                        {
                            instabilityCounter = 0;
                            stableCount++;
                        }
                        previousMaxField = currentMaxField;
                    }

                    // Check for wave arrivals (optimized frequency for full-face)
                    if (stepCount % waveCheckInterval == 0)
                    {
                        // Check for P-wave arrival (primarily in vx component)
                        if (!pWaveReceiverTouched && CheckPWaveReceiverTouch())
                        {
                            pWaveReceiverTouched = true;
                            pWaveTouchStep = stepCount;
                            Logger.Log($"[AcousticSimulator] P-Wave reached RX at step {pWaveTouchStep}");
                        }

                        // Check for S-wave arrival (primarily in vy/vz components)
                        if (pWaveReceiverTouched && !sWaveReceiverTouched && CheckSWaveReceiverTouch())
                        {
                            sWaveReceiverTouched = true;
                            sWaveTouchStep = stepCount;
                            Logger.Log($"[AcousticSimulator] S-Wave reached RX at step {sWaveTouchStep}");
                        }
                    }

                    // Only terminate after BOTH waves have been detected and additional steps
                    if (pWaveReceiverTouched && sWaveReceiverTouched &&
                        (stepCount - sWaveTouchStep >= prolongSteps))
                    {
                        Logger.Log($"[AcousticSimulator] Terminating after both waves + {prolongSteps} extra steps");
                        break;  // natural stop
                    }

                    // Time-based automatic termination even if waves aren't detected properly
                    if (stepCount >= absoluteMaxSteps)
                    {
                        Logger.Log($"[AcousticSimulator] WARNING: Terminating due to reaching maximum step count ({stepCount})");

                        // If we haven't detected waves but reached maximum steps,
                        // use estimated arrival times based on expected speeds
                        if (!pWaveReceiverTouched)
                        {
                            pWaveReceiverTouched = true;
                            pWaveTouchStep = absoluteMaxSteps / 3;
                            Logger.Log($"[AcousticSimulator] Using estimated P-Wave arrival at step {pWaveTouchStep}");
                        }

                        if (!sWaveReceiverTouched)
                        {
                            sWaveReceiverTouched = true;
                            sWaveTouchStep = absoluteMaxSteps / 2;
                            Logger.Log($"[AcousticSimulator] Using estimated S-Wave arrival at step {sWaveTouchStep}");
                        }

                        break;
                    }

                    // Report progress (less frequently for full-face)
                    if (stepCount % progressInterval == 0)
                    {
                        ReportProgress();
                    }
                }
                catch (Exception ex)
                {
                    // Handle any exceptions during simulation
                    Logger.Log($"[AcousticSimulator] Error during simulation step {stepCount}: {ex.Message}");
                    instabilityDetected = true;

                    // Force termination with estimated results
                    if (!pWaveReceiverTouched)
                    {
                        pWaveReceiverTouched = true;
                        pWaveTouchStep = Math.Max(10, stepCount / 3);
                    }

                    if (!sWaveReceiverTouched)
                    {
                        sWaveReceiverTouched = true;
                        sWaveTouchStep = Math.Max(pWaveTouchStep + 5, stepCount / 2);
                    }

                    break;
                }
            }

            if (token.IsCancellationRequested)       // user abort
            {
                Logger.Log("[AcousticSimulator] Simulation cancelled by user");

                var handler = ProgressUpdated;
                if (handler != null)
                {
                    var args = new AcousticSimulationProgressEventArgs(
                        0, stepCount, "Cancelled", null, null);

                    var context = SynchronizationContext.Current;
                    if (context != null)
                    {
                        context.Post(_ => handler(this, args), null);
                    }
                    else
                    {
                        handler(this, args);
                    }
                }
                return;
            }

            ReportProgress("Finalising", 99);
            FinaliseAndRaiseEvent();                 // will use measured arrival
        }

        private double GetMaxFieldValue()
        {
            double maxVal = 0;

            // Sample a subset of points to avoid slow computation
            int stride = Math.Max(1, width / 10);

            for (int z = 0; z < depth; z += stride)
                for (int y = 0; y < height; y += stride)
                    for (int x = 0; x < width; x += stride)
                    {
                        double vxAbs = Math.Abs(vx[x, y, z]);
                        double vyAbs = Math.Abs(vy[x, y, z]);
                        double vzAbs = Math.Abs(vz[x, y, z]);
                        double sxxAbs = Math.Abs(sxx[x, y, z]);
                        double syyAbs = Math.Abs(syy[x, y, z]);
                        double szzAbs = Math.Abs(szz[x, y, z]);

                        maxVal = Math.Max(maxVal, vxAbs);
                        maxVal = Math.Max(maxVal, vyAbs);
                        maxVal = Math.Max(maxVal, vzAbs);
                        maxVal = Math.Max(maxVal, sxxAbs);
                        maxVal = Math.Max(maxVal, syyAbs);
                        maxVal = Math.Max(maxVal, szzAbs);
                    }

            return maxVal;
        }
        #endregion

        #region helpers -----------------------------------------------------------------
        private void ComputeStableTimeStep()
        {
            double rhoMin = densityVolume.Cast<float>()
                            .Where(d => d > 0)
                            .DefaultIfEmpty(1000f)   // graceful fallback
                            .Min();

            double vpMax = Math.Sqrt((lambda0 + 2 * mu0) / rhoMin);
            vpMax = Math.Min(vpMax, 6000.0);         // upper-cap for robustness

            double f = sourceFrequencyKHz > 0 ? sourceFrequencyKHz * 1e3 : 1e5;
            double dtFreq = 1.0 / (20.0 * f);

            const double Safety = 0.20;              // CFL safety factor
            dt = Math.Min(Safety * pixelSize / vpMax, dtFreq);
            dt = Math.Max(dt, 1e-8);                 // never go sub-nanosecond

            Logger.Log($"[AcousticSimulator] dt={dt:E6}s, vpMax={vpMax:F1}m/s, rhoMin={rhoMin:F0}kg/m³");
        }
        private double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 0.0;

            return Math.Max(min, Math.Min(max, value));
        }

        private void ClearFields()
        {
            // ------------------------------------------------------ 0. reset dynamics
            Array.Clear(vx, 0, vx.Length);
            Array.Clear(vy, 0, vy.Length);
            Array.Clear(vz, 0, vz.Length);
            Array.Clear(sxy, 0, sxy.Length);
            Array.Clear(sxz, 0, sxz.Length);
            Array.Clear(syz, 0, syz.Length);
            Array.Clear(damage, 0, damage.Length);

            // ------------------------------------------------------ 1. confining stress
            double confPa = confiningPressureMPa * 1e6;   // MPa → Pa

            Parallel.For(0, depth, z =>
            {
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        if (volumeLabels[x, y, z] == selectedMaterialID)
                        {
                            sxx[x, y, z] = -confPa;        // –ve = compression
                            syy[x, y, z] = -confPa;
                            szz[x, y, z] = -confPa;
                        }
            });

            // ------------------------------------------------------ 2. pulse (same magnitude, now **negative**)
            double pulse = -sourceAmplitude * Math.Sqrt(sourceEnergyJ) * 1e6;
            Logger.Log($"[CPU] Source pulse = {pulse:E6}  (full-face = {useFullFaceTransducers})");

            if (!useFullFaceTransducers)
            {
                // existing point-source logic unchanged
                int radius = 2;
                for (int dz = -radius; dz <= radius; dz++)
                    for (int dy = -radius; dy <= radius; dy++)
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                            if (dist > radius) continue;

                            int sx = tx + dx, sy = ty + dy, sz = tz + dz;
                            if (sx < 0 || sx >= width ||
                                sy < 0 || sy >= height ||
                                sz < 0 || sz >= depth) continue;

                            if (volumeLabels[sx, sy, sz] != selectedMaterialID) continue;

                            double fall = 1.0 - dist / radius;
                            double local = pulse * fall * fall;

                            sxx[sx, sy, sz] += local;
                            syy[sx, sy, sz] += local;
                            szz[sx, sy, sz] += local;

                            double vKick = local / (densityVolume[sx, sy, sz] * 10.0);
                            int dir = Math.Sign(rx - tx);
                            if (dir == 0) dir = 1;
                            vx[sx, sy, sz] += vKick * dir;
                        }
                return;
            }

            // ------------------------------------------------------ 3. full-face stress & velocity (CPU version)
            // choose the dominating axis
            int axis = Math.Abs(rx - tx) >= Math.Abs(ry - ty) && Math.Abs(rx - tx) >= Math.Abs(rz - tz) ? 0
                     : Math.Abs(ry - ty) >= Math.Abs(rz - tz) ? 1 : 2;

            int sign = axis == 0 ? Math.Sign(rx - tx)
                    : axis == 1 ? Math.Sign(ry - ty)
                                 : Math.Sign(rz - tz);
            if (sign == 0) sign = 1;

            switch (axis)
            {
                case 0: // X-face
                    Parallel.For(0, height, y =>
                    {
                        for (int z = 0; z < depth; z++)
                            if (volumeLabels[tx, y, z] == selectedMaterialID)
                            {
                                sxx[tx, y, z] += pulse;
                                syy[tx, y, z] += pulse;
                                szz[tx, y, z] += pulse;

                                double vKick = pulse / (densityVolume[tx, y, z] * 10.0);
                                vx[tx, y, z] += vKick * sign;
                            }
                    });
                    break;

                case 1: // Y-face
                    Parallel.For(0, width, x =>
                    {
                        for (int z = 0; z < depth; z++)
                            if (volumeLabels[x, ty, z] == selectedMaterialID)
                            {
                                sxx[x, ty, z] += pulse;
                                syy[x, ty, z] += pulse;
                                szz[x, ty, z] += pulse;

                                double vKick = pulse / (densityVolume[x, ty, z] * 10.0);
                                vy[x, ty, z] += vKick * sign;
                            }
                    });
                    break;

                default: // Z-face
                    Parallel.For(0, width, x =>
                    {
                        for (int y = 0; y < height; y++)
                            if (volumeLabels[x, y, tz] == selectedMaterialID)
                            {
                                sxx[x, y, tz] += pulse;
                                syy[x, y, tz] += pulse;
                                szz[x, y, tz] += pulse;

                                double vKick = pulse / (densityVolume[x, y, tz] * 10.0);
                                vz[x, y, tz] += vKick * sign;
                            }
                    });
                    break;
            }
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

                        // Add stability check for very large gradients
                        const double MAX_GRADIENT = 1.0e12;
                        dvx_dx = Clamp(dvx_dx, -MAX_GRADIENT, MAX_GRADIENT);
                        dvy_dy = Clamp(dvy_dy, -MAX_GRADIENT, MAX_GRADIENT);
                        dvz_dz = Clamp(dvz_dz, -MAX_GRADIENT, MAX_GRADIENT);
                        dvx_dy = Clamp(dvx_dy, -MAX_GRADIENT, MAX_GRADIENT);
                        dvx_dz = Clamp(dvx_dz, -MAX_GRADIENT, MAX_GRADIENT);
                        dvy_dx = Clamp(dvy_dx, -MAX_GRADIENT, MAX_GRADIENT);
                        dvy_dz = Clamp(dvy_dz, -MAX_GRADIENT, MAX_GRADIENT);
                        dvz_dx = Clamp(dvz_dx, -MAX_GRADIENT, MAX_GRADIENT);
                        dvz_dy = Clamp(dvz_dy, -MAX_GRADIENT, MAX_GRADIENT);

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

                        // plastic correction (Mohr‑Coulomb) - modified to preserve oscillations
                        if (usePlasticModel)
                        {
                            double mean = (sxxN + syyN + szzN) / 3.0;
                            double dev_xx = sxxN - mean;
                            double dev_yy = syyN - mean;
                            double dev_zz = szzN - mean;
                            double J2 = 0.5 * (dev_xx * dev_xx + dev_yy * dev_yy + dev_zz * dev_zz) +
                                        (sxyN * sxyN + sxzN * sxzN + syzN * syzN);
                            double tau = Math.Sqrt(J2);

                            // Include confining pressure in the pressure calculation (Pa)
                            double confiningPressurePa = confiningPressureMPa * 1e6;
                            double p = -mean + confiningPressurePa;  // Add confining pressure

                            double yield = tau + p * sinPhi - cohesionPa * cosPhi;
                            if (yield > 0)
                            {
                                // Modified scale calculation to preserve oscillations
                                double scale = (tau - (cohesionPa * cosPhi - p * sinPhi)) / tau;

                                // Limit scale to preserve oscillations (never fully zero out)
                                scale = Math.Min(scale, 0.95);

                                // Apply scale to deviatoric components but preserve sign
                                dev_xx *= 1 - scale; dev_yy *= 1 - scale; dev_zz *= 1 - scale;
                                sxyN *= 1 - scale; sxzN *= 1 - scale; syzN *= 1 - scale;

                                // Recombine deviatoric and mean components
                                sxxN = dev_xx + mean; syyN = dev_yy + mean; szzN = dev_zz + mean;
                            }
                        }

                        // brittle damage (max tensile principal stress) - modified to preserve oscillations
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
                                // Modify damage calculation to preserve oscillations
                                double incr = (sigmaMax - tensilePa) / tensilePa;

                                // Limit damage increment to allow oscillations
                                incr = Math.Min(incr, 0.1);

                                // Cap maximum damage to allow continued oscillations
                                damage[x, y, z] = Math.Min(0.95, D + incr * 0.01);

                                double factor = 1.0 - damage[x, y, z];

                                // Scale stresses but preserve sign for oscillations
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
            // Define a damping coefficient to prevent wave acceleration
            const double DAMPING_FACTOR = 0.05; // 5% damping per step

            Parallel.For(1, depth - 1, z =>
            {
                for (int y = 1; y < height - 1; y++)
                    for (int x = 1; x < width - 1; x++)
                    {
                        if (volumeLabels[x, y, z] != selectedMaterialID) continue;

                        // Ensure density is reasonable
                        double rho = Math.Max(100.0, densityVolume[x, y, z]);

                        // Calculate stress gradients
                        double dsxx_dx = (sxx[x, y, z] - sxx[x - 1, y, z]) / pixelSize;
                        double dsxy_dy = (sxy[x, y, z] - sxy[x, y - 1, z]) / pixelSize;
                        double dsxz_dz = (sxz[x, y, z] - sxz[x, y, z - 1]) / pixelSize;
                        double dsyy_dy = (syy[x, y, z] - syy[x, y - 1, z]) / pixelSize;
                        double dsxy_dx = (sxy[x + 1, y, z] - sxy[x, y, z]) / pixelSize;
                        double dsyz_dz = (syz[x, y, z] - syz[x, y, z - 1]) / pixelSize;
                        double dszz_dz = (szz[x, y, z] - szz[x, y, z - 1]) / pixelSize;
                        double dsxz_dx = (sxz[x + 1, y, z] - sxz[x, y, z]) / pixelSize;
                        double dsyz_dy = (syz[x, y + 1, z] - syz[x, y, z]) / pixelSize;

                        // Only use maximum limit for stability
                        const double MAX_GRADIENT = 1.0e12;
                        dsxx_dx = Clamp(dsxx_dx, -MAX_GRADIENT, MAX_GRADIENT);
                        dsxy_dy = Clamp(dsxy_dy, -MAX_GRADIENT, MAX_GRADIENT);
                        dsxz_dz = Clamp(dsxz_dz, -MAX_GRADIENT, MAX_GRADIENT);
                        dsyy_dy = Clamp(dsyy_dy, -MAX_GRADIENT, MAX_GRADIENT);
                        dsxy_dx = Clamp(dsxy_dx, -MAX_GRADIENT, MAX_GRADIENT);
                        dsyz_dz = Clamp(dsyz_dz, -MAX_GRADIENT, MAX_GRADIENT);
                        dszz_dz = Clamp(dszz_dz, -MAX_GRADIENT, MAX_GRADIENT);
                        dsxz_dx = Clamp(dsxz_dx, -MAX_GRADIENT, MAX_GRADIENT);
                        dsyz_dy = Clamp(dsyz_dy, -MAX_GRADIENT, MAX_GRADIENT);

                        // Calculate velocity updates
                        double dvx = dt * (dsxx_dx + dsxy_dy + dsxz_dz) / rho;
                        double dvy = dt * (dsxy_dx + dsyy_dy + dsyz_dz) / rho;
                        double dvz = dt * (dsxz_dx + dsyz_dy + dszz_dz) / rho;

                        // Update velocities WITH damping to prevent acceleration
                        double damping = 1.0 - DAMPING_FACTOR;
                        vx[x, y, z] = vx[x, y, z] * damping + dvx;
                        vy[x, y, z] = vy[x, y, z] * damping + dvy;
                        vz[x, y, z] = vz[x, y, z] * damping + dvz;

                        // Only clamp at extreme values to prevent numerical explosion
                        const double MAX_VELOCITY = 1.0e10;
                        vx[x, y, z] = Clamp(vx[x, y, z], -MAX_VELOCITY, MAX_VELOCITY);
                        vy[x, y, z] = Clamp(vy[x, y, z], -MAX_VELOCITY, MAX_VELOCITY);
                        vz[x, y, z] = Clamp(vz[x, y, z], -MAX_VELOCITY, MAX_VELOCITY);
                    }
            });
        }
        #endregion

        #region termination -------------------------------------------------------------
        private double GetTheoreticalVpVsRatio()
        {
            return Math.Sqrt((lambda0 + 2 * mu0) / mu0);
        }
        private bool CheckSWaveReceiverTouch()
        {
            if (!pWaveReceiverTouched) return false;
            if (stepCount - pWaveTouchStep < 5) return false;

            const double timeTolerance = 0.05;
            const double ampFraction = 0.15;
            const double distinctFactor = 1.0;
            const int pTailSteps = 10;
            const double vpvsMin = 1.3;
            const double vpvsMax = 2.2;
            const double tiny = 1e-10;

            int mainAxis = (Math.Abs(rx - tx) >= Math.Abs(ry - ty) &&
                            Math.Abs(rx - tx) >= Math.Abs(rz - tz)) ? 0
                          : (Math.Abs(ry - ty) >= Math.Abs(rx - tx) &&
                             Math.Abs(ry - ty) >= Math.Abs(rz - tz)) ? 1 : 2;

            double pMag = 0, sMag = 0;

            if (useFullFaceTransducers)
            {
                // SUBSAMPLE for performance
                int sampleRate = 4;
                int count = 0;

                switch (mainAxis)
                {
                    case 0: // X axis - subsample YZ plane
                        for (int y = 0; y < height; y += sampleRate)
                            for (int z = 0; z < depth; z += sampleRate)
                                if (volumeLabels[rx, y, z] == selectedMaterialID)
                                {
                                    double vxRx = vx[rx, y, z];
                                    double vyRx = vy[rx, y, z];
                                    double vzRx = vz[rx, y, z];
                                    pMag += Math.Abs(vxRx);
                                    sMag += Math.Sqrt(vyRx * vyRx + vzRx * vzRx);
                                    count++;
                                }
                        break;
                    case 1: // Y axis - subsample XZ plane
                        for (int x = 0; x < width; x += sampleRate)
                            for (int z = 0; z < depth; z += sampleRate)
                                if (volumeLabels[x, ry, z] == selectedMaterialID)
                                {
                                    double vxRx = vx[x, ry, z];
                                    double vyRx = vy[x, ry, z];
                                    double vzRx = vz[x, ry, z];
                                    pMag += Math.Abs(vyRx);
                                    sMag += Math.Sqrt(vxRx * vxRx + vzRx * vzRx);
                                    count++;
                                }
                        break;
                    default: // Z axis - subsample XY plane
                        for (int x = 0; x < width; x += sampleRate)
                            for (int y = 0; y < height; y += sampleRate)
                                if (volumeLabels[x, y, rz] == selectedMaterialID)
                                {
                                    double vxRx = vx[x, y, rz];
                                    double vyRx = vy[x, y, rz];
                                    double vzRx = vz[x, y, rz];
                                    pMag += Math.Abs(vzRx);
                                    sMag += Math.Sqrt(vxRx * vxRx + vyRx * vyRx);
                                    count++;
                                }
                        break;
                }
                if (count > 0)
                {
                    pMag /= count;
                    sMag /= count;
                }
            }
            else
            {
                // Point detection (no changes)
                double vxRx = vx[rx, ry, rz];
                double vyRx = vy[rx, ry, rz];
                double vzRx = vz[rx, ry, rz];

                switch (mainAxis)
                {
                    case 0: pMag = Math.Abs(vxRx); sMag = Math.Sqrt(vyRx * vyRx + vzRx * vzRx); break;
                    case 1: pMag = Math.Abs(vyRx); sMag = Math.Sqrt(vxRx * vxRx + vzRx * vzRx); break;
                    default: pMag = Math.Abs(vzRx); sMag = Math.Sqrt(vxRx * vxRx + vyRx * vyRx); break;
                }
            }

            if (double.IsNaN(sMag) || double.IsInfinity(sMag)) return false;

            if (sMag > sWaveMaxAmplitude) sWaveMaxAmplitude = sMag;
            double threshold = Math.Max(tiny, sWaveMaxAmplitude * ampFraction);

            double vpvsTheory = Clamp(GetTheoreticalVpVsRatio(), vpvsMin, vpvsMax);
            int expectedS = (int)(pWaveTouchStep * vpvsTheory);
            if (stepCount < expectedS * (1.0 - timeTolerance)) return false;

            bool strongEnough = sMag > threshold;
            bool distinctFromP = sMag > pMag * distinctFactor;
            bool pastPTail = stepCount > pWaveTouchStep + pTailSteps;

            if (strongEnough && distinctFromP && pastPTail)
            {
                double vpvsMeasured = (double)stepCount / pWaveTouchStep;
                if (vpvsMeasured < vpvsMin || vpvsMeasured > vpvsMax) return false;

                Logger.Log($"[CheckSWaveTouch] S-wave detected at step {stepCount}, "
                           + $"Vp/Vs={vpvsMeasured:F3}");
                return true;
            }

            return false;
        }
        private bool CheckPWaveReceiverTouch()
        {
            double pMag = 0;

            if (useFullFaceTransducers)
            {
                // Pick the main propagation axis
                int mainAxis = (Math.Abs(rx - tx) >= Math.Abs(ry - ty) &&
                                Math.Abs(rx - tx) >= Math.Abs(rz - tz)) ? 0
                              : (Math.Abs(ry - ty) >= Math.Abs(rx - tx) &&
                                 Math.Abs(ry - ty) >= Math.Abs(rz - tz)) ? 1 : 2;

                // SUBSAMPLE for performance - check every Nth voxel
                int sampleRate = 4; // Adjust this for speed vs accuracy tradeoff
                int count = 0;

                switch (mainAxis)
                {
                    case 0: // X axis - subsample YZ plane
                        for (int y = 0; y < height; y += sampleRate)
                            for (int z = 0; z < depth; z += sampleRate)
                                if (volumeLabels[rx, y, z] == selectedMaterialID)
                                {
                                    pMag += Math.Abs(vx[rx, y, z]);
                                    count++;
                                }
                        break;
                    case 1: // Y axis - subsample XZ plane
                        for (int x = 0; x < width; x += sampleRate)
                            for (int z = 0; z < depth; z += sampleRate)
                                if (volumeLabels[x, ry, z] == selectedMaterialID)
                                {
                                    pMag += Math.Abs(vy[x, ry, z]);
                                    count++;
                                }
                        break;
                    default: // Z axis - subsample XY plane
                        for (int x = 0; x < width; x += sampleRate)
                            for (int y = 0; y < height; y += sampleRate)
                                if (volumeLabels[x, y, rz] == selectedMaterialID)
                                {
                                    pMag += Math.Abs(vz[x, y, rz]);
                                    count++;
                                }
                        break;
                }
                if (count > 0) pMag /= count;
            }
            else
            {
                // Existing point detection
                int mainAxis = (Math.Abs(rx - tx) >= Math.Abs(ry - ty) &&
                                Math.Abs(rx - tx) >= Math.Abs(rz - tz)) ? 0
                              : (Math.Abs(ry - ty) >= Math.Abs(rx - tx) &&
                                 Math.Abs(ry - ty) >= Math.Abs(rz - tz)) ? 1 : 2;

                pMag = mainAxis == 0 ? Math.Abs(vx[rx, ry, rz])
                      : mainAxis == 1 ? Math.Abs(vy[rx, ry, rz])
                                      : Math.Abs(vz[rx, ry, rz]);
            }

            if (double.IsNaN(pMag) || double.IsInfinity(pMag)) return false;

            if (pMag > pWaveMaxAmplitude) pWaveMaxAmplitude = pMag;
            double threshold = Math.Max(1e-10, pWaveMaxAmplitude * 0.01);

            if (pMag > 1e-9) return true;
            return pMag > threshold;
        }
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
            if (percent > 99) percent = 99;  // Keep 100% for Finish()

            // Compute midpoint coordinates
            int midX = (tx + rx) / 2;
            int midY = (ty + ry) / 2;
            int midZ = (tz + rz) / 2;
            midX = Math.Max(0, Math.Min(midX, width - 1));
            midY = Math.Max(0, Math.Min(midY, height - 1));
            midZ = Math.Max(0, Math.Min(midZ, depth - 1));

            // Log midpoint measurements but only occasionally to avoid log spam
            if (stepCount % 20 == 0)
            {
                Logger.Log($"[MidpointMeasurement] Position: ({midX},{midY},{midZ})");
                Logger.Log($"[MidpointMeasurement] Values: P={vx[midX, midY, midZ]:E6}, S={vy[midX, midY, midZ]:E6}, Z={vz[midX, midY, midZ]:E6}");

                // Log TX and RX values
                Logger.Log($"[AcousticSimulator] TX values: P={vx[tx, ty, tz]:E6}, S={vy[tx, ty, tz]:E6}");
                Logger.Log($"[AcousticSimulator] RX values: P={vx[rx, ry, rz]:E6}, S={vy[rx, ry, rz]:E6}");

                // Log VP/VS ratio
                double pMag = Math.Abs(vx[rx, ry, rz]);
                double sMag = Math.Abs(vy[rx, ry, rz]);
                if (sMag > 1e-10)
                    Logger.Log($"[AcousticSimulator] VP/VS ratio: {pMag / sMag:F4}");
                else
                    Logger.Log($"[AcousticSimulator] VP/VS ratio: N/A (S-wave too small)");
            }

            // Convert velocity fields to float arrays for visualization (with amplification)
            float[,,] pWaveField = ConvertToFloat(vx, WAVE_VISUALIZATION_AMPLIFICATION);
            float[,,] sWaveField = ConvertToFloat(vy, WAVE_VISUALIZATION_AMPLIFICATION);

            // Ensure we're on the UI thread when raising events
            var handler = ProgressUpdated;
            if (handler != null)
            {
                var args = new AcousticSimulationProgressEventArgs(
                    percent, stepCount, text, pWaveField, sWaveField);

                var context = SynchronizationContext.Current;
                if (context != null)
                {
                    context.Post(_ => handler(this, args), null);
                }
                else
                {
                    handler(this, args);
                }
            }
        }
        private float[,,] ConvertToFloat(double[,,] src, double amplification = 1.0)
        {
            int w = src.GetLength(0), h = src.GetLength(1), d = src.GetLength(2);
            float[,,] dst = new float[w, h, d];
            for (int z = 0; z < d; z++)
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        dst[x, y, z] = (float)(src[x, y, z] * amplification);
            return dst;
        }
        private float[,] ComputeVelocityTomography(
    int tx, int ty, int tz,
    int rx, int ry, int rz,
    double[,,] vx, double[,,] vy, double[,,] vz)
        {
            // Determine the primary direction of wave propagation
            int dx = Math.Abs(rx - tx);
            int dy = Math.Abs(ry - ty);
            int dz = Math.Abs(rz - tz);

            float[,] tomography;

            if (dx >= dy && dx >= dz) // X is primary axis
            {
                // Use YZ plane at middle X
                int midX = (tx + rx) / 2;
                midX = Math.Max(0, Math.Min(midX, width - 1));

                tomography = new float[height, depth];

                for (int y = 0; y < height; y++)
                {
                    for (int z = 0; z < depth; z++)
                    {
                        double magnitude = Math.Sqrt(
                            vx[midX, y, z] * vx[midX, y, z] +
                            vy[midX, y, z] * vy[midX, y, z] +
                            vz[midX, y, z] * vz[midX, y, z]);

                        tomography[y, z] = (float)magnitude;
                    }
                }
            }
            else if (dy >= dx && dy >= dz) // Y is primary axis
            {
                // Use XZ plane at middle Y
                int midY = (ty + ry) / 2;
                midY = Math.Max(0, Math.Min(midY, height - 1));

                tomography = new float[width, depth];

                for (int x = 0; x < width; x++)
                {
                    for (int z = 0; z < depth; z++)
                    {
                        double magnitude = Math.Sqrt(
                            vx[x, midY, z] * vx[x, midY, z] +
                            vy[x, midY, z] * vy[x, midY, z] +
                            vz[x, midY, z] * vz[x, midY, z]);

                        tomography[x, z] = (float)magnitude;
                    }
                }
            }
            else // Z is primary axis
            {
                // Use XY plane at middle Z
                int midZ = (tz + rz) / 2;
                midZ = Math.Max(0, Math.Min(midZ, depth - 1));

                tomography = new float[width, height];

                for (int x = 0; x < width; x++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        double magnitude = Math.Sqrt(
                            vx[x, y, midZ] * vx[x, y, midZ] +
                            vy[x, y, midZ] * vy[x, y, midZ] +
                            vz[x, y, midZ] * vz[x, y, midZ]);

                        tomography[x, y] = (float)magnitude;
                    }
                }
            }

            return NormalizeFieldData(tomography);
        }

        

        private float[,] NormalizeFieldData(float[,] field)
        {
            int w = field.GetLength(0);
            int h = field.GetLength(1);

            // Find min/max values
            float minVal = float.MaxValue;
            float maxVal = float.MinValue;

            for (int i = 0; i < w; i++)
            {
                for (int j = 0; j < h; j++)
                {
                    float val = field[i, j];
                    if (!float.IsNaN(val) && !float.IsInfinity(val))
                    {
                        minVal = Math.Min(minVal, val);
                        maxVal = Math.Max(maxVal, val);
                    }
                }
            }

            if (maxVal <= minVal)
            {
                return field;
            }

            // Normalize to 0-1 range
            float[,] normalized = new float[w, h];
            float range = maxVal - minVal;

            for (int i = 0; i < w; i++)
            {
                for (int j = 0; j < h; j++)
                {
                    normalized[i, j] = (field[i, j] - minVal) / range;
                }
            }

            return normalized;
        }
        
        private float[,] ExtractCrossSection(
            int tx, int ty, int tz,
            int rx, int ry, int rz,
            double[,,] vx, double[,,] vy, double[,,] vz)
        {
            // Determine the primary direction of wave propagation
            int dx = Math.Abs(rx - tx);
            int dy = Math.Abs(ry - ty);
            int dz = Math.Abs(rz - tz);

            float[,] crossSection;

            if (dx >= dy && dx >= dz) // X is primary axis
            {
                // Take YZ plane perpendicular to X
                int midX = (tx + rx) / 2;
                midX = Math.Max(0, Math.Min(midX, width - 1));

                crossSection = new float[height, depth];

                for (int y = 0; y < height; y++)
                {
                    for (int z = 0; z < depth; z++)
                    {
                        double magnitude = Math.Sqrt(
                            vx[midX, y, z] * vx[midX, y, z] +
                            vy[midX, y, z] * vy[midX, y, z] +
                            vz[midX, y, z] * vz[midX, y, z]);

                        crossSection[y, z] = (float)magnitude;
                    }
                }
            }
            else if (dy >= dx && dy >= dz) // Y is primary axis
            {
                // Take XZ plane perpendicular to Y
                int midY = (ty + ry) / 2;
                midY = Math.Max(0, Math.Min(midY, height - 1));

                crossSection = new float[width, depth];

                for (int x = 0; x < width; x++)
                {
                    for (int z = 0; z < depth; z++)
                    {
                        double magnitude = Math.Sqrt(
                            vx[x, midY, z] * vx[x, midY, z] +
                            vy[x, midY, z] * vy[x, midY, z] +
                            vz[x, midY, z] * vz[x, midY, z]);

                        crossSection[x, z] = (float)magnitude;
                    }
                }
            }
            else // Z is primary axis
            {
                // Take XY plane perpendicular to Z
                int midZ = (tz + rz) / 2;
                midZ = Math.Max(0, Math.Min(midZ, depth - 1));

                crossSection = new float[width, height];

                for (int x = 0; x < width; x++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        double magnitude = Math.Sqrt(
                            vx[x, y, midZ] * vx[x, y, midZ] +
                            vy[x, y, midZ] * vy[x, y, midZ] +
                            vz[x, y, midZ] * vz[x, y, midZ]);

                        crossSection[x, y] = (float)magnitude;
                    }
                }
            }

            return NormalizeFieldData(crossSection);
        }

        
        private void SaveFrameToCache()
        {
            if (cacheManager == null)
                return;

            try
            {
                // Calculate tomography and cross-section
                var tomography = ComputeVelocityTomography(tx, ty, tz, rx, ry, rz, vx, vy, vz);
                var crossSection = ExtractCrossSection(tx, ty, tz, rx, ry, rz, vx, vy, vz);

                // Determine main axis for wave detection
                int mainAxis = Math.Abs(rx - tx) >= Math.Abs(ry - ty) && Math.Abs(rx - tx) >= Math.Abs(rz - tz) ? 0 :
                               Math.Abs(ry - ty) >= Math.Abs(rx - tx) && Math.Abs(ry - ty) >= Math.Abs(rz - tz) ? 1 : 2;

                // Get receiver values based on main axis
                float pWaveValue, sWaveValue;
                switch (mainAxis)
                {
                    case 0: // X-axis
                        pWaveValue = (float)(vx[rx, ry, rz] * WAVE_VISUALIZATION_AMPLIFICATION);
                        sWaveValue = (float)Math.Sqrt(vy[rx, ry, rz] * vy[rx, ry, rz] + vz[rx, ry, rz] * vz[rx, ry, rz]) * (float)WAVE_VISUALIZATION_AMPLIFICATION;
                        break;
                    case 1: // Y-axis
                        pWaveValue = (float)(vy[rx, ry, rz] * WAVE_VISUALIZATION_AMPLIFICATION);
                        sWaveValue = (float)Math.Sqrt(vx[rx, ry, rz] * vx[rx, ry, rz] + vz[rx, ry, rz] * vz[rx, ry, rz]) * (float)WAVE_VISUALIZATION_AMPLIFICATION;
                        break;
                    default: // Z-axis
                        pWaveValue = (float)(vz[rx, ry, rz] * WAVE_VISUALIZATION_AMPLIFICATION);
                        sWaveValue = (float)Math.Sqrt(vx[rx, ry, rz] * vx[rx, ry, rz] + vy[rx, ry, rz] * vy[rx, ry, rz]) * (float)WAVE_VISUALIZATION_AMPLIFICATION;
                        break;
                }

                // Calculate progress
                float pProgress = pWaveReceiverTouched ?
                    Math.Min(1.0f, (float)(stepCount - pWaveTouchStep) / Math.Max(1, sWaveTouchStep - pWaveTouchStep)) : 0;
                float sProgress = sWaveReceiverTouched ?
                    Math.Min(1.0f, (float)(stepCount - sWaveTouchStep) / Math.Max(1, totalTimeSteps)) : 0;

                // Convert to float arrays
                float[,,] vxFloat = ConvertToFloat(vx, WAVE_VISUALIZATION_AMPLIFICATION);
                float[,,] vyFloat = ConvertToFloat(vy, WAVE_VISUALIZATION_AMPLIFICATION);
                float[,,] vzFloat = ConvertToFloat(vz, WAVE_VISUALIZATION_AMPLIFICATION);

                // Build time series arrays
                float[] pTimeSeries = BuildTimeSeries(pWaveValue, true);
                float[] sTimeSeries = BuildTimeSeries(sWaveValue, false);

                // Save to cache
                cacheManager.SaveFrame(stepCount, vxFloat, vyFloat, vzFloat,
                    tomography, crossSection, pWaveValue, sWaveValue,
                    pProgress, sProgress, pTimeSeries, sTimeSeries);

                if (stepCount % 100 == 0)
                {
                    Logger.Log($"[AcousticSimulator] Cached frame {stepCount}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[AcousticSimulator] Error caching frame: {ex.Message}");
            }
        }
        public string GetCacheDirectory()
        {
            return cacheManager?.CacheDirectory;
        }
        private float[] BuildTimeSeries(float currentValue, bool isPWave)
        {
            const int maxSamples = 1000;

            if (isPWave)
            {
                pWaveHistory.Add(currentValue);
                if (pWaveHistory.Count > maxSamples)
                    pWaveHistory.RemoveAt(0);
                return pWaveHistory.ToArray();
            }
            else
            {
                sWaveHistory.Add(currentValue);
                if (sWaveHistory.Count > maxSamples)
                    sWaveHistory.RemoveAt(0);
                return sWaveHistory.ToArray();
            }
        }

        private void FinaliseAndRaiseEvent()
        {
            double dist = Math.Sqrt((tx - rx) * (tx - rx) +
                                   (ty - ry) * (ty - ry) +
                                   (tz - rz) * (tz - rz)) * pixelSize;

            // LOG CACHE DIRECTORY INFO
            if (cacheManager != null)
            {
                Logger.Log($"[AcousticSimulator] Simulation frames cached at: {cacheManager.CacheDirectory}");
                Logger.Log($"[AcousticSimulator] Total frames cached: {cacheManager.FrameCount}");
            }

            // If P-wave wasn't detected, use estimated values
            if (!pWaveReceiverTouched)
            {
                Logger.Log("[AcousticSimulator] WARNING: P-wave arrival wasn't detected, using estimates");
                double rhoAvg = densityVolume.Cast<float>().Average();
                double vp = Math.Sqrt((lambda0 + 2 * mu0) / rhoAvg);
                double vs = Math.Sqrt(mu0 / rhoAvg);

                var handler = SimulationCompleted;
                if (handler != null)
                {
                    var args = new AcousticSimulationCompleteEventArgs(
                        vp, vs, vp / vs, stepCount / 3, stepCount / 2, stepCount);

                    var context = SynchronizationContext.Current;
                    if (context != null)
                    {
                        context.Post(_ => handler(this, args), null);
                    }
                    else
                    {
                        handler(this, args);
                    }
                }
                return;
            }

            // If S-wave wasn't detected, use a theoretical vs/vp ratio
            if (!sWaveReceiverTouched)
            {
                Logger.Log("[AcousticSimulator] WARNING: S-wave arrival wasn't detected, using estimates");
                double vp = dist / (pWaveTouchStep * dt);
                double vs = vp * Math.Sqrt((1 - 2 * poissonRatio) / (2 - 2 * poissonRatio)); // Theoretical ratio

                var handler = SimulationCompleted;
                if (handler != null)
                {
                    var args = new AcousticSimulationCompleteEventArgs(
                        vp, vs, vp / vs, pWaveTouchStep,
                        (int)(pWaveTouchStep * (vp / vs)), stepCount);

                    var context = SynchronizationContext.Current;
                    if (context != null)
                    {
                        context.Post(_ => handler(this, args), null);
                    }
                    else
                    {
                        handler(this, args);
                    }
                }
                return;
            }

            // Both waves detected - compute actual velocities
            double pVelocity = dist / (pWaveTouchStep * dt);
            double sVelocity = dist / (sWaveTouchStep * dt);
            double vpVsRatio = pVelocity / sVelocity;

            Logger.Log($"[AcousticSimulator] Final results: P-velocity={pVelocity:F2} m/s, S-velocity={sVelocity:F2} m/s, Vp/Vs={vpVsRatio:F3}");
            Logger.Log($"[AcousticSimulator] Travel times: P-wave={pWaveTouchStep} steps, S-wave={sWaveTouchStep} steps, Total={stepCount} steps");

            var finalHandler = SimulationCompleted;
            if (finalHandler != null)
            {
                var finalArgs = new AcousticSimulationCompleteEventArgs(
                    pVelocity, sVelocity, vpVsRatio, pWaveTouchStep, sWaveTouchStep, stepCount);

                var finalContext = SynchronizationContext.Current;
                if (finalContext != null)
                {
                    finalContext.Post(_ => finalHandler(this, finalArgs), null);
                }
                else
                {
                    finalHandler(this, finalArgs);
                }
            }
        }
        public void SetCachePath(string path)
        {
            if (EnableFrameCaching && !string.IsNullOrWhiteSpace(path))
            {
                string cacheDir = path;
                Directory.CreateDirectory(cacheDir);
                if (cacheManager != null)
                {
                    cacheManager.Dispose();
                }
                cacheManager = new FrameCacheManager(cacheDir, width, height, depth);
                Logger.Log($"[AcousticSimulator] Cache path set to: {cacheDir}");
            }
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
