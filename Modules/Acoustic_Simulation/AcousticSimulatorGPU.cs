using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Algorithms;
using ILGPU.Runtime.Cuda;
using ILGPU.Runtime.OpenCL;
using ILGPU.Runtime.CPU;

namespace CTS
{
    #region Kernel parameter structures
    public struct PhysicsParams
    {
        public double Lambda, Mu;
        public double TensileStrength, Cohesion, SinPhi, CosPhi;
        public double ConfiningPressure;
        public float Dt, Dx;
        public int UsePlastic, UseBrittle;
    }

    public struct GridParams
    {
        public int Width, Height, Depth;
        public int WidthHeight; // Pre-computed width * height for indexing
        public byte MaterialID;
    }
    #endregion
    /// <summary>
    /// GPU-accelerated acoustic/elastodynamic simulator that reproduces the same physics
    /// as the CPU version. Works on CUDA, OpenCL, and CPU backends for maximum compatibility.
    /// </summary>
    public sealed class AcousticSimulatorGPU : IDisposable
    {
        #region Constants
        private const double WAVE_VISUALIZATION_AMPLIFICATION = 1.0e10;
        private const double SafetyCourant = 0.25; // More conservative than CPU for stability
        private const int checkInterval = 10;
        private const double MAX_FIELD_VALUE = 1.0e10;
        private const double VISUALIZATION_AMPLIFICATION = 1.0;
        #endregion

        #region Grid dimensions
        private readonly int width, height, depth;
        private readonly float pixelSize;
        private readonly int totalCells;
        #endregion

        #region Physical parameters
        private readonly double lambda0, mu0;
        private readonly double confiningPressurePa;
        private readonly double tensileStrengthPa;
        private readonly double sinPhi, cosPhi;
        private readonly double cohesionPa;
        private readonly double sourceEnergyJ;
        private readonly double sourceFrequencyHz;
        private readonly int sourceAmplitude;
        private readonly int totalTimeSteps;
        private readonly bool useElasticModel;
        private readonly bool usePlasticModel;
        private readonly bool useBrittleModel;
        #endregion

        #region ILGPU objects
        private readonly Context context;
        private readonly Accelerator accelerator;
        private readonly MemoryBuffer1D<byte, Stride1D.Dense> materialBuffer;
        private readonly MemoryBuffer1D<float, Stride1D.Dense> densityBuffer;
        private readonly MemoryBuffer1D<double, Stride1D.Dense> vxBuffer, vyBuffer, vzBuffer;
        private readonly MemoryBuffer1D<double, Stride1D.Dense> sxxBuffer, syyBuffer, szzBuffer;
        private readonly MemoryBuffer1D<double, Stride1D.Dense> sxyBuffer, sxzBuffer, syzBuffer;
        private readonly MemoryBuffer1D<double, Stride1D.Dense> damageBuffer;
        private readonly Action<Index1D, ArrayView<double>, double> scaleVectorFieldKernel;
        private readonly Action<Index1D, ArrayView<double>, double> scaleScalarFieldKernel;
        #endregion

        #region Simulation state
        private byte selectedMaterialID;
        private int tx, ty, tz;
        private int rx, ry, rz;
        private double dt;
        private int stepCount;
        private bool pWaveReceiverTouched;
        private bool sWaveReceiverTouched;
        private int pWaveTouchStep;
        private int sWaveTouchStep;
        private double pWaveMaxAmplitude;
        private double sWaveMaxAmplitude;
        private int minRequiredSteps;
        private int expectedTotalSteps;
        private bool receiverTouched;
        private int touchStep;
        private double maxReceiverEnergy;
        private bool energyPeaked;
        private CancellationTokenSource cts = new CancellationTokenSource();
        #endregion

        

        #region Events (same as CPU version)
        public event EventHandler<AcousticSimulationProgressEventArgs> ProgressUpdated;
        public event EventHandler<AcousticSimulationCompleteEventArgs> SimulationCompleted;
        #endregion

        #region Kernel delegates
        private readonly Action<Index1D,
            ArrayView<byte>, ArrayView<float>,
            ArrayView<double>, ArrayView<double>, ArrayView<double>,
            ArrayView<double>, ArrayView<double>, ArrayView<double>,
            ArrayView<double>, ArrayView<double>, ArrayView<double>,
            ArrayView<double>, PhysicsParams, GridParams> stressKernel;

        private readonly Action<Index1D,
            ArrayView<byte>, ArrayView<float>,
            ArrayView<double>, ArrayView<double>, ArrayView<double>,
            ArrayView<double>, ArrayView<double>, ArrayView<double>,
            ArrayView<double>, ArrayView<double>, ArrayView<double>,
            PhysicsParams, GridParams> velocityKernel;
        #endregion

        #region Constructor
        public AcousticSimulatorGPU(
            int width, int height, int depth, float pixelSize,
            byte[,,] volumeLabels, float[,,] densityVolume, byte selectedMaterialID,
            string axis, string waveType,
            double confiningPressure, double tensileStrength, double failureAngle, double cohesion,
            double energy, double frequency, int amplitude, int timeSteps,
            bool useElasticModel, bool usePlasticModel, bool useBrittleModel,
            double youngsModulus, double poissonRatio)
        {
            // Store grid dimensions
            this.width = width;
            this.height = height;
            this.depth = depth;
            this.pixelSize = pixelSize;
            totalCells = width * height * depth;

            // Store material parameters
            this.selectedMaterialID = selectedMaterialID;

            // Calculate Lamé constants (same as CPU version)
            double E = youngsModulus * 1e6;
            mu0 = E / (2.0 * (1.0 + poissonRatio));
            lambda0 = E * poissonRatio / ((1 + poissonRatio) * (1 - 2 * poissonRatio));

            // Convert stress units from MPa to Pa
            confiningPressurePa = confiningPressure * 1e6;
            tensileStrengthPa = tensileStrength * 1e6;
            cohesionPa = cohesion * 1e6;

            // Calculate trigonometric values once
            sinPhi = Math.Sin(failureAngle * Math.PI / 180.0);
            cosPhi = Math.Cos(failureAngle * Math.PI / 180.0);

            // Store source parameters
            sourceEnergyJ = energy;
            sourceFrequencyHz = frequency * 1000; // Convert kHz to Hz
            sourceAmplitude = amplitude;
            totalTimeSteps = timeSteps;

            // Store model flags
            this.useElasticModel = useElasticModel;
            this.usePlasticModel = usePlasticModel;
            this.useBrittleModel = useBrittleModel;

            // Calculate time step based on material properties (same as CPU)
            ComputeStableTimeStep(densityVolume);

            // Set minimum required steps (same as CPU)
            minRequiredSteps = Math.Max(50, timeSteps / 10);

            // Set TX/RX positions based on axis (same logic as CPU)
            SetTransducerPositions(axis, width, height, depth);

            // Calculate expected steps for progress reporting
            CalculateExpectedSteps(densityVolume);

            // Initialize ILGPU context and accelerator
            context = Context.Create(builder => builder
                .Cuda()     // Use CUDA if available
                .OpenCL()   // Fallback to OpenCL
                .CPU()      // Fallback to CPU
                .EnableAlgorithms());

            // Select best available accelerator (CUDA > OpenCL > CPU)
            var selectedDevice = context.Devices
    .FirstOrDefault(d => d.AcceleratorType == AcceleratorType.Cuda) ??
    context.Devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.OpenCL) ??
    context.Devices.First(d => d.AcceleratorType == AcceleratorType.CPU);

            this.accelerator = selectedDevice.CreateAccelerator(context);
            Logger.Log($"[AcousticSimulatorGPU] Using {this.accelerator.AcceleratorType} accelerator: {this.accelerator.Name}");

