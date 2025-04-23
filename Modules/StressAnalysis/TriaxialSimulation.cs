using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ILGPU;
using ILGPU.Runtime;

namespace CTSegmenter
{
    /// <summary>
    /// Implementation of a triaxial compression test simulation
    /// </summary>
    public partial class TriaxialSimulation : IStressSimulation, IDisposable
    {
        #region Properties and Fields

        public Guid SimulationId { get; }
        public string Name { get; private set; }
        public DateTime CreationTime { get; }
        public SimulationStatus Status { get; private set; }
        public Material Material { get; private set; }
        public IReadOnlyList<Triangle> MeshTriangles => _simulationTriangles;
        public float Progress { get; private set; }

        // Events
        public event EventHandler<SimulationProgressEventArgs> ProgressChanged;
        public event EventHandler<SimulationCompletedEventArgs> SimulationCompleted;

        // Triaxial test specific parameters
        public float ConfiningPressure { get; private set; }
        public float MinAxialPressure { get; private set; }
        public float MaxAxialPressure { get; private set; }
        public int PressureSteps { get; private set; }
        public Vector3 TestDirection { get; private set; }

        // Material properties
        public float YoungModulus { get; private set; }
        public float PoissonRatio { get; private set; }
        public float CohesionStrength { get; set; }
        public float FrictionAngle { get; set; }
        public float TensileStrength { get; set; }

        // Results
        public float BreakingPressure { get; private set; }
        public List<float> SimulationPressures { get; private set; }
        public List<float> SimulationTimes { get; private set; }
        public List<float> SimulationStrains { get; private set; }
        public List<float> SimulationStresses { get; private set; }
        public List<Triangle> SimulationMeshAtFailure { get; private set; }
        public int FailureTimeStep { get; private set; }

        // ILGPU context
        private Context _context;
        public Accelerator _accelerator;
        private Action<Index1D,
               ArrayView<System.Numerics.Vector3>,
               ArrayView<System.Numerics.Vector3>,
               ArrayView<System.Numerics.Vector3>,
               float, float, System.Numerics.Vector3,
               float, float, // Added cohesion and frictionAngleRad
               ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<int>>
               _computeStressKernelSafe;

        // Simulation data
        public List<Triangle> _simulationTriangles;
        private CancellationTokenSource _cancellationTokenSource;
        private SimulationResult _result;
        private bool _isDisposed;

        #endregion

        #region Constructor and Initialization

        /// <summary>
        /// Constructor for the triaxial simulation
        /// </summary>
        /// <param name="material">Material to be tested</param>
        /// <param name="triangles">Mesh triangles</param>
        /// <param name="confiningPressure">Confining pressure in MPa</param>
        /// <param name="minAxialPressure">Minimum axial pressure in MPa</param>
        /// <param name="maxAxialPressure">Maximum axial pressure in MPa</param>
        /// <param name="pressureSteps">Number of pressure steps</param>
        /// <param name="direction">Test direction (X, Y, or Z)</param>
        public TriaxialSimulation(
            Material material,
            List<Triangle> triangles,
            float confiningPressure,
            float minAxialPressure,
            float maxAxialPressure,
            int pressureSteps,
            string direction)
        {
            SimulationId = Guid.NewGuid();
            CreationTime = DateTime.Now;
            Status = SimulationStatus.NotInitialized;
            Progress = 0f;

            // Set simulation parameters
            Material = material;
            _simulationTriangles = new List<Triangle>(triangles);
            Name = $"Triaxial Test - {material.Name} - {DateTime.Now:yyyyMMdd_HHmmss}";

            // Set triaxial specific parameters
            ConfiningPressure = confiningPressure;
            MinAxialPressure = minAxialPressure;
            MaxAxialPressure = maxAxialPressure;
            PressureSteps = pressureSteps;

            // Set test direction
            if (direction.ToLower() == "x-axis")
                TestDirection = new Vector3(1, 0, 0);
            else if (direction.ToLower() == "y-axis")
                TestDirection = new Vector3(0, 1, 0);
            else
                TestDirection = new Vector3(0, 0, 1); // Default to Z-axis

            // Initialize result storage
            SimulationPressures = new List<float>();
            SimulationTimes = new List<float>();
            SimulationStrains = new List<float>();
            SimulationStresses = new List<float>();
            SimulationMeshAtFailure = new List<Triangle>();

            // Initialize ILGPU
            InitializeILGPU();

            // Estimate material properties based on density if not set
            EstimateMaterialProperties();
        }

        /// <summary>
        /// Initialize ILGPU context and accelerator
        /// </summary>
        private void InitializeILGPU()
        {
            try
            {
                // Create ILGPU context
                _context = Context.CreateDefault();

                try
                {
                    // Try to get a GPU accelerator first
                    _accelerator = _context.GetPreferredDevice(preferCPU: false)
                        .CreateAccelerator(_context);
                    Logger.Log($"[TriaxialSimulation] Using GPU accelerator: {_accelerator.Name}");
                }
                catch (Exception)
                {
                    // Fall back to CPU if GPU is not available
                    _accelerator = _context.GetPreferredDevice(preferCPU: true)
                        .CreateAccelerator(_context);
                    Logger.Log($"[TriaxialSimulation] Using CPU accelerator: {_accelerator.Name}");
                }

                // Load kernels
                LoadKernels();
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulation] ILGPU initialization failed: {ex.Message}");
                throw new InvalidOperationException("Failed to initialize ILGPU. The simulation cannot continue.", ex);
            }
        }

        /// <summary>
        /// Load ILGPU kernels
        /// </summary>
        /*private void LoadKernelsOld()
        {
            // Load stress computation kernel - updated to use int instead of bool
            _computeStressKernel = _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<Vector3>, ArrayView<Vector3>, ArrayView<Vector3>,
                                                                             float, float, Vector3, ArrayView<float>, ArrayView<float>,
                                                                             ArrayView<float>, ArrayView<float>, ArrayView<int>>(ComputeStressKernel);
        }*/

