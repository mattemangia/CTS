using System;
using ILGPU;
using ILGPU.Runtime;

namespace CTSegmenter.Modules.Acoustic_Simulation
{
    internal class AcousticSimulatorGPU : IDisposable
    {
        // Auto-stop criteria fields
        private bool autoStopEnabled = true;
        private double energyThresholdRatio = 0.01; // Stop when energy drops to 1% of peak
        private double maxReceiverEnergy = 0;
        private bool energyPeaked = false;
        private int receiverX, receiverY, receiverZ; // Position at far end of rock
        private bool receiverPositionSet = false;
        private int checkInterval = 10; // Check every 10 steps
        private int minRequiredSteps = 50;

        // Progress tracking
        private int currentStep = 0;
        private int totalSteps = 100; // Default value
        private bool totalStepsSet = false;

        // ------------------------------------------------------------------ GPU context -----
        private readonly Context context;
        private readonly Accelerator accelerator;

        // ---------------------------------------------------------------- Device buffers ----
        private readonly MemoryBuffer1D<byte, Stride1D.Dense> materialBuffer;
        private readonly MemoryBuffer1D<double, Stride1D.Dense> pressureBuffer;
        private readonly MemoryBuffer1D<double, Stride1D.Dense> previousPressureBuffer;
        private readonly MemoryBuffer1D<double, Stride1D.Dense> velocityXBuffer;
        private readonly MemoryBuffer1D<double, Stride1D.Dense> velocityYBuffer;
        private readonly MemoryBuffer1D<double, Stride1D.Dense> velocityZBuffer;
        private readonly MemoryBuffer1D<float, Stride1D.Dense> materialPropertiesBuffer;

        // --------------------------------------------------------------- Kernel delegates ----
        private readonly Action<Index1D,
            ArrayView1D<byte, Stride1D.Dense>,
            ArrayView1D<double, Stride1D.Dense>,
            ArrayView1D<double, Stride1D.Dense>,
            byte, int, int, int, float> updatePressureKernel;

        private readonly Action<Index1D,
            ArrayView1D<byte, Stride1D.Dense>,
            ArrayView1D<double, Stride1D.Dense>,
            ArrayView1D<double, Stride1D.Dense>,
            ArrayView1D<double, Stride1D.Dense>,
            ArrayView1D<double, Stride1D.Dense>, byte> updateVelocityKernel;

        private readonly Action<Index1D,
            ArrayView1D<byte, Stride1D.Dense>,
            ArrayView1D<double, Stride1D.Dense>,
            ArrayView1D<double, Stride1D.Dense>,
            ArrayView1D<double, Stride1D.Dense>,
            byte, double, double, double> applySourceKernel;

        private readonly Action<Index1D,
            ArrayView1D<byte, Stride1D.Dense>,
            ArrayView1D<double, Stride1D.Dense>,
            ArrayView1D<double, Stride1D.Dense>,
            ArrayView1D<double, Stride1D.Dense>,
            byte, double, double> applyAbsorbingBoundaryKernel;

        private readonly Action<Index1D,
            ArrayView1D<byte, Stride1D.Dense>,
            ArrayView1D<float, Stride1D.Dense>,
            ArrayView1D<double, Stride1D.Dense>,
            ArrayView1D<double, Stride1D.Dense>,
            ArrayView1D<double, Stride1D.Dense>,
            ArrayView1D<double, Stride1D.Dense>,
            ArrayView1D<double, Stride1D.Dense>, byte, double> computeEnergyKernel;

        // ----------------------------------------------------------- Simulation parameters --
        private readonly int dimX, dimY, dimZ;
        private readonly float dt, dx;
        private readonly byte airMaterial = 0;
        private readonly byte absorberMaterial = 1;
        private readonly float speedOfSound = 343.0f;
        private readonly double absorberCoefficient = 0.1;

