using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace CTSegmenter
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
        // Simulation parameters (readonly after init)
        private readonly int width, height, depth;
        private readonly float pixelSize;
        private readonly ILabelVolumeData volumeLabels;
        private readonly float[,,] densityVolume;
        private readonly byte selectedMaterialID;
        private readonly double confiningPressureMPa;
        private readonly double initialAxialPressureMPa;
        private readonly double finalAxialPressureMPa;
        private readonly int pressureIncrements;
        private readonly bool useElastic;
        private readonly bool usePlastic;
        private readonly bool useBrittle;
        private readonly double tensileStrengthMPa;
        private readonly double frictionAngleDeg;
        private readonly double cohesionMPa;
        private readonly double youngsModulusMPa;
        private readonly double poissonRatio;
        private readonly int stepsPerIncrement;
        private readonly StressAxis primaryStressAxis;
        // Lame constants (derived from E and nu)
        private readonly double lambda0; // initial Lame's lambda (Pa)
        private readonly double mu0;     // initial Lame's mu (Pa)
        // Simulation state fields
        private double dt; // time step (s)
        private readonly double[,,] vx, vy, vz;
        private readonly double[,,] sxx, syy, szz;
        private readonly double[,,] sxy, sxz, syz;
        private readonly double[,,] damage;
        private readonly double[,,] dispX, dispY, dispZ;
        // State trackers
        private volatile bool simulationCancelled = false;
        private volatile bool simulationPaused = false;
        private List<double> strainHistory = new List<double>();
        private List<double> stressHistory = new List<double>();
        private readonly double failureThreshold = 0.75; // Damage threshold to detect material failure
        // Events
        public event EventHandler<TriaxialSimulationProgressEventArgs> ProgressUpdated;
        public event EventHandler<TriaxialSimulationCompleteEventArgs> SimulationCompleted;
        public event EventHandler<FailureDetectedEventArgs> FailureDetected;
        // Internal cancellation token source
        private readonly CancellationTokenSource cts = new CancellationTokenSource();

        public TriaxialSimulator(int width, int height, int depth, float pixelSize,
                                 ILabelVolumeData volumeLabels, float[,,] densityVolume, byte selectedMaterialID,
                                 double confiningPressure, double initialAxialPressure, double finalAxialPressure,
                                 int pressureIncrements, StressAxis primaryAxis,
                                 bool useElasticModel, bool usePlasticModel, bool useBrittleModel,
                                 double tensileStrength, double frictionAngle, double cohesion,
                                 double youngsModulus, double poissonRatio,
                                 int stepsPerIncrement = 200)
        {
            // Assign simulation size and data
            this.width = width;
            this.height = height;
            this.depth = depth;
            this.pixelSize = pixelSize;
            this.volumeLabels = volumeLabels;
            this.densityVolume = densityVolume;
            this.selectedMaterialID = selectedMaterialID;
            // Assign physical parameters
            this.confiningPressureMPa = confiningPressure;
            this.initialAxialPressureMPa = initialAxialPressure;
            this.finalAxialPressureMPa = finalAxialPressure;
            this.pressureIncrements = pressureIncrements;
            this.primaryStressAxis = primaryAxis;
            this.useElastic = useElasticModel;
            this.usePlastic = usePlasticModel;
            this.useBrittle = useBrittleModel;
            this.tensileStrengthMPa = tensileStrength;
            this.frictionAngleDeg = frictionAngle;
            this.cohesionMPa = cohesion;
            this.youngsModulusMPa = youngsModulus;
            this.poissonRatio = poissonRatio;
            this.stepsPerIncrement = stepsPerIncrement;
            // Compute Lame constants (Pa) from E (MPa) and ν
            double E_Pa = youngsModulusMPa * 1e6;
            this.mu0 = E_Pa / (2.0 * (1.0 + poissonRatio));
            this.lambda0 = E_Pa * poissonRatio / ((1.0 + poissonRatio) * (1.0 - 2.0 * poissonRatio));
            // Allocate field arrays
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
        private void UpdateStress()
        {
            double sinPhi = Math.Sin(frictionAngleDeg * Math.PI / 180.0);
            double cosPhi = Math.Cos(frictionAngleDeg * Math.PI / 180.0);
            double cohesionPa = cohesionMPa * 1e6;
            double confPa = confiningPressureMPa * 1e6;
            double tensilePa = tensileStrengthMPa * 1e6;

            Parallel.For(1, depth - 1, z =>
            {
                for (int y = 1; y < height - 1; y++)
                {
                    for (int x = 1; x < width - 1; x++)
                    {
                        if (GetVoxelLabel(x, y, z) != selectedMaterialID) continue;

                        double D = useBrittle ? damage[x, y, z] : 0.0;
                        double lambda = (1.0 - D) * lambda0;
                        double mu = (1.0 - D) * mu0;

                        // Velocity gradients (central differencing)
                        double dvx_dx = (vx[x + 1, y, z] - vx[x - 1, y, z]) / (2 * pixelSize);
                        double dvy_dy = (vy[x, y + 1, z] - vy[x, y - 1, z]) / (2 * pixelSize);
                        double dvz_dz = (vz[x, y, z + 1] - vz[x, y, z - 1]) / (2 * pixelSize);
                        double dvx_dy = (vx[x, y + 1, z] - vx[x, y - 1, z]) / (2 * pixelSize);
                        double dvx_dz = (vx[x, y, z + 1] - vx[x, y, z - 1]) / (2 * pixelSize);
                        double dvy_dx = (vy[x + 1, y, z] - vy[x - 1, y, z]) / (2 * pixelSize);
                        double dvy_dz = (vy[x, y, z + 1] - vy[x, y, z - 1]) / (2 * pixelSize);
                        double dvz_dx = (vz[x + 1, y, z] - vz[x - 1, y, z]) / (2 * pixelSize);
                        double dvz_dy = (vz[x, y + 1, z] - vz[x, y - 1, z]) / (2 * pixelSize);

                        // Clamp extreme gradients for stability
                        const double MAX_GRAD = 1e12;
                        dvx_dx = Math.Max(-MAX_GRAD, Math.Min(MAX_GRAD, dvx_dx));
                        dvy_dy = Math.Max(-MAX_GRAD, Math.Min(MAX_GRAD, dvy_dy));
                        dvz_dz = Math.Max(-MAX_GRAD, Math.Min(MAX_GRAD, dvz_dz));
                        dvx_dy = Math.Max(-MAX_GRAD, Math.Min(MAX_GRAD, dvx_dy));
                        dvx_dz = Math.Max(-MAX_GRAD, Math.Min(MAX_GRAD, dvx_dz));
                        dvy_dx = Math.Max(-MAX_GRAD, Math.Min(MAX_GRAD, dvy_dx));
                        dvy_dz = Math.Max(-MAX_GRAD, Math.Min(MAX_GRAD, dvy_dz));
                        dvz_dx = Math.Max(-MAX_GRAD, Math.Min(MAX_GRAD, dvz_dx));
                        dvz_dy = Math.Max(-MAX_GRAD, Math.Min(MAX_GRAD, dvz_dy));

                        // Volumetric strain rate
                        double divV = dvx_dx + dvy_dy + dvz_dz;

                        // Elastic predictor (increment stresses)
                        double dsxx = dt * (lambda * divV + 2 * mu * dvx_dx);
                        double dsyy = dt * (lambda * divV + 2 * mu * dvy_dy);
                        double dszz = dt * (lambda * divV + 2 * mu * dvz_dz);
                        double dsxy = dt * mu * (dvx_dy + dvy_dx);
                        double dsxz = dt * mu * (dvx_dz + dvz_dx);
                        double dsyz = dt * mu * (dvy_dz + dvz_dy);

                        // Trial new stress
                        double sxxN = sxx[x, y, z] + dsxx;
                        double syyN = syy[x, y, z] + dsyy;
                        double szzN = szz[x, y, z] + dszz;
                        double sxyN = sxy[x, y, z] + dsxy;
                        double sxzN = sxz[x, y, z] + dsxz;
                        double syzN = syz[x, y, z] + dsyz;

                        // Plastic correction (Mohr-Coulomb yield)
                        if (usePlastic)
                        {
                            double mean = (sxxN + syyN + szzN) / 3.0;
                            double dev_xx = sxxN - mean;
                            double dev_yy = syyN - mean;
                            double dev_zz = szzN - mean;
                            double J2 = 0.5 * (dev_xx * dev_xx + dev_yy * dev_yy + dev_zz * dev_zz)
                                      + (sxyN * sxyN + sxzN * sxzN + syzN * syzN);
                            double tau = Math.Sqrt(Math.Max(J2, 0.0));
                            double p = -mean + confPa;
                            double yield = tau + p * sinPhi - cohesionPa * cosPhi;

                            if (yield > 0.0)
                            {
                                double safeTau = (tau > 1e-10) ? tau : 1e-10;
                                double scale = (tau - (cohesionPa * cosPhi - p * sinPhi)) / safeTau;
                                scale = Math.Min(scale, 0.95);

                                // Reduce deviatoric stress components
                                dev_xx *= (1 - scale);
                                dev_yy *= (1 - scale);
                                dev_zz *= (1 - scale);
                                sxyN *= (1 - scale);
                                sxzN *= (1 - scale);
                                syzN *= (1 - scale);

                                // Recombine with mean stress
                                sxxN = dev_xx + mean;
                                syyN = dev_yy + mean;
                                szzN = dev_zz + mean;
                            }
                        }

                        // Brittle damage (tensile failure)
                        if (useBrittle)
                        {
                            // Calculate principal stresses via characteristic equation
                            double I1 = sxxN + syyN + szzN;
                            double I2 = sxxN * syyN + syyN * szzN + szzN * sxxN - (sxyN * sxyN + sxzN * sxzN + syzN * syzN);
                            double I3 = sxxN * (syyN * szzN - syzN * syzN)
                                      - sxyN * (sxyN * szzN - syzN * sxzN)
                                      + sxzN * (sxyN * syzN - syyN * sxzN);

                            // Solve cubic equation for principal stresses
                            double a = -I1;
                            double b = I2;
                            double c = -I3;
                            double q = (3 * b - a * a) / 9.0;
                            double r = (9 * a * b - 27 * c - 2 * a * a * a) / 54.0;
                            double disc = q * q * q + r * r;

                            double sigmaMax;
                            if (disc >= 0)
                            {
                                double sqrtDisc = Math.Sqrt(disc);
                                double s1 = Cbrt(r + sqrtDisc);
                                double s2 = Cbrt(r - sqrtDisc);
                                sigmaMax = -a / 3.0 + s1 + s2;
                            }
                            else
                            {
                                double theta = Math.Acos(r / Math.Sqrt(-q * q * q));
                                sigmaMax = 2.0 * Math.Sqrt(-q) * Math.Cos(theta / 3.0) - a / 3.0;
                            }

                            // Apply tensile damage if max principal stress exceeds tensile strength
                            if (sigmaMax > tensilePa && D < 1.0)
                            {
                                double incr = (sigmaMax - tensilePa) / tensilePa;
                                incr = Math.Min(incr, 0.1); // limit increment for stability
                                double newD = Math.Min(0.95, D + incr * 0.01);
                                damage[x, y, z] = newD;
                                double factor = 1.0 - newD;
                                sxxN *= factor;
                                syyN *= factor;
                                szzN *= factor;
                                sxyN *= factor;
                                sxzN *= factor;
                                syzN *= factor;
                            }
                        }

                        // Commit new stress state
                        sxx[x, y, z] = sxxN;
                        syy[x, y, z] = syyN;
                        szz[x, y, z] = szzN;
                        sxy[x, y, z] = sxyN;
                        sxz[x, y, z] = sxzN;
                        syz[x, y, z] = syzN;
                    }
                }
            });
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

        // Detect if material has significant damage/failure
        private bool CheckForFailure()
        {
            // Check for significant damage in the material
            int failedVoxelCount = 0;
            int totalMaterialVoxels = 0;
            double maxDamage = 0.0;

            Parallel.For(0, depth, z =>
            {
                int localFailedCount = 0;
                int localTotalCount = 0;
                double localMaxDamage = 0.0;

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (GetVoxelLabel(x, y, z) == selectedMaterialID)
                        {
                            localTotalCount++;

                            if (damage[x, y, z] > failureThreshold)
                            {
                                localFailedCount++;
                            }

                            localMaxDamage = Math.Max(localMaxDamage, damage[x, y, z]);
                        }
                    }
                }

                // Thread-safe update of shared counters
                Interlocked.Add(ref failedVoxelCount, localFailedCount);
                Interlocked.Add(ref totalMaterialVoxels, localTotalCount);

                // Thread-safe update of max damage
                lock (damage)
                {
                    maxDamage = Math.Max(maxDamage, localMaxDamage);
                }
            });

            // Consider failure if more than 10% of material voxels have significant damage
            double failedRatio = (double)failedVoxelCount / totalMaterialVoxels;
            return failedRatio > 0.1 || maxDamage > 0.9;
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
                return volumeLabels[x, y, z];
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // Simulation main loop (pressure increments)
        private void Run(CancellationToken token)
        {
            ComputeStableTimeStep();
            InitializeFields();

            // Identify the boundaries of the material based on the primary stress axis
            int minPos = 0, maxPos = 0;

            switch (primaryStressAxis)
            {
                case StressAxis.X:
                    // Find min and max X containing material
                    minPos = width - 1;
                    maxPos = 0;
                    for (int x = 0; x < width; x++)
                    {
                        bool containsMaterial = false;
                        for (int z = 0; z < depth && !containsMaterial; z++)
                        {
                            for (int y = 0; y < height && !containsMaterial; y++)
                            {
                                if (GetVoxelLabel(x, y, z) == selectedMaterialID)
                                {
                                    containsMaterial = true;
                                    minPos = Math.Min(minPos, x);
                                    maxPos = Math.Max(maxPos, x);
                                }
                            }
                        }
                    }
                    break;

                case StressAxis.Y:
                    // Find min and max Y containing material
                    minPos = height - 1;
                    maxPos = 0;
                    for (int y = 0; y < height; y++)
                    {
                        bool containsMaterial = false;
                        for (int z = 0; z < depth && !containsMaterial; z++)
                        {
                            for (int x = 0; x < width && !containsMaterial; x++)
                            {
                                if (GetVoxelLabel(x, y, z) == selectedMaterialID)
                                {
                                    containsMaterial = true;
                                    minPos = Math.Min(minPos, y);
                                    maxPos = Math.Max(maxPos, y);
                                }
                            }
                        }
                    }
                    break;

                case StressAxis.Z:
                default:
                    // Find min and max Z containing material
                    minPos = depth - 1;
                    maxPos = 0;
                    for (int z = 0; z < depth; z++)
                    {
                        bool containsMaterial = false;
                        for (int y = 0; y < height && !containsMaterial; y++)
                        {
                            for (int x = 0; x < width && !containsMaterial; x++)
                            {
                                if (GetVoxelLabel(x, y, z) == selectedMaterialID)
                                {
                                    containsMaterial = true;
                                    minPos = Math.Min(minPos, z);
                                    maxPos = Math.Max(maxPos, z);
                                }
                            }
                        }
                    }
                    break;
            }

            // Calculate initial sample size along primary axis for strain calculation
            double sampleSize = (maxPos - minPos + 1) * pixelSize;

            // Initialize stress-strain history (initial point at initial axial pressure and zero strain)
            strainHistory.Clear();
            stressHistory.Clear();
            strainHistory.Add(0.0);
            stressHistory.Add(initialAxialPressureMPa);

            // Axial pressure increment size
            double axialStepMPa = (finalAxialPressureMPa - initialAxialPressureMPa) / pressureIncrements;
            bool failureDetected = false;
            int failureStep = -1;

            for (int inc = 1; inc <= pressureIncrements; inc++)
            {
                if (simulationCancelled || token.IsCancellationRequested) break;

                // Wait when paused
                while (simulationPaused && !simulationCancelled && !token.IsCancellationRequested)
                {
                    Thread.Sleep(100);
                }

                if (simulationCancelled || token.IsCancellationRequested) break;

                double targetAxialMPa = initialAxialPressureMPa + axialStepMPa * inc;
                double targetAxialPa = targetAxialMPa * 1e6;

                // Apply updated axial pressure at the boundary based on primary axis
                switch (primaryStressAxis)
                {
                    case StressAxis.X:
                        // Apply pressure at max X boundary
                        Parallel.For(0, depth, z =>
                        {
                            for (int y = 0; y < height; y++)
                            {
                                if (GetVoxelLabel(maxPos, y, z) == selectedMaterialID)
                                {
                                    sxx[maxPos, y, z] = -targetAxialPa;
                                }
                            }
                        });
                        break;

                    case StressAxis.Y:
                        // Apply pressure at max Y boundary
                        Parallel.For(0, depth, z =>
                        {
                            for (int x = 0; x < width; x++)
                            {
                                if (GetVoxelLabel(x, maxPos, z) == selectedMaterialID)
                                {
                                    syy[x, maxPos, z] = -targetAxialPa;
                                }
                            }
                        });
                        break;

                    case StressAxis.Z:
                    default:
                        // Apply pressure at max Z boundary
                        Parallel.For(0, height, y =>
                        {
                            for (int x = 0; x < width; x++)
                            {
                                if (GetVoxelLabel(x, y, maxPos) == selectedMaterialID)
                                {
                                    szz[x, y, maxPos] = -targetAxialPa;
                                }
                            }
                        });
                        break;
                }

                // Run time steps for this increment to equilibrate stresses
                for (int step = 0; step < stepsPerIncrement; step++)
                {
                    if (simulationCancelled || token.IsCancellationRequested) break;

                    // Wait when paused
                    while (simulationPaused && !simulationCancelled && !token.IsCancellationRequested)
                    {
                        Thread.Sleep(100);
                    }

                    if (simulationCancelled || token.IsCancellationRequested) break;

                    UpdateStress();
                    UpdateVelocity();

                    // Progress update (every 10 steps)
                    if (step % 10 == 0)
                    {
                        int percent = (int)((double)inc / pressureIncrements * 100.0);
                        string status = $"Loading: {targetAxialMPa:F2} MPa, Step {step + 1}/{stepsPerIncrement}";
                        ProgressUpdated?.Invoke(this, new TriaxialSimulationProgressEventArgs(percent, inc, status));
                    }

                    // Check for failure (only if using brittle model)
                    if (useBrittle && !failureDetected && CheckForFailure())
                    {
                        failureDetected = true;
                        failureStep = inc;

                        // Calculate current strain
                        double currentStrain = CalculateAverageStrain(primaryStressAxis, minPos, maxPos, sampleSize);

                        // Notify failure detected
                        var failureArgs = new FailureDetectedEventArgs(targetAxialMPa, currentStrain, inc, pressureIncrements);
                        FailureDetected?.Invoke(this, failureArgs);

                        // Pause simulation to let user decide whether to continue
                        simulationPaused = true;
                    }
                }

                // Calculate average strain along the primary axis
                double axialStrain = CalculateAverageStrain(primaryStressAxis, minPos, maxPos, sampleSize);

                // Record stress-strain point
                strainHistory.Add(axialStrain);
                stressHistory.Add(targetAxialMPa);

                // Progress update
                int percentComplete = (int)((double)inc / pressureIncrements * 100.0);
                string statusMessage = $"Loading: {targetAxialMPa:F2} MPa";
                ProgressUpdated?.Invoke(this, new TriaxialSimulationProgressEventArgs(percentComplete, inc, statusMessage));
            }

            // If cancelled mid-run, notify cancellation
            if (simulationCancelled || token.IsCancellationRequested)
            {
                ProgressUpdated?.Invoke(this, new TriaxialSimulationProgressEventArgs(0, 0, "Cancelled"));
                return;
            }

            // Finalize results and raise completion event
            var resultArgs = new TriaxialSimulationCompleteEventArgs(
                strainHistory.ToArray(),
                stressHistory.ToArray(),
                failureDetected,
                failureStep);

            SimulationCompleted?.Invoke(this, resultArgs);

            // Auto-export results and images after simulation
            ExportResultsAndImages();
        }

        // Calculate average strain along the primary stress axis
        private double CalculateAverageStrain(StressAxis axis, int minPos, int maxPos, double originalSize)
        {
            double avgDisp = 0.0;
            int count = 0;

            switch (axis)
            {
                case StressAxis.X:
                    // Measure X displacement at max boundary
                    for (int z = 0; z < depth; z++)
                    {
                        for (int y = 0; y < height; y++)
                        {
                            if (GetVoxelLabel(maxPos, y, z) == selectedMaterialID)
                            {
                                avgDisp += dispX[maxPos, y, z];
                                count++;
                            }
                        }
                    }
                    break;

                case StressAxis.Y:
                    // Measure Y displacement at max boundary
                    for (int z = 0; z < depth; z++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            if (GetVoxelLabel(x, maxPos, z) == selectedMaterialID)
                            {
                                avgDisp += dispY[x, maxPos, z];
                                count++;
                            }
                        }
                    }
                    break;

                case StressAxis.Z:
                default:
                    // Measure Z displacement at max boundary
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            if (GetVoxelLabel(x, y, maxPos) == selectedMaterialID)
                            {
                                avgDisp += dispZ[x, y, maxPos];
                                count++;
                            }
                        }
                    }
                    break;
            }

            if (count > 0) avgDisp /= count;

            // Compressive strain is positive in geomechanics convention
            return -avgDisp / originalSize;
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