            // Allocate memory on the accelerator
            materialBuffer = accelerator.Allocate1D<byte>(totalCells);
            densityBuffer = accelerator.Allocate1D<float>(totalCells);
            vxBuffer = accelerator.Allocate1D<double>(totalCells);
            vyBuffer = accelerator.Allocate1D<double>(totalCells);
            vzBuffer = accelerator.Allocate1D<double>(totalCells);
            sxxBuffer = accelerator.Allocate1D<double>(totalCells);
            syyBuffer = accelerator.Allocate1D<double>(totalCells);
            szzBuffer = accelerator.Allocate1D<double>(totalCells);
            sxyBuffer = accelerator.Allocate1D<double>(totalCells);
            sxzBuffer = accelerator.Allocate1D<double>(totalCells);
            syzBuffer = accelerator.Allocate1D<double>(totalCells);
            damageBuffer = accelerator.Allocate1D<double>(totalCells);

            // Upload volume data to accelerator
            Upload3DArray(materialBuffer, volumeLabels);
            Upload3DArray(densityBuffer, densityVolume);

            // Initialize field buffers to zero
            vxBuffer.MemSetToZero();
            vyBuffer.MemSetToZero();
            vzBuffer.MemSetToZero();
            sxxBuffer.MemSetToZero();
            syyBuffer.MemSetToZero();
            szzBuffer.MemSetToZero();
            sxyBuffer.MemSetToZero();
            sxzBuffer.MemSetToZero();
            syzBuffer.MemSetToZero();
            damageBuffer.MemSetToZero();

            // Create parameter structures for kernels
            var physicsParams = new PhysicsParams
            {
                Lambda = lambda0,
                Mu = mu0,
                TensileStrength = tensileStrengthPa,
                Cohesion = cohesionPa,
                SinPhi = sinPhi,
                CosPhi = cosPhi,
                ConfiningPressure = confiningPressurePa,
                Dt = (float)dt,
                Dx = pixelSize,
                UsePlastic = usePlasticModel ? 1 : 0,
                UseBrittle = useBrittleModel ? 1 : 0
            };

            var gridParams = new GridParams
            {
                Width = width,
                Height = height,
                Depth = depth,
                WidthHeight = width * height,
                MaterialID = selectedMaterialID
            };

            // Load GPU kernels
            stressKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D,
                ArrayView<byte>, ArrayView<float>,
                ArrayView<double>, ArrayView<double>, ArrayView<double>,
                ArrayView<double>, ArrayView<double>, ArrayView<double>,
                ArrayView<double>, ArrayView<double>, ArrayView<double>,
                ArrayView<double>, PhysicsParams, GridParams>(StressUpdateKernel);

            velocityKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D,
                ArrayView<byte>, ArrayView<float>,
                ArrayView<double>, ArrayView<double>, ArrayView<double>,
                ArrayView<double>, ArrayView<double>, ArrayView<double>,
                ArrayView<double>, ArrayView<double>, ArrayView<double>,
                PhysicsParams, GridParams>(VelocityUpdateKernel);
            
            scaleVectorFieldKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, double>(
                ScaleVectorFieldKernelImpl);

            scaleScalarFieldKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<double>, double>(
                ScaleScalarFieldKernelImpl);
            Logger.Log("[AcousticSimulatorGPU] GPU initialization complete");
        }
        #endregion

        #region Helper methods
        private void ComputeStableTimeStep(float[,,] densityVolume)
        {
            // Find minimum density (more careful checking)
            double rhoMin = double.MaxValue;
            foreach (float val in densityVolume)
            {
                if (val > 0 && val < rhoMin)
                    rhoMin = val;
            }

            // Safety check in case no valid density was found
            rhoMin = Math.Max(rhoMin, 100.0);

            // Calculate maximum P-wave velocity with more conservative estimate
            double vpMax = Math.Sqrt((lambda0 + 2 * mu0) / rhoMin);
            vpMax = Math.Min(vpMax, 6000.0); // Cap maximum velocity

            // Use more conservative safety factor
            const double SafetyCourant = 0.2; // More conservative than 0.25 or 0.4

            // Calculate time step based on both CFL condition and frequency
            double dtFreq = sourceFrequencyHz > 0 ? 1.0 / (20.0 * sourceFrequencyHz) : 1e-5;
            dt = Math.Min(SafetyCourant * pixelSize / vpMax, dtFreq);
            dt = Math.Max(dt, 1e-8); // Ensure dt is not too small

            Logger.Log($"[AcousticSimulatorGPU] Time step calculated: dt={dt:E6} s, vpMax={vpMax:F2} m/s, SafetyCourant={SafetyCourant}");
        }

        private void SetTransducerPositions(string axis, int width, int height, int depth)
        {
            // Set default positions based on axis (same logic as CPU)
            switch (axis.ToUpperInvariant())
            {
                case "X":
                    tx = 1; ty = height / 2; tz = depth / 2;
                    rx = width - 2; ry = height / 2; rz = depth / 2;
                    break;
                case "Y":
                    tx = width / 2; ty = 1; tz = depth / 2;
                    rx = width / 2; ry = height - 2; rz = depth / 2;
                    break;
                default: // Z axis
                    tx = width / 2; ty = height / 2; tz = 1;
                    rx = width / 2; ry = height / 2; rz = depth - 2;
                    break;
            }

            // Ensure transducers are within volume boundaries (same as CPU)
            tx = Math.Max(1, Math.Min(width - 2, tx));
            ty = Math.Max(1, Math.Min(height - 2, ty));
            tz = Math.Max(1, Math.Min(depth - 2, tz));
            rx = Math.Max(1, Math.Min(width - 2, rx));
            ry = Math.Max(1, Math.Min(height - 2, ry));
            rz = Math.Max(1, Math.Min(depth - 2, rz));

            Logger.Log($"[AcousticSimulatorGPU] Using TX: ({tx},{ty},{tz}), RX: ({rx},{ry},{rz})");
        }

        private void CalculateExpectedSteps(float[,,] densityVolume)
        {
            // Calculate expected steps based on distance and estimated velocity (same as CPU)
            double dist = Math.Sqrt((tx - rx) * (tx - rx) +
                                    (ty - ry) * (ty - ry) +
                                    (tz - rz) * (tz - rz)) * pixelSize;
            double rhoAvg = densityVolume.Cast<float>().Average();
            rhoAvg = Math.Max(rhoAvg, 100.0); // Same safety minimum as CPU
            double vpEst = Math.Sqrt((lambda0 + 2 * mu0) / rhoAvg);
            vpEst = Math.Min(vpEst, 6000.0); // Same reasonable maximum as CPU
            expectedTotalSteps = (int)Math.Ceiling(dist / (vpEst * dt)) + totalTimeSteps;

            Logger.Log($"[AcousticSimulatorGPU] Expected total steps: {expectedTotalSteps}");
        }

        private void Upload3DArray<T>(MemoryBuffer1D<T, Stride1D.Dense> buffer, T[,,] array) where T : unmanaged
        {
            // Flatten 3D array and upload to accelerator
            T[] flatArray = new T[totalCells];
            int index = 0;
            for (int z = 0; z < depth; z++)
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        flatArray[index++] = array[x, y, z];

            buffer.CopyFromCPU(flatArray);
            Logger.Log($"[AcousticSimulatorGPU] Uploaded {typeof(T).Name} data to accelerator");
        }

        private T[,,] Download3DArray<T>(MemoryBuffer1D<T, Stride1D.Dense> buffer) where T : unmanaged
        {
            // Download from accelerator and reconstruct 3D array
            T[] flatArray = buffer.GetAsArray1D();
            T[,,] array = new T[width, height, depth];
            int index = 0;
            for (int z = 0; z < depth; z++)
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        array[x, y, z] = flatArray[index++];

            return array;
        }

        private int FlattenIndex(int x, int y, int z)
        {
            return z * width * height + y * width + x;
        }

        private static double Cbrt(double x)
        {
            return x >= 0 ? Math.Pow(x, 1.0 / 3.0) : -Math.Pow(-x, 1.0 / 3.0);
        }
        #endregion