        // ===================================================================== Constructor ==
        public AcousticSimulatorGPU(int dimX, int dimY, int dimZ, float dx = 1f, float dt = .5f)
        {
            Logger.Log("[AcousticSimulatorGPU] Initializing simulator with dimensions: " + dimX + "x" + dimY + "x" + dimZ);
            this.dimX = dimX; this.dimY = dimY; this.dimZ = dimZ;
            this.dx = dx; this.dt = dt;

            Logger.Log("[AcousticSimulatorGPU] Creating GPU context");
            context = Context.CreateDefault();
            accelerator = context.GetPreferredDevice(preferCPU: false)
                                 .CreateAccelerator(context);
            Logger.Log("[AcousticSimulatorGPU] Using accelerator: " + accelerator.Name);

            int totalInt = dimX * dimY * dimZ;
            long totalLong = totalInt;
            Logger.Log("[AcousticSimulatorGPU] Allocating buffers for " + totalLong + " elements");

            materialBuffer = accelerator.Allocate1D<byte>(totalLong);
            pressureBuffer = accelerator.Allocate1D<double>(totalLong);
            previousPressureBuffer = accelerator.Allocate1D<double>(totalLong);
            velocityXBuffer = accelerator.Allocate1D<double>(totalLong);
            velocityYBuffer = accelerator.Allocate1D<double>(totalLong);
            velocityZBuffer = accelerator.Allocate1D<double>(totalLong);
            materialPropertiesBuffer = accelerator.Allocate1D<float>(256);

            Logger.Log("[AcousticSimulatorGPU] Initializing buffers to zero");
            pressureBuffer.MemSetToZero();
            previousPressureBuffer.MemSetToZero();
            velocityXBuffer.MemSetToZero();
            velocityYBuffer.MemSetToZero();
            velocityZBuffer.MemSetToZero();

            Logger.Log("[AcousticSimulatorGPU] Loading GPU kernels");
            updatePressureKernel = accelerator.LoadAutoGroupedStreamKernel<
                Index1D,
                ArrayView1D<byte, Stride1D.Dense>,
                ArrayView1D<double, Stride1D.Dense>,
                ArrayView1D<double, Stride1D.Dense>,
                byte, int, int, int, float>(UpdatePressureKernel);

            updateVelocityKernel = accelerator.LoadAutoGroupedStreamKernel<
                Index1D,
                ArrayView1D<byte, Stride1D.Dense>,
                ArrayView1D<double, Stride1D.Dense>,
                ArrayView1D<double, Stride1D.Dense>,
                ArrayView1D<double, Stride1D.Dense>,
                ArrayView1D<double, Stride1D.Dense>, byte>(UpdateVelocityKernel);

            applySourceKernel = accelerator.LoadAutoGroupedStreamKernel<
                Index1D,
                ArrayView1D<byte, Stride1D.Dense>,
                ArrayView1D<double, Stride1D.Dense>,
                ArrayView1D<double, Stride1D.Dense>,
                ArrayView1D<double, Stride1D.Dense>,
                byte, double, double, double>(ApplySourceKernel);

            applyAbsorbingBoundaryKernel = accelerator.LoadAutoGroupedStreamKernel<
                Index1D,
                ArrayView1D<byte, Stride1D.Dense>,
                ArrayView1D<double, Stride1D.Dense>,
                ArrayView1D<double, Stride1D.Dense>,
                ArrayView1D<double, Stride1D.Dense>,
                byte, double, double>(ApplyAbsorbingBoundaryKernel);

            computeEnergyKernel = accelerator.LoadAutoGroupedStreamKernel<
                Index1D,
                ArrayView1D<byte, Stride1D.Dense>,
                ArrayView1D<float, Stride1D.Dense>,
                ArrayView1D<double, Stride1D.Dense>,
                ArrayView1D<double, Stride1D.Dense>,
                ArrayView1D<double, Stride1D.Dense>,
                ArrayView1D<double, Stride1D.Dense>,
                ArrayView1D<double, Stride1D.Dense>, byte, double>(ComputeEnergyKernel);

            Logger.Log("[AcousticSimulatorGPU] Initialization complete");
        }

        #region Buffer helpers
        public void SetMaterials(byte[] data)
        {
            Logger.Log("[AcousticSimulatorGPU] Copying material data to GPU (" + data.Length + " bytes)");
            materialBuffer.CopyFromCPU(data);
        }

        public byte[] GetMaterials()
        {
            Logger.Log("[AcousticSimulatorGPU] Retrieving material data from GPU");
            var a = new byte[materialBuffer.Length];
            materialBuffer.CopyToCPU(a);
            return a;
        }

