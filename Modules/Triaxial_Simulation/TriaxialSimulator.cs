using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace CTS
{
    public enum StressAxis
    {
        X,
        Y,
        Z
    }

    // Progress event arguments for triaxial simulation
    public class TriaxialSimulationProgressEventArgs : EventArgs
    {
        public int Percent { get; }
        public int Step { get; }
        public string Status { get; }
        public TriaxialSimulationProgressEventArgs(int percent, int step, string status)
        {
            Percent = percent;
            Step = step;
            Status = status;
        }
    }

    // Completion event arguments for triaxial simulation
    public class TriaxialSimulationCompleteEventArgs : EventArgs
    {
        public double[] AxialStrain { get; }
        public double[] AxialStress { get; }
        public double PeakStress { get; }
        public double PeakStrain { get; }
        public int TotalSteps { get; }
        public bool FailureDetected { get; }
        public int FailureStep { get; }

        public TriaxialSimulationCompleteEventArgs(double[] strain, double[] stress, bool failureDetected = false, int failureStep = -1)
        {
            AxialStrain = strain;
            AxialStress = stress;
            PeakStress = stress.Length > 0 ? stress.Max() : 0.0;
            int idx = stress.Length > 0 ? Array.IndexOf(stress, PeakStress) : -1;
            PeakStrain = (idx >= 0 && idx < strain.Length) ? strain[idx] : 0.0;
            TotalSteps = strain.Length > 0 ? strain.Length - 1 : 0;
            FailureDetected = failureDetected;
            FailureStep = failureStep;
        }
    }

    // Failure detection event arguments
    public class FailureDetectedEventArgs : EventArgs
    {
        public double CurrentStress { get; }
        public double CurrentStrain { get; }
        public int CurrentStep { get; }
        public int TotalSteps { get; }

        public FailureDetectedEventArgs(double stress, double strain, int step, int totalSteps)
        {
            CurrentStress = stress;
            CurrentStrain = strain;
            CurrentStep = step;
            TotalSteps = totalSteps;
        }
    }

    // CPU-based triaxial simulator class
    public class TriaxialSimulator : IDisposable
    {
        #region fields ─ user-supplied geometry & material
        private readonly System.Threading.CancellationTokenSource cts
    = new System.Threading.CancellationTokenSource();
        private readonly bool _debugMode;
        private readonly int width, height, depth;
        private readonly float pixelSize;                // m
        private readonly byte selectedMaterialID;

        private readonly ILabelVolumeData labels;         // same dims as ρ-volume
        private readonly float[,,] densityVolume;  // kg m-3

        private readonly double referenceDensity;         // ρ₀ (kg m-3)

        // Lamé for the reference density (Pa)
        private readonly double lambda0;
        private readonly double mu0;

        // strength parameters (MPa or deg, as entered in UI)
        private readonly double tensileStrengthMPa;
        private readonly double cohesionMPa;
        private readonly double frictionAngleDeg;

        // confining / axial pressures (MPa)
        private readonly double confiningPressureMPa;
        private readonly double initialAxialPressureMPa;
        private readonly double finalAxialPressureMPa;

        private readonly int pressureIncrements;
        private readonly int stepsPerIncrement;
        private readonly StressAxis primaryStressAxis;

        private readonly bool useElastic;
        private readonly bool usePlastic;
        private readonly bool useBrittle;

        #endregion

        #region fields ─ state arrays

        private readonly double[,,] vx, vy, vz;           // m s-1
        private readonly double[,,] sxx, syy, szz;        // Pa
        private readonly double[,,] sxy, sxz, syz;        // Pa
        private readonly double[,,] damage;               // 0-1
        private readonly double[,,] dispX, dispY, dispZ;  // m

        #endregion

        #region histories & events (unchanged)

        private readonly List<double> strainHistory = new List<double>();
        private readonly List<double> stressHistory = new List<double>();

        public event EventHandler<TriaxialSimulationProgressEventArgs> ProgressUpdated;
        public event EventHandler<TriaxialSimulationCompleteEventArgs> SimulationCompleted;
        public event EventHandler<FailureDetectedEventArgs> FailureDetected;

        private volatile bool simulationPaused = false;
        private volatile bool simulationCancelled = false;
        private double dt;
        private int currentStep = 0;
        private int totalSteps = 0;
        #endregion

        public TriaxialSimulator(
             int width, int height, int depth, float pixelSize,
             ILabelVolumeData labelVolume, float[,,] densityVolume,
             byte selectedMaterialID,
             double confPressureMPa, double initAxialMPa, double finalAxialMPa,
             int pressureIncrements, StressAxis axis,
             bool useElastic, bool usePlastic, bool useBrittle,
             double tensileStrengthMPa, double frictionAngleDeg, double cohesionMPa,
             double youngsModulusMPa, double poisson,
             int stepsPerIncrement = 200, bool debugMode = false)
        {
            // geometry & arrays -------------------------------------------------
            this.width = width;
            this.height = height;
            this.depth = depth;
            this.pixelSize = pixelSize;

            this.labels = labelVolume;
            this.densityVolume = densityVolume;
            this.selectedMaterialID = selectedMaterialID;

            // pressures / loading ----------------------------------------------
            this.confiningPressureMPa = confPressureMPa;
            this.initialAxialPressureMPa = initAxialMPa;
            this.finalAxialPressureMPa = finalAxialMPa;
            this.pressureIncrements = pressureIncrements;
            this.primaryStressAxis = axis;

            // model flags -------------------------------------------------------
            this.useElastic = useElastic;
            this.usePlastic = usePlastic;
            this.useBrittle = useBrittle;

            // strength & stiffness ---------------------------------------------
            this.tensileStrengthMPa = tensileStrengthMPa;
            this.frictionAngleDeg = frictionAngleDeg;
            this.cohesionMPa = cohesionMPa;

            double EPa = youngsModulusMPa * 1e6;
            this.mu0 = EPa / (2 * (1 + poisson));
            this.lambda0 = EPa * poisson / ((1 + poisson) * (1 - 2 * poisson));

            this.stepsPerIncrement = stepsPerIncrement;

            // ── compute reference density ρ₀ -----------------------------------
            referenceDensity = ComputeReferenceDensity();

            // allocate arrays ---------------------------------------------------
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

            dispX = new double[width, height, depth];
            dispY = new double[width, height, depth];
            dispZ = new double[width, height, depth];
            _debugMode = debugMode;
            Logger.Log($"[TriaxialSimulator] Debug mode: {debugMode}");
        }
        private double ComputeReferenceDensity()
        {
            double sum = 0.0;
            long n = 0;
            double minDensity = double.MaxValue;
            double maxDensity = double.MinValue;

            for (int z = 0; z < depth; z++)
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        if (labels[x, y, z] == selectedMaterialID &&
                            densityVolume[x, y, z] > 0)
                        {
                            double value = densityVolume[x, y, z];
                            sum += value;
                            n++;
                            minDensity = Math.Min(minDensity, value);
                            maxDensity = Math.Max(maxDensity, value);
                        }

            double refRho = (n > 0) ? sum / n : 2500.0;

            // Log detailed statistics to help with debugging
            Logger.Log($"[TriaxialSimulator] Density statistics: min={minDensity:F0}, max={maxDensity:F0}, " +
                       $"avg={refRho:F0} kg/m³, samples={n}");

            return refRho;
        }

        // Compute stable time step (CFL condition + safety factor)
        private void ComputeStableTimeStep()
        {
            double rhoMin = double.MaxValue;

            // Use parallel processing to find minimum density
            object lockObj = new object();
            Parallel.For(0, depth, z =>
            {
                double localMin = double.MaxValue;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (GetVoxelLabel(x, y, z) == selectedMaterialID)
                        {
                            float rho = densityVolume[x, y, z];
                            if (rho > 0 && rho < localMin) localMin = rho;
                        }
                    }
                }

                // Thread-safe update of global minimum
                if (localMin < double.MaxValue)
                {
                    lock (lockObj)
                    {
                        if (localMin < rhoMin) rhoMin = localMin;
                    }
                }
            });

            if (rhoMin == double.MaxValue || rhoMin <= 0.0) rhoMin = 100.0; // minimum realistic density

            // P-wave velocity (m/s) = sqrt((lambda + 2*mu)/rho)
            double vpMax = Math.Sqrt((lambda0 + 2 * mu0) / rhoMin);
            vpMax = Math.Min(vpMax, 6000.0);  // cap P-wave velocity to avoid tiny dt

            // CFL condition: dt < dx / vp * safety_factor
            double safety = 0.2;  // conservative safety factor
            dt = safety * pixelSize / vpMax;

            // Ensure dt is not too small for numerical precision
            if (dt < 1e-8) dt = 1e-8;
        }

        // Initialize fields: zero velocities and shear, apply confining pressure initial stress
        private void InitializeFields()
        {
            Array.Clear(vx, 0, vx.Length);
            Array.Clear(vy, 0, vy.Length);
            Array.Clear(vz, 0, vz.Length);
            Array.Clear(sxy, 0, sxy.Length);
            Array.Clear(sxz, 0, sxz.Length);
            Array.Clear(syz, 0, syz.Length);
            Array.Clear(damage, 0, damage.Length);
            Array.Clear(dispX, 0, dispX.Length);
            Array.Clear(dispY, 0, dispY.Length);
            Array.Clear(dispZ, 0, dispZ.Length);

            // Convert confining pressure from MPa to Pa (and compressive stress is negative)
            double confPa = confiningPressureMPa * 1e6;

            // Use parallel processing to initialize stress state
            Parallel.For(0, depth, z =>
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (GetVoxelLabel(x, y, z) == selectedMaterialID)
                        {
                            sxx[x, y, z] = -confPa;
                            syy[x, y, z] = -confPa;
                            szz[x, y, z] = -confPa;
                        }
                    }
                }
            });
        }

        // Update stress field (explicit integration using velocities)
        // -----------------------------------------------------------------------------
        //  UpdateStress – single explicit Euler step of the staggered-grid solver
        //  * elastic update
        //  * Mohr–Coulomb plastic projection (if enabled)
        //  * tensile-brittle damage (if enabled)
        // -----------------------------------------------------------------------------
        // -----------------------------------------------------------------------------
        private void UpdateStress()
        {
            double sinPhi = Math.Sin(frictionAngleDeg * Math.PI / 180.0);
            double cosPhi = Math.Cos(frictionAngleDeg * Math.PI / 180.0);

            double tensile0 = tensileStrengthMPa * 1e6;   // Pa
            double cohesion0 = cohesionMPa * 1e6;   // Pa

            double dx = pixelSize;
            double inv2 = 1.0 / (2 * dx);

            Parallel.For(1, depth - 1, z =>
            {
                for (int y = 1; y < height - 1; y++)
                {
                    for (int x = 1; x < width - 1; x++)
                    {
                        if (labels[x, y, z] != selectedMaterialID) continue;

                        //------------------------------------------------------------------
                        // density scaling factors
                        //------------------------------------------------------------------
                        double rho = Math.Max(100.0, densityVolume[x, y, z]);
                        double r = rho / referenceDensity;

                        double D = useBrittle ? damage[x, y, z] : 0.0;

                        double lambda = (1.0 - D) * lambda0 * r;
                        double mu = (1.0 - D) * mu0 * r;

                        //------------------------------------------------------------------
                        // velocity gradients   (central)
                        //------------------------------------------------------------------
                        double dvx_dx = (vx[x + 1, y, z] - vx[x - 1, y, z]) * inv2;
                        double dvy_dy = (vy[x, y + 1, z] - vy[x, y - 1, z]) * inv2;
                        double dvz_dz = (vz[x, y, z + 1] - vz[x, y, z - 1]) * inv2;

                        double dvx_dy = (vx[x, y + 1, z] - vx[x, y - 1, z]) * inv2;
                        double dvx_dz = (vx[x, y, z + 1] - vx[x, y, z - 1]) * inv2;
                        double dvy_dx = (vy[x + 1, y, z] - vy[x - 1, y, z]) * inv2;
                        double dvy_dz = (vy[x, y, z + 1] - vy[x, y, z - 1]) * inv2;
                        double dvz_dx = (vz[x + 1, y, z] - vz[x - 1, y, z]) * inv2;
                        double dvz_dy = (vz[x, y + 1, z] - vz[x, y - 1, z]) * inv2;

                        //------------------------------------------------------------------
                        // elastic predictor
                        //------------------------------------------------------------------
                        double sxxN = sxx[x, y, z] +
                                      dt * ((lambda + 2 * mu) * dvx_dx + lambda * (dvy_dy + dvz_dz));
                        double syyN = syy[x, y, z] +
                                      dt * ((lambda + 2 * mu) * dvy_dy + lambda * (dvx_dx + dvz_dz));
                        double szzN = szz[x, y, z] +
                                      dt * ((lambda + 2 * mu) * dvz_dz + lambda * (dvx_dx + dvy_dy));

                        double sxyN = sxy[x, y, z] + dt * (mu * (dvy_dx + dvx_dy));
                        double sxzN = sxz[x, y, z] + dt * (mu * (dvz_dx + dvx_dz));
                        double syzN = syz[x, y, z] + dt * (mu * (dvz_dy + dvy_dz));

                        //------------------------------------------------------------------
                        // Mohr–Coulomb
                        //------------------------------------------------------------------
                        if (usePlastic)
                        {
                            double mean = (sxxN + syyN + szzN) / 3.0;
                            double dxx = sxxN - mean;
                            double dyy = syyN - mean;
                            double dzz = szzN - mean;

                            double J2 = 0.5 * (dxx * dxx + dyy * dyy + dzz * dzz) +
                                        (sxyN * sxyN + sxzN * sxzN + syzN * syzN);
                            double tau = Math.Sqrt(Math.Max(J2, 0.0));

                            double p = -mean;

                            double cohesionVoxel = cohesion0 * r;

                            double yield = tau + p * sinPhi - cohesionVoxel * cosPhi;

                            if (yield > 0.0)
                            {
                                double fac = (tau > 1e-10)
                                   ? (tau - (cohesionVoxel * cosPhi - p * sinPhi)) / tau
                                   : 0.0;
                                fac = Clamp(fac, 0.0, 0.95);

                                dxx *= (1 - fac);
                                dyy *= (1 - fac);
                                dzz *= (1 - fac);
                                sxyN *= (1 - fac);
                                sxzN *= (1 - fac);
                                syzN *= (1 - fac);

                                sxxN = dxx + mean;
                                syyN = dyy + mean;
                                szzN = dzz + mean;
                            }
                        }

                        //------------------------------------------------------------------
                        // tensile damage
                        //------------------------------------------------------------------
                        if (useBrittle)
                        {
                            double sigMax = Math.Max(sxxN, Math.Max(syyN, szzN));

                            // Get voxel density and calculate ratio
                            double rho0 = densityVolume[x, y, z];
                            rho0 = Math.Max(100.0, rho0);  // Ensure minimum density
                            double r0 = rho0 / referenceDensity;

                            // Scale tensile strength by density ratio
                            double tensileVoxel = tensile0 * r0;
                            double D0 = damage[x, y, z];

                            if (sigMax > tensileVoxel && D0 < 0.99)
                            {
                                // Calculate overstress ratio
                                double over = (sigMax - tensileVoxel) / (tensileVoxel + 1.0);

                                double dD;
                                if (_debugMode)
                                {
                                    // Debug mode: MUCH faster damage accumulation
                                    // SCALE by density ratio r (stronger materials accumulate damage more slowly)
                                    dD = Clamp(over * 0.5 / r0, 0.01, 0.05);

                                    if (D0 > 0.10)
                                    {
                                        Logger.Log($"[TriaxialSimulator] DEBUG: Damage={D0:F2} at ({x},{y},{z}), " +
                                                 $"increment={dD:F3}, stress={sigMax / 1e6:F2}MPa, ρ={rho0:F0}kg/m³, r={r0:F2}");
                                    }
                                }
                                else
                                {
                                    // Normal mode: realistic damage accumulation
                                    // SCALE by density ratio r
                                    dD = Clamp(over * 0.05 / r0, 0.0, 0.005);

                                    // When close to failure threshold, boost damage to help cross threshold
                                    if (D0 > 0.65 && D < 0.75)
                                    {
                                        dD *= 1.5;
                                    }

                                    if (D0 > 0.5 && dD > 0.001)
                                    {
                                        Logger.Log($"[TriaxialSimulator] Significant damage: D={D0:F3}, dD={dD:F4}, " +
                                                 $"sigMax={sigMax / 1e6:F2}MPa, tensile={tensileVoxel / 1e6:F2}MPa, r={r0:F2}");
                                    }
                                }

                                D0 = Math.Min(0.99, D0 + dD);
                                damage[x, y, z] = D0;

                                // The softening also depends on material density
                                double soften = 1 - (dD * r0);  // Denser materials have more energy to release
                                sxxN *= soften; syyN *= soften; szzN *= soften;
                                sxyN *= soften; sxzN *= soften; syzN *= soften;
                            }

                            //------------------------------------------------------------------
                            // store
                            //------------------------------------------------------------------
                            sxx[x, y, z] = sxxN;
                        syy[x, y, z] = syyN;
                        szz[x, y, z] = szzN;
                        sxy[x, y, z] = sxyN;
                        sxz[x, y, z] = sxzN;
                        syz[x, y, z] = syzN;
                    }
                }
                }
            });
        
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
        // Update velocity field (explicit integration using stress divergence + damping)
        private void UpdateVelocity()
        {
            const double DAMPING = 0.05; // 5% per step

            Parallel.For(1, depth - 1, z =>
            {
                for (int y = 1; y < height - 1; y++)
                {
                    for (int x = 1; x < width - 1; x++)
                    {
                        if (GetVoxelLabel(x, y, z) != selectedMaterialID) continue;

                        double rho = Math.Max(100.0, densityVolume[x, y, z]);

                        // Stress gradients (backward differences for stability)
                        double dsxx_dx = (sxx[x, y, z] - sxx[x - 1, y, z]) / pixelSize;
                        double dsyy_dy = (syy[x, y, z] - syy[x, y - 1, z]) / pixelSize;
                        double dszz_dz = (szz[x, y, z] - szz[x, y, z - 1]) / pixelSize;
                        double dsxy_dx = (sxy[x + 1, y, z] - sxy[x, y, z]) / pixelSize;
                        double dsxy_dy = (sxy[x, y, z] - sxy[x, y - 1, z]) / pixelSize;
                        double dsxz_dx = (sxz[x + 1, y, z] - sxz[x, y, z]) / pixelSize;
                        double dsxz_dz = (sxz[x, y, z] - sxz[x, y, z - 1]) / pixelSize;
                        double dsyz_dy = (syz[x, y + 1, z] - syz[x, y, z]) / pixelSize;
                        double dsyz_dz = (syz[x, y, z] - syz[x, y, z - 1]) / pixelSize;

                        // Clamp extreme gradients
                        const double MAX_GRAD = 1e12;
                        dsxx_dx = Math.Max(-MAX_GRAD, Math.Min(MAX_GRAD, dsxx_dx));
                        dsyy_dy = Math.Max(-MAX_GRAD, Math.Min(MAX_GRAD, dsyy_dy));
                        dszz_dz = Math.Max(-MAX_GRAD, Math.Min(MAX_GRAD, dszz_dz));
                        dsxy_dx = Math.Max(-MAX_GRAD, Math.Min(MAX_GRAD, dsxy_dx));
                        dsxy_dy = Math.Max(-MAX_GRAD, Math.Min(MAX_GRAD, dsxy_dy));
                        dsxz_dx = Math.Max(-MAX_GRAD, Math.Min(MAX_GRAD, dsxz_dx));
                        dsxz_dz = Math.Max(-MAX_GRAD, Math.Min(MAX_GRAD, dsxz_dz));
                        dsyz_dy = Math.Max(-MAX_GRAD, Math.Min(MAX_GRAD, dsyz_dy));
                        dsyz_dz = Math.Max(-MAX_GRAD, Math.Min(MAX_GRAD, dsyz_dz));

                        // Velocity increments (Newton's second law)
                        double dvx = dt * (dsxx_dx + dsxy_dy + dsxz_dz) / rho;
                        double dvy = dt * (dsxy_dx + dsyy_dy + dsyz_dz) / rho;
                        double dvz = dt * (dsxz_dx + dsyz_dy + dszz_dz) / rho;

                        // Update velocities with damping
                        double damping = 1.0 - DAMPING;
                        vx[x, y, z] = vx[x, y, z] * damping + dvx;
                        vy[x, y, z] = vy[x, y, z] * damping + dvy;
                        vz[x, y, z] = vz[x, y, z] * damping + dvz;

                        // Integrate displacements for strain measurement
                        dispX[x, y, z] += vx[x, y, z] * dt;
                        dispY[x, y, z] += vy[x, y, z] * dt;
                        dispZ[x, y, z] += vz[x, y, z] * dt;
                    }
                }
            });
        }

        // -----------------------------------------------------------------------------
        //  CheckForFailure – returns true if any voxel’s damage exceeds its
        //  density-dependent threshold  Dcrit = 0.75 × ρ/ρ₀
        // -----------------------------------------------------------------------------
        private bool CheckForFailure()
        {
            // Use lower threshold in debug mode
            double baseCrit = _debugMode ? 0.15 : 0.75;

            double maxDamage = 0.0;  // Track max damage for logging
            bool failureFound = false;
            int materialVoxels = 0;
            double avgDensity = 0.0;
            double maxRatio = 0.0;
            int maxDamageX = -1, maxDamageY = -1, maxDamageZ = -1;

            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (GetVoxelLabel(x, y, z) != selectedMaterialID) continue;

                        materialVoxels++;
                        double rho = Math.Max(100.0, densityVolume[x, y, z]);
                        avgDensity += rho;
                        double crit = baseCrit * (rho / referenceDensity);
                        double damageVal = damage[x, y, z];
                        double ratio = damageVal / crit;

                        if (damageVal > maxDamage)
                        {
                            maxDamage = damageVal;
                            maxDamageX = x;
                            maxDamageY = y;
                            maxDamageZ = z;
                        }

                        if (ratio > maxRatio)
                        {
                            maxRatio = ratio;
                        }

                        if (damageVal > crit)
                        {
                            failureFound = true;
                            Logger.Log($"[TriaxialSimulator] Failure detected at ({x},{y},{z}). " +
                                      $"Damage={damageVal:F3}, Density={rho:F0}, Threshold={crit:F3}, Ratio={ratio:F3}");
                        }
                    }
                }
            }

            if (materialVoxels > 0)
            {
                avgDensity /= materialVoxels;
            }

            string modeLabel = _debugMode ? "DEBUG MODE" : "Normal mode";
            Logger.Log($"[TriaxialSimulator] {modeLabel} Failure check: max damage={maxDamage:F3} at ({maxDamageX},{maxDamageY},{maxDamageZ}), " +
                      $"max ratio={maxRatio:F3}, material voxels={materialVoxels}, " +
                      $"avg density={avgDensity:F0} kg/m³, ref density={referenceDensity:F0} kg/m³, " +
                      $"failure detected={failureFound}");

            return failureFound;
        }

        // Start simulation asynchronously
        public Task StartSimulationAsync()
        {
            return Task.Run(() => Run(cts.Token));
        }

        // Cancel the simulation run
        public void CancelSimulation()
        {
            simulationCancelled = true;
            cts.Cancel();
        }

        // Pause/resume simulation
        public void PauseSimulation()
        {
            simulationPaused = true;
        }

        public void ResumeSimulation()
        {
            simulationPaused = false;
        }

        // Continue simulation after failure detected
        public void ContinueAfterFailure()
        {
            lock (this)
            {
                simulationPaused = false;
            }
        }

        // Get a voxel label safely - handles ILabelVolumeData interface
        private byte GetVoxelLabel(int x, int y, int z)
        {
            if (x < 0 || x >= width || y < 0 || y >= height || z < 0 || z >= depth)
                return 0;

            try
            {
                return labels[x, y, z];
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // CPU version – full Run method with lateral BC, radial measurements, and pause/cancel
        private void Run(CancellationToken token)
        {
            if (useBrittle)
                Logger.Log($"[TriaxialSimulator] Brittle model enabled with tensile strength={tensileStrengthMPa}MPa");
            else
                Logger.Log("[TriaxialSimulator] WARNING: Brittle model is disabled - failure detection may not work!");

            ComputeStableTimeStep();
            InitializeFields();

            // 1. Find specimen bounds on all three axes
            int minX = width, maxX = 0,
                minY = height, maxY = 0,
                minZ = depth, maxZ = 0;
            for (int z = 0; z < depth; z++)
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        if (GetVoxelLabel(x, y, z) == selectedMaterialID)
                        {
                            minX = Math.Min(minX, x);
                            maxX = Math.Max(maxX, x);
                            minY = Math.Min(minY, y);
                            maxY = Math.Max(maxY, y);
                            minZ = Math.Min(minZ, z);
                            maxZ = Math.Max(maxZ, z);
                        }
            if (minX > maxX)
            {
                ProgressUpdated?.Invoke(this, new TriaxialSimulationProgressEventArgs(0, 0, "No specimen found"));
                return;
            }

            // 2. Identify primary (axial) bounds and lateral bounds
            int minPos, maxPos, minLat1, maxLat1, minLat2, maxLat2;
            switch (primaryStressAxis)
            {
                case StressAxis.X:
                    minPos = minX; maxPos = maxX;
                    minLat1 = minY; maxLat1 = maxY;
                    minLat2 = minZ; maxLat2 = maxZ;
                    break;
                case StressAxis.Y:
                    minPos = minY; maxPos = maxY;
                    minLat1 = minX; maxLat1 = maxX;
                    minLat2 = minZ; maxLat2 = maxZ;
                    break;
                default: // Z
                    minPos = minZ; maxPos = maxZ;
                    minLat1 = minX; maxLat1 = maxX;
                    minLat2 = minY; maxLat2 = maxY;
                    break;
            }

            // 3. Compute initial sample sizes
            double axialSize = (maxPos - minPos + 1) * pixelSize;
            double radialSize1Init = (maxLat1 - minLat1 + 1) * pixelSize;
            double radialSize2Init = (maxLat2 - minLat2 + 1) * pixelSize;

            // 4. Histories
            strainHistory.Clear();
            stressHistory.Clear();
            strainHistory.Add(0.0);
            stressHistory.Add(initialAxialPressureMPa);

            // 5. Increment loop
            double dP = (finalAxialPressureMPa - initialAxialPressureMPa) / pressureIncrements;
            bool failureDetected = false;
            int failureStep = -1;

            for (int inc = 1; inc <= pressureIncrements; inc++)
            {
                // 5.1 handle pause/cancel before starting increment
                if (simulationCancelled || token.IsCancellationRequested) break;
                while (simulationPaused && !simulationCancelled && !token.IsCancellationRequested)
                    Thread.Sleep(100);
                if (simulationCancelled || token.IsCancellationRequested) break;

                double targetMPa = initialAxialPressureMPa + dP * inc;
                double targetPa = targetMPa * 1e6;
                double confPa = confiningPressureMPa * 1e6;

                // 5.2 Apply axial & lateral BC on all faces
                //  – Axial faces:
                switch (primaryStressAxis)
                {
                    case StressAxis.X:
                        Parallel.For(0, depth, z =>
                        {
                            for (int y = 0; y < height; y++)
                            {
                                if (GetVoxelLabel(minPos, y, z) == selectedMaterialID) sxx[minPos, y, z] = -targetPa;
                                if (GetVoxelLabel(maxPos, y, z) == selectedMaterialID) sxx[maxPos, y, z] = -targetPa;
                            }
                        });
                        break;
                    case StressAxis.Y:
                        Parallel.For(0, depth, z =>
                        {
                            for (int x = 0; x < width; x++)
                            {
                                if (GetVoxelLabel(x, minPos, z) == selectedMaterialID) syy[x, minPos, z] = -targetPa;
                                if (GetVoxelLabel(x, maxPos, z) == selectedMaterialID) syy[x, maxPos, z] = -targetPa;
                            }
                        });
                        break;
                    default:
                        Parallel.For(0, height, y =>
                        {
                            for (int x = 0; x < width; x++)
                            {
                                if (GetVoxelLabel(x, y, minPos) == selectedMaterialID) szz[x, y, minPos] = -targetPa;
                                if (GetVoxelLabel(x, y, maxPos) == selectedMaterialID) szz[x, y, maxPos] = -targetPa;
                            }
                        });
                        break;
                }
                //  – Lateral faces (axis1):
                switch (primaryStressAxis)
                {
                    case StressAxis.X:
                        Parallel.For(0, depth, z =>
                        {
                            for (int x = minX; x <= maxX; x++)
                            {
                                if (GetVoxelLabel(x, minLat1, z) == selectedMaterialID) syy[x, minLat1, z] = -confPa;
                                if (GetVoxelLabel(x, maxLat1, z) == selectedMaterialID) syy[x, maxLat1, z] = -confPa;
                            }
                        });
                        break;
                    case StressAxis.Y:
                        Parallel.For(0, depth, z =>
                        {
                            for (int y = minY; y <= maxY; y++)
                            {
                                if (GetVoxelLabel(minLat1, y, z) == selectedMaterialID) sxx[minLat1, y, z] = -confPa;
                                if (GetVoxelLabel(maxLat1, y, z) == selectedMaterialID) sxx[maxLat1, y, z] = -confPa;
                            }
                        });
                        break;
                    default:
                        Parallel.For(0, height, y =>
                        {
                            for (int z = minZ; z <= maxZ; z++)
                            {
                                if (GetVoxelLabel(minLat1, y, z) == selectedMaterialID) sxx[minLat1, y, z] = -confPa;
                                if (GetVoxelLabel(maxLat1, y, z) == selectedMaterialID) sxx[maxLat1, y, z] = -confPa;
                            }
                        });
                        break;
                }
                //  – Lateral faces (axis2):
                switch (primaryStressAxis)
                {
                    case StressAxis.X:
                        Parallel.For(0, height, y =>
                        {
                            for (int x = minX; x <= maxX; x++)
                            {
                                if (GetVoxelLabel(x, y, minLat2) == selectedMaterialID) szz[x, y, minLat2] = -confPa;
                                if (GetVoxelLabel(x, y, maxLat2) == selectedMaterialID) szz[x, y, maxLat2] = -confPa;
                            }
                        });
                        break;
                    case StressAxis.Y:
                        Parallel.For(0, height, y =>
                        {
                            for (int x = minX; x <= maxX; x++)
                            {
                                if (GetVoxelLabel(x, y, minLat2) == selectedMaterialID) szz[x, y, minLat2] = -confPa;
                                if (GetVoxelLabel(x, y, maxLat2) == selectedMaterialID) szz[x, y, maxLat2] = -confPa;
                            }
                        });
                        break;
                    default:
                        Parallel.For(0, depth, z =>
                        {
                            for (int x = minX; x <= maxX; x++)
                            {
                                if (GetVoxelLabel(x, minLat2, z) == selectedMaterialID) syy[x, minLat2, z] = -confPa;
                                if (GetVoxelLabel(x, maxLat2, z) == selectedMaterialID) syy[x, maxLat2, z] = -confPa;
                            }
                        });
                        break;
                }

                // 5.3 Time-integration with pause/cancel and intermediate data recording
                int recordInterval = Math.Max(1, stepsPerIncrement / 10); // Record ~10 points per increment

                for (int step = 0; step < stepsPerIncrement; step++)
                {
                    if (simulationCancelled || token.IsCancellationRequested) break;
                    while (simulationPaused && !simulationCancelled && !token.IsCancellationRequested)
                        Thread.Sleep(100);

                    UpdateStress();
                    UpdateVelocity();

                    // Record intermediate data points for smoother curve
                    if (step % recordInterval == 0 && step > 0 && step < stepsPerIncrement - 1)
                    {
                        double axialStrain = CalculateAverageStrain(primaryStressAxis, minPos, maxPos, axialSize);
                        double stressMPa = MeasureBoundaryStress(primaryStressAxis, minPos, maxPos);

                        // Add intermediate data point
                        strainHistory.Add(axialStrain);
                        stressHistory.Add(stressMPa);

                        // Log progress
                        int percentComplete = (int)((double)(inc - 1) / pressureIncrements * 100.0) +
                                              (int)((double)step / stepsPerIncrement * (100.0 / pressureIncrements));
                        ProgressUpdated?.Invoke(this, new TriaxialSimulationProgressEventArgs(
                            percentComplete, inc, $"Loading: {targetMPa:F2} MPa, Step {step + 1}/{stepsPerIncrement}"));
                    }

                    // Check for failure more frequently in debug mode
                    int checkInterval = _debugMode ? 2 : 5;
                    if (step % checkInterval == 0 && !failureDetected)
                    {
                        if (CheckForFailure())
                        {
                            failureDetected = true;
                            failureStep = inc;

                            // Record strain and stress at failure
                            double axialStrain = CalculateAverageStrain(primaryStressAxis, minPos, maxPos, axialSize);
                            double stressMPa = MeasureBoundaryStress(primaryStressAxis, minPos, maxPos);

                            FailureDetected?.Invoke(this, new FailureDetectedEventArgs(
                                stressMPa, axialStrain, inc, pressureIncrements));

                            // Pause simulation to let user decide whether to continue
                            simulationPaused = true;
                        }
                    }
                }
                if (simulationCancelled || token.IsCancellationRequested) break;

                // 5.4 Measure final strains and stresses for this increment
                double finalAxialStrain = CalculateAverageStrain(primaryStressAxis, minPos, maxPos, axialSize);
                double radialStrain1 = CalculateAverageStrain(
                    primaryStressAxis == StressAxis.X ? StressAxis.Y :
                    primaryStressAxis == StressAxis.Y ? StressAxis.X : StressAxis.X,
                    minLat1, maxLat1, radialSize1Init);
                double radialStrain2 = CalculateAverageStrain(
                    primaryStressAxis == StressAxis.Z ? StressAxis.Y : StressAxis.Z,
                    minLat2, maxLat2, radialSize2Init);

                // 5.5 Measure boundary stresses at end of increment
                double finalStressMPa = MeasureBoundaryStress(primaryStressAxis, minPos, maxPos, targetMPa);
                double radialStress1MPa = MeasureBoundaryStress(
                    primaryStressAxis == StressAxis.X ? StressAxis.Y :
                    primaryStressAxis == StressAxis.Y ? StressAxis.X : StressAxis.X,
                    minLat1, maxLat1, confiningPressureMPa);
                double radialStress2MPa = MeasureBoundaryStress(
                    primaryStressAxis == StressAxis.Z ? StressAxis.Y : StressAxis.Z,
                    minLat2, maxLat2, confiningPressureMPa);

                // 5.6 Record final point for this increment
                strainHistory.Add(finalAxialStrain);
                stressHistory.Add(finalStressMPa);
                Logger.Log($"Step {inc}: axial ε={finalAxialStrain:F4}, σ={finalStressMPa:F2}MPa; " +
                           $"radial ε₁={radialStrain1:F4}, σ₁={radialStress1MPa:F2}MPa; " +
                           $"ε₂={radialStrain2:F4}, σ₂={radialStress2MPa:F2}MPa");

                // Report progress
                int pctComplete = (int)((double)inc / pressureIncrements * 100.0);
                ProgressUpdated?.Invoke(this, new TriaxialSimulationProgressEventArgs(
                    pctComplete, inc, $"Loading: {finalStressMPa:F2} MPa (complete)"));
            }

            // 6. Finish and report completion with failure info
            var resultArgs = new TriaxialSimulationCompleteEventArgs(
                strainHistory.ToArray(),
                stressHistory.ToArray(),
                failureDetected: failureDetected,
                failureStep: failureStep);
            SimulationCompleted?.Invoke(this, resultArgs);

            // 7. Export results if requested
            ExportResultsAndImages();
        }


        private double MeasureBoundaryStress(StressAxis axis, int minPos, int maxPos, double targetPressureMPa = 0)
        {
            double sigSum = 0;
            int sigN = 0;

            switch (axis)
            {
                case StressAxis.X:
                    for (int z = 0; z < depth; z++)
                        for (int y = 0; y < height; y++)
                        {
                            if (GetVoxelLabel(minPos, y, z) == selectedMaterialID)
                            {
                                sigSum += -sxx[minPos, y, z];
                                sigN++;
                            }
                            if (GetVoxelLabel(maxPos, y, z) == selectedMaterialID)
                            {
                                sigSum += -sxx[maxPos, y, z];
                                sigN++;
                            }
                        }
                    break;

                case StressAxis.Y:
                    for (int z = 0; z < depth; z++)
                        for (int x = 0; x < width; x++)
                        {
                            if (GetVoxelLabel(x, minPos, z) == selectedMaterialID)
                            {
                                sigSum += -syy[x, minPos, z];
                                sigN++;
                            }
                            if (GetVoxelLabel(x, maxPos, z) == selectedMaterialID)
                            {
                                sigSum += -syy[x, maxPos, z];
                                sigN++;
                            }
                        }
                    break;

                default: // Z
                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                        {
                            if (GetVoxelLabel(x, y, minPos) == selectedMaterialID)
                            {
                                sigSum += -szz[x, y, minPos];
                                sigN++;
                            }
                            if (GetVoxelLabel(x, y, maxPos) == selectedMaterialID)
                            {
                                sigSum += -szz[x, y, maxPos];
                                sigN++;
                            }
                        }
                    break;
            }

            // If no boundary measurements, use target pressure
            double stressMPa;
            if (sigN == 0)
            {
                // Use the provided target pressure instead of calculating it
                stressMPa = (axis == primaryStressAxis) ?
                    targetPressureMPa :
                    confiningPressureMPa;

                Logger.Log($"[TriaxialSimulator] No boundary measurements available for {axis}, using programmed pressure: {stressMPa:F2} MPa");
            }
            else
            {
                stressMPa = (sigSum / sigN) / 1e6;   // Pa → MPa
                Logger.Log($"[TriaxialSimulator] Measured {axis} stress at {sigN} boundary points: {stressMPa:F2} MPa");
            }

            return stressMPa;
        }
        // -----------------------------------------------------------------------------
        //  CalculateAverageStrain – engineering strain along the primary axis
        //  ε = (Δu_max − Δu_min) / L₀   (compression > 0)
        // -----------------------------------------------------------------------------
        private double CalculateAverageStrain(StressAxis axis, int minPos, int maxPos, double originalSize)
        {
            // find the smallest and largest displacement of any material voxel
            double dispMin = double.MaxValue;
            double dispMax = double.MinValue;

            for (int z = 0; z < depth; z++)
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        if (GetVoxelLabel(x, y, z) != selectedMaterialID) continue;

                        double d = 0;
                        switch (axis)
                        {
                            case StressAxis.X: d = dispX[x, y, z]; break;
                            case StressAxis.Y: d = dispY[x, y, z]; break;
                            default: d = dispZ[x, y, z]; break;
                        }

                        if (d < dispMin) dispMin = d;
                        if (d > dispMax) dispMax = d;
                    }

            // if no voxels moved (or none found), treat as zero deformation
            if (dispMin == double.MaxValue || dispMax == double.MinValue)
            {
                dispMin = dispMax = 0.0;
            }

            // compression positive: ε = -(Δu_max − Δu_min) / L₀
            return -(dispMax - dispMin) / originalSize;
        }

        // Export stress-strain data to CSV/XLS and generate images
        private void ExportResultsAndImages()
        {
            try
            {
                // Prepare CSV content for stress-strain data
                string csvContent = "Step,AxialStrain,AxialStress(MPa)\r\n";
                for (int i = 0; i < strainHistory.Count; i++)
                {
                    csvContent += $"{i},{strainHistory[i]},{stressHistory[i]}\r\n";
                }

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string csvPath = $"TriaxialResults_{timestamp}.csv";
                File.WriteAllText(csvPath, csvContent);

                // Save as .xls (Excel-compatible, using same CSV content)
                string xlsPath = $"TriaxialResults_{timestamp}.xls";
                File.WriteAllText(xlsPath, csvContent);

                // Generate stress-strain plot image (PNG)
                int imgW = 800, imgH = 600;
                using (Bitmap bmp = new Bitmap(imgW, imgH))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.White);
                        Pen axisPen = Pens.Black;

                        // Axes: leave margins of 50px
                        g.DrawLine(axisPen, 50, imgH - 50, 50, 50);
                        g.DrawLine(axisPen, 50, imgH - 50, imgW - 50, imgH - 50);

                        Font font = new Font("Arial", 10);

                        // Labels
                        g.DrawString("Axial Strain", font, Brushes.Black, imgW / 2 - 40, imgH - 40);
                        g.DrawString("Axial Stress (MPa)", font, Brushes.Black, 5, 60);

                        // Determine plot scale
                        double maxStrain = strainHistory.Count > 0 ? strainHistory.Max() : 1.0;
                        double maxStress = stressHistory.Count > 0 ? stressHistory.Max() : 1.0;

                        if (maxStrain <= 0) maxStrain = 1.0;
                        if (maxStress <= 0) maxStress = 1.0;

                        // Draw tick marks and labels
                        int ticks = 5;
                        for (int t = 0; t <= ticks; t++)
                        {
                            // Vertical stress ticks (Y axis)
                            double sVal = maxStress * t / ticks;
                            int yPix = (int)((imgH - 100) * (1 - (sVal / maxStress)) + 50);
                            g.DrawLine(Pens.Gray, 45, yPix, 50, yPix);
                            g.DrawString($"{sVal:F1}", font, Brushes.Black, 5, yPix - 8);

                            // Horizontal strain ticks (X axis)
                            double eVal = maxStrain * t / ticks;
                            int xPix = (int)((imgW - 100) * (eVal / maxStrain) + 50);
                            g.DrawLine(Pens.Gray, xPix, imgH - 45, xPix, imgH - 50);
                            g.DrawString($"{eVal:F3}", font, Brushes.Black, xPix - 15, imgH - 40);
                        }

                        // Plot stress-strain curve
                        if (strainHistory.Count > 1)
                        {
                            PointF[] curve = new PointF[strainHistory.Count];
                            for (int i = 0; i < strainHistory.Count; i++)
                            {
                                float x = 50 + (float)((imgW - 100) * (strainHistory[i] / maxStrain));
                                float y = (float)((imgH - 50) - (imgH - 100) * (stressHistory[i] / maxStress));
                                curve[i] = new PointF(x, y);
                            }

                            g.DrawLines(Pens.Red, curve);

                            // Mark peak point
                            double peakStress = stressHistory.Max();
                            int peakIdx = Array.IndexOf(stressHistory.ToArray(), peakStress);

                            if (peakIdx >= 0 && peakIdx < curve.Length)
                            {
                                PointF peakPt = curve[peakIdx];
                                g.FillEllipse(Brushes.Blue, peakPt.X - 4, peakPt.Y - 4, 8, 8);
                                string peakLabel = $"Peak: {peakStress:F1} MPa at {strainHistory[peakIdx]:F3} strain";
                                g.DrawString(peakLabel, font, Brushes.Blue, peakPt.X + 10, peakPt.Y);
                            }
                        }
                    }

                    string imgPath = $"TriaxialResults_{timestamp}.png";
                    bmp.Save(imgPath, ImageFormat.Png);
                }

                // Create damage visualization
                ExportDamageVisualization(timestamp);

                // Export fracture mesh
                ExportFractureMesh(timestamp);
            }
            catch (Exception ex)
            {
                // Log error but don't disrupt simulation completion
                Logger.Log("Error exporting results: {ex.Message}");
            }
        }

        // Export 3D model of fracture surfaces
        private void ExportFractureMesh(string timestamp)
        {
            try
            {
                // Create a label array with cracks (damaged voxels) treated as exterior
                byte[,,] crackLabels = new byte[width, height, depth];

                // Fill the array with material labels except for highly damaged regions
                Parallel.For(0, depth, z =>
                {
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            byte label = GetVoxelLabel(x, y, z);

                            // If voxel is part of selected material but has high damage, mark as exterior (0)
                            if (label == selectedMaterialID && damage[x, y, z] >= 0.7)
                            {
                                crackLabels[x, y, z] = 0;
                            }
                            else
                            {
                                crackLabels[x, y, z] = label;
                            }
                        }
                    }
                });

                // Create a LabelVolumeDataArray from the crack labels
                var labelVolumeData = new LabelVolumeDataArray(width, height, depth);

                // Copy data to the volume
                Parallel.For(0, depth, z =>
                {
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            labelVolumeData[x, y, z] = crackLabels[x, y, z];
                        }
                    }
                });

                // Generate mesh from fractures using MeshGenerator
                MeshGenerator meshGen = new MeshGenerator(labelVolumeData, selectedMaterialID,
                                                         width, height, depth, targetFacets: 100000, progress: null);

                MeshGenerator.Mesh fractureMesh = meshGen.GenerateMeshAsync(CancellationToken.None).Result;

                // Export STL (ASCII)
                string stlPath = $"TriaxialFractures_{timestamp}.stl";
                using (StreamWriter sw = new StreamWriter(stlPath))
                {
                    sw.WriteLine("solid Fractures");
                    for (int i = 0; i < fractureMesh.Indices.Count; i += 3)
                    {
                        int i1 = fractureMesh.Indices[i];
                        int i2 = fractureMesh.Indices[i + 1];
                        int i3 = fractureMesh.Indices[i + 2];

                        Vector3 v1 = fractureMesh.Vertices[i1];
                        Vector3 v2 = fractureMesh.Vertices[i2];
                        Vector3 v3 = fractureMesh.Vertices[i3];
                        Vector3 normal = fractureMesh.Normals[i / 3];

                        sw.WriteLine($"facet normal {normal.X} {normal.Y} {normal.Z}");
                        sw.WriteLine(" outer loop");
                        sw.WriteLine($"  vertex {v1.X} {v1.Y} {v1.Z}");
                        sw.WriteLine($"  vertex {v2.X} {v2.Y} {v2.Z}");
                        sw.WriteLine($"  vertex {v3.X} {v3.Y} {v3.Z}");
                        sw.WriteLine(" endloop");
                        sw.WriteLine("endfacet");
                    }
                    sw.WriteLine("endsolid Fractures");
                }

                // Export OBJ (with normals)
                string objPath = $"TriaxialFractures_{timestamp}.obj";
                using (StreamWriter sw = new StreamWriter(objPath))
                {
                    foreach (var v in fractureMesh.Vertices)
                        sw.WriteLine($"v {v.X} {v.Y} {v.Z}");

                    foreach (var n in fractureMesh.Normals)
                        sw.WriteLine($"vn {n.X} {n.Y} {n.Z}");

                    for (int i = 0; i < fractureMesh.Indices.Count; i += 3)
                    {
                        int v1 = fractureMesh.Indices[i] + 1;
                        int v2 = fractureMesh.Indices[i + 1] + 1;
                        int v3 = fractureMesh.Indices[i + 2] + 1;
                        int ni = i / 3 + 1;

                        // Reference vertices and normal
                        sw.WriteLine($"f {v1}//{ni} {v2}//{ni} {v3}//{ni}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log("Error exporting fracture mesh: {ex.Message}");
            }
        }
        /// <summary>
        /// Copy damage data to the destination array
        /// </summary>
        /// <param name="destArray">Destination array to receive damage data</param>
        public void CopyDamageToCPU(double[,,] destArray)
        {
            // Make sure we have valid data
            if (damage == null || destArray == null)
                return;

            // Copy from internal damage array to destination array
            if (destArray.GetLength(0) == damage.GetLength(0) &&
                destArray.GetLength(1) == damage.GetLength(1) &&
                destArray.GetLength(2) == damage.GetLength(2))
            {
                Array.Copy(damage, destArray, damage.Length);
            }
            else
            {
                // Dimensions don't match, do manual copy with bounds checking
                int width = Math.Min(destArray.GetLength(0), damage.GetLength(0));
                int height = Math.Min(destArray.GetLength(1), damage.GetLength(1));
                int depth = Math.Min(destArray.GetLength(2), damage.GetLength(2));

                for (int z = 0; z < depth; z++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            destArray[x, y, z] = damage[x, y, z];
                        }
                    }
                }
            }
        }
        /// <summary>
        /// Find the point with maximum damage
        /// </summary>
        /// <returns>Coordinates of the point with maximum damage</returns>
        public (int x, int y, int z) FindMaxDamagePoint()
        {
            int maxX = 0, maxY = 0, maxZ = 0;
            double maxDamage = 0;

            // Find the maximum damage value and its location
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        // Only consider points that are part of the selected material
                        if (GetVoxelLabel(x, y, z) == selectedMaterialID && damage[x, y, z] > maxDamage)
                        {
                            maxDamage = damage[x, y, z];
                            maxX = x;
                            maxY = y;
                            maxZ = z;
                        }
                    }
                }
            }

            return (maxX, maxY, maxZ);
        }
        /// <summary>
        /// Access to damage array (for direct access in some cases)
        /// </summary>
        public double[,,] GetDamageData()
        {
            return damage;
        }
        // Export damage visualization as 2D slices
        private void ExportDamageVisualization(string timestamp)
        {
            try
            {
                // Create a mid-section slice along the primary stress axis
                int slicePos;
                Bitmap damageBitmap;

                switch (primaryStressAxis)
                {
                    case StressAxis.X:
                        slicePos = width / 2;
                        damageBitmap = new Bitmap(depth, height);

                        // Fill the bitmap with damage values
                        for (int y = 0; y < height; y++)
                        {
                            for (int z = 0; z < depth; z++)
                            {
                                double damageVal = 0;
                                if (GetVoxelLabel(slicePos, y, z) == selectedMaterialID)
                                {
                                    damageVal = damage[slicePos, y, z];
                                }

                                // Convert damage (0-1) to color (blue->red)
                                int r = (int)(255 * damageVal);
                                int g = 0;
                                int b = (int)(255 * (1.0 - damageVal));

                                damageBitmap.SetPixel(z, y, Color.FromArgb(r, g, b));
                            }
                        }
                        break;

                    case StressAxis.Y:
                        slicePos = height / 2;
                        damageBitmap = new Bitmap(width, depth);

                        // Fill the bitmap with damage values
                        for (int z = 0; z < depth; z++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                double damageVal = 0;
                                if (GetVoxelLabel(x, slicePos, z) == selectedMaterialID)
                                {
                                    damageVal = damage[x, slicePos, z];
                                }

                                // Convert damage (0-1) to color (blue->red)
                                int r = (int)(255 * damageVal);
                                int g = 0;
                                int b = (int)(255 * (1.0 - damageVal));

                                damageBitmap.SetPixel(x, z, Color.FromArgb(r, g, b));
                            }
                        }
                        break;

                    case StressAxis.Z:
                    default:
                        slicePos = depth / 2;
                        damageBitmap = new Bitmap(width, height);

                        // Fill the bitmap with damage values
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                double damageVal = 0;
                                if (GetVoxelLabel(x, y, slicePos) == selectedMaterialID)
                                {
                                    damageVal = damage[x, y, slicePos];
                                }

                                // Convert damage (0-1) to color (blue->red)
                                int r = (int)(255 * damageVal);
                                int g = 0;
                                int b = (int)(255 * (1.0 - damageVal));

                                damageBitmap.SetPixel(x, y, Color.FromArgb(r, g, b));
                            }
                        }
                        break;
                }

                // Save the damage visualization
                string damageImagePath = $"TriaxialDamage_{timestamp}.png";
                damageBitmap.Save(damageImagePath, ImageFormat.Png);
                damageBitmap.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Log("Error exporting damage visualization: {ex.Message}");
            }
        }

        public void Dispose()
        {
            cts.Cancel();
            simulationCancelled = true;
            // No unmanaged resources to clean up
            GC.SuppressFinalize(this);
        }

        // Helper method for cubic root (including negative values)
        public static double Cbrt(double value)
        {
            if (value >= 0.0)
                return Math.Pow(value, 1.0 / 3.0);
            else
                return -Math.Pow(-value, 1.0 / 3.0);
        }
    }
}