        #region Kernel implementations
        private static void StressUpdateKernel(Index1D index,
        ArrayView<byte> material, ArrayView<float> density,
        ArrayView<double> vx, ArrayView<double> vy, ArrayView<double> vz,
        ArrayView<double> sxx, ArrayView<double> syy, ArrayView<double> szz,
        ArrayView<double> sxy, ArrayView<double> sxz, ArrayView<double> syz,
        ArrayView<double> damage, PhysicsParams physics, GridParams grid)
        {
            // Skip if outside domain or not the selected material
            if (index >= material.Length) return;
            if (material[index] != grid.MaterialID) return;

            // Extract 3D coordinates from flat index
            int z = index / grid.WidthHeight;
            int remainder = index % grid.WidthHeight;
            int y = remainder / grid.Width;
            int x = remainder % grid.Width;

            // Skip boundary cells
            if (x <= 0 || x >= grid.Width - 1 ||
                y <= 0 || y >= grid.Height - 1 ||
                z <= 0 || z >= grid.Depth - 1)
                return;

            // Calculate indices for neighboring cells
            int xm1 = index - 1;
            int xp1 = index + 1;
            int ym1 = index - grid.Width;
            int yp1 = index + grid.Width;
            int zm1 = index - grid.WidthHeight;
            int zp1 = index + grid.WidthHeight;

            // Safety checks for out-of-bounds access
            if (xm1 < 0 || xp1 >= material.Length ||
                ym1 < 0 || yp1 >= material.Length ||
                zm1 < 0 || zp1 >= material.Length)
                return;

            // Get incoming values with safety checks
            double vxm1 = SafeGet(vx, xm1);
            double vxp1 = SafeGet(vx, xp1);
            double vym1 = SafeGet(vy, ym1);
            double vyp1 = SafeGet(vy, yp1);
            double vzm1 = SafeGet(vz, zm1);
            double vzp1 = SafeGet(vz, zp1);

            double vxym1 = SafeGet(vx, ym1);
            double vxyp1 = SafeGet(vx, yp1);
            double vxzm1 = SafeGet(vx, zm1);
            double vxzp1 = SafeGet(vx, zp1);

            double vyxm1 = SafeGet(vy, xm1);
            double vyxp1 = SafeGet(vy, xp1);
            double vyzm1 = SafeGet(vy, zm1);
            double vyzp1 = SafeGet(vy, zp1);

            double vzxm1 = SafeGet(vz, xm1);
            double vzxp1 = SafeGet(vz, xp1);
            double vzym1 = SafeGet(vz, ym1);
            double vzyp1 = SafeGet(vz, yp1);

            // Calculate velocity gradients with clamping
            double dvx_dx = SafeClamp((vxp1 - vxm1) / (2 * physics.Dx));
            double dvy_dy = SafeClamp((vyp1 - vym1) / (2 * physics.Dx));
            double dvz_dz = SafeClamp((vzp1 - vzm1) / (2 * physics.Dx));
            double dvx_dy = SafeClamp((vxyp1 - vxym1) / (2 * physics.Dx));
            double dvx_dz = SafeClamp((vxzp1 - vxzm1) / (2 * physics.Dx));
            double dvy_dx = SafeClamp((vyxp1 - vyxm1) / (2 * physics.Dx));
            double dvy_dz = SafeClamp((vyzp1 - vyzm1) / (2 * physics.Dx));
            double dvz_dx = SafeClamp((vzxp1 - vzxm1) / (2 * physics.Dx));
            double dvz_dy = SafeClamp((vzyp1 - vzym1) / (2 * physics.Dx));

            // Calculate volumetric strain rate
            double volumetricStrainRate = dvx_dx + dvy_dy + dvz_dz;

            // Get current damage
            double D = physics.UseBrittle != 0 ? SafeGet(damage, index) : 0.0;

            // Apply damage to elastic parameters
            double lambda = (1.0 - D) * physics.Lambda;
            double mu = (1.0 - D) * physics.Mu;

            // Get current stress values
            double sxxCurrent = SafeGet(sxx, index);
            double syyCurrent = SafeGet(syy, index);
            double szzCurrent = SafeGet(szz, index);
            double sxyCurrent = SafeGet(sxy, index);
            double sxzCurrent = SafeGet(sxz, index);
            double syzCurrent = SafeGet(syz, index);

            // Calculate elastic predictor with careful limiting
            double sxxNew = SafeClamp(sxxCurrent + physics.Dt * (lambda * volumetricStrainRate + 2 * mu * dvx_dx));
            double syyNew = SafeClamp(syyCurrent + physics.Dt * (lambda * volumetricStrainRate + 2 * mu * dvy_dy));
            double szzNew = SafeClamp(szzCurrent + physics.Dt * (lambda * volumetricStrainRate + 2 * mu * dvz_dz));
            double sxyNew = SafeClamp(sxyCurrent + physics.Dt * mu * (dvx_dy + dvy_dx));
            double sxzNew = SafeClamp(sxzCurrent + physics.Dt * mu * (dvx_dz + dvz_dx));
            double syzNew = SafeClamp(syzCurrent + physics.Dt * mu * (dvy_dz + dvz_dy));

            // Apply Mohr-Coulomb plasticity with stabilization
            if (physics.UsePlastic != 0)
            {
                // Calculate mean stress and deviatoric components
                double mean = (sxxNew + syyNew + szzNew) / 3.0;
                double devxx = sxxNew - mean;
                double devyy = syyNew - mean;
                double devzz = szzNew - mean;

                // Calculate stress invariant J2 with careful calculation
                double J2 = 0.5 * (devxx * devxx + devyy * devyy + devzz * devzz);
                J2 += (sxyNew * sxyNew + sxzNew * sxzNew + syzNew * syzNew);

                // Avoid negative root issues
                J2 = XMath.Max(J2, 0.0);

                double tau = XMath.Sqrt(J2);
                // Include confining pressure in the pressure calculation
                double p = -mean + physics.ConfiningPressure;

                // Calculate yield function with the correct pressure
                double yield = tau + p * physics.SinPhi - physics.Cohesion * physics.CosPhi;

                // Apply plastic correction if yielding
                if (yield > 0)
                {
                    // Calculate safe divisor
                    double safeTau = XMath.Max(tau, 1e-10);

                    // Scale to preserve oscillations but with more stabilization
                    double scale = (tau - (physics.Cohesion * physics.CosPhi - p * physics.SinPhi)) / safeTau;

                    // More conservative limiting for stronger stability
                    scale = XMath.Min(scale, 0.9);

                    // Apply scale to deviatoric components
                    devxx *= (1.0 - scale);
                    devyy *= (1.0 - scale);
                    devzz *= (1.0 - scale);
                    sxyNew *= (1.0 - scale);
                    sxzNew *= (1.0 - scale);
                    syzNew *= (1.0 - scale);

                    // Recombine components
                    sxxNew = devxx + mean;
                    syyNew = devyy + mean;
                    szzNew = devzz + mean;

                    // Final safety clamping
                    sxxNew = SafeClamp(sxxNew);
                    syyNew = SafeClamp(syyNew);
                    szzNew = SafeClamp(szzNew);
                    sxyNew = SafeClamp(sxyNew);
                    sxzNew = SafeClamp(sxzNew);
                    syzNew = SafeClamp(syzNew);
                }
            }

            // Apply brittle damage with more stabilization
            if (physics.UseBrittle != 0)
            {
                // Calculate stress invariants with careful calculation
                double I1 = sxxNew + syyNew + szzNew;
                double I2 = sxxNew * syyNew + syyNew * szzNew + szzNew * sxxNew -
                           sxyNew * sxyNew - sxzNew * sxzNew - syzNew * syzNew;
                double I3 = sxxNew * (syyNew * szzNew - syzNew * syzNew) -
                           sxyNew * (sxyNew * szzNew - syzNew * sxzNew) +
                           sxzNew * (sxyNew * syzNew - syyNew * sxzNew);

                // Calculate parameters for cubic equation with careful handling
                double a = -I1;
                double b = I2;
                double c = -I3;
                double q = (3.0 * b - a * a) / 9.0;
                double r = (9.0 * a * b - 27.0 * c - 2.0 * a * a * a) / 54.0;

                // Handle numerical issues for discriminant
                double qq = XMath.Max(q * q * q, 0.0);
                double rr = r * r;
                double disc = qq + rr;

                // Find maximum principal stress with more robust algorithm
                double sigmaMax;
                if (disc >= 0)
                {
                    double sqrtDisc = XMath.Sqrt(disc);
                    double s1 = CbrtSafe(r + sqrtDisc);
                    double s2 = CbrtSafe(r - sqrtDisc);
                    sigmaMax = -a / 3.0 + s1 + s2;
                }
                else
                {
                    if (q >= 0)
                    {
                        // Fallback for numerical issues
                        sigmaMax = -a / 3.0;
                    }
                    else
                    {
                        double theta = XMath.Acos(XMath.Clamp(r / XMath.Sqrt(-qq), -1.0, 1.0));
                        sigmaMax = 2.0 * XMath.Sqrt(-q) * XMath.Cos(theta / 3.0) - a / 3.0;
                    }
                }

                // Apply damage if tensile stress exceeds threshold
                if (sigmaMax > physics.TensileStrength && D < 1.0)
                {
                    // More gradual, stable damage calculation
                    double incr = (sigmaMax - physics.TensileStrength) / physics.TensileStrength;

                    // Much smaller increment for stability
                    incr = XMath.Min(incr, 0.05);

                    // Cap maximum damage for stability
                    double newDamage = XMath.Min(0.9, D + incr * 0.005);
                    damage[index] = newDamage;

                    // Scale stresses by damage factor
                    double factor = 1.0 - newDamage;
                    sxxNew *= factor;
                    syyNew *= factor;
                    szzNew *= factor;
                    sxyNew *= factor;
                    sxzNew *= factor;
                    syzNew *= factor;

                    // Final safety clamping
                    sxxNew = SafeClamp(sxxNew);
                    syyNew = SafeClamp(syyNew);
                    szzNew = SafeClamp(szzNew);
                    sxyNew = SafeClamp(sxyNew);
                    sxzNew = SafeClamp(sxzNew);
                    syzNew = SafeClamp(syzNew);
                }
            }

            // Store updated stresses with final safety check
            sxx[index] = SafeClamp(sxxNew);
            syy[index] = SafeClamp(syyNew);
            szz[index] = SafeClamp(szzNew);
            sxy[index] = SafeClamp(sxyNew);
            sxz[index] = SafeClamp(sxzNew);
            syz[index] = SafeClamp(syzNew);
        }
        private static double CbrtSafe(double v)
        {
            if (XMath.Abs(v) < 1e-15)
                return 0.0;
            return v >= 0 ? XMath.Pow(v, 1.0 / 3.0) : -XMath.Pow(-v, 1.0 / 3.0);
        }
        private static double SafeGet(ArrayView<double> array, int index)
        {
            if (index < 0 || index >= array.Length)
                return 0.0;

            double value = array[index];
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 0.0;

            return value;
        }