        public void SetPressure(double[] p)
        {
            Logger.Log("[AcousticSimulatorGPU] Copying pressure data to GPU (" + p.Length + " elements)");
            pressureBuffer.CopyFromCPU(p);
        }

        public double[] GetPressure()
        {
            Logger.Log("[AcousticSimulatorGPU] Retrieving pressure data from GPU");
            var a = new double[pressureBuffer.Length];
            pressureBuffer.CopyToCPU(a);
            return a;
        }

        public double[] GetVelocityX()
        {
            Logger.Log("[AcousticSimulatorGPU] Retrieving velocity X data from GPU");
            var a = new double[velocityXBuffer.Length];
            velocityXBuffer.CopyToCPU(a);
            return a;
        }

        public double[] GetVelocityY()
        {
            Logger.Log("[AcousticSimulatorGPU] Retrieving velocity Y data from GPU");
            var a = new double[velocityYBuffer.Length];
            velocityYBuffer.CopyToCPU(a);
            return a;
        }

        public double[] GetVelocityZ()
        {
            Logger.Log("[AcousticSimulatorGPU] Retrieving velocity Z data from GPU");
            var a = new double[velocityZBuffer.Length];
            velocityZBuffer.CopyToCPU(a);
            return a;
        }

        public void SetMaterialProperties(float[] props)
        {
            Logger.Log("[AcousticSimulatorGPU] Copying material properties to GPU (" + props.Length + " elements)");
            materialPropertiesBuffer.CopyFromCPU(props);
        }
        #endregion

        #region Auto-Stop Configuration

        // Configure auto-stop with receiver position at the end of the rock core
        public void ConfigureAutoStop(int receiverX, int receiverY, int receiverZ,
                                     bool enabled = true, double thresholdRatio = 0.01,
                                     int checkInterval = 10, int minSteps = 50)
        {
            this.receiverX = Math.Max(0, Math.Min(dimX - 1, receiverX));
            this.receiverY = Math.Max(0, Math.Min(dimY - 1, receiverY));
            this.receiverZ = Math.Max(0, Math.Min(dimZ - 1, receiverZ));
            this.receiverPositionSet = true;

            this.autoStopEnabled = enabled;
            this.energyThresholdRatio = thresholdRatio;
            this.checkInterval = checkInterval;
            this.minRequiredSteps = minSteps;
            this.maxReceiverEnergy = 0;
            this.energyPeaked = false;

            Logger.Log($"[AcousticSimulatorGPU] Auto-stop configured: receiver=({receiverX},{receiverY},{receiverZ}), " +
                      $"threshold={thresholdRatio:F4}, checkInterval={checkInterval}, minSteps={minSteps}");
        }

        // Helper to set receiver position automatically based on propagation axis
        public void SetReceiverPositionForAxis(string axis)
        {
            switch (axis.ToUpper())
            {
                case "X":
                    ConfigureAutoStop(dimX - 1, dimY / 2, dimZ / 2);
                    break;
                case "Y":
                    ConfigureAutoStop(dimX / 2, dimY - 1, dimZ / 2);
                    break;
                case "Z":
                    ConfigureAutoStop(dimX / 2, dimY / 2, dimZ - 1);
                    break;
                default:
                    Logger.Log("[AcousticSimulatorGPU] Warning: Invalid axis for receiver. Using X axis.");
                    ConfigureAutoStop(dimX - 1, dimY / 2, dimZ / 2);
                    break;
            }
        }

        #endregion

        #region Simulation API
        // Sets the total number of steps for progress tracking
        public void SetTotalSteps(int steps)
        {
            if (steps <= 0)
            {
                Logger.Log($"[AcousticSimulatorGPU] Warning: Invalid step count {steps}, using default of 100");
                this.totalSteps = 100;
            }
            else
            {
                this.totalSteps = steps;
                this.totalStepsSet = true;
            }

            currentStep = 0;
            Logger.Log($"[AcousticSimulatorGPU] Simulation configured for {totalSteps} total steps");
        }