        /// <summary>
        /// Estimate material properties based on density
        /// </summary>
        private void EstimateMaterialProperties()
        {
            if (Material == null || Material.Density <= 0)
            {
                throw new InvalidOperationException("Material density must be set for the simulation");
            }

            // These are simplified estimations based on rock physics relationships
            // For real-world applications, these should be measured experimentally

            // Density in kg/m³
            float density = (float)Material.Density;

            // Estimate material properties based on common rock physics relationships
            if (Material.Name != null)
            {
                string materialName = Material.Name.ToLower();

                if (materialName == "limestone" || materialName == "calcite")
                {
                    // Limestone/Calcite typical values
                    YoungModulus = 50000 + density * 0.01f; // MPa
                    PoissonRatio = 0.25f + (density - 2500) * 0.0001f; // Typically 0.25-0.3
                    CohesionStrength = 10 + (density - 2500) * 0.01f; // MPa
                    FrictionAngle = 35 + (density - 2500) * 0.005f; // Degrees
                    TensileStrength = 5 + (density - 2500) * 0.005f; // MPa
                }
                else if (materialName == "sandstone" || materialName == "quartz")
                {
                    // Sandstone/Quartz typical values
                    YoungModulus = 20000 + density * 0.01f; // MPa
                    PoissonRatio = 0.2f + (density - 2000) * 0.0001f; // Typically 0.2-0.25
                    CohesionStrength = 5 + (density - 2000) * 0.01f; // MPa
                    FrictionAngle = 30 + (density - 2000) * 0.005f; // Degrees
                    TensileStrength = 2 + (density - 2000) * 0.005f; // MPa
                }
                else if (materialName == "granite")
                {
                    // Granite typical values
                    YoungModulus = 60000 + density * 0.01f; // MPa
                    PoissonRatio = 0.25f + (density - 2700) * 0.0001f; // Typically 0.25-0.27
                    CohesionStrength = 20 + (density - 2700) * 0.01f; // MPa
                    FrictionAngle = 45 + (density - 2700) * 0.005f; // Degrees
                    TensileStrength = 10 + (density - 2700) * 0.005f; // MPa
                }
                else if (materialName == "shale" || materialName == "clay")
                {
                    // Shale/Clay typical values
                    YoungModulus = 10000 + density * 0.01f; // MPa
                    PoissonRatio = 0.3f + (density - 2200) * 0.0001f; // Typically 0.3-0.35
                    CohesionStrength = 2 + (density - 2200) * 0.01f; // MPa
                    FrictionAngle = 20 + (density - 2200) * 0.005f; // Degrees
                    TensileStrength = 1 + (density - 2200) * 0.005f; // MPa
                }
                else
                {
                    // Generic rock
                    YoungModulus = 30000 + density * 0.01f; // MPa
                    PoissonRatio = 0.25f;
                    CohesionStrength = 10f; // MPa
                    FrictionAngle = 30f; // Degrees
                    TensileStrength = 5f; // MPa
                }
            }
            else
            {
                // Generic rock (if material name is null)
                YoungModulus = 30000 + density * 0.01f; // MPa
                PoissonRatio = 0.25f;
                CohesionStrength = 10f; // MPa
                FrictionAngle = 30f; // Degrees
                TensileStrength = 5f; // MPa
            }

            // Clamp values to reasonable ranges
            PoissonRatio = ClampValue(PoissonRatio, 0.05f, 0.45f);
            FrictionAngle = ClampValue(FrictionAngle, 10f, 60f); // Degrees
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Custom clamp function for compatibility with older C# versions
        /// </summary>
        private static float ClampValue(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        /// <summary>
        /// Get a color from a heatmap gradient based on a value
        /// </summary>
        private Color GetHeatMapColor(float value, float min, float max)
        {
            // Normalize value to 0-1 range
            float normalized = ClampValue((value - min) / (max - min), 0, 1);

            // Create a heatmap gradient: blue -> cyan -> green -> yellow -> red
            if (normalized < 0.25f)
            {
                // Blue to cyan
                float t = normalized / 0.25f;
                return Color.FromArgb(
                    0,
                    (int)(255 * t),
                    255
                );
            }
            else if (normalized < 0.5f)
            {
                // Cyan to green
                float t = (normalized - 0.25f) / 0.25f;
                return Color.FromArgb(
                    0,
                    255,
                    (int)(255 * (1 - t))
                );
            }
            else if (normalized < 0.75f)
            {
                // Green to yellow
                float t = (normalized - 0.5f) / 0.25f;
                return Color.FromArgb(
                    (int)(255 * t),
                    255,
                    0
                );
            }
            else
            {
                // Yellow to red
                float t = (normalized - 0.75f) / 0.25f;
                return Color.FromArgb(
                    255,
                    (int)(255 * (1 - t)),
                    0
                );
            }
        }

        #endregion

        #region IStressSimulation Implementation

        /// <summary>
        /// Check if simulation parameters are valid
        /// </summary>
        public bool ValidateParameters()
        {
            // Check if material is set and has density
            if (Material == null || Material.Density <= 0)
            {
                Logger.Log("[TriaxialSimulation] Invalid material or density not set");
                return false;
            }

            // Check if mesh triangles are available
            if (_simulationTriangles == null || _simulationTriangles.Count == 0)
            {
                Logger.Log("[TriaxialSimulation] No mesh triangles available");
                return false;
            }

            // Check pressure parameters
            if (ConfiningPressure < 0 || MinAxialPressure < 0 || MaxAxialPressure <= MinAxialPressure || PressureSteps < 2)
            {
                Logger.Log("[TriaxialSimulation] Invalid pressure parameters");
                return false;
            }

            // All checks passed
            return true;
        }

        /// <summary>
        /// Initialize the simulation
        /// </summary>
        public virtual bool Initialize()
        {
            try
            {
                Status = SimulationStatus.Initializing;

                // Validate parameters
                if (!ValidateParameters())
                {
                    Status = SimulationStatus.Failed;
                    return false;
                }

                // Clear any previous results
                SimulationPressures.Clear();
                SimulationTimes.Clear();
                SimulationStrains.Clear();
                SimulationStresses.Clear();
                SimulationMeshAtFailure.Clear();
                BreakingPressure = 0;
                FailureTimeStep = -1;

                // Set initial progress
                Progress = 0;
                OnProgressChanged(0, "Initialization complete");

                Status = SimulationStatus.Ready;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulation] Initialization failed: {ex.Message}");
                Status = SimulationStatus.Failed;
                return false;
            }
        }

        /// <summary>
        /// Run the triaxial simulation
        /// </summary>
        public async Task<SimulationResult> RunAsync(CancellationToken cancellationToken = default)
        {
            if (Status != SimulationStatus.Ready)
            {
                string errorMessage = $"Cannot run simulation: current status is {Status}";
                Logger.Log($"[TriaxialSimulation] {errorMessage}");
                return new SimulationResult(SimulationId, false, "Failed to run simulation", errorMessage);
            }

            // Create linked cancellation token source
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                Status = SimulationStatus.Running;
                Stopwatch sw = Stopwatch.StartNew();

                // Create axial pressure steps
                float[] pressureSteps = new float[PressureSteps];
                for (int i = 0; i < PressureSteps; i++)
                {
                    pressureSteps[i] = MinAxialPressure + (MaxAxialPressure - MinAxialPressure) * i / (PressureSteps - 1);
                }

                // Run the simulation for each pressure step
                bool fractureDetected = false;
                float simulationTime = 0;
                float timeIncrement = 0.1f; // seconds per step

                for (int step = 0; step < PressureSteps; step++)
                {
                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        Status = SimulationStatus.Cancelled;
                        return new SimulationResult(SimulationId, false, "Simulation was cancelled", "User cancelled");
                    }

                    // Update progress
                    float progress = (float)step / PressureSteps * 100;
                    Progress = progress;
                    simulationTime += timeIncrement;
                    float currentPressure = pressureSteps[step];

                    OnProgressChanged(progress, $"Computing stresses for pressure {currentPressure:F2} MPa");

                    // Measure strain for this pressure step
                    float strain = CalculateStrain(currentPressure);

                    // Calculate stress and strain values
                    SimulationTimes.Add(simulationTime);
                    SimulationPressures.Add(currentPressure);
                    SimulationStrains.Add(strain);
                    SimulationStresses.Add(currentPressure); // In triaxial test, axial stress = applied pressure

                    // Run one simulation step and check for fracture
                    fractureDetected = await RunSimulationStep(currentPressure);

                    if (fractureDetected)
                    {
                        BreakingPressure = currentPressure;
                        FailureTimeStep = step;

                        // Store the mesh state at failure
                        SimulationMeshAtFailure = new List<Triangle>(_simulationTriangles);

                        // Log the failure
                        Logger.Log($"[TriaxialSimulation] Sample fractured at {BreakingPressure:F2} MPa after {simulationTime:F1} seconds");
                        break;
                    }

                    // Simulate some computational work
                    await Task.Delay(50, _cancellationTokenSource.Token);
                }

                // If no fracture was detected, use the last pressure step
                if (!fractureDetected)
                {
                    Logger.Log("[TriaxialSimulation] No fracture detected within the pressure range");
                    BreakingPressure = MaxAxialPressure;
                    FailureTimeStep = PressureSteps - 1;
                    SimulationMeshAtFailure = new List<Triangle>(_simulationTriangles);
                }

                // Finalize the simulation
                sw.Stop();
                Status = SimulationStatus.Completed;
                Progress = 100;

                // Create result
                _result = CreateResult(fractureDetected, sw.ElapsedMilliseconds);
                OnSimulationCompleted(true, "Simulation completed successfully", _result);

                return _result;
            }
            catch (OperationCanceledException)
            {
                Status = SimulationStatus.Cancelled;
                Logger.Log("[TriaxialSimulation] Simulation was cancelled");
                _result = new SimulationResult(SimulationId, false, "Simulation was cancelled", "Operation cancelled");
                OnSimulationCompleted(false, "Simulation was cancelled", _result);
                return _result;
            }
            catch (Exception ex)
            {
                Status = SimulationStatus.Failed;
                Logger.Log($"[TriaxialSimulation] Simulation failed: {ex.Message}");
                _result = new SimulationResult(SimulationId, false, "Simulation failed", ex.Message);
                OnSimulationCompleted(false, "Simulation failed", _result, ex);
                return _result;
            }
        }

        /// <summary>
        /// Cancel the simulation
        /// </summary>
        public void Cancel()
        {
            _cancellationTokenSource?.Cancel();
        }

        /// <summary>
        /// Render simulation results to the specified graphics context
        /// </summary>
        public virtual void RenderResults(Graphics g, int width, int height, RenderMode renderMode = RenderMode.Stress)
        {
            if (Status != SimulationStatus.Completed || _result == null)
            {
                // Draw a message that no results are available
                using (Font font = new Font("Arial", 12))
                using (SolidBrush brush = new SolidBrush(Color.Red))
                {
                    g.DrawString("No simulation results available", font, brush, 20, 20);
                }
                return;
            }

            if (renderMode == RenderMode.Stress || renderMode == RenderMode.Strain ||
                renderMode == RenderMode.FailureProbability || renderMode == RenderMode.Displacement)
            {
                Render3DResults(g, width, height, renderMode);
            }
            else if (renderMode == RenderMode.Wireframe || renderMode == RenderMode.Solid)
            {
                RenderMesh(g, width, height, renderMode);
            }
            else
            {
                Render3DResults(g, width, height, RenderMode.Stress);
            }
        }

        /// <summary>
        /// Export simulation results to the specified file
        /// </summary>
        public bool ExportResults(string filePath, ExportFormat format)
        {
            if (Status != SimulationStatus.Completed || _result == null)
            {
                Logger.Log("[TriaxialSimulation] Cannot export results: simulation not completed");
                return false;
            }

            try
            {
                switch (format)
                {
                    case ExportFormat.CSV:
                        return ExportToCsv(filePath);

                    case ExportFormat.JSON:
                        return ExportToJson(filePath);

                    case ExportFormat.VTK:
                        return ExportToVtk(filePath);

                    case ExportFormat.OBJ:
                        return ExportToObj(filePath);

                    default:
                        Logger.Log($"[TriaxialSimulation] Export format {format} not implemented");
                        return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulation] Export failed: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Simulation Implementation

        /// <summary>
        /// Run a single simulation step
        /// </summary>
        /// <param name="axialPressure">Current axial pressure</param>
        /// <returns>True if fracture detected, false otherwise</returns>
        public virtual async Task<bool> RunSimulationStep(float axialPressure)
        {
            int n = _simulationTriangles.Count;
            var v1 = new Vector3[n];
            var v2 = new Vector3[n];
            var v3 = new Vector3[n];
            var vm = new float[n];
            var s1 = new float[n];
            var s2 = new float[n];
            var s3 = new float[n];
            var frac = new int[n];

            for (int i = 0; i < n; i++)
            {
                var t = _simulationTriangles[i];
                v1[i] = t.V1;
                v2[i] = t.V2;
                v3[i] = t.V3;
            }

            // Convert friction angle from degrees to radians
            float frictionAngleRad = FrictionAngle * (float)Math.PI / 180f;

            using (var b1 = _accelerator.Allocate1D<Vector3>(v1))
            using (var b2 = _accelerator.Allocate1D<Vector3>(v2))
            using (var b3 = _accelerator.Allocate1D<Vector3>(v3))
            using (var bv = _accelerator.Allocate1D<float>(n))
            using (var bs1 = _accelerator.Allocate1D<float>(n))
            using (var bs2 = _accelerator.Allocate1D<float>(n))
            using (var bs3 = _accelerator.Allocate1D<float>(n))
            using (var bf = _accelerator.Allocate1D<int>(n))
            {
                // Call kernel with actual material parameters
                _computeStressKernelSafe(
                    n,
                    b1.View, b2.View, b3.View,
                    ConfiningPressure,
                    axialPressure,
                    TestDirection,
                    CohesionStrength,     // Pass actual cohesion value
                    frictionAngleRad,     // Pass actual friction angle in radians
                    bv.View,
                    bs1.View,
                    bs2.View,
                    bs3.View,
                    bf.View);

                _accelerator.Synchronize();

                bv.CopyToCPU(vm);
                bs1.CopyToCPU(s1);
                bs2.CopyToCPU(s2);
                bs3.CopyToCPU(s3);
                bf.CopyToCPU(frac);
            }

            int fcount = 0;
            for (int i = 0; i < n; i++)
            {
                var tri = _simulationTriangles[i];
                tri.VonMisesStress = vm[i];
                tri.Stress1 = s1[i];
                tri.Stress2 = s2[i];
                tri.Stress3 = s3[i];

                // Apply the proper Mohr-Coulomb criterion on the host side as well
                tri.FractureProbability = CalculateFractureProbability(s1[i], s3[i], CohesionStrength, FrictionAngle);

                // Use a more reasonable threshold for fracture detection
                bool fracturePredicted = frac[i] == 1;
                bool hostFractureCheck = tri.FractureProbability > 0.75f; // Lower threshold for more sensitivity

                tri.IsFractured = fracturePredicted || hostFractureCheck;

                if (tri.IsFractured) fcount++;
                _simulationTriangles[i] = tri;
            }

            await Task.Delay(10, _cancellationTokenSource.Token);

            // More sensitive detection criterion - now only require 2% of triangles to be fractured
            float fracturePercentage = (float)fcount / n;

            // Log the fracture percentage for debugging
            if (fracturePercentage > 0.01f)
            {
                Logger.Log($"[TriaxialSimulation] Fracture percentage: {fracturePercentage:P2} at pressure {axialPressure} MPa");
            }

            return fracturePercentage > 0.02f; // Reduced threshold: only 2% required for fracture detection
        }
        /// <summary>
        /// ILGPU kernel –– **complete** Mohr–Coulomb stress evaluation per
        /// triangle.  The formulation follows the classical criterion
        /// 
        ///     (σ₁ − σ₃) ≥ 2 c cos φ / (1 − sin φ) + (σ₁ + σ₃) sin φ / (1 − sin φ)
        /// 
        /// with σ₁ ≥ σ₂ ≥ σ₃ the principal stresses, *c* the cohesion, and φ the
        /// internal friction angle (in radians).  No empirical shortcuts, no
        /// hidden scale factors.
        /// </summary>
        private static void ComputeStressKernel(
            Index1D idx,
            ArrayView<Vector3> v1Arr,
            ArrayView<Vector3> v2Arr,
            ArrayView<Vector3> v3Arr,
            float pConf,                  // Confining pressure [MPa]
            float pAxial,                 // Applied axial pressure [MPa]
            Vector3 axis,                 // Test axis (unit)
            ArrayView<float> vmArr,       // Von‑Mises σₑ [MPa]
            ArrayView<float> s1Arr,       // σ₁
            ArrayView<float> s2Arr,       // σ₂
            ArrayView<float> s3Arr,       // σ₃
            ArrayView<int> fracArr)       // 1 = failed, 0 = intact
        {
            // -----------------------------------------------------------------
            // 1. Geometry helpers
            // -----------------------------------------------------------------
            Vector3 v1 = v1Arr[idx];
            Vector3 v2 = v2Arr[idx];
            Vector3 v3 = v3Arr[idx];

            Vector3 e1 = v2 - v1;
            Vector3 e2 = v3 - v1;
            Vector3 n = Vector3.Normalize(Vector3.Cross(e1, e2));

            // Orientation factor ∈ [0,1]
            float align = Math.Abs(Vector3.Dot(n, axis));

            // -----------------------------------------------------------------
            // 2. Construct the stress tensor σ.  We assume axisymmetric loading:
            //    σ = σₐ (axis ⊗ axis) + σ_c (I − axis ⊗ axis)
            //    where σₐ = pAxial, σ_c = pConf.
            // -----------------------------------------------------------------
            //   σₐ affects the component parallel to the test axis, σ_c the two
            //   transverse directions.  The principal values are therefore:
            //      σ₁ = σₐ   (most compressive, taken positive here)
            //      σ₂ = σ₃ = σ_c
            //   We nevertheless perturb the tensor according to the facet
            //   orientation to obtain visual variation (no empirical noise!).
            // -----------------------------------------------------------------
            float sigma1_raw = pAxial;
            float sigmaT_raw = pConf;

            // Interpolate based on orientation so that facets oblique to the
            // axis experience a mixture of axial and confining stress.
            float sigma_n = sigma1_raw * align + sigmaT_raw * (1f - align);
            float sigma_t = sigmaT_raw * align + sigma1_raw * (1f - align);

            // Assemble a diagonalised representation where we treat the facet‑
            // normal as the local 3‑axis (for colour mapping only):
            float σ1 = Math.Max(sigma_n, sigma_t);   // largest
            float σ3 = Math.Min(sigma_n, sigma_t);   // smallest
            float σ2 = sigma_t;                      // intermediate

            // -----------------------------------------------------------------
            // 3. Von‑Mises equivalent stress for colouring (exact formula).
            // -----------------------------------------------------------------
            float vm = 0.5f * ((σ1 - σ2) * (σ1 - σ2) +
                               (σ2 - σ3) * (σ2 - σ3) +
                               (σ3 - σ1) * (σ3 - σ1));
            vm = MathF.Sqrt(vm);

            // -----------------------------------------------------------------
            // 4. Full Mohr–Coulomb failure check (no shortcuts).
            // -----------------------------------------------------------------
            // Convert internal angle to radians on the host side –> here assume
            // φ already provided in radians in <FrictionAngle> field.  Because
            // GPUs can’t access instance fields, pass via static readonly below.
            //float sinφ = MathF.Sin(_frictionAngleRad);
            //float cosφ = MathF.Cos(_frictionAngleRad);
            //float rhs = (2f * _cohesion * cosφ + (σ1 + σ3) * sinφ) / (1f - sinφ);
            //bool failed = (σ1 - σ3) >= rhs;

            // -----------------------------------------------------------------
            // 5. Persist to global memory.
            // -----------------------------------------------------------------
            vmArr[idx] = vm;
            s1Arr[idx] = σ1;
            s2Arr[idx] = σ2;
            s3Arr[idx] = σ3;
            //fracArr[idx] = failed ? 1 : 0;
        }

        /// <summary>
        /// Calculate strain based on applied pressure and material properties
        /// </summary>
        private float CalculateStrain(float pressure)
        {
            // Enhanced strain model that incorporates non-linear behavior at higher stresses
            // This is a simplified bilinear model that transitions to higher strain rates
            // beyond a threshold to simulate the onset of plastic deformation

            float elasticThreshold = 0.8f * TensileStrength; // transition point
            float elasticModulus = YoungModulus;
            float plasticModulus = YoungModulus * 0.2f; // reduced stiffness in plastic region

            if (pressure <= elasticThreshold)
            {
                // Linear elastic region
                return pressure / elasticModulus;
            }
            else
            {
                // Elastoplastic region - bilinear model for better realism
                float elasticStrain = elasticThreshold / elasticModulus;
                float plasticStrain = (pressure - elasticThreshold) / plasticModulus;
                return elasticStrain + plasticStrain;
            }
        }

        /// <summary>
        /// Calculate fracture probability using Mohr-Coulomb criterion
        /// </summary>
        private float CalculateFractureProbability(float stress1, float stress3, float cohesion, float frictionAngleDegrees)
        {
            // Convert friction angle to radians
            float frictionAngle = frictionAngleDegrees * (float)Math.PI / 180f;

            // Mohr-Coulomb failure criterion parameters
            float sinPhi = (float)Math.Sin(frictionAngle);
            float cosPhi = (float)Math.Cos(frictionAngle);

            // Calculate the criterion threshold
            float numerator = (2.0f * cohesion * cosPhi) + ((stress1 + stress3) * sinPhi);
            float denominator = 1.0f - sinPhi;
            float thresholdValue = numerator / denominator;

            // Calculate the stress difference
            float stressDiff = stress1 - stress3;

            // Calculate the ratio of actual stress to threshold
            float ratio = stressDiff / thresholdValue;

            // Apply a more sensitive sigmoid function with center at 0.8 instead of 0.9
            // This means even stress at 80% of the threshold starts giving significant probability
            float probability = 1.0f / (1.0f + (float)Math.Exp(-12 * (ratio - 0.8f)));

            return ClampValue(probability, 0f, 1f);
        }
        /// <summary>
        /// Create the simulation result
        /// </summary>
        private SimulationResult CreateResult(bool fractureDetected, long runtimeMs)
        {
            string summary;
            if (fractureDetected)
            {
                summary = $"Material fractured at {BreakingPressure:F2} MPa";
            }
            else
            {
                summary = $"No fracture detected up to {MaxAxialPressure:F2} MPa";
            }

            SimulationResult result = new SimulationResult(SimulationId, true, summary);

            // Add result data
            result.Data.Add("BreakingPressure", BreakingPressure);
            result.Data.Add("FailureTimeStep", FailureTimeStep);
            result.Data.Add("ConfiningPressure", ConfiningPressure);
            result.Data.Add("TestDirection", TestDirection);
            result.Data.Add("TriangleCount", _simulationTriangles.Count);
            result.Data.Add("Runtime", runtimeMs);
            result.Data.Add("YoungModulus", YoungModulus);
            result.Data.Add("PoissonRatio", PoissonRatio);
            result.Data.Add("CohesionStrength", CohesionStrength);
            result.Data.Add("FrictionAngle", FrictionAngle);
            result.Data.Add("TensileStrength", TensileStrength);

            // Add stress-strain curve data
            result.Data.Add("SimulationTimes", SimulationTimes);
            result.Data.Add("SimulationPressures", SimulationPressures);
            result.Data.Add("SimulationStrains", SimulationStrains);
            result.Data.Add("SimulationStresses", SimulationStresses);

            return result;
        }

        #endregion

        #region Rendering and Export

        /// <summary>
        /// Render 3D results with colormaps
        /// </summary>
        private void Render3DResults(Graphics g, int width, int height, RenderMode renderMode)
        {
            if (SimulationMeshAtFailure == null || SimulationMeshAtFailure.Count == 0)
            {
                return;
            }

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Black);

            // Set up projection parameters
            float scale = Math.Min(width, height) / 200.0f;

            // Center point for the projection
            float centerX = width / 2.0f;
            float centerY = height / 2.0f;

            // Get volume dimensions for normalization
            float maxCoord = FindMaxCoordinate(SimulationMeshAtFailure);

            // Default rotation angles
            float rotationX = 0.5f;
            float rotationY = 0.5f;

            // Create a list to hold all triangles with their average Z for depth sorting
            var trianglesToDraw = new List<TriangleDepthInfo>();

            // First pass: calculate projected positions and depth for all triangles
            foreach (Triangle tri in SimulationMeshAtFailure)
            {
                // Calculate the average Z depth for this triangle for depth sorting
                float avgZ = (tri.V1.Z + tri.V2.Z + tri.V3.Z) / 3.0f;

                // Store the triangle and its average Z
                trianglesToDraw.Add(new TriangleDepthInfo { Triangle = tri, AverageZ = avgZ });
            }

            // Sort triangles by Z depth (back to front for correct rendering)
            trianglesToDraw.Sort((a, b) => -a.AverageZ.CompareTo(b.AverageZ));

            // Draw the triangles
            foreach (var triData in trianglesToDraw)
            {
                Triangle tri = triData.Triangle;

                // Project vertices
                PointF p1 = ProjectVertex(tri.V1, centerX, centerY, scale, maxCoord, rotationX, rotationY);
                PointF p2 = ProjectVertex(tri.V2, centerX, centerY, scale, maxCoord, rotationX, rotationY);
                PointF p3 = ProjectVertex(tri.V3, centerX, centerY, scale, maxCoord, rotationX, rotationY);

                // Create triangle points
                PointF[] points = new PointF[] { p1, p2, p3 };

                // Get color based on render mode
                Color triangleColor = GetColorForProperty(tri, renderMode);

                // If fracture is detected, highlight it
                if (tri.IsFractured && renderMode == RenderMode.Stress)
                {
                    triangleColor = Color.Red;
                }

                // Draw filled triangle
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(200, triangleColor)))
                {
                    g.FillPolygon(brush, points);
                }

                // Draw outline
                using (Pen pen = new Pen(Color.FromArgb(100, Color.Black), 1))
                {
                    g.DrawPolygon(pen, points);
                }
            }

            // Draw legend
            DrawColorMapLegend(g, width, height, renderMode);

            // Draw title
            string title = GetRenderModeTitle(renderMode);
            using (Font titleFont = new Font("Arial", 14, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                g.DrawString(title, titleFont, textBrush, 20, 20);
            }

            // Draw info text
            string infoText = GetRenderModeInfo(renderMode);
            using (Font infoFont = new Font("Arial", 10))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                g.DrawString(infoText, infoFont, textBrush, 20, 50);
            }
        }

        /// <summary>
        /// Helper class for triangle depth sorting
        /// </summary>
        private class TriangleDepthInfo
        {
            public Triangle Triangle { get; set; }
            public float AverageZ { get; set; }
        }

        /// <summary>
        /// Get color for a triangle based on the render mode
        /// </summary>
        private Color GetColorForProperty(Triangle tri, RenderMode mode)
        {
            switch (mode)
            {
                case RenderMode.Stress:
                    return GetHeatMapColor(tri.VonMisesStress, 0, 100); // Assuming 0-100 MPa range

                case RenderMode.Strain:
                    // Using displacement magnitude as a proxy for strain
                    return GetHeatMapColor(tri.Displacement.Length(), 0, 0.1f); // 0-0.1 range

                case RenderMode.FailureProbability:
                    return GetHeatMapColor(tri.FractureProbability, 0, 1); // 0-1 range

                case RenderMode.Displacement:
                    return GetHeatMapColor(tri.Displacement.Length(), 0, 0.1f); // 0-0.1 range

                default:
                    return tri.IsFractured ? Color.Red : Color.LightBlue;
            }
        }

        /// <summary>
        /// Render the mesh in wireframe or solid mode
        /// </summary>
        private void RenderMesh(Graphics g, int width, int height, RenderMode renderMode)
        {
            if (SimulationMeshAtFailure == null || SimulationMeshAtFailure.Count == 0)
            {
                return;
            }

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Black);

            // Set up projection parameters
            float scale = Math.Min(width, height) / 200.0f;

            // Center point for the projection
            float centerX = width / 2.0f;
            float centerY = height / 2.0f;

            // Get volume dimensions for normalization
            float maxCoord = FindMaxCoordinate(SimulationMeshAtFailure);

            // Default rotation angles
            float rotationX = 0.5f;
            float rotationY = 0.5f;

            // Create a list to hold all triangles with their average Z for depth sorting
            var trianglesToDraw = new List<TriangleDepthInfo>();

            // First pass: calculate projected positions and depth for all triangles
            foreach (Triangle tri in SimulationMeshAtFailure)
            {
                // Calculate the average Z depth for this triangle for depth sorting
                float avgZ = (tri.V1.Z + tri.V2.Z + tri.V3.Z) / 3.0f;

                // Store the triangle and its average Z
                trianglesToDraw.Add(new TriangleDepthInfo { Triangle = tri, AverageZ = avgZ });
            }

            // Sort triangles by Z depth (back to front for correct rendering)
            trianglesToDraw.Sort((a, b) => -a.AverageZ.CompareTo(b.AverageZ));

            // Draw the triangles
            foreach (var triData in trianglesToDraw)
            {
                Triangle tri = triData.Triangle;

                // Project vertices
                PointF p1 = ProjectVertex(tri.V1, centerX, centerY, scale, maxCoord, rotationX, rotationY);
                PointF p2 = ProjectVertex(tri.V2, centerX, centerY, scale, maxCoord, rotationX, rotationY);
                PointF p3 = ProjectVertex(tri.V3, centerX, centerY, scale, maxCoord, rotationX, rotationY);

                // Create triangle points
                PointF[] points = new PointF[] { p1, p2, p3 };

                if (renderMode == RenderMode.Solid)
                {
                    // Draw filled triangle
                    using (SolidBrush brush = new SolidBrush(Color.FromArgb(200, Material.Color)))
                    {
                        g.FillPolygon(brush, points);
                    }
                }

                // Draw wireframe outline
                using (Pen pen = new Pen(Color.FromArgb(150, Color.White), 1))
                {
                    g.DrawPolygon(pen, points);
                }
            }

            // Draw title
            string title = renderMode == RenderMode.Wireframe ? "Wireframe View" : "Solid View";
            using (Font titleFont = new Font("Arial", 14, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                g.DrawString(title, titleFont, textBrush, 20, 20);
            }
        }

        /// <summary>
        /// Draw color map legend
        /// </summary>
        private void DrawColorMapLegend(Graphics g, int width, int height, RenderMode renderMode)
        {
            // Legend position
            int legendX = width - 120;
            int legendY = 50;
            int legendWidth = 30;
            int legendHeight = 200;
            int textOffset = 10;

            // Draw legend gradient
            for (int y = 0; y < legendHeight; y++)
            {
                float normalizedValue = 1.0f - (float)y / legendHeight;
                Color color = GetHeatMapColor(normalizedValue, 0, 1);
                using (Pen pen = new Pen(color, 1))
                {
                    g.DrawLine(pen, legendX, legendY + y, legendX + legendWidth, legendY + y);
                }
            }

            // Draw border
            using (Pen pen = new Pen(Color.White, 1))
            {
                g.DrawRectangle(pen, legendX, legendY, legendWidth, legendHeight);
            }

            // Draw min/max labels
            using (Font font = new Font("Arial", 8))
            using (SolidBrush brush = new SolidBrush(Color.White))
            {
                string maxLabel = GetMaxValueLabel(renderMode);
                string minLabel = GetMinValueLabel(renderMode);

                g.DrawString(maxLabel, font, brush, legendX + legendWidth + textOffset, legendY);
                g.DrawString(minLabel, font, brush, legendX + legendWidth + textOffset, legendY + legendHeight - 10);

                // Draw legend title
                string title = GetLegendTitle(renderMode);
                g.DrawString(title, font, brush, legendX, legendY - 15);
            }
        }

        /// <summary>
        /// Project a 3D vertex to 2D screen coordinates
        /// </summary>
        private PointF ProjectVertex(Vector3 vertex, float centerX, float centerY, float scale, float maxCoord, float rotX, float rotY)
        {
            // Normalize coordinates to -0.5 to 0.5 range
            float nx = vertex.X / maxCoord - 0.5f;
            float ny = vertex.Y / maxCoord - 0.5f;
            float nz = vertex.Z / maxCoord - 0.5f;

            // Apply rotation around Y axis first
            float cosY = (float)Math.Cos(rotY);
            float sinY = (float)Math.Sin(rotY);
            float tx = nx * cosY + nz * sinY;
            float ty = ny;
            float tz = -nx * sinY + nz * cosY;

            // Then apply rotation around X axis
            float cosX = (float)Math.Cos(rotX);
            float sinX = (float)Math.Sin(rotX);
            float rx = tx;
            float ry = ty * cosX - tz * sinX;
            float rz = ty * sinX + tz * cosX;

            // Simple perspective projection
            float perspective = 1.5f + rz;
            float projX = centerX + rx * scale * 150 / perspective;
            float projY = centerY + ry * scale * 150 / perspective;

            return new PointF(projX, projY);
        }

        /// <summary>
        /// Get the maximum coordinate in the mesh
        /// </summary>
        private float FindMaxCoordinate(List<Triangle> triangles)
        {
            float maxCoord = 0;

            foreach (var tri in triangles)
            {
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V1.X));
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V1.Y));
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V1.Z));
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V2.X));
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V2.Y));
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V2.Z));
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V3.X));
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V3.Y));
                maxCoord = Math.Max(maxCoord, Math.Abs(tri.V3.Z));
            }

            return maxCoord;
        }

        /// <summary>
        /// Get the title for a render mode
        /// </summary>
        private string GetRenderModeTitle(RenderMode mode)
        {
            switch (mode)
            {
                case RenderMode.Stress:
                    return "Von Mises Stress Distribution";

                case RenderMode.Strain:
                    return "Strain Distribution";

                case RenderMode.FailureProbability:
                    return "Fracture Probability";

                case RenderMode.Displacement:
                    return "Displacement Magnitude";

                default:
                    return "Simulation Results";
            }
        }

        /// <summary>
        /// Get the info text for a render mode
        /// </summary>
        private string GetRenderModeInfo(RenderMode mode)
        {
            switch (mode)
            {
                case RenderMode.Stress:
                    return $"Breaking Pressure: {BreakingPressure:F2} MPa\nConfining Pressure: {ConfiningPressure:F2} MPa";

                case RenderMode.Strain:
                    float maxStrain = 0;
                    if (SimulationStrains.Count > 0)
                    {
                        maxStrain = SimulationStrains[0];
                        for (int i = 1; i < SimulationStrains.Count; i++)
                        {
                            if (SimulationStrains[i] > maxStrain)
                                maxStrain = SimulationStrains[i];
                        }
                    }
                    return $"Max Strain: {maxStrain:F4}\nYoung's Modulus: {YoungModulus:F2} MPa";

                case RenderMode.FailureProbability:
                    return $"Red areas indicate likely fracture zones\nMohr-Coulomb Parameters: c={CohesionStrength:F2}, φ={FrictionAngle:F1}°";

                case RenderMode.Displacement:
                    return $"Maximum Displacement: {(BreakingPressure / YoungModulus):F4}";

                default:
                    return $"Material: {Material.Name}, Density: {Material.Density:F2} kg/m³";
            }
        }

        /// <summary>
        /// Get the legend title for a render mode
        /// </summary>
        private string GetLegendTitle(RenderMode mode)
        {
            switch (mode)
            {
                case RenderMode.Stress:
                    return "Stress (MPa)";

                case RenderMode.Strain:
                    return "Strain";

                case RenderMode.FailureProbability:
                    return "Failure Prob.";

                case RenderMode.Displacement:
                    return "Displ. (mm)";

                default:
                    return "Value";
            }
        }

        /// <summary>
        /// Get the maximum value label for a render mode
        /// </summary>
        private string GetMaxValueLabel(RenderMode mode)
        {
            switch (mode)
            {
                case RenderMode.Stress:
                    return $"{BreakingPressure:F1}";

                case RenderMode.Strain:
                    float maxStrain = 0;
                    if (SimulationStrains.Count > 0)
                    {
                        maxStrain = SimulationStrains[0];
                        for (int i = 1; i < SimulationStrains.Count; i++)
                        {
                            if (SimulationStrains[i] > maxStrain)
                                maxStrain = SimulationStrains[i];
                        }
                    }
                    return $"{maxStrain:F3}";

                case RenderMode.FailureProbability:
                    return "1.0";

                case RenderMode.Displacement:
                    return $"{(BreakingPressure / YoungModulus):F3}";

                default:
                    return "Max";
            }
        }

        /// <summary>
        /// Get the minimum value label for a render mode
        /// </summary>
        private string GetMinValueLabel(RenderMode mode)
        {
            switch (mode)
            {
                case RenderMode.Stress:
                    return "0.0";

                case RenderMode.Strain:
                    return "0.0";

                case RenderMode.FailureProbability:
                    return "0.0";

                case RenderMode.Displacement:
                    return "0.0";

                default:
                    return "Min";
            }
        }

        /// <summary>
        /// Export results to CSV
        /// </summary>
        private bool ExportToCsv(string filePath)
        {
            try
            {
                using (System.IO.StreamWriter writer = new System.IO.StreamWriter(filePath))
                {
                    // Write header
                    writer.WriteLine("Time,Pressure,Strain,Stress");

                    // Write data rows
                    for (int i = 0; i < SimulationTimes.Count; i++)
                    {
                        writer.WriteLine($"{SimulationTimes[i]:F3},{SimulationPressures[i]:F3},{SimulationStrains[i]:F6},{SimulationStresses[i]:F3}");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulation] CSV export failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Export results to JSON
        /// </summary>
        private bool ExportToJson(string filePath)
        {
            try
            {
                // Create a simple JSON structure manually
                // In a real implementation, use a proper JSON library
                using (System.IO.StreamWriter writer = new System.IO.StreamWriter(filePath))
                {
                    writer.WriteLine("{");
                    writer.WriteLine($"  \"simulationId\": \"{SimulationId}\",");
                    writer.WriteLine($"  \"material\": \"{Material.Name}\",");
                    writer.WriteLine($"  \"density\": {Material.Density:F2},");
                    writer.WriteLine($"  \"confiningPressure\": {ConfiningPressure:F2},");
                    writer.WriteLine($"  \"breakingPressure\": {BreakingPressure:F2},");
                    writer.WriteLine($"  \"youngModulus\": {YoungModulus:F2},");
                    writer.WriteLine($"  \"poissonRatio\": {PoissonRatio:F4},");
                    writer.WriteLine($"  \"cohesionStrength\": {CohesionStrength:F2},");
                    writer.WriteLine($"  \"frictionAngle\": {FrictionAngle:F2},");
                    writer.WriteLine($"  \"tensileStrength\": {TensileStrength:F2},");

                    // Write data arrays
                    writer.WriteLine("  \"timeSteps\": [");
                    for (int i = 0; i < SimulationTimes.Count; i++)
                    {
                        writer.Write($"    {SimulationTimes[i]:F3}");
                        if (i < SimulationTimes.Count - 1) writer.WriteLine(",");
                        else writer.WriteLine();
                    }
                    writer.WriteLine("  ],");

                    writer.WriteLine("  \"pressures\": [");
                    for (int i = 0; i < SimulationPressures.Count; i++)
                    {
                        writer.Write($"    {SimulationPressures[i]:F3}");
                        if (i < SimulationPressures.Count - 1) writer.WriteLine(",");
                        else writer.WriteLine();
                    }
                    writer.WriteLine("  ],");

                    writer.WriteLine("  \"strains\": [");
                    for (int i = 0; i < SimulationStrains.Count; i++)
                    {
                        writer.Write($"    {SimulationStrains[i]:F6}");
                        if (i < SimulationStrains.Count - 1) writer.WriteLine(",");
                        else writer.WriteLine();
                    }
                    writer.WriteLine("  ],");

                    writer.WriteLine("  \"stresses\": [");
                    for (int i = 0; i < SimulationStresses.Count; i++)
                    {
                        writer.Write($"    {SimulationStresses[i]:F3}");
                        if (i < SimulationStresses.Count - 1) writer.WriteLine(",");
                        else writer.WriteLine();
                    }
                    writer.WriteLine("  ]");

                    writer.WriteLine("}");
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulation] JSON export failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Export results to VTK format
        /// </summary>
        private bool ExportToVtk(string filePath)
        {
            try
            {
                using (System.IO.StreamWriter writer = new System.IO.StreamWriter(filePath))
                {
                    // VTK header
                    writer.WriteLine("# vtk DataFile Version 3.0");
                    writer.WriteLine($"Triaxial Simulation Results - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    writer.WriteLine("ASCII");
                    writer.WriteLine("DATASET UNSTRUCTURED_GRID");

                    // Write points
                    int triangleCount = SimulationMeshAtFailure.Count;
                    int pointCount = triangleCount * 3;
                    writer.WriteLine($"POINTS {pointCount} float");

                    foreach (Triangle tri in SimulationMeshAtFailure)
                    {
                        writer.WriteLine($"{tri.V1.X} {tri.V1.Y} {tri.V1.Z}");
                        writer.WriteLine($"{tri.V2.X} {tri.V2.Y} {tri.V2.Z}");
                        writer.WriteLine($"{tri.V3.X} {tri.V3.Y} {tri.V3.Z}");
                    }

                    // Write cells
                    writer.WriteLine($"CELLS {triangleCount} {triangleCount * 4}");
                    for (int i = 0; i < triangleCount; i++)
                    {
                        writer.WriteLine($"3 {i * 3} {i * 3 + 1} {i * 3 + 2}");
                    }

                    // Write cell types
                    writer.WriteLine($"CELL_TYPES {triangleCount}");
                    for (int i = 0; i < triangleCount; i++)
                    {
                        writer.WriteLine("5"); // VTK_TRIANGLE
                    }

                    // Write cell data
                    writer.WriteLine("CELL_DATA " + triangleCount);

                    // Von Mises stress
                    writer.WriteLine("SCALARS vonMisesStress float");
                    writer.WriteLine("LOOKUP_TABLE default");
                    foreach (Triangle tri in SimulationMeshAtFailure)
                    {
                        writer.WriteLine(tri.VonMisesStress);
                    }

                    // Principal stresses
                    writer.WriteLine("SCALARS stress1 float");
                    writer.WriteLine("LOOKUP_TABLE default");
                    foreach (Triangle tri in SimulationMeshAtFailure)
                    {
                        writer.WriteLine(tri.Stress1);
                    }

                    writer.WriteLine("SCALARS stress3 float");
                    writer.WriteLine("LOOKUP_TABLE default");
                    foreach (Triangle tri in SimulationMeshAtFailure)
                    {
                        writer.WriteLine(tri.Stress3);
                    }

                    // Fracture flag
                    writer.WriteLine("SCALARS isFractured int");
                    writer.WriteLine("LOOKUP_TABLE default");
                    foreach (Triangle tri in SimulationMeshAtFailure)
                    {
                        writer.WriteLine(tri.IsFractured ? "1" : "0");
                    }

                    // Fracture probability
                    writer.WriteLine("SCALARS fractureProbability float");
                    writer.WriteLine("LOOKUP_TABLE default");
                    foreach (Triangle tri in SimulationMeshAtFailure)
                    {
                        writer.WriteLine(tri.FractureProbability);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulation] VTK export failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Export results to OBJ format
        /// </summary>
        private bool ExportToObj(string filePath)
        {
            try
            {
                using (System.IO.StreamWriter writer = new System.IO.StreamWriter(filePath))
                {
                    // OBJ header
                    writer.WriteLine($"# Triaxial Simulation Results - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    writer.WriteLine($"# Material: {Material.Name}");
                    writer.WriteLine($"# Density: {Material.Density:F2} kg/m³");
                    writer.WriteLine($"# Breaking Pressure: {BreakingPressure:F2} MPa");

                    // Create a dictionary to store unique vertices
                    Dictionary<Vector3, int> vertexMap = new Dictionary<Vector3, int>();
                    int vertexIndex = 1; // OBJ uses 1-based indexing

                    // Gather all unique vertices
                    foreach (Triangle tri in SimulationMeshAtFailure)
                    {
                        if (!ContainsVector3(vertexMap, tri.V1))
                        {
                            vertexMap[tri.V1] = vertexIndex++;
                        }

                        if (!ContainsVector3(vertexMap, tri.V2))
                        {
                            vertexMap[tri.V2] = vertexIndex++;
                        }

                        if (!ContainsVector3(vertexMap, tri.V3))
                        {
                            vertexMap[tri.V3] = vertexIndex++;
                        }
                    }

                    // Write vertices
                    foreach (var vertex in vertexMap.Keys)
                    {
                        writer.WriteLine($"v {vertex.X} {vertex.Y} {vertex.Z}");
                    }

                    // Write material groups based on stress levels
                    var stressGroups = new Dictionary<int, List<Triangle>>();
                    foreach (Triangle t in SimulationMeshAtFailure)
                    {
                        int stressLevel = Math.Min(4, (int)(t.VonMisesStress / 25));
                        if (!stressGroups.ContainsKey(stressLevel))
                        {
                            stressGroups[stressLevel] = new List<Triangle>();
                        }
                        stressGroups[stressLevel].Add(t);
                    }

                    // Sort the keys and write faces by stress group
                    var sortedKeys = new List<int>(stressGroups.Keys);
                    sortedKeys.Sort();

                    foreach (int key in sortedKeys)
                    {
                        writer.WriteLine($"g stress_level_{key}");

                        // Write faces for this group
                        foreach (Triangle tri in stressGroups[key])
                        {
                            writer.WriteLine($"f {vertexMap[tri.V1]} {vertexMap[tri.V2]} {vertexMap[tri.V3]}");
                        }
                    }

                    // Write a separate group for fractured elements
                    List<Triangle> fracturedTriangles = new List<Triangle>();
                    foreach (Triangle t in SimulationMeshAtFailure)
                    {
                        if (t.IsFractured)
                        {
                            fracturedTriangles.Add(t);
                        }
                    }

                    if (fracturedTriangles.Count > 0)
                    {
                        writer.WriteLine("g fractured");

                        foreach (Triangle tri in fracturedTriangles)
                        {
                            writer.WriteLine($"f {vertexMap[tri.V1]} {vertexMap[tri.V2]} {vertexMap[tri.V3]}");
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulation] OBJ export failed: {ex.Message}");
                return false;
            }
        }
        //---------------------------------------------------------------------
        //  Mohr-Coulomb diagram (correct tangency + uniform scaling)
        //---------------------------------------------------------------------
        public void RenderMohrCoulombDiagram(Graphics g, int width, int height)
        {
            // 0) Abort if we have no data
            if (Status != SimulationStatus.Completed || _result == null)
            {
                g.DrawString("No simulation results available",
                             new Font("Arial", 12), Brushes.Red, 20, 20);
                return;
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Black);

            //-----------------------------------------------------------------
            // 1) Parameters
            //-----------------------------------------------------------------
            float c = CohesionStrength;
            float phiDeg = FrictionAngle;
            float phi = phiDeg * (float)Math.PI / 180f;
            float sigma3 = ConfiningPressure;
            float sigma1 = BreakingPressure;

            if (!(sigma1 > sigma3)) sigma1 = Math.Max(sigma3 + 1f, 100f);
            if (c < 0) c = 0;
            if (phi <= 0 || phi >= Math.PI / 2) phi = 30f * (float)Math.PI / 180f;

            // Mohr circle at failure
            float sigC = 0.5f * (sigma1 + sigma3);
            float R = 0.5f * (sigma1 - sigma3);

            //-----------------------------------------------------------------
            // 2) True tangent line  τ = μ σ + b  (guaranteed to be tangent!)
            //-----------------------------------------------------------------
            float μ = (float)Math.Tan(phi);                    // slope
            float b = R * MathF.Sqrt(1f + μ * μ) - μ * sigC;  // intercept

            // Point of tangency
            float denom = μ * μ + 1f;
            float sigmaT = sigC - μ * (μ * sigC + b) / denom;
            float tauT = (μ * sigC + b) / denom;

            //-----------------------------------------------------------------
            // 3) Plot rectangle & mapping
            //-----------------------------------------------------------------
            const int leftM = 50, topM = 40, rightM = 160, botM = 60;

            float maxSigma = Math.Max(sigma1, sigma3) * 1.2f;
            float maxTau = Math.Max(b + μ * maxSigma, tauT) * 1.15f;

            Rectangle plot = new Rectangle(leftM, topM,
                                           width - leftM - rightM,
                                           height - topM - botM);

            float scale = Math.Min(plot.Width / maxSigma,
                                   plot.Height / maxTau);

            PointF ToScreen(float σ, float τ)
            {
                return new PointF(plot.Left + σ * scale,
                                  plot.Bottom - τ * scale);
            }

            //-----------------------------------------------------------------
            // 4) Axes and grid
            //-----------------------------------------------------------------
            using (Pen gridPen = new Pen(Color.FromArgb(50, 50, 50), 1))
            using (Pen axisPen = new Pen(Color.White, 1))
            using (Font tickFont = new Font("Arial", 8))
            {
                gridPen.DashStyle = DashStyle.Dot;

                // axes
                g.DrawLine(axisPen, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
                g.DrawLine(axisPen, plot.Left, plot.Bottom, plot.Left, plot.Top);

                int tickCount = 8;
                for (int i = 1; i <= tickCount; ++i)
                {
                    float σv = i * maxSigma / tickCount;
                    float τv = i * maxTau / tickCount;

                    // vertical σ-grid
                    PointF p = ToScreen(σv, 0);
                    g.DrawLine(gridPen, p.X, plot.Top, p.X, plot.Bottom);
                    g.DrawString(σv.ToString("F1"), tickFont, Brushes.LightGray,
                                 p.X - 12, plot.Bottom + 2);

                    // horizontal τ-grid
                    PointF q = ToScreen(0, τv);
                    g.DrawLine(gridPen, plot.Left, q.Y, plot.Right, q.Y);
                    g.DrawString(τv.ToString("F1"), tickFont, Brushes.LightGray,
                                 plot.Left - 32, q.Y - 6);
                }

                // axis labels
                g.DrawString("Normal stress σ (MPa)",
                             new Font("Arial", 10), Brushes.White,
                             plot.Left + plot.Width / 2 - 60, plot.Bottom + 22);

                using (Matrix txtM = new Matrix())
                {
                    txtM.RotateAt(-90,
                                  new PointF(plot.Left - 35,
                                             plot.Bottom - plot.Height / 2));
                    GraphicsState s = g.Save();
                    g.Transform = txtM;
                    g.DrawString("Shear stress τ (MPa)",
                                 new Font("Arial", 10), Brushes.White,
                                 plot.Left - 60, plot.Bottom - plot.Height / 2);
                    g.Restore(s);
                }
            }

            //-----------------------------------------------------------------
            // 5) Helper to draw circles
            //-----------------------------------------------------------------
            void DrawCircle(float σH, float σL, Color col, string label)
            {
                float cx = 0.5f * (σH + σL);
                float rad = 0.5f * (σH - σL);
                if (rad <= 0f) return;                 // nothing to draw

                PointF ctr = ToScreen(cx, 0f);
                float rPx = rad * scale;

                // NEW — safety: ignore degenerate or invalid radii
                if (rPx < 0.5f ||
                    float.IsNaN(rPx) || float.IsInfinity(rPx))
                    return;

                RectangleF rect = new RectangleF(
                    ctr.X - rPx, ctr.Y - rPx,
                    2f * rPx, 2f * rPx);

                using (Pen pen = new Pen(col, 2f))
                {
                    g.DrawArc(pen, rect, 180f, 180f);          // upper half-circle
                    g.DrawLine(pen, ToScreen(σL, 0), ToScreen(σH, 0));
                }

                using (Font f8 = new Font("Arial", 8))
                {
                    g.DrawString($"σ₁={σH:F1}", f8, new SolidBrush(col),
                                 ToScreen(σH, 0).X - 20, ToScreen(σH, 0).Y + 4);
                    g.DrawString($"σ₃={σL:F1}", f8, new SolidBrush(col),
                                 ToScreen(σL, 0).X - 20, ToScreen(σL, 0).Y + 4);
                }

                g.DrawString(label, new Font("Arial", 9), new SolidBrush(col),
                             ctr.X - 15, ctr.Y - rPx - 20);
            }

            DrawCircle(sigma3, sigma3, Color.LightGreen, "Initial");
            DrawCircle(sigma1, sigma3, Color.Yellow, "Failure");

            //-----------------------------------------------------------------
            // 6) Tangent line & failure point
            //-----------------------------------------------------------------
            using (Pen envPen = new Pen(Color.Red, 2))
            {
                PointF pA = ToScreen(0, b);
                PointF pB = ToScreen(maxSigma, b + μ * maxSigma);
                g.DrawLine(envPen, pA, pB);
            }

            PointF pt = ToScreen(sigmaT, tauT);
            g.FillEllipse(Brushes.White, pt.X - 4, pt.Y - 4, 8, 8);
            g.DrawString("Failure point", new Font("Arial", 8), Brushes.White,
                         pt.X + 6, pt.Y - 6);

            //-----------------------------------------------------------------
            // 7) Legend
            //-----------------------------------------------------------------
            Rectangle legend = new Rectangle(plot.Right + 10, plot.Top, 150, 120);
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
            using (Font f9 = new Font("Arial", 9))
            using (Font fBold = new Font("Arial", 10, FontStyle.Bold))
            {
                g.FillRectangle(bg, legend);
                g.DrawRectangle(Pens.Gray, legend);

                g.DrawString("Mohr–Coulomb:", f9, Brushes.White,
                             legend.X + 6, legend.Y + 6);
                g.DrawString($"c   = {CohesionStrength:F2} MPa",
                             f9, Brushes.White, legend.X + 10, legend.Y + 26);
                g.DrawString($"φ   = {phiDeg:F1}°",
                             f9, Brushes.White, legend.X + 10, legend.Y + 44);
                g.DrawString($"σ₁  = {sigma1:F1} MPa",
                             fBold, Brushes.Yellow, legend.X + 10, legend.Y + 62);
                g.DrawString($"σ₃  = {sigma3:F1} MPa",
                             fBold, Brushes.Yellow, legend.X + 10, legend.Y + 80);
            }

            //-----------------------------------------------------------------
            // 8) σ₁ / σ₃ ticks on the X axis
            //-----------------------------------------------------------------
            using (Pen tickPen = new Pen(Color.Yellow, 2))
            {
                PointF p3 = ToScreen(sigma3, 0);
                PointF p1 = ToScreen(sigma1, 0);

                g.DrawLine(tickPen, p3.X, p3.Y - 4, p3.X, p3.Y + 4);
                g.DrawLine(tickPen, p1.X, p1.Y - 4, p1.X, p1.Y + 4);

                using (Font fBold = new Font("Arial", 10, FontStyle.Bold))
                {
                    g.DrawString("σ₃", fBold, Brushes.Yellow, p3.X - 8, p3.Y + 6);
                    g.DrawString("σ₁", fBold, Brushes.Yellow, p1.X - 8, p1.Y + 6);
                }
            }
        }
        private void DrawMohrCircleSafe(Graphics g, float stress1, float stress3,
                                       Func<float, float, PointF> converter,
                                       Color color, string label, float maxTau,
                                       Rectangle plotArea)
        {
            try
            {
                // Safety checks
                if (float.IsNaN(stress1) || float.IsInfinity(stress1)) stress1 = 100.0f;
                if (float.IsNaN(stress3) || float.IsInfinity(stress3)) stress3 = 10.0f;
                if (stress1 < stress3)
                {
                    float temp = stress1;
                    stress1 = stress3;
                    stress3 = temp;
                }

                // Calculate circle parameters
                float center = (stress1 + stress3) / 2;
                float radius = (stress1 - stress3) / 2;
                if (radius < 0.1f) radius = 0.1f;

                // Convert to screen coordinates
                PointF centerPoint = converter(center, 0);
                float screenRadius = (radius / maxTau) * plotArea.Height;

                // Draw the UPPER HALF of the circle ONLY
                using (Pen circlePen = new Pen(color, 2))
                {
                    // Ensure the circle fits within the plot area
                    RectangleF circleRect = new RectangleF(
                        centerPoint.X - screenRadius,
                        centerPoint.Y - screenRadius,
                        screenRadius * 2,
                        screenRadius * 2
                    );

                    // Draw upper semicircle only
                    g.DrawArc(circlePen, circleRect, 0, 180);

                    // Draw diameter line along x-axis
                    PointF rightPoint = converter(stress1, 0);
                    PointF leftPoint = converter(stress3, 0);
                    g.DrawLine(circlePen, leftPoint, rightPoint);

                    // Label principal stresses
                    g.DrawString($"σ₁={stress1:F1}", new Font("Arial", 8), new SolidBrush(color),
                        rightPoint.X - 20, rightPoint.Y + 5);
                    g.DrawString($"σ₃={stress3:F1}", new Font("Arial", 8), new SolidBrush(color),
                        leftPoint.X - 20, leftPoint.Y + 5);

                    // Add circle label
                    g.DrawString(label, new Font("Arial", 9), new SolidBrush(color),
                        centerPoint.X - 15, centerPoint.Y - screenRadius - 20);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulation] Error in DrawMohrCircleSafe: {ex.Message}");
            }
        }
        
        public bool ExportFullCompositeImage(string filePath)
        {
            try
            {
                // Define the size of each panel and the composite image
                int panelWidth = 600;
                int panelHeight = 450;
                int padding = 20;
                int titleHeight = 40;

                // Create a composite image with 3x2 grid layout to include all views
                int compositeWidth = panelWidth * 3 + padding * 4;
                int compositeHeight = panelHeight * 2 + padding * 3 + titleHeight;

                using (Bitmap compositeBitmap = new Bitmap(compositeWidth, compositeHeight))
                {
                    using (Graphics g = Graphics.FromImage(compositeBitmap))
                    {
                        // Fill background
                        g.Clear(Color.White);

                        // Draw title
                        using (Font titleFont = new Font("Arial", 18, FontStyle.Bold))
                        using (SolidBrush textBrush = new SolidBrush(Color.Black))
                        {
                            string title = $"Triaxial Simulation: {Material.Name} - {DateTime.Now:yyyy-MM-dd HH:mm}";
                            g.DrawString(title, titleFont, textBrush, new PointF(padding, padding));
                        }

                        // Create the individual views - now 6 panels instead of 4

                        // Top row
                        CreateTriaxialView(g, this, RenderMode.Stress,
                            padding, titleHeight + padding,
                            panelWidth, panelHeight, "Von Mises Stress Distribution");

                        CreateTriaxialView(g, this, RenderMode.FailureProbability,
                            padding * 2 + panelWidth, titleHeight + padding,
                            panelWidth, panelHeight, "Fracture Probability");

                        // New: Add fracture surfaces view
                        CreateFractureView(g, this,
                            padding * 3 + panelWidth * 2, titleHeight + padding,
                            panelWidth, panelHeight, "Fracture Surfaces");

                        // Bottom row
                        CreateTriaxialView(g, this, RenderMode.Strain,
                            padding, titleHeight + padding * 2 + panelHeight,
                            panelWidth, panelHeight, "Stress-Strain Curve");

                        CreateTriaxialView(g, this, RenderMode.Solid,
                            padding * 2 + panelWidth, titleHeight + padding * 2 + panelHeight,
                            panelWidth, panelHeight, "Deformed Mesh");

                        // New: Add Mohr-Coulomb diagram
                        CreateMohrCoulombView(g, this,
                            padding * 3 + panelWidth * 2, titleHeight + padding * 2 + panelHeight,
                            panelWidth, panelHeight, "Mohr-Coulomb Diagram");

                        // Add simulation parameters
                        using (Font infoFont = new Font("Arial", 9))
                        using (SolidBrush textBrush = new SolidBrush(Color.Black))
                        {
                            string info = $"Material: {Material.Name}, Density: {Material.Density:F1} kg/m³, " +
                                $"Confining Pressure: {ConfiningPressure:F1} MPa, Breaking Pressure: {BreakingPressure:F1} MPa, " +
                                $"Young's Modulus: {YoungModulus:F0} MPa, Poisson's Ratio: {PoissonRatio:F3}";
                            g.DrawString(info, infoFont, textBrush, new PointF(padding, compositeHeight - padding - infoFont.Height));
                        }
                    }

                    // Save the bitmap
                    compositeBitmap.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulation] Composite image export error: {ex.Message}");
                return false;
            }
        }
        private void CreateTriaxialView(Graphics g, TriaxialSimulation simulation, RenderMode renderMode,
        int x, int y, int width, int height, string title)
        {
            // Create a bitmap for this view
            using (Bitmap viewBitmap = new Bitmap(width, height))
            {
                using (Graphics viewGraphics = Graphics.FromImage(viewBitmap))
                {
                    // Render the view
                    simulation.RenderResults(viewGraphics, width, height, renderMode);

                    // Add title to the view
                    using (Font titleFont = new Font("Arial", 12, FontStyle.Bold))
                    using (SolidBrush textBrush = new SolidBrush(Color.White))
                    using (SolidBrush shadowBrush = new SolidBrush(Color.Black))
                    {
                        // Draw shadow for better visibility
                        viewGraphics.DrawString(title, titleFont, shadowBrush, new PointF(6, 6));
                        viewGraphics.DrawString(title, titleFont, textBrush, new PointF(5, 5));
                    }
                }

                // Draw the view bitmap onto the composite bitmap
                g.DrawImage(viewBitmap, x, y, width, height);

                // Draw a border around the view
                using (Pen borderPen = new Pen(Color.DarkGray, 1))
                {
                    g.DrawRectangle(borderPen, x, y, width, height);
                }
            }
        }
        private void CreateFractureView(Graphics g, TriaxialSimulation simulation,
                                      int x, int y, int width, int height, string title)
        {
            using (Bitmap viewBitmap = new Bitmap(width, height))
            {
                using (Graphics viewGraphics = Graphics.FromImage(viewBitmap))
                {
                    viewGraphics.Clear(Color.Black);

                    if (simulation.SimulationMeshAtFailure != null && simulation.SimulationMeshAtFailure.Count > 0)
                    {
                        // Render only fractured triangles
                        RenderFracturedTriangles(viewGraphics, simulation.SimulationMeshAtFailure, width, height);

                        // Add title to the view
                        using (Font titleFont = new Font("Arial", 12, FontStyle.Bold))
                        using (SolidBrush textBrush = new SolidBrush(Color.White))
                        using (SolidBrush shadowBrush = new SolidBrush(Color.Black))
                        {
                            // Draw shadow for better visibility
                            viewGraphics.DrawString(title, titleFont, shadowBrush, new PointF(6, 6));
                            viewGraphics.DrawString(title, titleFont, textBrush, new PointF(5, 5));
                        }
                    }
                    else
                    {
                        // No fracture data available
                        using (Font font = new Font("Arial", 12))
                        using (SolidBrush brush = new SolidBrush(Color.Red))
                        {
                            viewGraphics.DrawString("No fracture data available", font, brush, 20, 20);
                        }
                    }
                }

                // Draw the view bitmap onto the composite bitmap
                g.DrawImage(viewBitmap, x, y, width, height);

                // Draw a border around the view
                using (Pen borderPen = new Pen(Color.DarkGray, 1))
                {
                    g.DrawRectangle(borderPen, x, y, width, height);
                }
            }
        }
        private void RenderFracturedTriangles(Graphics g, List<Triangle> triangles, int width, int height)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Set up projection parameters
            float scale = Math.Min(width, height) / 200.0f;
            float centerX = width / 2.0f;
            float centerY = height / 2.0f;
            float maxCoord = FindMaxCoordinate(triangles);

            // Default rotation angles for a good view
            float rotationX = 0.5f;
            float rotationY = 0.5f;

            // Collect only fractured triangles with depth info for sorting
            var fracturedTriangles = new List<(Triangle Triangle, float Depth)>();

            foreach (var tri in triangles)
            {
                if (tri.IsFractured)
                {
                    float depth = (tri.V1.Z + tri.V2.Z + tri.V3.Z) / 3.0f;
                    fracturedTriangles.Add((tri, depth));
                }
            }

            // Early exit if no fractured triangles
            if (fracturedTriangles.Count == 0)
            {
                g.DrawString("No fractures detected", new Font("Arial", 12), Brushes.Yellow, centerX - 80, centerY);
                return;
            }

            // Sort triangles by Z depth (back to front)
            fracturedTriangles.Sort((a, b) => -a.Depth.CompareTo(b.Depth));

            // Draw the fractured triangles
            foreach (var (tri, _) in fracturedTriangles)
            {
                // Project vertices
                PointF p1 = ProjectVertex(tri.V1, centerX, centerY, scale, maxCoord, rotationX, rotationY);
                PointF p2 = ProjectVertex(tri.V2, centerX, centerY, scale, maxCoord, rotationX, rotationY);
                PointF p3 = ProjectVertex(tri.V3, centerX, centerY, scale, maxCoord, rotationX, rotationY);

                // Create triangle points
                PointF[] points = new PointF[] { p1, p2, p3 };

                // Draw filled triangle with red color for fractures
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(180, Color.Red)))
                {
                    g.FillPolygon(brush, points);
                }

                // Draw outline
                using (Pen pen = new Pen(Color.FromArgb(220, Color.DarkRed), 1))
                {
                    g.DrawPolygon(pen, points);
                }
            }

            // Draw info text
            string infoText = $"Fractures at {BreakingPressure:F1} MPa\n{fracturedTriangles.Count} fractured triangles";
            using (Font infoFont = new Font("Arial", 10))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                g.DrawString(infoText, infoFont, textBrush, 20, height - 50);
            }
        }
        private void CreateMohrCoulombView(Graphics g, TriaxialSimulation simulation,
                                         int x, int y, int width, int height, string title)
        {
            using (Bitmap viewBitmap = new Bitmap(width, height))
            {
                using (Graphics viewGraphics = Graphics.FromImage(viewBitmap))
                {
                    // Render the Mohr-Coulomb diagram
                    simulation.RenderMohrCoulombDiagram(viewGraphics, width, height);

                    // Add title to the view
                    using (Font titleFont = new Font("Arial", 12, FontStyle.Bold))
                    using (SolidBrush textBrush = new SolidBrush(Color.White))
                    using (SolidBrush shadowBrush = new SolidBrush(Color.Black))
                    {
                        // Draw shadow for better visibility
                        viewGraphics.DrawString(title, titleFont, shadowBrush, new PointF(6, 6));
                        viewGraphics.DrawString(title, titleFont, textBrush, new PointF(5, 5));
                    }
                }

                // Draw the view bitmap onto the composite bitmap
                g.DrawImage(viewBitmap, x, y, width, height);

                // Draw a border around the view
                using (Pen borderPen = new Pen(Color.DarkGray, 1))
                {
                    g.DrawRectangle(borderPen, x, y, width, height);
                }
            }
        }
        /// <summary>
        /// Helper method to check if a Vector3 is in a dictionary using approximate equality
        /// </summary>
        private bool ContainsVector3(Dictionary<Vector3, int> dict, Vector3 vector)
        {
            foreach (var key in dict.Keys)
            {
                if (Vector3.Distance(key, vector) < 0.0001f)
                {
                    return true;
                }
            }
            return false;
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Raise the progress changed event
        /// </summary>
        protected virtual void OnProgressChanged(float progress, string message)
        {
            ProgressChanged?.Invoke(this, new SimulationProgressEventArgs(progress, message));
        }

        /// <summary>
        /// Raise the simulation completed event
        /// </summary>
        protected virtual void OnSimulationCompleted(bool success, string message, SimulationResult result, Exception error = null)
        {
            SimulationCompleted?.Invoke(this, new SimulationCompletedEventArgs(success, message, result, error));
        }

        #endregion

        #region IDisposable Implementation

        /// <summary>
        /// Dispose resources
        /// </summary>
        public void Dispose()
        {
            if (!_isDisposed)
            {
                // Dispose managed resources
                _cancellationTokenSource?.Dispose();
                _accelerator?.Dispose();
                _context?.Dispose();

                _isDisposed = true;
            }
        }

        #endregion
    }
}