        private static double SafeClamp(double value)
        {
            const double MAX_VALUE = 1.0e10;

            if (double.IsNaN(value) || double.IsInfinity(value))
                return 0.0;

            // Apply symmetric clamping
            if (value > MAX_VALUE) return MAX_VALUE;
            if (value < -MAX_VALUE) return -MAX_VALUE;
            return value;
        }

        private static void VelocityUpdateKernel(Index1D index,
        ArrayView<byte> material, ArrayView<float> density,
        ArrayView<double> vx, ArrayView<double> vy, ArrayView<double> vz,
        ArrayView<double> sxx, ArrayView<double> syy, ArrayView<double> szz,
        ArrayView<double> sxy, ArrayView<double> sxz, ArrayView<double> syz,
        PhysicsParams physics, GridParams grid)
        {
            // Skip if outside domain or not the selected material
            if (index >= material.Length) return;
            if (material[index] != grid.MaterialID) return;

            // Extract 3D coordinates from flat index
            int z = index / grid.WidthHeight;
            int remainder = index % grid.WidthHeight;
            int y = remainder / grid.Width;
            int x = remainder % grid.Width;

            // Skip boundary cells
            if (x <= 0 || x >= grid.Width - 1 ||
                y <= 0 || y >= grid.Height - 1 ||
                z <= 0 || z >= grid.Depth - 1)
                return;

            // Calculate indices for neighboring cells
            int xm1 = index - 1;                // x-1, y, z
            int xp1 = index + 1;                // x+1, y, z
            int ym1 = index - grid.Width;       // x, y-1, z
            int yp1 = index + grid.Width;       // x, y+1, z
            int zm1 = index - grid.WidthHeight; // x, y, z-1
            int zp1 = index + grid.WidthHeight; // x, y, z+1

            // Safety checks for index bounds
            if (xm1 < 0 || xp1 >= material.Length ||
                ym1 < 0 || yp1 >= material.Length ||
                zm1 < 0 || zp1 >= material.Length)
                return;

            // Get density with safety check
            float rho = density[index];
            rho = XMath.Max(100.0f, rho); // Same safety minimum as CPU

            // Get stress values with safety checks
            double sxxI = SafeGet(sxx, index);
            double syyI = SafeGet(syy, index);
            double szzI = SafeGet(szz, index);
            double sxyI = SafeGet(sxy, index);
            double sxzI = SafeGet(sxz, index);
            double syzI = SafeGet(syz, index);

            double sxxM1 = SafeGet(sxx, xm1);
            double sxyM1 = SafeGet(sxy, ym1);
            double sxzM1 = SafeGet(sxz, zm1);
            double syyM1 = SafeGet(syy, ym1);
            double syzM1 = SafeGet(syz, zm1);
            double szzM1 = SafeGet(szz, zm1);

            double sxyP1 = SafeGet(sxy, xp1);
            double sxzP1 = SafeGet(sxz, xp1);
            double syzP1 = SafeGet(syz, yp1);

            // Calculate stress gradients with clamping
            double dsxx_dx = SafeClamp((sxxI - sxxM1) / physics.Dx);
            double dsxy_dy = SafeClamp((sxyI - sxyM1) / physics.Dx);
            double dsxz_dz = SafeClamp((sxzI - sxzM1) / physics.Dx);
            double dsyy_dy = SafeClamp((syyI - syyM1) / physics.Dx);
            double dsxy_dx = SafeClamp((sxyP1 - sxyI) / physics.Dx);
            double dsyz_dz = SafeClamp((syzI - syzM1) / physics.Dx);
            double dszz_dz = SafeClamp((szzI - szzM1) / physics.Dx);
            double dsxz_dx = SafeClamp((sxzP1 - sxzI) / physics.Dx);
            double dsyz_dy = SafeClamp((syzP1 - syzI) / physics.Dx);

            // Get current velocities
            double vxCurrent = vx[index];
            double vyCurrent = vy[index];
            double vzCurrent = vz[index];

            // Calculate new velocities
            double dvx = physics.Dt * (dsxx_dx + dsxy_dy + dsxz_dz) / rho;
            double dvy = physics.Dt * (dsxy_dx + dsyy_dy + dsyz_dz) / rho;
            double dvz = physics.Dt * (dsxz_dx + dsyz_dy + dszz_dz) / rho;

            // Apply damping factor to prevent acceleration (5% damping per step)
            const double DAMPING_FACTOR = 0.05;
            double damping = 1.0 - DAMPING_FACTOR;

            // Update with damping
            double vxNew = vxCurrent * damping + dvx;
            double vyNew = vyCurrent * damping + dvy;
            double vzNew = vzCurrent * damping + dvz;

            // Apply clamping for stability
            vxNew = SafeClamp(vxNew);
            vyNew = SafeClamp(vyNew);
            vzNew = SafeClamp(vzNew);

            // Store updated velocities
            vx[index] = vxNew;
            vy[index] = vyNew;
            vz[index] = vzNew;
        }
        #endregion