        // Modified Step method to return false when simulation should stop
        public bool Step()
        {
            currentStep++;

            // Calculate progress percentage safely
            int progressPercent = (int)((float)currentStep / Math.Max(1, totalSteps) * 100);

            // Cap at 99% if totalSteps wasn't explicitly set
            if (!totalStepsSet && progressPercent >= 100)
                progressPercent = 99;

            Logger.Log($"[AcousticSimulatorGPU] Step {currentStep}/{totalSteps} ({progressPercent}% complete)");

            int total = dimX * dimY * dimZ;
            float coeff = speedOfSound * speedOfSound * dt * dt / (dx * dx);

            Logger.Log("[AcousticSimulatorGPU] Updating pressure field");
            updatePressureKernel(total,
                materialBuffer.View, pressureBuffer.View, previousPressureBuffer.View,
                airMaterial, dimX, dimY, dimZ, coeff);

            Logger.Log("[AcousticSimulatorGPU] Updating velocity field");
            updateVelocityKernel(total,
                materialBuffer.View, pressureBuffer.View,
                velocityXBuffer.View, velocityYBuffer.View, velocityZBuffer.View, airMaterial);

            Logger.Log("[AcousticSimulatorGPU] Synchronizing GPU");
            accelerator.Synchronize();

            // Check if we should stop based on energy criterion (periodically)
            if (autoStopEnabled && receiverPositionSet &&
                currentStep >= minRequiredSteps &&
                currentStep % checkInterval == 0)
            {
                if (CheckEnergyStopping())
                {
                    Logger.Log($"[AcousticSimulatorGPU] Auto-stop criterion met at step {currentStep} - ending simulation early");
                    return false; // Stop simulation
                }
            }

            Logger.Log($"[AcousticSimulatorGPU] Simulation step {currentStep} complete");

            // Continue if we haven't reached max steps
            return currentStep < totalSteps;
        }

        private bool CheckEnergyStopping()
        {
            // Get fields at receiver
            double[] pressure = GetPressure();
            double[] vx = GetVelocityX();
            double[] vy = GetVelocityY();
            double[] vz = GetVelocityZ();

            // Calculate index and energy
            int index = receiverZ * dimX * dimY + receiverY * dimX + receiverX;
            double energy = CalculateEnergyAtPoint(pressure[index], vx[index], vy[index], vz[index]);

            // Update max energy seen
            if (energy > maxReceiverEnergy)
            {
                maxReceiverEnergy = energy;
                return false; // Still seeing increasing energy, don't stop
            }

            // Check if energy has peaked (dropped to half of max)
            if (!energyPeaked && maxReceiverEnergy > 0 && energy < 0.5 * maxReceiverEnergy)
            {
                energyPeaked = true;
                Logger.Log($"[AcousticSimulatorGPU] Energy peaked at {maxReceiverEnergy:E2}, now at {energy:E2}");
            }

            // If energy has peaked and dropped below threshold, stop
            if (energyPeaked && energy < energyThresholdRatio * maxReceiverEnergy)
            {
                Logger.Log($"[AcousticSimulatorGPU] Energy at receiver dropped to {energy / maxReceiverEnergy:P2} of maximum");
                return true; // Stop simulation
            }

            return false; // Continue
        }

        private double CalculateEnergyAtPoint(double p, double vx, double vy, double vz)
        {
            double density = 1.225; // Air density in kg/m³
            double kineticEnergy = 0.5 * density * (vx * vx + vy * vy + vz * vz);
            double potentialEnergy = 0.5 * p * p / (speedOfSound * speedOfSound * density);
            return kineticEnergy + potentialEnergy;
        }

        public void ApplySource(double[] waveform, double sx, double sy, double sz)
        {
            Logger.Log("[AcousticSimulatorGPU] Applying acoustic source at position: " + sx + ", " + sy + ", " + sz);
            int total = dimX * dimY * dimZ;
            using (var src = accelerator.Allocate1D<double>(waveform.Length))
            {
                Logger.Log("[AcousticSimulatorGPU] Copying waveform data to GPU (" + waveform.Length + " elements)");
                src.CopyFromCPU(waveform);
                applySourceKernel(total,
                    materialBuffer.View, pressureBuffer.View, previousPressureBuffer.View,
                    src.View, airMaterial, sx, sy, sz);
                Logger.Log("[AcousticSimulatorGPU] Synchronizing GPU");
                accelerator.Synchronize();
            }
            Logger.Log("[AcousticSimulatorGPU] Source application complete");
        }