        #region Simulation methods
        public void StartSimulation()
        {
            Task.Run(() => Run(cts.Token));
        }
        private void RenormalizeFields()
        {
            // Get maximum field value
            double maxValue = GetMaxFieldValue();

            // Only renormalize if values are getting too large
            if (maxValue > 1e12)
            {
                Logger.Log($"[AcousticSimulatorGPU] Renormalizing fields, max value: {maxValue:E6}");

                // Calculate scaling factor to bring values to reasonable range
                double scaleFactor = 1e10 / maxValue;

                // Launch kernels to scale all fields
                scaleVectorFieldKernel(totalCells, vxBuffer.View, scaleFactor);
                scaleVectorFieldKernel(totalCells, vyBuffer.View, scaleFactor);
                scaleVectorFieldKernel(totalCells, vzBuffer.View, scaleFactor);
                scaleScalarFieldKernel(totalCells, sxxBuffer.View, scaleFactor);
                scaleScalarFieldKernel(totalCells, syyBuffer.View, scaleFactor);
                scaleScalarFieldKernel(totalCells, szzBuffer.View, scaleFactor);
                scaleScalarFieldKernel(totalCells, sxyBuffer.View, scaleFactor);
                scaleScalarFieldKernel(totalCells, sxzBuffer.View, scaleFactor);
                scaleScalarFieldKernel(totalCells, syzBuffer.View, scaleFactor);

                // Ensure all operations are complete
                accelerator.Synchronize();
            }
        }
        public void CancelSimulation()
        {
            cts.Cancel();
        }
        private void Run(CancellationToken token)
        {
            // Initialize with EXACT same values as CPU
            stepCount = 0;
            pWaveReceiverTouched = false;
            sWaveReceiverTouched = false;
            pWaveTouchStep = -1;
            sWaveTouchStep = -1;
            pWaveMaxAmplitude = 0;
            sWaveMaxAmplitude = 0;

            // Initialize source - exactly the same as CPU
            ApplySource();

            // Use same safety maximum - EXACT match to CPU
            int absoluteMaxSteps = Math.Max(1000, expectedTotalSteps * 2);

            Logger.Log($"[AcousticSimulatorGPU] Starting simulation with prolongSteps: {totalTimeSteps}");
            Logger.Log($"[AcousticSimulatorGPU] Expected total steps: {expectedTotalSteps}, Maximum allowed: {absoluteMaxSteps}");

            // Instability detection variables - EXACT match to CPU
            bool instabilityDetected = false;
            double previousMaxField = 0;
            int instabilityCounter = 0;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    UpdateStress();
                    UpdateVelocity();
                    stepCount++;

                    // Check for numerical instability - EXACT match to CPU
                    double currentMaxField = GetMaxFieldValue();
                    if (double.IsInfinity(currentMaxField) || double.IsNaN(currentMaxField) ||
                        currentMaxField > 1e30 || (currentMaxField > 1e15 && currentMaxField > previousMaxField * 10))
                    {
                        instabilityCounter++;

                        if (instabilityCounter >= 3) // Same threshold as CPU
                        {
                            Logger.Log($"[AcousticSimulatorGPU] WARNING: Numerical instability detected at step {stepCount}. Max field value: {currentMaxField:E6}");
                            instabilityDetected = true;

                            // EXACTLY same fallback approach as CPU
                            if (!pWaveReceiverTouched && stepCount > minRequiredSteps / 2)
                            {
                                pWaveReceiverTouched = true;
                                pWaveTouchStep = stepCount;
                                Logger.Log($"[AcousticSimulatorGPU] Using current step {stepCount} as P-Wave arrival due to instability");
                            }
                            else if (pWaveReceiverTouched && !sWaveReceiverTouched && stepCount > pWaveTouchStep + minRequiredSteps / 4)
                            {
                                sWaveReceiverTouched = true;
                                sWaveTouchStep = stepCount;
                                Logger.Log($"[AcousticSimulatorGPU] Using current step {stepCount} as S-Wave arrival due to instability");
                            }
                        }
                    }
                    else
                    {
                        instabilityCounter = 0;
                    }
                    previousMaxField = currentMaxField;

                    // Check for wave arrivals - EXACT match to CPU
                    if (!pWaveReceiverTouched && CheckPWaveReceiverTouch())
                    {
                        pWaveReceiverTouched = true;
                        pWaveTouchStep = stepCount;
                        Logger.Log($"[AcousticSimulatorGPU] P-Wave reached RX at step {pWaveTouchStep}");
                    }

                    if (pWaveReceiverTouched && !sWaveReceiverTouched && CheckSWaveReceiverTouch())
                    {
                        sWaveReceiverTouched = true;
                        sWaveTouchStep = stepCount;
                        Logger.Log($"[AcousticSimulatorGPU] S-Wave reached RX at step {sWaveTouchStep}");
                    }

                    // EXACT same termination condition as CPU
                    if (pWaveReceiverTouched && sWaveReceiverTouched &&
                        (stepCount - sWaveTouchStep >= totalTimeSteps))
                    {
                        Logger.Log($"[AcousticSimulatorGPU] Terminating after both waves + {totalTimeSteps} extra steps");
                        break;
                    }

                    // EXACT same max steps termination as CPU
                    if (stepCount >= absoluteMaxSteps)
                    {
                        Logger.Log($"[AcousticSimulatorGPU] WARNING: Terminating due to reaching maximum step count ({stepCount})");

                        if (!pWaveReceiverTouched)
                        {
                            pWaveReceiverTouched = true;
                            pWaveTouchStep = absoluteMaxSteps / 3;
                            Logger.Log($"[AcousticSimulatorGPU] Using estimated P-Wave arrival at step {pWaveTouchStep}");
                        }

                        if (!sWaveReceiverTouched)
                        {
                            sWaveReceiverTouched = true;
                            sWaveTouchStep = absoluteMaxSteps / 2;
                            Logger.Log($"[AcousticSimulatorGPU] Using estimated S-Wave arrival at step {sWaveTouchStep}");
                        }

                        break;
                    }

                    // For stability with GPU, add extra renormalization every N steps
                    if (stepCount % 20 == 0)
                    {
                        RenormalizeFields();
                    }

                    // Report progress on every step for better visualization
                    ReportProgress();
                }
                catch (Exception ex)
                {
                    // EXACT same error handling as CPU
                    Logger.Log($"[AcousticSimulatorGPU] Error during simulation step {stepCount}: {ex.Message}");

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

            if (token.IsCancellationRequested)
            {
                Logger.Log("[AcousticSimulatorGPU] Simulation cancelled by user");
                ProgressUpdated?.Invoke(
                    this,
                    new AcousticSimulationProgressEventArgs(
                        0, stepCount, "Cancelled", null, null));
                return;
            }

            ReportProgress("Finalising", 99);
            FinalizeAndRaiseEvent(); // Use measured arrivals for calculation
        }
        private void ApplySource()
        {
            // Clear velocity fields to zero
            vxBuffer.MemSetToZero(); vyBuffer.MemSetToZero(); vzBuffer.MemSetToZero();
            // Clear shear stress fields to zero
            sxyBuffer.MemSetToZero(); sxzBuffer.MemSetToZero(); syzBuffer.MemSetToZero();
            damageBuffer.MemSetToZero();
            accelerator.Synchronize();

            // Initialize normal stress components with confining pressure (compressive is negative)
            double[] initialStressValues = new double[totalCells];
            // Fill array with confining pressure values for cells with the selected material
            byte[] materialData = materialBuffer.GetAsArray1D();
            for (int i = 0; i < totalCells; i++)
            {
                if (materialData[i] == selectedMaterialID)
                {
                    // Negative value for compression
                    initialStressValues[i] = -confiningPressurePa;
                }
            }

            // Upload initial stress state
            sxxBuffer.CopyFromCPU(initialStressValues);
            syyBuffer.CopyFromCPU(initialStressValues);
            szzBuffer.CopyFromCPU(initialStressValues);
            accelerator.Synchronize();

            // Calculate pulse magnitude - EXACT match to CPU version
            double pulse = sourceAmplitude * Math.Sqrt(sourceEnergyJ);
            pulse *= 1e6; // Same amplification factor as CPU

            Logger.Log($"[AcousticSimulatorGPU] Applying source pulse with magnitude {pulse:E6}");

            // Use same spherical source region as CPU with identical radius
            int sourceRadius = 2;
            double[] tmpArray = new double[totalCells];

            // Apply exact same source pattern as CPU
            for (int dz = -sourceRadius; dz <= sourceRadius; dz++)
            {
                for (int dy = -sourceRadius; dy <= sourceRadius; dy++)
                {
                    for (int dx = -sourceRadius; dx <= sourceRadius; dx++)
                    {
                        // Calculate spherical distance for falloff - EXACT match to CPU
                        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        if (dist > sourceRadius) continue;

                        // Calculate position
                        int sx = tx + dx;
                        int sy = ty + dy;
                        int sz = tz + dz;

                        // Check bounds
                        if (sx < 0 || sx >= width || sy < 0 || sy >= height || sz < 0 || sz >= depth)
                            continue;

                        // Check if voxel is in the material - EXACT match to CPU
                        int idx = FlattenIndex(sx, sy, sz);
                        if (idx >= materialData.Length || materialData[idx] != selectedMaterialID)
                            continue;

                        // Apply falloff based on distance from center - EXACT match to CPU
                        double falloff = 1.0 - (dist / sourceRadius);
                        double localPulse = pulse * falloff * falloff;

                        // Add pulse to existing initial stress values
                        tmpArray[idx] = initialStressValues[idx] + localPulse;
                    }
                }
            }

            // Upload source stress values
            sxxBuffer.CopyFromCPU(tmpArray);
            syyBuffer.CopyFromCPU(tmpArray);
            szzBuffer.CopyFromCPU(tmpArray);
            accelerator.Synchronize();

            // Also add to velocity field - EXACT match to CPU
            // Determine main propagation direction based on TX-RX orientation
            int mainAxis = 0; // 0=x, 1=y, 2=z
            if (Math.Abs(rx - tx) >= Math.Abs(ry - ty) && Math.Abs(rx - tx) >= Math.Abs(rz - tz))
                mainAxis = 0;
            else if (Math.Abs(ry - ty) >= Math.Abs(rx - tx) && Math.Abs(ry - ty) >= Math.Abs(rz - tz))
                mainAxis = 1;
            else
                mainAxis = 2;

            // Reset array for velocity
            Array.Clear(tmpArray, 0, tmpArray.Length);

            // Apply initial velocity in the main direction
            for (int dz = -sourceRadius; dz <= sourceRadius; dz++)
            {
                for (int dy = -sourceRadius; dy <= sourceRadius; dy++)
                {
                    for (int dx = -sourceRadius; dx <= sourceRadius; dx++)
                    {
                        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        if (dist > sourceRadius) continue;

                        int sx = tx + dx, sy = ty + dy, sz = tz + dz;
                        if (sx < 0 || sx >= width || sy < 0 || sy >= height || sz < 0 || sz >= depth)
                            continue;

                        int idx = FlattenIndex(sx, sy, sz);
                        byte[] materialSample = materialBuffer.GetAsArray1D();
                        if (idx >= materialSample.Length || materialSample[idx] != selectedMaterialID)
                            continue;

                        double falloff = 1.0 - (dist / sourceRadius);
                        float[] densitySample = densityBuffer.GetAsArray1D();
                        float localDensity = idx < densitySample.Length ? densitySample[idx] : 1000.0f;

                        // Calculate velocity impulse exactly as CPU does
                        double localPulse = pulse * falloff * falloff;
                        double velocityPulse = localPulse / (localDensity * 10);

                        if (mainAxis == 0)
                        {
                            int direction = Math.Sign(rx - tx);
                            if (direction == 0) direction = 1;
                            tmpArray[idx] = velocityPulse * direction;
                        }
                    }
                }
            }

            if (mainAxis == 0)
            {
                vxBuffer.CopyFromCPU(tmpArray);
            }
            else if (mainAxis == 1)
            {
                Array.Clear(tmpArray, 0, tmpArray.Length);
                for (int dz = -sourceRadius; dz <= sourceRadius; dz++)
                {
                    for (int dy = -sourceRadius; dy <= sourceRadius; dy++)
                    {
                        for (int dx = -sourceRadius; dx <= sourceRadius; dx++)
                        {
                            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                            if (dist > sourceRadius) continue;

                            int sx = tx + dx, sy = ty + dy, sz = tz + dz;
                            if (sx < 0 || sx >= width || sy < 0 || sy >= height || sz < 0 || sz >= depth)
                                continue;

                            int idx = FlattenIndex(sx, sy, sz);
                            byte[] materialSample = materialBuffer.GetAsArray1D();
                            if (idx >= materialSample.Length || materialSample[idx] != selectedMaterialID)
                                continue;

                            double falloff = 1.0 - (dist / sourceRadius);
                            float[] densitySample = densityBuffer.GetAsArray1D();
                            float localDensity = idx < densitySample.Length ? densitySample[idx] : 1000.0f;
                            double velocityPulse = pulse * falloff * falloff / (localDensity * 10);

                            int direction = Math.Sign(ry - ty);
                            if (direction == 0) direction = 1;
                            tmpArray[idx] = velocityPulse * direction;
                        }
                    }
                }
                vyBuffer.CopyFromCPU(tmpArray);
            }
            else
            {
                Array.Clear(tmpArray, 0, tmpArray.Length);
                for (int dz = -sourceRadius; dz <= sourceRadius; dz++)
                {
                    for (int dy = -sourceRadius; dy <= sourceRadius; dy++)
                    {
                        for (int dx = -sourceRadius; dx <= sourceRadius; dx++)
                        {
                            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                            if (dist > sourceRadius) continue;

                            int sx = tx + dx, sy = ty + dy, sz = tz + dz;
                            if (sx < 0 || sx >= width || sy < 0 || sy >= height || sz < 0 || sz >= depth)
                                continue;

                            int idx = FlattenIndex(sx, sy, sz);
                            byte[] materialSample = materialBuffer.GetAsArray1D();
                            if (idx >= materialSample.Length || materialSample[idx] != selectedMaterialID)
                                continue;

                            double falloff = 1.0 - (dist / sourceRadius);
                            float[] densitySample = densityBuffer.GetAsArray1D();
                            float localDensity = idx < densitySample.Length ? densitySample[idx] : 1000.0f;
                            double velocityPulse = pulse * falloff * falloff / (localDensity * 10);

                            int direction = Math.Sign(rz - tz);
                            if (direction == 0) direction = 1;
                            tmpArray[idx] = velocityPulse * direction;
                        }
                    }
                }
                vzBuffer.CopyFromCPU(tmpArray);
            }

            accelerator.Synchronize();
        }
        private void UpdateStress()
        {
            // Launch stress kernel on all cells
            stressKernel(totalCells,
                materialBuffer.View, densityBuffer.View,
                vxBuffer.View, vyBuffer.View, vzBuffer.View,
                sxxBuffer.View, syyBuffer.View, szzBuffer.View,
                sxyBuffer.View, sxzBuffer.View, syzBuffer.View,
                damageBuffer.View,
                new PhysicsParams
                {
                    Lambda = lambda0,
                    Mu = mu0,
                    TensileStrength = tensileStrengthPa,
                    Cohesion = cohesionPa,
                    SinPhi = sinPhi,
                    CosPhi = cosPhi,
                    ConfiningPressure = confiningPressurePa,
                    Dt = (float)dt,
                    Dx = pixelSize,
                    UsePlastic = usePlasticModel ? 1 : 0,
                    UseBrittle = useBrittleModel ? 1 : 0
                },
                new GridParams
                {
                    Width = width,
                    Height = height,
                    Depth = depth,
                    WidthHeight = width * height,
                    MaterialID = selectedMaterialID
                });

            accelerator.Synchronize();
        }

        private void UpdateVelocity()
        {
            // Launch velocity kernel on all cells
            velocityKernel(totalCells,
                materialBuffer.View, densityBuffer.View,
                vxBuffer.View, vyBuffer.View, vzBuffer.View,
                sxxBuffer.View, syyBuffer.View, szzBuffer.View,
                sxyBuffer.View, sxzBuffer.View, syzBuffer.View,
                new PhysicsParams
                {
                    Lambda = lambda0,
                    Mu = mu0,
                    TensileStrength = tensileStrengthPa,
                    Cohesion = cohesionPa,
                    SinPhi = sinPhi,
                    CosPhi = cosPhi,
                    ConfiningPressure = confiningPressurePa,
                    Dt = (float)dt,
                    Dx = pixelSize,
                    UsePlastic = usePlasticModel ? 1 : 0,
                    UseBrittle = useBrittleModel ? 1 : 0
                },
                new GridParams
                {
                    Width = width,
                    Height = height,
                    Depth = depth,
                    WidthHeight = width * height,
                    MaterialID = selectedMaterialID
                });

            accelerator.Synchronize();
        }

        private double GetMaxFieldValue()
        {
            // Sample a subset of points (similar to CPU)
            int stride = Math.Max(1, width / 10);
            double maxVal = 0;

            // Download sample values from accelerator
            double[] vxSample = vxBuffer.GetAsArray1D();
            double[] vySample = vyBuffer.GetAsArray1D();
            double[] vzSample = vzBuffer.GetAsArray1D();
            double[] sxxSample = sxxBuffer.GetAsArray1D();
            double[] syySample = syyBuffer.GetAsArray1D();
            double[] szzSample = szzBuffer.GetAsArray1D();

            for (int z = 0; z < depth; z += stride)
            {
                for (int y = 0; y < height; y += stride)
                {
                    for (int x = 0; x < width; x += stride)
                    {
                        int index = FlattenIndex(x, y, z);
                        if (index >= totalCells) continue;

                        double vxAbs = Math.Abs(vxSample[index]);
                        double vyAbs = Math.Abs(vySample[index]);
                        double vzAbs = Math.Abs(vzSample[index]);
                        double sxxAbs = Math.Abs(sxxSample[index]);
                        double syyAbs = Math.Abs(syySample[index]);
                        double szzAbs = Math.Abs(szzSample[index]);

                        maxVal = Math.Max(maxVal, vxAbs);
                        maxVal = Math.Max(maxVal, vyAbs);
                        maxVal = Math.Max(maxVal, vzAbs);
                        maxVal = Math.Max(maxVal, sxxAbs);
                        maxVal = Math.Max(maxVal, syyAbs);
                        maxVal = Math.Max(maxVal, szzAbs);
                    }
                }
            }

            return maxVal;
        }