        public void ApplyAbsorbingBoundary()
        {
            Logger.Log("[AcousticSimulatorGPU] Applying absorbing boundary conditions");
            int total = dimX * dimY * dimZ;

            Logger.Log("[AcousticSimulatorGPU] Processing X direction (33%)");
            applyAbsorbingBoundaryKernel(total,
                materialBuffer.View, pressureBuffer.View, previousPressureBuffer.View,
                velocityXBuffer.View, absorberMaterial, absorberCoefficient, dt);

            Logger.Log("[AcousticSimulatorGPU] Processing Y direction (66%)");
            applyAbsorbingBoundaryKernel(total,
                materialBuffer.View, pressureBuffer.View, previousPressureBuffer.View,
                velocityYBuffer.View, absorberMaterial, absorberCoefficient, dt);

            Logger.Log("[AcousticSimulatorGPU] Processing Z direction (100%)");
            applyAbsorbingBoundaryKernel(total,
                materialBuffer.View, pressureBuffer.View, previousPressureBuffer.View,
                velocityZBuffer.View, absorberMaterial, absorberCoefficient, dt);

            Logger.Log("[AcousticSimulatorGPU] Synchronizing GPU");
            accelerator.Synchronize();
            Logger.Log("[AcousticSimulatorGPU] Absorbing boundary application complete");
        }

        public double[] ComputeEnergyField(double density = 1.225)
        {
            Logger.Log("[AcousticSimulatorGPU] Computing energy field (density: " + density + ")");
            int total = dimX * dimY * dimZ;
            double[] host = new double[total];

            using (var energy = accelerator.Allocate1D<double>(total))
            {
                Logger.Log("[AcousticSimulatorGPU] Running energy computation kernel (50%)");
                computeEnergyKernel(total,
                    materialBuffer.View, materialPropertiesBuffer.View,
                    pressureBuffer.View, velocityXBuffer.View, velocityYBuffer.View, velocityZBuffer.View,
                    energy.View, airMaterial, density);

                Logger.Log("[AcousticSimulatorGPU] Copying energy data from GPU (75%)");
                energy.CopyToCPU(host);
                Logger.Log("[AcousticSimulatorGPU] Synchronizing GPU (100%)");
                accelerator.Synchronize();
            }
            Logger.Log("[AcousticSimulatorGPU] Energy computation complete");
            return host;
        }

        public void Reset()
        {
            Logger.Log("[AcousticSimulatorGPU] Resetting simulation state");
            pressureBuffer.MemSetToZero();
            previousPressureBuffer.MemSetToZero();
            velocityXBuffer.MemSetToZero();
            velocityYBuffer.MemSetToZero();
            velocityZBuffer.MemSetToZero();

            // Reset energy monitoring
            maxReceiverEnergy = 0;
            energyPeaked = false;

            // Reset step counter
            currentStep = 0;
            Logger.Log($"[AcousticSimulatorGPU] Reset complete, progress: 0/{totalSteps} (0%)");

            Logger.Log("[AcousticSimulatorGPU] Synchronizing GPU");
            accelerator.Synchronize();
        }
        #endregion

        #region GPU Kernels
        static void UpdatePressureKernel(
            Index1D idx,
            ArrayView1D<byte, Stride1D.Dense> mat,
            ArrayView1D<double, Stride1D.Dense> p,
            ArrayView1D<double, Stride1D.Dense> pPrev,
            byte air, int nx, int ny, int nz, float coeff)
        {
            int i = idx; int total = nx * ny * nz; if (i >= total) return;
            int z = i / (nx * ny); int r = i % (nx * ny); int y = r / nx; int x = r % nx;

            if (mat[i] != air) { p[i] = 0; return; }

            double div = 0;
            if (x > 0 && x < nx - 1) div += (p[i + 1] - p[i - 1]) * 0.5;
            if (y > 0 && y < ny - 1) div += (p[i + nx] - p[i - nx]) * 0.5;
            if (z > 0 && z < nz - 1) div += (p[i + nx * ny] - p[i - nx * ny]) * 0.5;

            double tmp = pPrev[i];
            pPrev[i] = p[i];
            p[i] = (2 * p[i] - tmp) + coeff * div;
        }

        static void UpdateVelocityKernel(
            Index1D idx,
            ArrayView1D<byte, Stride1D.Dense> mat,
            ArrayView1D<double, Stride1D.Dense> p,
            ArrayView1D<double, Stride1D.Dense> vx,
            ArrayView1D<double, Stride1D.Dense> vy,
            ArrayView1D<double, Stride1D.Dense> vz,
            byte air)
        {
            int i = idx; if (i >= mat.Length) return;
            if (mat[i] != air) { vx[i] = vy[i] = vz[i] = 0; return; }

            int dimXY = (int)Math.Sqrt(mat.Length);
            int nx = dimXY, ny = dimXY, nz = (int)(mat.Length / (nx * ny));
            int z = i / (nx * ny); int r = i % (nx * ny); int y = r / nx; int x = r % nx;

            if (x > 0 && x < nx - 1) vx[i] = -0.5 * (p[i + 1] - p[i - 1]);
            if (y > 0 && y < ny - 1) vy[i] = -0.5 * (p[i + nx] - p[i - nx]);
            if (z > 0 && z < nz - 1) vz[i] = -0.5 * (p[i + nx * ny] - p[i - nx * ny]);
        }

        static void ApplySourceKernel(
            Index1D idx,
            ArrayView1D<byte, Stride1D.Dense> mat,
            ArrayView1D<double, Stride1D.Dense> p,
            ArrayView1D<double, Stride1D.Dense> pPrev,
            ArrayView1D<double, Stride1D.Dense> src,
            byte air, double sx, double sy, double sz)
        {
            int i = idx; if (i >= mat.Length) return;
            int dimXY = (int)Math.Sqrt(mat.Length);
            int nx = dimXY, ny = dimXY, nz = (int)(mat.Length / (nx * ny));
            int z = i / (nx * ny); int r = i % (nx * ny); int y = r / nx; int x = r % nx;

            double d = Math.Sqrt((x - sx) * (x - sx) + (y - sy) * (y - sy) + (z - sz) * (z - sz));
            if (d < 2.0 && mat[i] == air)
            {
                double v = src[i % src.Length];
                p[i] += v; pPrev[i] += v;
            }
        }

        static void ApplyAbsorbingBoundaryKernel(
            Index1D idx,
            ArrayView1D<byte, Stride1D.Dense> mat,
            ArrayView1D<double, Stride1D.Dense> p,
            ArrayView1D<double, Stride1D.Dense> pPrev,
            ArrayView1D<double, Stride1D.Dense> v,
            byte absorber, double coeff, double dt)
        {
            int i = idx; if (i >= mat.Length) return;
            if (mat[i] == absorber) { p[i] = pPrev[i] * (1 - coeff); v[i] *= (1 - coeff); }
        }

        static void ComputeEnergyKernel(
            Index1D idx,
            ArrayView1D<byte, Stride1D.Dense> mat,
            ArrayView1D<float, Stride1D.Dense> props,
            ArrayView1D<double, Stride1D.Dense> p,
            ArrayView1D<double, Stride1D.Dense> vx,
            ArrayView1D<double, Stride1D.Dense> vy,
            ArrayView1D<double, Stride1D.Dense> vz,
            ArrayView1D<double, Stride1D.Dense> e,
            byte air, double rho)
        {
            int i = idx; if (i >= mat.Length) return;
            if (mat[i] == air)
            {
                double ke = 0.5 * rho * (vx[i] * vx[i] + vy[i] * vy[i] + vz[i] * vz[i]);
                double c = props[air];
                double pe = 0.5 * p[i] * p[i] / (c * c * rho);
                e[i] = ke + pe;
            }
            else e[i] = 0;
        }
        #endregion

        // -------------------------------------------------------------------- IDisposable ---
        public void Dispose()
        {
            Logger.Log("[AcousticSimulatorGPU] Disposing resources");
            materialBuffer.Dispose();
            pressureBuffer.Dispose();
            previousPressureBuffer.Dispose();
            velocityXBuffer.Dispose();
            velocityYBuffer.Dispose();
            velocityZBuffer.Dispose();
            materialPropertiesBuffer.Dispose();
            accelerator.Dispose();
            context.Dispose();
            Logger.Log("[AcousticSimulatorGPU] All resources disposed");
        }
    }
}