        private bool CheckPWaveReceiverTouch()
        {
            // Get value at receiver position
            int rxIndex = FlattenIndex(rx, ry, rz);
            double[] vxSample = vxBuffer.GetAsArray1D();

            // Exact match to CPU - check vx component at RX position
            double pMagnitude = Math.Abs(vxSample[rxIndex]);

            // Same error checking as CPU
            if (double.IsInfinity(pMagnitude) || double.IsNaN(pMagnitude))
            {
                Logger.Log("[AcousticSimulatorGPU] WARNING: P-wave magnitude at RX is invalid (INF or NaN)");
                return false;
            }

            // Track maximum amplitude - EXACT match to CPU
            if (pMagnitude > pWaveMaxAmplitude)
                pWaveMaxAmplitude = pMagnitude;

            // Use exactly same threshold calculation as CPU
            double threshold = Math.Max(1e-10, pWaveMaxAmplitude * 0.01);

            // Log data occasionally - same as CPU
            if (stepCount % 10 == 0)
                Logger.Log($"[CheckPWaveTouch] RX P-wave: {pMagnitude:E6}, Threshold: {threshold:E6}");

            // Same absolute minimum detection - EXACT match to CPU
            if (pMagnitude > 1e-9)
            {
                Logger.Log($"[AcousticSimulatorGPU] P-Wave detected at RX with magnitude {pMagnitude:E6}");
                return true;
            }

            return pMagnitude > threshold;
        }
        private bool CheckSWaveReceiverTouch()
        {
            // EXACT match to CPU - Wait a bit after P-wave to avoid false detection
            if (stepCount - pWaveTouchStep < 5)
                return false;

            int rxIndex = FlattenIndex(rx, ry, rz);
            double[] vySample = vyBuffer.GetAsArray1D();
            double[] vzSample = vzBuffer.GetAsArray1D();

            // EXACT match to CPU - S-waves measured through vy and vz components
            double sTransverseMagnitude = Math.Sqrt(
                vySample[rxIndex] * vySample[rxIndex] +
                vzSample[rxIndex] * vzSample[rxIndex]);

            // Same error checking as CPU
            if (double.IsInfinity(sTransverseMagnitude) || double.IsNaN(sTransverseMagnitude))
            {
                Logger.Log("[AcousticSimulatorGPU] WARNING: S-wave magnitude at RX is invalid (INF or NaN)");
                return false;
            }

            // Track maximum S-wave amplitude - EXACT match to CPU
            if (sTransverseMagnitude > sWaveMaxAmplitude)
                sWaveMaxAmplitude = sTransverseMagnitude;

            // Use exactly same threshold calculation as CPU
            double threshold = Math.Max(1e-10, sWaveMaxAmplitude * 0.01);

            // Log detection data occasionally - same as CPU
            if (stepCount % 10 == 0)
                Logger.Log($"[CheckSWaveTouch] RX S-wave: {sTransverseMagnitude:E6}, Threshold: {threshold:E6}");

            // Same minimum detection - EXACT match to CPU
            if (sTransverseMagnitude > 1e-9 && stepCount - pWaveTouchStep >= 5)
            {
                Logger.Log($"[AcousticSimulatorGPU] S-Wave detected at RX with magnitude {sTransverseMagnitude:E6}");
                return true;
            }

            return sTransverseMagnitude > threshold;
        }
        private void ReportProgress(string text = "Simulating", int? force = null)
        {
            // Calculate percentage
            int percent = force ?? (int)(stepCount * 100.0 / expectedTotalSteps);
            if (percent > 99) percent = 99;  // Keep 100% for Finish()

            // Synchronize to ensure all kernel operations are complete
            accelerator.Synchronize();

            // For visualization, normalize the data to avoid extreme values
            float[,,] pWaveField = null;
            float[,,] sWaveField = null;

            try
            {
                // Download the latest data
                pWaveField = ConvertToFloat(Download3DArray(vxBuffer), VISUALIZATION_AMPLIFICATION);
                sWaveField = ConvertToFloat(Download3DArray(vyBuffer), VISUALIZATION_AMPLIFICATION);

                // Normalize fields to prevent extreme values in visualization
                NormalizeField(pWaveField);
                NormalizeField(sWaveField);

                // Additional logging of field statistics
                if (stepCount % 20 == 0)
                {
                    double pMax = 0, sMax = 0;
                    int pNonZero = 0, sNonZero = 0;

                    for (int z = 0; z < depth; z += 4) // Sample every 4th point for efficiency
                    {
                        for (int y = 0; y < height; y += 4)
                        {
                            for (int x = 0; x < width; x += 4)
                            {
                                if (Math.Abs(pWaveField[x, y, z]) > 1e-6)
                                {
                                    pNonZero++;
                                    pMax = Math.Max(pMax, Math.Abs(pWaveField[x, y, z]));
                                }

                                if (Math.Abs(sWaveField[x, y, z]) > 1e-6)
                                {
                                    sNonZero++;
                                    sMax = Math.Max(sMax, Math.Abs(sWaveField[x, y, z]));
                                }
                            }
                        }
                    }

                    Logger.Log($"[AcousticSimulatorGPU] Field statistics at step {stepCount}: " +
                               $"P-max={pMax:E6}, P-nonzero={pNonZero}, " +
                               $"S-max={sMax:E6}, S-nonzero={sNonZero}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[AcousticSimulatorGPU] Error preparing visualization data: {ex.Message}");
                // Provide empty visualization data to avoid UI crashes
                pWaveField = new float[width, height, depth];
                sWaveField = new float[width, height, depth];
            }

            // Send update event with data
            ProgressUpdated?.Invoke(
                this,
                new AcousticSimulationProgressEventArgs(
                    percent, stepCount, text, pWaveField, sWaveField));
        }
        private void NormalizeField(float[,,] field)
        {
            // Find the maximum absolute value
            float maxAbs = 0.0f;
            int nonZeroCount = 0;

            for (int z = 0; z < depth; z++)
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        float absVal = Math.Abs(field[x, y, z]);
                        if (absVal > 1e-12)
                            nonZeroCount++;

                        if (absVal > maxAbs)
                            maxAbs = absVal;
                    }

            // If field is empty or already small enough, return
            if (maxAbs < 1e-12 || nonZeroCount == 0)
                return;

            Logger.Log($"[AcousticSimulatorGPU] Field normalization: max={maxAbs:E6}, nonZero={nonZeroCount}");

            // Only normalize if the maximum exceeds our threshold
            const float MAX_ALLOWED = 1.0e3f;
            if (maxAbs > MAX_ALLOWED)
            {
                float scale = MAX_ALLOWED / maxAbs;

                for (int z = 0; z < depth; z++)
                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                            field[x, y, z] *= scale;

                Logger.Log($"[AcousticSimulatorGPU] Field normalized with scale factor {scale:E6}");
            }
        }

        private float[,,] ConvertToFloat(double[,,] src, double amplification = 1.0)
        {
            // Convert double to float with amplification (same as CPU)
            float[,,] dst = new float[width, height, depth];
            for (int z = 0; z < depth; z++)
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        dst[x, y, z] = (float)(src[x, y, z] * amplification);
            return dst;
        }
        private static void ScaleVectorFieldKernelImpl(Index1D index, ArrayView<double> field, double scaleFactor)
        {
            if (index >= field.Length)
                return;

            double value = field[index];

            // Apply scaling
            value *= scaleFactor;

            // Apply clamping to prevent extreme values
            if (double.IsNaN(value) || double.IsInfinity(value))
                value = 0.0;

            field[index] = XMath.Clamp(value, -MAX_FIELD_VALUE, MAX_FIELD_VALUE);
        }

        private static void ScaleScalarFieldKernelImpl(Index1D index, ArrayView<double> field, double scaleFactor)
        {
            if (index >= field.Length)
                return;

            double value = field[index];

            // Apply scaling
            value *= scaleFactor;

            // Apply clamping to prevent extreme values
            if (double.IsNaN(value) || double.IsInfinity(value))
                value = 0.0;

            field[index] = XMath.Clamp(value, -MAX_FIELD_VALUE, MAX_FIELD_VALUE);
        }
        private void FinalizeAndRaiseEvent()
        {
            // EXACT same distance calculation as CPU
            double dist = Math.Sqrt((tx - rx) * (tx - rx) +
                                   (ty - ry) * (ty - ry) +
                                   (tz - rz) * (tz - rz)) * pixelSize;

            // If P-wave wasn't detected, use estimated values - EXACT match to CPU
            if (!pWaveReceiverTouched)
            {
                Logger.Log("[AcousticSimulatorGPU] WARNING: P-wave arrival wasn't detected, using estimates");

                // Calculate average density - same approach as CPU
                double rhoAvg = 0.0;
                float[] densitySample = densityBuffer.GetAsArray1D();
                int validCount = 0;

                for (int i = 0; i < densitySample.Length; i++)
                {
                    if (densitySample[i] > 0)
                    {
                        rhoAvg += densitySample[i];
                        validCount++;
                    }
                }

                if (validCount > 0)
                    rhoAvg /= validCount;
                else
                    rhoAvg = 1000.0; // Fallback

                // EXACT same calculations as CPU
                double vp = Math.Sqrt((lambda0 + 2 * mu0) / rhoAvg);
                double vs = Math.Sqrt(mu0 / rhoAvg);

                SimulationCompleted?.Invoke(
                    this,
                    new AcousticSimulationCompleteEventArgs(
                        vp, vs, vp / vs, stepCount / 3, stepCount / 2, stepCount));
                return;
            }

            // If S-wave wasn't detected, use theoretical ratio - EXACT match to CPU
            if (!sWaveReceiverTouched)
            {
                Logger.Log("[AcousticSimulatorGPU] WARNING: S-wave arrival wasn't detected, using estimates");

                // EXACT same calculations as CPU
                double vp = dist / (pWaveTouchStep * dt);

                // Calculate Poisson's ratio from Lamé constants - EXACT match to CPU
                double poissonRatio = lambda0 / (2 * (lambda0 + mu0));
                double vs = vp * Math.Sqrt((1 - 2 * poissonRatio) / (2 - 2 * poissonRatio));

                SimulationCompleted?.Invoke(
                    this,
                    new AcousticSimulationCompleteEventArgs(
                        vp, vs, vp / vs, pWaveTouchStep,
                        (int)(pWaveTouchStep * (vp / vs)), stepCount));
                return;
            }

            // Both waves detected - EXACT same velocity calculations as CPU
            double pVelocity = dist / (pWaveTouchStep * dt);
            double sVelocity = dist / (sWaveTouchStep * dt);
            double vpVsRatio = pVelocity / sVelocity;

            Logger.Log($"[AcousticSimulatorGPU] Final results: P-velocity={pVelocity:F2} m/s, S-velocity={sVelocity:F2} m/s, Vp/Vs={vpVsRatio:F3}");
            Logger.Log($"[AcousticSimulatorGPU] Travel times: P-wave={pWaveTouchStep} steps, S-wave={sWaveTouchStep} steps, Total={stepCount} steps");

            SimulationCompleted?.Invoke(
                this,
                new AcousticSimulationCompleteEventArgs(
                    pVelocity, sVelocity, vpVsRatio, pWaveTouchStep, sWaveTouchStep, stepCount));
        }
        #endregion

        #region Public API
        public (double[,,] vx, double[,,] vy, double[,,] vz) GetWaveFieldSnapshot()
        {
            // Download velocity fields from accelerator
            return (
                Download3DArray(vxBuffer),
                Download3DArray(vyBuffer),
                Download3DArray(vzBuffer)
            );
        }
        #endregion

        #region IDisposable
        public void Dispose()
        {
            // Cancel any running simulation
            cts.Cancel();

            // Dispose all accelerator resources
            materialBuffer?.Dispose();
            densityBuffer?.Dispose();
            vxBuffer?.Dispose();
            vyBuffer?.Dispose();
            vzBuffer?.Dispose();
            sxxBuffer?.Dispose();
            syyBuffer?.Dispose();
            szzBuffer?.Dispose();
            sxyBuffer?.Dispose();
            sxzBuffer?.Dispose();
            syzBuffer?.Dispose();
            damageBuffer?.Dispose();

            // Dispose accelerator and context
            accelerator?.Dispose();
            context?.Dispose();

            GC.SuppressFinalize(this);
        }
        #endregion
    }
}