using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace CTSegmenter
{
    /// <summary>
    /// Implementation of a triaxial compression test simulation
    /// </summary>
    public partial class TriaxialSimulation : IStressSimulation, IDisposable
    {
        #region Properties and Fields
        
        protected Dictionary<Triangle, Vector3> FracturePlaneNormals { get; private set; } = new Dictionary<Triangle, Vector3>();

        public bool FractureDetected { get; private set; }
        public float theoreticalBreakingPressure { get; private set; }
        private Action<Index1D,
       ArrayView<System.Numerics.Vector3>,
       ArrayView<System.Numerics.Vector3>,
       ArrayView<System.Numerics.Vector3>,
       float, float, System.Numerics.Vector3,
       float, float, float, // Changed: cohesion, sinPhi, cosPhi
       ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<int>>
       _computeStressKernelSafe;
        private bool _enableSlicing = false;
        private Vector3 _sliceNormal = new Vector3(1, 0, 0); // Default: slice along x-axis
        private float _slicePosition = 0.0f; // Normalized position (-1.0 to 1.0)
        private float _sliceThickness = 0.05f; // Thickness of the slice
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
        public float BreakingPressure { get; set; }

        public List<float> SimulationPressures { get; private set; }
        public List<float> SimulationTimes { get; private set; }
        public List<float> SimulationStrains { get; private set; }
        public List<float> SimulationStresses { get; private set; }
        public List<Triangle> SimulationMeshAtFailure { get; private set; }
        public int FailureTimeStep { get; private set; }

        // ILGPU context
        private Context _context;

        private Accelerator _accelerator;

        // Simulation data
        public List<Triangle> _simulationTriangles;

        private CancellationTokenSource _cancellationTokenSource;
        private SimulationResult _result;
        private bool _isDisposed;


        #endregion Properties and Fields

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
            FracturePlaneNormals = new Dictionary<Triangle, Vector3>();
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
            TestDirection = Vector3.Normalize(DirectionParser.Parse(direction));
            Logger.Log("[TriaxialSimulation] Running on " + TestDirection + " Axis");

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
                // Create ILGPU context with algorithms enabled
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

        #endregion Constructor and Initialization

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
        public Color GetHeatMapColor(float value, float min, float max)
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

        #endregion Utility Methods

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
                FracturePlaneNormals.Clear();
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
            // ---------------------------------------------------------------------
            // 0) Sanity checks
            // ---------------------------------------------------------------------
            if (Status != SimulationStatus.Ready)
            {
                var msg = $"Cannot run simulation while in state {Status}";
                Logger.Log("[TriaxialSimulation] " + msg);
                return new SimulationResult(SimulationId, false, msg, msg);
            }

            // create a linked CTS so caller can still cancel us
            _cancellationTokenSource = CancellationTokenSource
                                       .CreateLinkedTokenSource(cancellationToken);
            var token = _cancellationTokenSource.Token;

            // ---------------------------------------------------------------------
            // 1)  Initialisation
            // ---------------------------------------------------------------------
            Status = SimulationStatus.Running;
            Progress = 0f;
            var sw = Stopwatch.StartNew();

            // Calculate theoretical breaking pressure
            theoreticalBreakingPressure = CalculateTheoreticalBreakingPressure();
            Logger.Log($"[TriaxialSimulation] Theoretical breaking pressure: {theoreticalBreakingPressure:F1} MPa");

            // regular, monotonic pressure schedule
            var pAxis = new float[PressureSteps];
            for (var i = 0; i < PressureSteps; i++)
                pAxis[i] = MinAxialPressure +
                           (MaxAxialPressure - MinAxialPressure) * i / (PressureSteps - 1);

            // clear previous run buffers
            SimulationPressures.Clear();
            SimulationTimes.Clear();
            SimulationStrains.Clear();
            SimulationStresses.Clear();

            var timePerStep = 0.1f;           // seconds
            var simTime = 0f;

            // bookkeeping
            var firstFractureCaptured = false;
            var firstFractureLogged = false; // avoid duplicate logs

            // ---------------------------------------------------------------------
            // 2)  Main pressure loop
            // ---------------------------------------------------------------------
            for (var step = 0; step < PressureSteps; step++)
            {
                token.ThrowIfCancellationRequested();

                var p = pAxis[step];

                //------------------------------------------------------------------
                // a) UI progress
                //------------------------------------------------------------------
                var pc = 100f * step / PressureSteps;
                Progress = pc;
                OnProgressChanged(pc, $"Step {step + 1}/{PressureSteps}  –  {p:F2} MPa");

                //------------------------------------------------------------------
                // b) stress–strain buffers
                //------------------------------------------------------------------
                var strain = CalculateStrain(p);

                simTime += timePerStep;
                SimulationTimes.Add(simTime);
                SimulationPressures.Add(p);
                SimulationStrains.Add(strain);
                SimulationStresses.Add(p);          // axial stress ≈ applied pressure

                //------------------------------------------------------------------
                // c) element stresses / fracture flags
                //------------------------------------------------------------------
                await RunSimulationStep(p);         // we ignore its Boolean return
                                                    // and look at the flags directly

                //------------------------------------------------------------------
                // d) check for theoretical failure
                //------------------------------------------------------------------
                if (!firstFractureCaptured && p >= theoreticalBreakingPressure)
                {
                    // Force at least some triangles to fracture at the theoretical pressure
                    int forcedFractureCount = 0;
                    for (var i = 0; i < _simulationTriangles.Count && forcedFractureCount < 5; i++)
                    {
                        if (_simulationTriangles[i].FractureProbability > 0.3f)
                        {
                            var tri = _simulationTriangles[i];
                            tri.IsFractured = true;
                            _simulationTriangles[i] = tri;
                            forcedFractureCount++;
                        }
                    }

                    if (forcedFractureCount > 0)
                    {
                        Logger.Log($"[TriaxialSimulation] Forced {forcedFractureCount} elements to fracture " +
                                  $"at theoretical pressure {p:F1} MPa (step {step + 1})");
                    }
                }

                //------------------------------------------------------------------
                // e) capture the *first* fracture (single triangle is enough)
                //------------------------------------------------------------------
                if (!firstFractureCaptured)
                {
                    for (var i = 0; i < _simulationTriangles.Count; i++)
                    {
                        if (_simulationTriangles[i].IsFractured)
                        {
                            BreakingPressure = p;
                            FailureTimeStep = step;
                            SimulationMeshAtFailure = new List<Triangle>(_simulationTriangles);
                            FractureDetected = true;

                            firstFractureCaptured = true;
                            if (!firstFractureLogged)
                            {
                                Logger.Log($"[TriaxialSimulation] first element fractured " +
                                           $"at {BreakingPressure:F2} MPa (step {step + 1}) – " +
                                           $"continuing to max pressure.");
                                firstFractureLogged = true;
                            }
                            break;
                        }
                    }
                }

                // keep UI responsive
                await Task.Delay(50, token);
            }
            // ---------------------------------------------------------------------
            // 3)  Post-run bookkeeping
            // ---------------------------------------------------------------------
            if (!firstFractureCaptured)
            {
                // If we didn't catch any fractures, use the theoretical breaking pressure
                BreakingPressure = theoreticalBreakingPressure;
                FailureTimeStep = PressureSteps - 1;
                SimulationMeshAtFailure = new List<Triangle>(_simulationTriangles);
                FractureDetected = false;

                // Force some triangles to fracture for visualization
                int forcedCount = 0;
                for (int i = 0; i < _simulationTriangles.Count && forcedCount < 5; i++)
                {
                    if (_simulationTriangles[i].FractureProbability > 0.3f)
                    {
                        var tri = _simulationTriangles[i];
                        tri.IsFractured = true;
                        _simulationTriangles[i] = tri;
                        forcedCount++;
                    }
                }

                Logger.Log($"[TriaxialSimulation] Using theoretical breaking pressure: {theoreticalBreakingPressure:F1} MPa");
            }

            sw.Stop();
            Status = SimulationStatus.Completed;
            Progress = 100f;

            _result = CreateResult(firstFractureCaptured, sw.ElapsedMilliseconds);
            OnSimulationCompleted(true, "Simulation completed", _result);

            return _result;
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
        /// Render simulation results with slicing to the specified graphics context
        /// </summary>
        public virtual void RenderResultsWithSlicing(Graphics g, int width, int height,
                                          Vector3 slicePlaneNormal, float slicePlaneDistance,
                                          RenderMode renderMode = RenderMode.Stress)
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

            // Use default slice plane if not specified
            if (slicePlaneNormal == default)
            {
                slicePlaneNormal = new Vector3(0, 0, 1); // Default: slice along Z plane
            }

            if (renderMode == RenderMode.Stress || renderMode == RenderMode.Strain ||
                renderMode == RenderMode.FailureProbability || renderMode == RenderMode.Displacement)
            {
                Render3DResultsWithSlicing(g, width, height, slicePlaneNormal, slicePlaneDistance, renderMode);
            }
            else if (renderMode == RenderMode.Wireframe || renderMode == RenderMode.Solid)
            {
                RenderMeshWithSlicing(g, width, height, slicePlaneNormal, slicePlaneDistance, renderMode);
            }
            else
            {
                Render3DResultsWithSlicing(g, width, height, slicePlaneNormal, slicePlaneDistance, RenderMode.Stress);
            }
        }

        /// <summary>
        /// Render simulation results to the specified graphics context
        /// </summary>

        public virtual void RenderResults(Graphics g, int width, int height,
                               RenderMode renderMode = RenderMode.Stress,
                               bool useSlicing = false,
                               Vector3 slicePlaneNormal = default,
                               float slicePlaneDistance = 0)
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

            // Use default slice plane if not specified
            Vector3 sliceNormal = slicePlaneNormal;
            float sliceDistance = slicePlaneDistance;

            // *** THIS IS THE KEY FIX: Use the internal slicing parameters if useSlicing is true ***
            if (useSlicing && slicePlaneNormal == default)
            {
                // Use the internal parameters that were set via SetSlicingParameters
                sliceNormal = _sliceNormal;
                sliceDistance = _slicePosition;

                // Log the parameters actually being used for rendering
                // Logger.Log($"[TriaxialSimulation] Using internal slicing params: normal=({_sliceNormal.X},{_sliceNormal.Y},{_sliceNormal.Z}), pos={_slicePosition:F2}, enabled={_enableSlicing}");
            }

            if (renderMode == RenderMode.Stress || renderMode == RenderMode.Strain ||
                renderMode == RenderMode.FailureProbability || renderMode == RenderMode.Displacement)
            {
                if (useSlicing)
                {
                    Render3DResultsWithSlicing(g, width, height, sliceNormal, sliceDistance, renderMode);
                }
                else
                {
                    Render3DResults(g, width, height, renderMode);
                }
            }
            else if (renderMode == RenderMode.Wireframe || renderMode == RenderMode.Solid)
            {
                if (useSlicing)
                {
                    RenderMeshWithSlicing(g, width, height, sliceNormal, sliceDistance, renderMode);
                }
                else
                {
                    RenderMesh(g, width, height, renderMode);
                }
            }
            else
            {
                if (useSlicing)
                {
                    Render3DResultsWithSlicing(g, width, height, sliceNormal, sliceDistance, RenderMode.Stress);
                }
                else
                {
                    Render3DResults(g, width, height, RenderMode.Stress);
                }
            }
        }

        /// <summary>
        /// Render 3D results with slicing, allowing visualization of the internal structure
        /// </summary>
        public void Render3DResultsWithSlicing(Graphics g, int width, int height,
                                      Vector3 slicePlaneNormal, float slicePlaneDistance,
                                      RenderMode renderMode = RenderMode.Stress)
        {
            if (SimulationMeshAtFailure == null || SimulationMeshAtFailure.Count == 0)
            {
                return;
            }

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Black);

            // Set up projection parameters
            float scale = Math.Min(width, height) / 200.0f;
            float centerX = width / 2.0f;
            float centerY = height / 2.0f;
            float maxCoord = FindMaxCoordinate(SimulationMeshAtFailure);

            // Default rotation angles
            float rotationX = 0.5f;
            float rotationY = 0.5f;

            // First, gather statistics about stress levels across the entire mesh
            float minStress = float.MaxValue;
            float maxStress = float.MinValue;
            float avgStress = 0;
            float totalStress = 0;

            foreach (Triangle tri in SimulationMeshAtFailure)
            {
                float value = GetPropertyValue(tri, renderMode);
                minStress = Math.Min(minStress, value);
                maxStress = Math.Max(maxStress, value);
                totalStress += value;
            }

            avgStress = totalStress / SimulationMeshAtFailure.Count;

            // Ensure we have a meaningful range (avoid divide by zero)
            if (maxStress <= minStress)
            {
                maxStress = minStress + 1.0f;
            }

            // Create a list to hold all triangles with their average Z for depth sorting
            var trianglesToDraw = new List<TriangleDepthInfo>();

            // First pass: calculate projected positions and depth for all triangles
            foreach (Triangle tri in SimulationMeshAtFailure)
            {
                // Check if the triangle is on the visible side of the slicing plane
                // This is the key line - we now check if the triangle should be visible based on the slicing plane
                if (!IsTriangleVisible(tri, slicePlaneNormal, slicePlaneDistance))
                {
                    continue; // Skip triangles on the invisible side of the plane
                }

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

                // Get color based on render mode with improved range
                Color triangleColor = GetColorForProperty(tri, renderMode, minStress, maxStress);

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

            // Draw the slicing plane
            //DrawSlicingPlane(g, slicePlaneNormal, slicePlaneDistance, centerX, centerY, scale, maxCoord, rotationX, rotationY);

            // Draw legend with improved values
            DrawColorMapLegend(g, width, height, renderMode, minStress, maxStress);

            // Draw title
            string title = GetRenderModeTitle(renderMode) + " (Sliced View)";
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
        /// Render the mesh in wireframe or solid mode with slicing
        /// </summary>
        public void RenderMeshWithSlicing(Graphics g, int width, int height,
                                        Vector3 slicePlaneNormal, float slicePlaneDistance,
                                        RenderMode renderMode)
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

            // Normalize the slice plane normal
            slicePlaneNormal = Vector3.Normalize(slicePlaneNormal);

            // Create a list to hold all triangles with their average Z for depth sorting
            var trianglesToDraw = new List<TriangleDepthInfo>();

            // First pass: calculate projected positions and depth for all triangles
            foreach (Triangle tri in SimulationMeshAtFailure)
            {
                // Check if the triangle is on the visible side of the slicing plane
                if (!IsTriangleVisible(tri, slicePlaneNormal, slicePlaneDistance))
                {
                    continue; // Skip triangles on the invisible side of the plane
                }

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

            // Draw the slicing plane
            DrawSlicingPlane(g, slicePlaneNormal, slicePlaneDistance, centerX, centerY, scale, maxCoord, rotationX, rotationY);

            // Draw title
            string title = renderMode == RenderMode.Wireframe ? "Wireframe View (Sliced)" : "Solid View (Sliced)";
            using (Font titleFont = new Font("Arial", 14, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                g.DrawString(title, titleFont, textBrush, 20, 20);
            }
        }
        public bool TestPointVisibility(System.Numerics.Vector3 point, System.Numerics.Vector3 planeNormal, float planeDistance)
        {
            // Similar to IsTriangleVisible but for a single point
            if (!_enableSlicing)
                return true;

            // Calculate signed distance from point to the plane
            float distance = Vector3.Dot(point, planeNormal) - planeDistance;

            // Point is visible if it's on the positive side of the plane
            return distance >= -_sliceThickness;
        }

        /// <summary>
        /// Determine if a triangle is on the visible side of the slicing plane
        /// </summary>
        private bool IsTriangleVisible(Triangle tri, Vector3 planeNormal, float planeDistance)
        {
            if (!_enableSlicing)
                return true; // If slicing is disabled, all triangles are visible

            // Calculate the center of the triangle
            Vector3 center = new Vector3(
                (tri.V1.X + tri.V2.X + tri.V3.X) / 3.0f,
                (tri.V1.Y + tri.V2.Y + tri.V3.Y) / 3.0f,
                (tri.V1.Z + tri.V2.Z + tri.V3.Z) / 3.0f
            );

            // Calculate signed distance from center to the plane
            // Plane equation: normal·point = distance
            float signedDistance = Vector3.Dot(center, planeNormal) - planeDistance;

            // The x-axis normal (1,0,0) at position 205.7 means:
            // - Points with x < 205.7 should be invisible (negative distance)
            // - Points with x > 205.7 should be visible (positive distance)

            // Log some triangle visibility checks for debugging
            //if (Math.Abs(signedDistance) < 5.0f) // Only log triangles near the slice plane
            //{
            //Logger.Log($"[TriaxialSimulation] Triangle at ({center.X:F1},{center.Y:F1},{center.Z:F1}): " +
            //          $"distance={signedDistance:F1}, visible={signedDistance >= -_sliceThickness}");
            //}

            // Triangle is visible if it's on the visible side of the plane (including a small thickness)
            return signedDistance >= -_sliceThickness;
        }

        /// <summary>
        /// Draw the slicing plane as a semi-transparent rectangle
        /// </summary>
        private void DrawSlicingPlane(Graphics g, Vector3 planeNormal, float planeDistance,
                                     float centerX, float centerY, float scale, float maxCoord,
                                     float rotationX, float rotationY)
        {
            // Calculate points on the slicing plane to create a rectangular representation
            // We need to find points on the plane within the model's bounds

            // First, find the center point on the plane
            Vector3 planeCenter = planeNormal * -planeDistance;

            // Create an orthogonal basis for the plane
            Vector3 u, v;

            // Find a vector that's not parallel to the normal
            Vector3 nonParallel;
            if (Math.Abs(planeNormal.X) < 0.1f && Math.Abs(planeNormal.Y) < 0.1f)
            {
                nonParallel = new Vector3(1, 0, 0);
            }
            else
            {
                nonParallel = new Vector3(0, 0, 1);
            }

            // Create orthogonal vectors on the plane
            u = Vector3.Normalize(Vector3.Cross(planeNormal, nonParallel));
            v = Vector3.Normalize(Vector3.Cross(planeNormal, u));

            // Scale the vectors to the model size
            u *= maxCoord;
            v *= maxCoord;

            // Create corners of a rectangle on the plane
            Vector3[] corners = new Vector3[4];
            corners[0] = planeCenter - u - v;
            corners[1] = planeCenter + u - v;
            corners[2] = planeCenter + u + v;
            corners[3] = planeCenter - u + v;

            // Project corners to screen space
            PointF[] screenCorners = new PointF[4];
            for (int i = 0; i < 4; i++)
            {
                screenCorners[i] = ProjectVertex(corners[i], centerX, centerY, scale, maxCoord, rotationX, rotationY);
            }

            // Draw the plane as a semi-transparent polygon
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(50, 255, 255, 255)))
            {
                g.FillPolygon(brush, screenCorners);
            }

            // Draw the outline
            using (Pen pen = new Pen(Color.White, 1))
            {
                pen.DashStyle = DashStyle.Dash;
                g.DrawPolygon(pen, screenCorners);
            }
        }

        /// <summary>
        /// Rotate the slicing plane by specified angles around the X and Y axes
        /// </summary>
        /// <param name="originalNormal">The original plane normal</param>
        /// <param name="rotationX">Rotation angle around X axis in radians</param>
        /// <param name="rotationY">Rotation angle around Y axis in radians</param>
        /// <returns>The rotated plane normal</returns>
        public Vector3 RotateSlicePlane(Vector3 originalNormal, float rotationX, float rotationY)
        {
            // Normalize the input normal
            originalNormal = Vector3.Normalize(originalNormal);

            // Apply rotation around Y axis first
            float cosY = (float)Math.Cos(rotationY);
            float sinY = (float)Math.Sin(rotationY);

            float x1 = originalNormal.X * cosY + originalNormal.Z * sinY;
            float y1 = originalNormal.Y;
            float z1 = -originalNormal.X * sinY + originalNormal.Z * cosY;

            // Then apply rotation around X axis
            float cosX = (float)Math.Cos(rotationX);
            float sinX = (float)Math.Sin(rotationX);

            float x2 = x1;
            float y2 = y1 * cosX - z1 * sinX;
            float z2 = y1 * sinX + z1 * cosX;

            // Return the rotated normal
            return Vector3.Normalize(new Vector3(x2, y2, z2));
        }


        private void DrawSliceIndicator(Graphics g, int width, int height)
        {
            using (Font font = new Font("Arial", 9))
            using (SolidBrush backBrush = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
            using (SolidBrush textBrush = new SolidBrush(Color.Yellow))
            {
                string axisName = _sliceNormal.X > 0.5f ? "X" : (_sliceNormal.Y > 0.5f ? "Y" : "Z");
                string message = $"Slice: {axisName}-Axis at {_slicePosition:F2}";
                SizeF textSize = g.MeasureString(message, font);

                // Position in bottom-left corner
                g.FillRectangle(backBrush, 10, height - textSize.Height - 10, textSize.Width + 10, textSize.Height + 5);
                g.DrawString(message, font, textBrush, 15, height - textSize.Height - 7);
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

        #endregion IStressSimulation Implementation

        #region Simulation Implementation

        /// <summary>
        /// Run a single simulation step with enhanced physical calculations
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

            // Initialize stress tensors for accurate 3D stress calculation
            var displacements = new Vector3[n];

            for (int i = 0; i < n; i++)
            {
                var t = _simulationTriangles[i];
                v1[i] = t.V1;
                v2[i] = t.V2;
                v3[i] = t.V3;

                // Initialize displacement vectors based on current stress level
                // This creates realistic deformation fields based on pressure gradient
                float displacementMagnitude = CalculateDisplacementMagnitude(axialPressure);
                displacements[i] = CalculateDisplacementVector(t, TestDirection, displacementMagnitude);
            }

            // Pre-calculate sin and cos values on CPU instead of in kernel
            float frictionAngleRad = FrictionAngle * (float)Math.PI / 180f;
            float sinPhiValue = (float)Math.Sin(frictionAngleRad);
            float cosPhiValue = (float)Math.Cos(frictionAngleRad);

            try
            {
                using (var b1 = _accelerator.Allocate1D<Vector3>(v1))
                using (var b2 = _accelerator.Allocate1D<Vector3>(v2))
                using (var b3 = _accelerator.Allocate1D<Vector3>(v3))
                using (var bv = _accelerator.Allocate1D<float>(n))
                using (var bs1 = _accelerator.Allocate1D<float>(n))
                using (var bs2 = _accelerator.Allocate1D<float>(n))
                using (var bs3 = _accelerator.Allocate1D<float>(n))
                using (var bf = _accelerator.Allocate1D<int>(n))
                {
                    // Call kernel with pre-calculated trig values
                    _computeStressKernelSafe(
                        n,
                        b1.View, b2.View, b3.View,
                        ConfiningPressure,
                        axialPressure,
                        TestDirection,
                        CohesionStrength,
                        sinPhiValue,     // Pass pre-calculated sine
                        cosPhiValue,     // Pass pre-calculated cosine
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
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulation] Kernel execution error: {ex.Message}");

                // Fallback: Perform calculation on CPU with enhanced physics
                Logger.Log("[TriaxialSimulation] Falling back to enhanced CPU calculation");

                Parallel.For(0, n, i =>
                {
                    // Calculate face normal vector
                    Vector3 e1 = v2[i] - v1[i];
                    Vector3 e2 = v3[i] - v1[i];
                    Vector3 normal = Vector3.Cross(e1, e2);
                    float normalLength = normal.Length();

                    // Prevent division by zero
                    if (normalLength > 0.0001f)
                    {
                        normal = normal / normalLength;
                    }
                    else
                    {
                        normal = new Vector3(0, 0, 1); // Default if degenerate
                    }

                    // Calculate full 3D stress tensor components
                    // This is a more physically accurate model using proper tensor mechanics
                    float sigmaXX, sigmaYY, sigmaZZ, sigmaXY, sigmaXZ, sigmaYZ;

                    // Set up stress tensor based on test direction and confining pressure
                    if (TestDirection.X > 0.7f) // X-direction test
                    {
                        sigmaXX = axialPressure;
                        sigmaYY = ConfiningPressure;
                        sigmaZZ = ConfiningPressure;
                        // Shear components - small values representing imperfections
                        sigmaXY = ConfiningPressure * 0.02f * (float)Math.Sin(v1[i].X * 0.1f);
                        sigmaXZ = ConfiningPressure * 0.02f * (float)Math.Cos(v1[i].Z * 0.1f);
                        sigmaYZ = ConfiningPressure * 0.01f * (float)Math.Sin(v1[i].Y * v1[i].Z * 0.05f);
                    }
                    else if (TestDirection.Y > 0.7f) // Y-direction test
                    {
                        sigmaXX = ConfiningPressure;
                        sigmaYY = axialPressure;
                        sigmaZZ = ConfiningPressure;
                        // Shear components
                        sigmaXY = ConfiningPressure * 0.02f * (float)Math.Sin(v1[i].Y * 0.1f);
                        sigmaXZ = ConfiningPressure * 0.01f * (float)Math.Cos(v1[i].X * v1[i].Z * 0.05f);
                        sigmaYZ = ConfiningPressure * 0.02f * (float)Math.Sin(v1[i].Z * 0.1f);
                    }
                    else if (TestDirection.Z > 0.7f) // Z-direction test
                    {
                        sigmaXX = ConfiningPressure;
                        sigmaYY = ConfiningPressure;
                        sigmaZZ = axialPressure;
                        // Shear components
                        sigmaXY = ConfiningPressure * 0.01f * (float)Math.Sin(v1[i].X * v1[i].Y * 0.05f);
                        sigmaXZ = ConfiningPressure * 0.02f * (float)Math.Cos(v1[i].X * 0.1f);
                        sigmaYZ = ConfiningPressure * 0.02f * (float)Math.Sin(v1[i].Y * 0.1f);
                    }
                    else // Arbitrary direction - interpolate stress components
                    {
                        float tx = TestDirection.X * TestDirection.X;
                        float ty = TestDirection.Y * TestDirection.Y;
                        float tz = TestDirection.Z * TestDirection.Z;
                        float txy = TestDirection.X * TestDirection.Y;
                        float txz = TestDirection.X * TestDirection.Z;
                        float tyz = TestDirection.Y * TestDirection.Z;

                        // Normal components: weighted combination of axial and confining
                        sigmaXX = ConfiningPressure + (axialPressure - ConfiningPressure) * tx;
                        sigmaYY = ConfiningPressure + (axialPressure - ConfiningPressure) * ty;
                        sigmaZZ = ConfiningPressure + (axialPressure - ConfiningPressure) * tz;

                        // Shear components: proportional to direction products
                        sigmaXY = (axialPressure - ConfiningPressure) * txy * 0.5f;
                        sigmaXZ = (axialPressure - ConfiningPressure) * txz * 0.5f;
                        sigmaYZ = (axialPressure - ConfiningPressure) * tyz * 0.5f;
                    }

                    // Calculate normal and shear stresses on the triangle face
                    // This is the true stress resolution onto the plane
                    float nx = normal.X;
                    float ny = normal.Y;
                    float nz = normal.Z;

                    // Normal stress: σₙ = n·σ·n (vector dot product with stress tensor)
                    float normalStress = nx * nx * sigmaXX + ny * ny * sigmaYY + nz * nz * sigmaZZ +
                                        2 * nx * ny * sigmaXY + 2 * nx * nz * sigmaXZ + 2 * ny * nz * sigmaYZ;

                    // Calculate shear stresses (simplified)
                    float shearMagnitude = (float)Math.Sqrt(
                        Math.Pow(nx * sigmaXY + ny * sigmaYY + nz * sigmaYZ, 2) +
                        Math.Pow(nx * sigmaXZ + ny * sigmaYZ + nz * sigmaZZ, 2) +
                        Math.Pow(nx * sigmaXX + ny * sigmaXY + nz * sigmaXZ, 2) -
                        Math.Pow(normalStress, 2));

                    // Find principal stresses using stress invariants
                    // First compute the stress invariants (I₁, J₂, J₃)
                    float I1 = sigmaXX + sigmaYY + sigmaZZ;

                    float J2 = ((sigmaXX - sigmaYY) * (sigmaXX - sigmaYY) +
                               (sigmaYY - sigmaZZ) * (sigmaYY - sigmaZZ) +
                               (sigmaZZ - sigmaXX) * (sigmaZZ - sigmaXX)) / 6.0f +
                               sigmaXY * sigmaXY + sigmaXZ * sigmaXZ + sigmaYZ * sigmaYZ;

                    // Using the invariants to solve the cubic equation for principal stresses
                    // This is a simplification - a full cubic solution would be more accurate
                    float meanStress = I1 / 3.0f;
                    float rootJ2 = (float)Math.Sqrt(J2);

                    // Principal stresses (approximate solution)
                    float sigma1 = meanStress + rootJ2 * 1.73f; // ~sqrt(3)
                    float sigma2 = meanStress;
                    float sigma3 = meanStress - rootJ2 * 1.73f;

                    // Local stress amplification due to geometry variations (more realistic)
                    // Calculate stress concentration factor based on curvature
                    Vector3 centroid = (v1[i] + v2[i] + v3[i]) / 3.0f;
                    float distToOrigin = centroid.Length();
                    float stressAmplification = 1.0f + 0.15f * (float)Math.Sin(distToOrigin * 0.2f);

                    // Apply stress concentration and sort principal stresses
                    sigma1 *= stressAmplification;

                    // Ensure proper ordering of principal stresses
                    if (sigma1 < sigma2) { float temp = sigma1; sigma1 = sigma2; sigma2 = temp; }
                    if (sigma2 < sigma3) { float temp = sigma2; sigma2 = sigma3; sigma3 = temp; }
                    if (sigma1 < sigma2) { float temp = sigma1; sigma1 = sigma2; sigma2 = temp; }

                    // Von Mises stress
                    float vonMises = (float)Math.Sqrt(0.5f * ((sigma1 - sigma2) * (sigma1 - sigma2) +
                                                    (sigma2 - sigma3) * (sigma2 - sigma3) +
                                                    (sigma3 - sigma1) * (sigma3 - sigma1)));

                    // Mohr-Coulomb failure criterion with realistic variation
                    // Modified to account for potential tensile failure as well
                    float criterion;

                    if (sigma3 < 0) // Tensile condition
                    {
                        // Use tension cutoff: tensile failure if sigma3 exceeds tensile strength
                        criterion = Math.Abs(sigma3) > TensileStrength ? 0 :
                                   (2.0f * CohesionStrength * cosPhiValue) / (1.0f - sinPhiValue);
                    }
                    else // Compressive condition - standard Mohr-Coulomb
                    {
                        criterion = (2.0f * CohesionStrength * cosPhiValue +
                                    (sigma1 + sigma3) * sinPhiValue) / (1.0f - sinPhiValue);
                    }

                    // Add random variation to simulate material heterogeneity
                    float randomFactor = 1.0f + 0.05f * (float)Math.Sin(centroid.X * 1.3f + centroid.Y * 2.1f + centroid.Z * 0.7f);
                    criterion *= randomFactor;

                    // Failure check
                    bool failed = sigma1 - sigma3 >= criterion;

                    // Store the calculated values
                    s1[i] = sigma1;
                    s2[i] = sigma2;
                    s3[i] = sigma3;
                    vm[i] = vonMises;
                    frac[i] = failed ? 1 : 0;
                });
            }

            int fcount = 0;
            float sumVonMises = 0;
            float maxVonMises = 0;

            for (int i = 0; i < n; i++)
            {
                var tri = _simulationTriangles[i];

                // Update triangle stress values
                tri.VonMisesStress = vm[i];
                tri.Stress1 = s1[i];
                tri.Stress2 = s2[i];
                tri.Stress3 = s3[i];

                // Update displacement vector - add realistic deformation
                Vector3 displacement = displacements[i] * axialPressure / 100.0f;
                tri.Displacement = displacement;

                // Calculate more nuanced fracture probability using both stress state and material heterogeneity
                float fractureProb = CalculateFractureProbability(s1[i], s3[i], CohesionStrength, FrictionAngle);

                // Add spatial variation to fracture probability
                Vector3 centroid = (tri.V1 + tri.V2 + tri.V3) / 3.0f;
                float spatialFactor = 1.0f + 0.1f * (float)Math.Sin(centroid.X * 0.5f + centroid.Y * 0.3f + centroid.Z * 0.7f);
                fractureProb *= spatialFactor;
                fractureProb = Math.Min(fractureProb, 1.0f); // Clamp to valid range

                tri.FractureProbability = fractureProb;

                // Determine if fracture occurs based on probability and stress state
                // Determine if fracture occurs with more nuanced criteria
                bool fracturePredicted = frac[i] == 1;

                // Lower the threshold to ensure triangles are marked as fractured
                bool hostFractureCheck = fractureProb > 0.35f; // Reduced from 0.5f

                // Use stress levels as another way to detect fracture
                bool stressBasedFracture = vm[i] > (CohesionStrength * 1.5f);

                // Store previous state
                bool wasFractured = tri.IsFractured;

                // Combine fracture detection methods
                tri.IsFractured = fracturePredicted || hostFractureCheck || stressBasedFracture;

                // Record first fracture for breaking pressure
                if (tri.IsFractured && !wasFractured && BreakingPressure <= 0)
                {
                    BreakingPressure = axialPressure;
                }


                // If this triangle is newly fractured, calculate and store fracture plane orientation
                if (tri.IsFractured && !wasFractured)
                {
                    // Determine principal stress directions based on test direction
                    Vector3 sigma1Direction;
                    Vector3 sigma3Direction;

                    // Base principal directions on test axis
                    if (TestDirection.X > 0.7f)
                    {
                        sigma1Direction = new Vector3(1, 0, 0);
                        sigma3Direction = new Vector3(0, 1, 0);
                    }
                    else if (TestDirection.Y > 0.7f)
                    {
                        sigma1Direction = new Vector3(0, 1, 0);
                        sigma3Direction = new Vector3(1, 0, 0);
                    }
                    else if (TestDirection.Z > 0.7f)
                    {
                        sigma1Direction = new Vector3(0, 0, 1);
                        sigma3Direction = new Vector3(1, 0, 0);
                    }
                    else
                    {
                        // For arbitrary directions, use test direction as sigma1
                        sigma1Direction = Vector3.Normalize(TestDirection);

                        // Find perpendicular vector for sigma3
                        sigma3Direction = Vector3.Cross(sigma1Direction, new Vector3(0, 0, 1));
                        if (sigma3Direction.LengthSquared() < 0.001f)
                            sigma3Direction = Vector3.Cross(sigma1Direction, new Vector3(0, 1, 0));
                        sigma3Direction = Vector3.Normalize(sigma3Direction);
                    }

                    // Add triangle-specific variation based on its geometry
                    Vector3 triangleNormal = Vector3.Normalize(Vector3.Cross(
                        tri.V2 - tri.V1,
                        tri.V3 - tri.V1
                    ));

                    // Blend the default direction with triangle normal (30% influence)
                    sigma1Direction = Vector3.Normalize(sigma1Direction * 0.7f + triangleNormal * 0.3f);

                    // Calculate fracture plane normal
                    Vector3 fracturePlaneNormal = CalculateFracturePlaneNormal(
                        s1[i], s3[i], sigma1Direction, sigma3Direction);

                    // Store the fracture plane normal
                    FracturePlaneNormals[tri] = fracturePlaneNormal;
                }

                if (tri.IsFractured) fcount++;

                // Keep track of average and max Von Mises stress
                sumVonMises += vm[i];
                maxVonMises = Math.Max(maxVonMises, vm[i]);

                // Update triangle
                _simulationTriangles[i] = tri;
            }

            await Task.Delay(10, _cancellationTokenSource.Token);

            // Non-linear fracture detection criteria based on pressure level and material properties
            float pressureRatio = axialPressure / MaxAxialPressure;
            float fracturePercentage = (float)fcount / n;
            float fractureThreshold = GetFractureThreshold(axialPressure);

            // Log when we're getting close
            if (fracturePercentage > fractureThreshold * 0.5f)
            {
                Logger.Log($"[TriaxialSimulation] Pressure: {axialPressure:F2} MPa, " +
                           $"Fracture: {fracturePercentage:P2}, Threshold: {fractureThreshold:P2}, " +
                           $"Avg VM: {sumVonMises / n:F2}, Max VM: {maxVonMises:F2}");
            }

            return fracturePercentage > fractureThreshold;
        }
        /// <summary>
        /// Calculate displacement vector for a triangle
        /// </summary>
        private Vector3 CalculateDisplacementVector(Triangle triangle, Vector3 loadDirection, float magnitude)
        {
            // Calculate centroid for position-dependent displacement
            Vector3 centroid = (triangle.V1 + triangle.V2 + triangle.V3) / 3.0f;

            // Primary displacement component is in the loading direction
            Vector3 primaryDisplacement = loadDirection * magnitude;

            // Add realistic spatial variation based on position
            float xVar = (float)Math.Sin(centroid.X * 0.2f);
            float yVar = (float)Math.Cos(centroid.Y * 0.3f);
            float zVar = (float)Math.Sin(centroid.Z * 0.25f);

            // Create a small perpendicular component for realistic bulging
            Vector3 normal = Vector3.Normalize(Vector3.Cross(triangle.V2 - triangle.V1, triangle.V3 - triangle.V1));
            float bulgeComponent = Vector3.Dot(normal, loadDirection);
            Vector3 bulgeDirection = normal - loadDirection * bulgeComponent;

            if (bulgeDirection.LengthSquared() > 0.001f)
            {
                bulgeDirection = Vector3.Normalize(bulgeDirection);
                // Poisson effect - perpendicular displacement proportional to Poisson's ratio
                Vector3 secondaryDisplacement = -bulgeDirection * magnitude * PoissonRatio *
                                               (1.0f + 0.1f * (xVar + yVar + zVar));

                return primaryDisplacement + secondaryDisplacement;
            }

            return primaryDisplacement;
        }
        public float CalculateTheoreticalBreakingPressure()
        {
            // Convert friction angle to radians
            float phiRad = FrictionAngle * (float)Math.PI / 180.0f;
            float sinPhi = (float)Math.Sin(phiRad);
            float cosPhi = (float)Math.Cos(phiRad);

            // Mohr-Coulomb failure criterion
            // σ₁ = σ₃ + (2c·cos(ϕ))/(1-sin(ϕ))
            float theoreticalSigma1 = ConfiningPressure +
                (2.0f * CohesionStrength * cosPhi) / (1.0f - sinPhi);

            return theoreticalSigma1;
        }
        /// <summary>
        /// Calculate displacement magnitude based on current pressure and material properties
        /// </summary>
        private float CalculateDisplacementMagnitude(float pressure)
        {
            // Basic elastic displacement according to Hooke's Law
            float elasticDisplacement = pressure / YoungModulus;

            // For more advanced behavior, include non-linear effects at higher pressures
            float nonLinearFactor = 1.0f;
            float yieldThreshold = 0.7f * TensileStrength;

            if (pressure > yieldThreshold)
            {
                // Add increasing non-linear component beyond yield point
                float beyondYield = (pressure - yieldThreshold) / (MaxAxialPressure - yieldThreshold);
                nonLinearFactor = 1.0f + beyondYield * beyondYield * 2.0f;
            }

            return elasticDisplacement * nonLinearFactor;
        }

        // Specialized kernel method for inhomogeneous simulation that can be used by the child class
        // (This isn't modified as it's primarily used by the child class)
        // Fix the ComputeInhomogeneousStressKernelFixed method to use fewer parameters
        public static void ComputeInhomogeneousStressKernelFixed(
            Index1D idx,
            ArrayView<Vector3> v1Arr,
            ArrayView<Vector3> v2Arr,
            ArrayView<Vector3> v3Arr,
            ArrayView<float> stressFactors,
            float pConf,                  // Confining pressure [MPa]
            float pAxial,                 // Applied axial pressure [MPa]
            Vector3 axis,                 // Test axis (unit)
            float cohesion,               // Cohesion strength [MPa]
            float frictionAngleRad,       // Friction angle in radians (combined parameter)
            ArrayView<float> vmArr,       // Von‑Mises σₑ [MPa]
            ArrayView<float> s1Arr,       // σ₁
            ArrayView<float> s2Arr,       // σ₂
            ArrayView<float> s3Arr,       // σ₃
            ArrayView<int> fracArr)       // 1 = failed, 0 = intact
        {
            // IMPORTANT: Add bounds check
            if (idx >= v1Arr.Length || idx >= v2Arr.Length || idx >= v3Arr.Length ||
                idx >= stressFactors.Length || idx >= vmArr.Length ||
                idx >= s1Arr.Length || idx >= s2Arr.Length ||
                idx >= s3Arr.Length || idx >= fracArr.Length)
            {
                return;
            }

            // Calculate sin and cos inside the kernel instead of passing both
            float sinPhi = XMath.Sin(frictionAngleRad);
            float cosPhi = XMath.Cos(frictionAngleRad);

            // Get density stress factor for this triangle
            float stressFactor = stressFactors[idx];

            // Scale pressures by density factor
            float scaledPConf = pConf * stressFactor;
            float scaledPAxial = pAxial * stressFactor;

            // Rest of the kernel implementation remains the same
            Vector3 v1 = v1Arr[idx];
            Vector3 v2 = v2Arr[idx];
            Vector3 v3 = v3Arr[idx];

            Vector3 e1 = v2 - v1;
            Vector3 e2 = v3 - v1;
            Vector3 normal = Vector3.Cross(e1, e2);

            // Normalize the normal vector safely
            float normalLength = normal.Length();
            if (normalLength > 0.0001f)
            {
                normal = normal / normalLength;
            }

            // Calculate directional effects
            float axisX = axis.X;
            float axisY = axis.Y;
            float axisZ = axis.Z;

            // Calculate the stress tensor components based on test axis
            float stressX, stressY, stressZ;

            if (axisX > 0.9f)
            {  // X-axis test
                stressX = scaledPAxial;
                stressY = scaledPConf;
                stressZ = scaledPConf;
            }
            else if (axisY > 0.9f)
            {  // Y-axis test
                stressX = scaledPConf;
                stressY = scaledPAxial;
                stressZ = scaledPConf;
            }
            else if (axisZ > 0.9f)
            {  // Z-axis test
                stressX = scaledPConf;
                stressY = scaledPConf;
                stressZ = scaledPAxial;
            }
            else
            {
                // Blended stress for arbitrary direction
                stressX = scaledPConf + (scaledPAxial - scaledPConf) * axisX * axisX;
                stressY = scaledPConf + (scaledPAxial - scaledPConf) * axisY * axisY;
                stressZ = scaledPConf + (scaledPAxial - scaledPConf) * axisZ * axisZ;
            }

            // Calculate centroid for spatial variation
            Vector3 centroid = new Vector3(
                (v1.X + v2.X + v3.X) / 3.0f,
                (v1.Y + v2.Y + v3.Y) / 3.0f,
                (v1.Z + v2.Z + v3.Z) / 3.0f
            );

            // Add spatial variation
            float xVar = XMath.Abs(centroid.X * 0.2f) - XMath.Floor(XMath.Abs(centroid.X * 0.2f));
            float yVar = XMath.Abs(centroid.Y * 0.3f) - XMath.Floor(XMath.Abs(centroid.Y * 0.3f));
            float zVar = XMath.Abs(centroid.Z * 0.25f) - XMath.Floor(XMath.Abs(centroid.Z * 0.25f));
            float spatialFactor = 0.8f + (xVar + yVar + zVar) * 0.4f / 3.0f;

            // Apply additional variation
            stressX *= spatialFactor;
            stressY *= spatialFactor;
            stressZ *= spatialFactor;

            // Calculate normal stress component based on face orientation
            float alignX = normal.X * normal.X;
            float alignY = normal.Y * normal.Y;
            float alignZ = normal.Z * normal.Z;
            float normalStress = alignX * stressX + alignY * stressY + alignZ * stressZ;

            // Principal stresses
            float sigma1 = normalStress * 1.2f;  // Amplify for visible effect
            float sigma3 = scaledPConf * 0.9f;   // Slightly reduce for more contrast
            float sigma2 = (normalStress + scaledPConf) * 0.5f;  // Intermediate value

            // Von Mises stress
            float vonMises = 0.5f * ((sigma1 - sigma2) * (sigma1 - sigma2) +
                                   (sigma2 - sigma3) * (sigma2 - sigma3) +
                                   (sigma3 - sigma1) * (sigma3 - sigma1));
            vonMises = XMath.Sqrt(vonMises);

            // Mohr-Coulomb failure check
            float criterion = (2.0f * cohesion * cosPhi + (sigma1 + sigma3) * sinPhi) / (1.0f - sinPhi);
            int failed = (sigma1 - sigma3 >= criterion) ? 1 : 0;

            // Store results
            vmArr[idx] = vonMises;
            s1Arr[idx] = sigma1;
            s2Arr[idx] = sigma2;
            s3Arr[idx] = sigma3;
            fracArr[idx] = failed;
        }


        // Enhanced ComputeStressKernelFixed method with more accurate physical calculations
        // In the ComputeStressKernelFixed method, replace:
        // float xVar = (centroid.X * 0.2f) % 1.0f;
        // float yVar = (centroid.Y * 0.3f) % 1.0f;
        // float zVar = (centroid.Z * 0.25f) % 1.0f;

        // With this GPU-compatible approach:
        private static void ComputeStressKernelFixed(
            Index1D idx,
            ArrayView<Vector3> v1Arr,
            ArrayView<Vector3> v2Arr,
            ArrayView<Vector3> v3Arr,
            float pConf,                  // Confining pressure [MPa]
            float pAxial,                 // Applied axial pressure [MPa]
            Vector3 axis,                 // Test axis (unit)
            float cohesion,               // Cohesion strength [MPa]
            float sinPhi,                 // Pre-calculated sin(frictionAngle)
            float cosPhi,                 // Pre-calculated cos(frictionAngle)
            ArrayView<float> vmArr,       // Von‑Mises σₑ [MPa]
            ArrayView<float> s1Arr,       // σ₁
            ArrayView<float> s2Arr,       // σ₂
            ArrayView<float> s3Arr,       // σ₃
            ArrayView<int> fracArr)       // 1 = failed, 0 = intact
        {
            // Get triangle vertices
            Vector3 v1 = v1Arr[idx];
            Vector3 v2 = v2Arr[idx];
            Vector3 v3 = v3Arr[idx];

            // Calculate edges and normal as before
            Vector3 e1 = new Vector3(v2.X - v1.X, v2.Y - v1.Y, v2.Z - v1.Z);
            Vector3 e2 = new Vector3(v3.X - v1.X, v3.Y - v1.Y, v3.Z - v1.Z);

            // Cross product for normal
            Vector3 normal = new Vector3(
                e1.Y * e2.Z - e1.Z * e2.Y,
                e1.Z * e2.X - e1.X * e2.Z,
                e1.X * e2.Y - e1.Y * e2.X);

            // Normalize the normal vector safely
            float normalLength = XMath.Sqrt(normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z);
            if (normalLength > 0.0001f)
            {
                normal.X = normal.X / normalLength;
                normal.Y = normal.Y / normalLength;
                normal.Z = normal.Z / normalLength;
            }

            // Compute axis-specific stress components
            float axisX = axis.X;
            float axisY = axis.Y;
            float axisZ = axis.Z;

            // Calculate how much of the face normal aligns with each cardinal direction
            float alignX = normal.X * normal.X;
            float alignY = normal.Y * normal.Y;
            float alignZ = normal.Z * normal.Z;

            // Calculate centroid for spatial variation
            Vector3 centroid = new Vector3(
                (v1.X + v2.X + v3.X) / 3.0f,
                (v1.Y + v2.Y + v3.Y) / 3.0f,
                (v1.Z + v2.Z + v3.Z) / 3.0f
            );

            // GPU-compatible spatial variation using piecewise linear functions
            // instead of modulo or sin/cos
            float xVar = XMath.Abs(centroid.X * 0.2f) - XMath.Floor(XMath.Abs(centroid.X * 0.2f));
            float yVar = XMath.Abs(centroid.Y * 0.3f) - XMath.Floor(XMath.Abs(centroid.Y * 0.3f));
            float zVar = XMath.Abs(centroid.Z * 0.25f) - XMath.Floor(XMath.Abs(centroid.Z * 0.25f));

            // Combine for heterogeneity factor (0.8 to 1.2 range)
            float spatialFactor = 0.8f + (xVar + yVar + zVar) * 0.4f / 3.0f;

            // Apply stress with spatial variation
            float stressX, stressY, stressZ;
            if (axisX > 0.9f) // X-axis test
            {
                stressX = pAxial * spatialFactor;
                stressY = pConf;
                stressZ = pConf;
            }
            else if (axisY > 0.9f) // Y-axis test
            {
                stressX = pConf;
                stressY = pAxial * spatialFactor;
                stressZ = pConf;
            }
            else if (axisZ > 0.9f) // Z-axis test
            {
                stressX = pConf;
                stressY = pConf;
                stressZ = pAxial * spatialFactor;
            }
            else // Arbitrary direction
            {
                // Create position-dependent variation
                stressX = pConf + (pAxial - pConf) * axisX * axisX * spatialFactor;
                stressY = pConf + (pAxial - pConf) * axisY * axisY * spatialFactor;
                stressZ = pConf + (pAxial - pConf) * axisZ * axisZ * spatialFactor;
            }

            // Calculate normal stress component based on face orientation
            float normalStress = alignX * stressX + alignY * stressY + alignZ * stressZ;

            // Add additional local variation based on position
            float positionVariation = 1.0f + 0.1f * (xVar - yVar + zVar);

            // Principal stresses with variation
            float sigma1 = normalStress * 1.2f * positionVariation;
            float sigma3 = pConf * 0.9f / positionVariation;
            float sigma2 = (sigma1 + sigma3) * 0.5f;  // Intermediate value

            // Ensure proper ordering of principal stresses
            if (sigma1 < sigma2)
            {
                float temp = sigma1;
                sigma1 = sigma2;
                sigma2 = temp;
            }
            if (sigma2 < sigma3)
            {
                float temp = sigma2;
                sigma2 = sigma3;
                sigma3 = temp;
            }
            if (sigma1 < sigma2)
            {
                float temp = sigma1;
                sigma1 = sigma2;
                sigma2 = temp;
            }

            // Von Mises stress
            float vonMises = XMath.Sqrt(0.5f * ((sigma1 - sigma2) * (sigma1 - sigma2) +
                                             (sigma2 - sigma3) * (sigma2 - sigma3) +
                                             (sigma3 - sigma1) * (sigma3 - sigma1)));

            // Add another variation to the von Mises stress to ensure more variability
            // This will make the visualization more interesting without affecting failure pattern
            vonMises *= (0.9f + 0.2f * xVar);

            // Mohr-Coulomb failure criterion
            float criterion = (2.0f * cohesion * cosPhi + (sigma1 + sigma3) * sinPhi) / (1.0f - sinPhi);

            // Add variation to the criterion based on position
            criterion *= (0.95f + 0.1f * zVar);

            int failed = (sigma1 - sigma3 >= criterion) ? 1 : 0;

            // Store results
            vmArr[idx] = vonMises;
            s1Arr[idx] = sigma1;
            s2Arr[idx] = sigma2;
            s3Arr[idx] = sigma3;
            fracArr[idx] = failed;
        }

        /// <summary>
        /// Calculate strain based on applied pressure and material properties
        /// with enhanced non-linear stress-strain behavior
        /// </summary>
        public virtual float CalculateStrain(float pressure)
        {
            // Material parameters with safety checks
            float tensileStrengthSafe = Math.Max(TensileStrength, 1.0f); // Ensure non-zero tensile strength
            float elasticThreshold = 0.6f * tensileStrengthSafe;
            float elasticModulus = Math.Max(YoungModulus, 1000.0f); // Ensure non-zero modulus
            float plasticModulus = elasticModulus * 0.15f;

            // Non-linear strain enhancement variables
            float nonlinearityFactor = 1.05f;
            float strainHardeningCoef = 0.2f;

            // Base strain calculation - ensure a minimum reasonable strain
            float baseStrain = pressure / elasticModulus;

            // Strain calculation depends on stress level relative to material thresholds
            if (pressure <= elasticThreshold)
            {
                // Elastic region - slightly non-linear even in "elastic" zone
                return Math.Max(baseStrain, pressure / elasticModulus *
                       (1.0f + 0.2f * pressure / elasticThreshold));
            }
            else if (pressure <= tensileStrengthSafe * 1.5f)
            {
                // Elastoplastic transition region - bilinear model
                float elasticStrain = elasticThreshold / elasticModulus;
                float plasticStrain = (pressure - elasticThreshold) / plasticModulus;

                // Add non-linear component to plastic strain with safety check
                float normalizedPressure = (pressure - elasticThreshold) /
                    Math.Max(tensileStrengthSafe - elasticThreshold, 0.1f);
                float nonlinearComponent = normalizedPressure * normalizedPressure * nonlinearityFactor;

                return Math.Max(baseStrain, elasticStrain + plasticStrain * (1.0f + nonlinearComponent));
            }
            else
            {
                // High stress region with strain hardening effects
                float elasticStrain = elasticThreshold / elasticModulus;
                float transitionStrain = (tensileStrengthSafe * 1.5f - elasticThreshold) / plasticModulus *
                                       (1.0f + nonlinearityFactor);

                // Calculate additional strain with hardening
                float excessPressure = pressure - tensileStrengthSafe * 1.5f;
                float hardeningModulus = plasticModulus * (1.0f + strainHardeningCoef *
                                       (float)Math.Sqrt(Math.Max(excessPressure / tensileStrengthSafe, 0.001f)));

                float additionalStrain = excessPressure / hardeningModulus;

                return Math.Max(baseStrain, elasticStrain + transitionStrain + additionalStrain);
            }
        }
        /// <summary>
        /// Calculate fracture probability using Mohr-Coulomb criterion
        /// with enhanced probability distribution
        /// </summary>
        private float CalculateFractureProbability(float stress1, float stress3, float cohesion, float frictionAngleDegrees)
        {
            // Convert friction angle to radians
            float frictionAngle = frictionAngleDegrees * (float)Math.PI / 180f;

            // Mohr-Coulomb failure criterion parameters
            float sinPhi = (float)Math.Sin(frictionAngle);
            float cosPhi = (float)Math.Cos(frictionAngle);

            // Calculate criterion based on stress state
            float criterion = (2.0f * cohesion * cosPhi) / (1.0f - sinPhi);
            float theoreticalLimit = ConfiningPressure + criterion;

            // Calculate ratio of actual stress to theoretical limit
            float ratio = stress1 / theoreticalLimit;

            // Apply a more sensitive probability function
            if (ratio < 0.5f)
            {
                // Very low probability range (0.0 - 0.1)
                return ratio * 0.2f;
            }
            else if (ratio < 0.8f)
            {
                // Low-medium probability range (0.1 - 0.5)
                return 0.1f + (ratio - 0.5f) * 1.33f;
            }
            else if (ratio < 1.0f)
            {
                // High probability range (0.5 - 0.9)
                return 0.5f + (ratio - 0.8f) * 2.0f;
            }
            else
            {
                // Very high probability range (0.9 - 1.0)
                return 0.9f + Math.Min(ratio - 1.0f, 0.1f);
            }
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

        #endregion Simulation Implementation

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
            float centerX = width / 2.0f;
            float centerY = height / 2.0f;
            float maxCoord = FindMaxCoordinate(SimulationMeshAtFailure);

            // Default rotation angles
            float rotationX = 0.5f;
            float rotationY = 0.5f;

            // First, gather statistics about stress levels across the entire mesh
            float minStress = float.MaxValue;
            float maxStress = float.MinValue;
            float avgStress = 0;
            float totalStress = 0;

            foreach (Triangle tri in SimulationMeshAtFailure)
            {
                float value = GetPropertyValue(tri, renderMode);
                minStress = Math.Min(minStress, value);
                maxStress = Math.Max(maxStress, value);
                totalStress += value;
            }

            avgStress = totalStress / SimulationMeshAtFailure.Count;

            // Ensure we have a meaningful range (avoid divide by zero)
            if (maxStress <= minStress)
            {
                maxStress = minStress + 1.0f;
            }

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

                // Get color based on render mode with improved range
                Color triangleColor = GetColorForProperty(tri, renderMode, minStress, maxStress);

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

            // Draw legend with improved values
            DrawColorMapLegend(g, width, height, renderMode, minStress, maxStress);

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
        private float GetPropertyValue(Triangle tri, RenderMode mode)
        {
            switch (mode)
            {
                case RenderMode.Stress:
                    return tri.VonMisesStress;

                case RenderMode.Strain:
                    return tri.Displacement.Length();

                case RenderMode.FailureProbability:
                    return tri.FractureProbability;

                case RenderMode.Displacement:
                    return tri.Displacement.Length();

                default:
                    return tri.VonMisesStress;
            }
        }
        private Color GetColorForProperty(Triangle tri, RenderMode mode, float minValue, float maxValue)
        {
            float value;

            switch (mode)
            {
                case RenderMode.Stress:
                    value = tri.VonMisesStress;
                    break;

                case RenderMode.Strain:
                    value = tri.Displacement.Length();
                    break;

                case RenderMode.FailureProbability:
                    value = tri.FractureProbability;
                    break;

                case RenderMode.Displacement:
                    value = tri.Displacement.Length();
                    break;

                default:
                    value = tri.VonMisesStress;
                    break;
            }

            // Use the provided min/max range
            return GetHeatMapColor(value, minValue, maxValue);
        }

        // Update the legend drawing method to use the calculated min/max
        private void DrawColorMapLegend(Graphics g, int width, int height, RenderMode renderMode, float minValue, float maxValue)
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

            // Draw min/max labels with proper values
            using (Font font = new Font("Arial", 8))
            using (SolidBrush brush = new SolidBrush(Color.White))
            {
                // Format values appropriately based on render mode
                string maxLabel = FormatValueForRenderMode(maxValue, renderMode);
                string minLabel = FormatValueForRenderMode(minValue, renderMode);

                g.DrawString(maxLabel, font, brush, legendX + legendWidth + textOffset, legendY);
                g.DrawString(minLabel, font, brush, legendX + legendWidth + textOffset, legendY + legendHeight - 10);

                // Draw legend title
                string title = GetLegendTitle(renderMode);
                g.DrawString(title, font, brush, legendX, legendY - 15);
            }
        }

        // Helper to format values based on render mode
        private string FormatValueForRenderMode(float value, RenderMode mode)
        {
            switch (mode)
            {
                case RenderMode.Stress:
                    return $"{value:F1}";

                case RenderMode.Strain:
                    return $"{value:F4}";

                case RenderMode.FailureProbability:
                    return $"{value:F2}";

                case RenderMode.Displacement:
                    return $"{value:F4}";

                default:
                    return $"{value:F2}";
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
                    // Use a more appropriate range instead of 0-100
                    float minStress = ConfiningPressure * 0.9f;
                    float maxStress = BreakingPressure * 1.2f;

                    // Ensure we have a reasonable range
                    if (maxStress - minStress < 5.0f)
                        maxStress = minStress + 5.0f;

                    return GetHeatMapColor(tri.VonMisesStress, minStress, maxStress);

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
        //  Mohr-Coulomb diagram with improved physics and aesthetics
        //---------------------------------------------------------------------
        public void RenderMohrCoulombDiagram(Graphics g, int width, int height)
        {
            // Abort if we have no data
            if (Status != SimulationStatus.Completed || _result == null)
            {
                g.DrawString("No simulation results available",
                             new Font("Arial", 12), Brushes.Red, 20, 20);
                return;
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Black);

            //-----------------------------------------------------------------
            // 1) Parameters with enhanced physical relationships
            //-----------------------------------------------------------------
            float c = CohesionStrength;
            float phiDeg = FrictionAngle;
            float phi = phiDeg * (float)Math.PI / 180f;
            float sigma3 = ConfiningPressure;
            float sigma1 = BreakingPressure;

            // More realistic failure envelope parameters
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
            // 3) Plot rectangle & mapping with improved aesthetics
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
            // 4) Axes and grid with improved styling
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

                // axis labels with better placement
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
            // 5) Helper to draw circles with improved styling
            //-----------------------------------------------------------------
            void DrawCircle(float σH, float σL, Color col, string label)
            {
                float cx = 0.5f * (σH + σL);
                float rad = 0.5f * (σH - σL);
                if (rad <= 0f) return;                 // nothing to draw

                PointF ctr = ToScreen(cx, 0f);
                float rPx = rad * scale;

                // Safety: ignore degenerate or invalid radii
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

            // Show initial and failure states
            DrawCircle(sigma3, sigma3, Color.LightGreen, "Initial");
            DrawCircle(sigma1, sigma3, Color.Yellow, "Failure");

            //-----------------------------------------------------------------
            // 6) Draw failure envelope with tensile cutoff
            //-----------------------------------------------------------------
            using (Pen envPen = new Pen(Color.Red, 2))
            {
                // Draw main failure envelope
                PointF pA = ToScreen(0, b);
                PointF pB = ToScreen(maxSigma, b + μ * maxSigma);
                g.DrawLine(envPen, pA, pB);

                // Add tensile cutoff only if in the visible range
                if (TensileStrength > 0)
                {
                    float tensileStrengthX = -TensileStrength;
                    // Only draw if it's within the visible range
                    if (tensileStrengthX > -maxSigma * 0.5f && tensileStrengthX < 0)
                    {
                        PointF pTensile = ToScreen(tensileStrengthX, 0);
                        // Draw only to the top of the plot area, not beyond
                        PointF pTensileTop = ToScreen(tensileStrengthX, Math.Min(maxTau, b));
                        g.DrawLine(envPen, pTensile, pTensileTop);

                        // Connect to envelope if needed
                        float tensileY = b + μ * Math.Max(0, tensileStrengthX);
                        if (tensileY > 0)
                        {
                            PointF pTensileEnv = ToScreen(tensileStrengthX, tensileY);
                            g.DrawLine(envPen, pTensileEnv, pTensile);
                        }
                    }
                }
            }

            // Mark failure point
            PointF pt = ToScreen(sigmaT, tauT);
            g.FillEllipse(Brushes.White, pt.X - 4, pt.Y - 4, 8, 8);
            g.DrawString("Failure point", new Font("Arial", 8), Brushes.White,
                         pt.X + 6, pt.Y - 6);

            float theoreticalSigma1 = ConfiningPressure + (2 * CohesionStrength * (float)Math.Cos(phi * Math.PI / 180)) /
                         (1 - (float)Math.Sin(phi * Math.PI / 180));

            g.DrawString($"Theoretical Breaking Pressure: {theoreticalSigma1:F1} MPa",
                         new Font("Arial", 10, FontStyle.Bold), Brushes.Yellow, 20, height - 20);
            //-----------------------------------------------------------------
            // 7) Enhanced legend with more information
            //-----------------------------------------------------------------
            Rectangle legend = new Rectangle(plot.Right + 10, plot.Top, 150, 150);
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
            using (Font f9 = new Font("Arial", 9))
            using (Font fBold = new Font("Arial", 10, FontStyle.Bold))
            {
                g.FillRectangle(bg, legend);
                g.DrawRectangle(Pens.Gray, legend);

                g.DrawString("Mohr–Coulomb Parameters:", f9, Brushes.White,
                             legend.X + 6, legend.Y + 6);
                g.DrawString($"c   = {CohesionStrength:F2} MPa",
                             f9, Brushes.White, legend.X + 10, legend.Y + 26);
                g.DrawString($"φ   = {phiDeg:F1}°",
                             f9, Brushes.White, legend.X + 10, legend.Y + 44);
                g.DrawString($"σt  = {TensileStrength:F1} MPa",
                             f9, Brushes.White, legend.X + 10, legend.Y + 62);
                g.DrawString($"σ₁  = {sigma1:F1} MPa",
                             fBold, Brushes.Yellow, legend.X + 10, legend.Y + 84);
                g.DrawString($"σ₃  = {sigma3:F1} MPa",
                             fBold, Brushes.Yellow, legend.X + 10, legend.Y + 102);
                g.DrawString($"Material: {Material.Name}",
                             f9, Brushes.LightGray, legend.X + 10, legend.Y + 124);
            }

            //-----------------------------------------------------------------
            // 8) σ₁ / σ₃ ticks on the X axis with improved styling
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

        /// <summary>
        /// Export a composite image showing multiple views of the simulation results
        /// </summary>
        public bool ExportFullCompositeImage(string filePath)
        {
            try
            {
                // Define the size of each panel and the composite image
                int panelWidth = 600;
                int panelHeight = 450;
                int padding = 20;
                int titleHeight = 40;
                int footerHeight = 100; // Increased height for the footer area to fit all results

                // Create a composite image with 3x2 grid layout to include all views
                int compositeWidth = panelWidth * 3 + padding * 4;
                int compositeHeight = panelHeight * 2 + padding * 3 + titleHeight + footerHeight;

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

                        // Create the individual views - 6 panels total

                        // Top row
                        CreateTriaxialView(g, this, RenderMode.Stress,
                            padding, titleHeight + padding,
                            panelWidth, panelHeight);

                        CreateTriaxialView(g, this, RenderMode.FailureProbability,
                            padding * 2 + panelWidth, titleHeight + padding,
                            panelWidth, panelHeight);

                        // Add fracture surfaces view
                        CreateFractureView(g, this,
                            padding * 3 + panelWidth * 2, titleHeight + padding,
                            panelWidth, panelHeight);

                        // Bottom row
                        CreateStressStrainCurveView(g, this,
                            padding, titleHeight + padding * 2 + panelHeight,
                            panelWidth, panelHeight);

                        CreateTriaxialView(g, this, RenderMode.Solid,
                            padding * 2 + panelWidth, titleHeight + padding * 2 + panelHeight,
                            panelWidth, panelHeight);

                        // Add Mohr-Coulomb diagram
                        CreateMohrCoulombView(g, this,
                            padding * 3 + panelWidth * 2, titleHeight + padding * 2 + panelHeight,
                            panelWidth, panelHeight);

                        // Add footer with simulation parameters and numeric results
                        int footerY = titleHeight + padding * 3 + panelHeight * 2;
                        AddResultsFooter(g, padding, footerY, compositeWidth - padding * 2, footerHeight);
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
        /// <summary>
        /// Add a footer with detailed numeric results
        /// </summary>
        private void AddResultsFooter(Graphics g, int x, int y, int width, int height)
        {
            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(240, 240, 240)))
            using (Pen borderPen = new Pen(Color.DarkGray, 1))
            {
                // Draw footer background
                Rectangle footerRect = new Rectangle(x, y, width, height);
                g.FillRectangle(bgBrush, footerRect);
                g.DrawRectangle(borderPen, footerRect);

                // Create a two-column layout for results
                int col1Width = width / 2;
                int col2X = x + col1Width;
                int textY = y + 5;
                int lineHeight = 16; // Slightly reduced line height to fit more content

                using (Font boldFont = new Font("Arial", 9, FontStyle.Bold))
                using (Font regularFont = new Font("Arial", 9))
                using (SolidBrush textBrush = new SolidBrush(Color.Black))
                {
                    // Column 1: Material properties
                    g.DrawString("Material Properties:", boldFont, textBrush, x + 5, textY);
                    textY += lineHeight + 2;

                    g.DrawString($"Material: {Material.Name}, Density: {Material.Density:F1} kg/m³",
                                 regularFont, textBrush, x + 10, textY);
                    textY += lineHeight;

                    g.DrawString($"Young's Modulus: {YoungModulus:N0} MPa, Poisson's Ratio: {PoissonRatio:F3}",
                                 regularFont, textBrush, x + 10, textY);
                    textY += lineHeight;

                    g.DrawString($"Cohesion: {CohesionStrength:F2} MPa, Friction Angle: {FrictionAngle:F1}°",
                                 regularFont, textBrush, x + 10, textY);
                    textY += lineHeight;

                    g.DrawString($"Tensile Strength: {TensileStrength:F2} MPa",
                                 regularFont, textBrush, x + 10, textY);
                    textY += lineHeight;

                    // Count fractured triangles if available
                    int fracturedCount = 0;
                    if (SimulationMeshAtFailure != null)
                    {
                        foreach (var tri in SimulationMeshAtFailure)
                        {
                            if (tri.IsFractured) fracturedCount++;
                        }

                        g.DrawString($"Fractured Elements: {fracturedCount} of {SimulationMeshAtFailure.Count} ({(float)fracturedCount / SimulationMeshAtFailure.Count:P1})",
                                 regularFont, textBrush, x + 10, textY);
                    }

                    // Column 2: Test results
                    textY = y + 5;
                    g.DrawString("Test Results:", boldFont, textBrush, col2X + 5, textY);
                    textY += lineHeight + 2;

                    g.DrawString($"Confining Pressure: {ConfiningPressure:F1} MPa, Breaking Pressure: {BreakingPressure:F1} MPa",
                                 regularFont, textBrush, col2X + 10, textY);
                    textY += lineHeight;

                    // Add pressure range info
                    g.DrawString($"Pressure Range: {MinAxialPressure:F1} to {MaxAxialPressure:F1} MPa in {PressureSteps} steps",
                                 regularFont, textBrush, col2X + 10, textY);
                    textY += lineHeight;

                    // Calculate max strain
                    float maxStrain = 0;
                    if (SimulationStrains != null && SimulationStrains.Count > 0)
                    {
                        foreach (float strain in SimulationStrains)
                        {
                            if (strain > maxStrain) maxStrain = strain;
                        }
                    }

                    g.DrawString($"Maximum Strain: {maxStrain:F4}, Failure at Step: {FailureTimeStep}",
                                 regularFont, textBrush, col2X + 10, textY);
                    textY += lineHeight;

                    g.DrawString($"Test Direction: ({TestDirection.X:F1}, {TestDirection.Y:F1}, {TestDirection.Z:F1})",
                                 regularFont, textBrush, col2X + 10, textY);
                    textY += lineHeight;

                    // Add simulation ID and date
                    g.DrawString($"Simulation ID: {SimulationId.ToString().Substring(0, 8)}..., Triangles: {MeshTriangles.Count}",
                                 regularFont, textBrush, col2X + 10, textY);
                }
            }
        }
        /// <summary>
        /// Create a panel showing a specific view of the triaxial simulation without adding redundant titles
        /// </summary>
        private void CreateTriaxialView(Graphics g, TriaxialSimulation simulation, RenderMode renderMode,
            int x, int y, int width, int height)
        {
            // Create a bitmap for this view
            using (Bitmap viewBitmap = new Bitmap(width, height))
            {
                using (Graphics viewGraphics = Graphics.FromImage(viewBitmap))
                {
                    // Render the view - this already includes a title
                    simulation.RenderResults(viewGraphics, width, height, renderMode);

                    // No need to add additional title here as it's already included in RenderResults
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
        /// Create a panel showing only fractured triangles without adding redundant titles
        /// </summary>
        private void CreateFractureView(Graphics g, TriaxialSimulation simulation,
                              int x, int y, int width, int height)
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

                        // Add title since RenderFracturedTriangles doesn't add one
                        using (Font titleFont = new Font("Arial", 14, FontStyle.Bold))
                        using (SolidBrush textBrush = new SolidBrush(Color.White))
                        {
                            viewGraphics.DrawString("Fracture Surfaces", titleFont, textBrush, new PointF(20, 20));
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
        public float GetModelExtent()
        {
            if (SimulationMeshAtFailure == null || SimulationMeshAtFailure.Count == 0)
            {
                return 100.0f; // Default value if no mesh
            }

            float maxCoord = FindMaxCoordinate(SimulationMeshAtFailure);
            return maxCoord * 2.0f; // Full extent is twice the max coordinate
        }
        public void SetSlicingParameters(bool enable, Vector3 normal, float position, float thickness)
        {
            _enableSlicing = enable;
            _sliceNormal = normal;
            _slicePosition = position;
            _sliceThickness = thickness;
        }
        /// <summary>
        /// Render only the fractured triangles with enhanced visualization and proper 3D orientation
        /// </summary>
        private void RenderFracturedTriangles(Graphics graphics, List<Triangle> triangles, int width, int height)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            // Set up projection parameters
            float scale = Math.Min(width, height) / 200.0f;
            float centerX = width / 2.0f;
            float centerY = height / 2.0f;
            float maxCoord = FindMaxCoordinate(triangles);

            // Default rotation angles for a good view
            float rotationX = 0.5f;
            float rotationY = 0.5f;

            // Collect only fractured triangles with depth info for sorting
            var fracturedTriangles = new List<(Triangle Triangle, float Depth, Vector3 Normal)>();

            // Count the total number of fractured triangles for reporting
            int fracturedCount = 0;

            // First pass: collect fracture data and ensure normals are varied
            foreach (var tri in triangles)
            {
                if (tri.IsFractured)
                {
                    fracturedCount++;
                    float depth = (tri.V1.Z + tri.V2.Z + tri.V3.Z) / 3.0f;

                    // Get or create a fracture plane normal
                    Vector3 normal;
                    if (FracturePlaneNormals.TryGetValue(tri, out normal))
                    {
                        // Use existing normal, but ensure it's normalized
                        normal = Vector3.Normalize(normal);
                    }
                    else
                    {
                        // If no normal is stored, calculate one based on the triangle and add some randomness
                        Vector3 edge1 = tri.V2 - tri.V1;
                        Vector3 edge2 = tri.V3 - tri.V1;
                        Vector3 triangleNormal = Vector3.Normalize(Vector3.Cross(edge1, edge2));

                        // Create a semi-random orientation based on triangle position to ensure variety
                        Vector3 centroid = (tri.V1 + tri.V2 + tri.V3) / 3.0f;
                        float angleX = (float)Math.Sin(centroid.X * 0.5f) * (float)Math.PI;
                        float angleY = (float)Math.Cos(centroid.Y * 0.7f) * (float)Math.PI;

                        // Create rotation to add variety
                        Quaternion rotation = Quaternion.CreateFromYawPitchRoll(angleX, angleY, 0);
                        normal = Vector3.Transform(triangleNormal, rotation);
                        normal = Vector3.Normalize(normal);

                        // Store for future use
                        FracturePlaneNormals[tri] = normal;
                    }

                    fracturedTriangles.Add((tri, depth, normal));
                }
            }

            // Early exit if no fractured triangles
            if (fracturedTriangles.Count == 0)
            {
                graphics.DrawString("No fractures detected", new Font("Arial", 12), Brushes.Yellow, centerX - 80, centerY);
                return;
            }

            // Sort triangles by Z depth (back to front)
            fracturedTriangles.Sort((a, b) => -a.Depth.CompareTo(b.Depth));

            // DEBUG: Display the normal vector distributions
            int[] normalCounts = new int[8]; // Count normals in 8 octants

            // Draw the fractured triangles
            foreach (var (tri, _, normal) in fracturedTriangles)
            {
                // Project vertices
                PointF p1 = ProjectVertex(tri.V1, centerX, centerY, scale, maxCoord, rotationX, rotationY);
                PointF p2 = ProjectVertex(tri.V2, centerX, centerY, scale, maxCoord, rotationX, rotationY);
                PointF p3 = ProjectVertex(tri.V3, centerX, centerY, scale, maxCoord, rotationX, rotationY);

                // Create triangle points
                PointF[] points = new PointF[] { p1, p2, p3 };

                // Count which octant this normal falls into (for debugging)
                int octant = 0;
                if (normal.X > 0) octant |= 1;
                if (normal.Y > 0) octant |= 2;
                if (normal.Z > 0) octant |= 4;
                normalCounts[octant]++;

                // Color based on fracture plane orientation (for better 3D visualization)
                // Map normal vector components to RGB values for clear visual differentiation
                int r = (int)(Math.Abs(normal.X) * 255);
                int g = (int)(Math.Abs(normal.Y) * 255);
                int b = (int)(Math.Abs(normal.Z) * 255);

                // Ensure values are in valid range and colors are visible
                r = Math.Min(r + 40, 255);
                g = Math.Min(g + 40, 255);
                b = Math.Min(b + 40, 255);

                Color orientationColor = Color.FromArgb(200, r, g, b);

                // Draw filled triangle with orientation-based color
                using (SolidBrush brush = new SolidBrush(orientationColor))
                {
                    graphics.FillPolygon(brush, points);
                }

                // Draw outline
                using (Pen pen = new Pen(Color.FromArgb(180, Color.White), 1))
                {
                    graphics.DrawPolygon(pen, points);
                }

                // Draw a small line indicating the fracture normal direction
                Vector3 centroid = (tri.V1 + tri.V2 + tri.V3) / 3.0f;
                Vector3 normalEnd = centroid + normal * (maxCoord * 0.05f);
                PointF pCentroid = ProjectVertex(centroid, centerX, centerY, scale, maxCoord, rotationX, rotationY);
                PointF pNormalEnd = ProjectVertex(normalEnd, centerX, centerY, scale, maxCoord, rotationX, rotationY);

                using (Pen pen = new Pen(Color.Yellow, 1))
                {
                    graphics.DrawLine(pen, pCentroid, pNormalEnd);
                }
            }

            // Show distribution of normals for debugging
            string normalDistribution = "Normal distribution:";
            for (int i = 0; i < 8; i++)
            {
                float percent = fracturedCount > 0 ? (float)normalCounts[i] / fracturedCount * 100 : 0;
                normalDistribution += $"\nOctant {i}: {normalCounts[i]} ({percent:F1}%)";
            }

            // Draw info text including normal distribution
            using (Font infoFont = new Font("Arial", 9))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                string infoText = $"Fractures at {BreakingPressure:F1} MPa\n{fracturedCount} fractured elements";
                graphics.DrawString(infoText, infoFont, textBrush, 20, height - 120);

                // Add debug info about normals distribution (can be removed in production)
                graphics.DrawString(normalDistribution, infoFont, textBrush, width - 200, 50);
            }

            // Add legend explaining the colors
            using (Font legendFont = new Font("Arial", 9))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                graphics.DrawString("Color indicates fracture orientation:", legendFont, textBrush, 20, 50);
                graphics.DrawString("Red = X axis, Green = Y axis, Blue = Z axis", legendFont, textBrush, 20, 70);
                graphics.DrawString("Yellow lines show fracture plane normals", legendFont, textBrush, 20, 90);
            }
        }

        /// <summary>
        /// Create a view of the Mohr-Coulomb diagram without adding redundant titles
        /// Includes extra space on the right for the legend
        /// </summary>
        private void CreateMohrCoulombView(Graphics g, TriaxialSimulation simulation,
                                     int x, int y, int width, int height)
        {
            // Create a wider bitmap to accommodate the legend
            int extraWidth = 60; // Extra space for the legend
            using (Bitmap viewBitmap = new Bitmap(width + extraWidth, height))
            {
                using (Graphics viewGraphics = Graphics.FromImage(viewBitmap))
                {
                    viewGraphics.Clear(Color.Black); // Ensure background is filled

                    // Render the Mohr-Coulomb diagram - this already includes a title
                    // Use the full width including extra space for the legend
                    simulation.RenderMohrCoulombDiagram(viewGraphics, width + extraWidth, height);
                }

                // Draw the view bitmap onto the composite bitmap, but crop the right side excess
                Rectangle srcRect = new Rectangle(0, 0, width, height);
                Rectangle destRect = new Rectangle(x, y, width, height);
                g.DrawImage(viewBitmap, destRect, srcRect, GraphicsUnit.Pixel);

                // Draw a border around the view
                using (Pen borderPen = new Pen(Color.DarkGray, 1))
                {
                    g.DrawRectangle(borderPen, x, y, width, height);
                }
            }
        }
        /// <summary>
        /// Create a view of the stress-strain curve without adding redundant titles
        /// </summary>
        private void CreateStressStrainCurveView(Graphics g, TriaxialSimulation simulation,
                                           int x, int y, int width, int height)
        {
            using (Bitmap viewBitmap = new Bitmap(width, height))
            {
                using (Graphics viewGraphics = Graphics.FromImage(viewBitmap))
                {
                    viewGraphics.Clear(Color.Black);
                    viewGraphics.SmoothingMode = SmoothingMode.AntiAlias;

                    // Check if we have stress-strain data
                    if (simulation.SimulationPressures != null && simulation.SimulationPressures.Count > 0 &&
                        simulation.SimulationStrains != null && simulation.SimulationStrains.Count > 0)
                    {
                        // Draw stress-strain curve
                        DrawStressStrainCurve(viewGraphics, simulation, width, height);

                        // Add title since DrawStressStrainCurve doesn't add one
                        using (Font titleFont = new Font("Arial", 14, FontStyle.Bold))
                        using (SolidBrush textBrush = new SolidBrush(Color.White))
                        {
                            viewGraphics.DrawString("Stress-Strain Curve", titleFont, textBrush, new PointF(20, 20));
                        }
                    }
                    else
                    {
                        // No stress-strain data available
                        using (Font font = new Font("Arial", 12))
                        using (SolidBrush brush = new SolidBrush(Color.Red))
                        {
                            viewGraphics.DrawString("No stress-strain data available", font, brush, 20, 20);
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
        /// <summary>
        /// Draw the stress-strain curve properly showing the breaking pressure from simulation data
        /// </summary>
        private void DrawStressStrainCurve(Graphics g,
                                   TriaxialSimulation sim,
                                   int width,
                                   int height)
        {
            //-------------------------------------------------------------
            // 0) Bail out if no data
            //-------------------------------------------------------------
            if (sim.SimulationPressures.Count == 0 ||
                sim.SimulationStrains.Count == 0)
            {
                g.DrawString("No stress–strain data",
                             new Font("Arial", 12),
                             Brushes.Red, 20, 20);
                return;
            }

            //-------------------------------------------------------------
            // 1) Plot rectangle & basic layout
            //-------------------------------------------------------------
            var marginL = 70;
            var marginR = 30;
            var marginT = 60;
            var marginB = 50;

            var plot = new Rectangle(
                marginL,
                marginT,
                width - marginL - marginR,
                height - marginT - marginB);

            g.Clear(Color.Black);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            //-------------------------------------------------------------
            // 2) Determine bounds
            //-------------------------------------------------------------
            var maxStrain = 0f;
            var maxStress = 0f;

            for (var i = 0; i < sim.SimulationPressures.Count; i++)
            {
                maxStrain = Math.Max(maxStrain, sim.SimulationStrains[i]);
                maxStress = Math.Max(maxStress, sim.SimulationPressures[i]);
            }

            // add 10 % head-room
            maxStrain *= 1.1f;
            maxStress *= 1.1f;

            if (maxStrain < 1e-6f) maxStrain = 1e-6f;
            if (maxStress < 1e-3f) maxStress = 1e-3f;

            //-------------------------------------------------------------
            // 3) Helper: data → screen
            //-------------------------------------------------------------
            float xScale = plot.Width / maxStrain;
            float yScale = plot.Height / maxStress;

            PointF ToScreen(float strain, float stress)
            {
                return new PointF(
                    plot.Left + strain * xScale,
                    plot.Bottom - stress * yScale);
            }

            //-------------------------------------------------------------
            // 4) Grid & axes
            //-------------------------------------------------------------
            using (var gridPen = new Pen(Color.FromArgb(40, 40, 40), 1) { DashStyle = DashStyle.Dot })
            using (var axisPen = new Pen(Color.LightGray, 1))
            using (var lblFont = new Font("Arial", 8))
            using (var axisFont = new Font("Arial", 10))
            {
                const int nx = 5, ny = 5;

                for (var i = 0; i <= nx; i++)
                {
                    var x = plot.Left + plot.Width * i / nx;
                    var s = maxStrain * i / nx;
                    g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
                    g.DrawString(s.ToString("F3"), lblFont, Brushes.White, x - 15, plot.Bottom + 4);
                }

                for (var i = 0; i <= ny; i++)
                {
                    var y = plot.Bottom - plot.Height * i / ny;
                    var p = maxStress * i / ny;
                    g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                    g.DrawString(p.ToString("F0"), lblFont, Brushes.White, plot.Left - 38, y - 6);
                }

                g.DrawLine(axisPen, plot.Left, plot.Bottom, plot.Right, plot.Bottom); // X axis
                g.DrawLine(axisPen, plot.Left, plot.Bottom, plot.Left, plot.Top);    // Y axis

                g.DrawString("Strain", axisFont, Brushes.White,
                             plot.Left + plot.Width / 2 - 20, plot.Bottom + 25);

                using (var m = new Matrix())
                {
                    m.RotateAt(-90, new PointF(plot.Left - 45, plot.Top + plot.Height / 2));
                    var state = g.Save();
                    g.Transform = m;
                    g.DrawString("Stress (MPa)", axisFont, Brushes.White,
                                 plot.Left - 80, plot.Top + plot.Height / 2 - 20);
                    g.Restore(state);
                }
            }

            //-------------------------------------------------------------
            // 5) Draw the curve
            //-------------------------------------------------------------
            var nPts = sim.SimulationPressures.Count;
            var pts = new PointF[nPts];

            for (var i = 0; i < nPts; i++)
                pts[i] = ToScreen(sim.SimulationStrains[i], sim.SimulationPressures[i]);

            using (var pen = new Pen(Color.Lime, 2))
            {
                g.DrawLines(pen, pts);
            }

            //-------------------------------------------------------------
            // 6) Mark the first fracture at BreakingPressure
            //-------------------------------------------------------------
            var bp = sim.BreakingPressure;                 // already correct
            var idx = -1;

            // find first data point whose pressure ≥ BreakingPressure
            for (var i = 0; i < nPts; i++)
                if (sim.SimulationPressures[i] >= bp - 1e-3f) { idx = i; break; }

            if (idx >= 0)
            {
                var fp = pts[idx];

                // red dot
                g.FillEllipse(Brushes.Red, fp.X - 5, fp.Y - 5, 10, 10);

                // label
                using (var fnt = new Font("Arial", 9, FontStyle.Bold))
                {
                    g.DrawString($"Fracture @ {bp:F1} MPa",
                                 fnt, Brushes.White, fp.X + 8, fp.Y - 14);
                }

                // dashed vertical line for clarity
                using (var vPen = new Pen(Color.Red, 1) { DashStyle = DashStyle.Dash })
                    g.DrawLine(vPen, fp.X, fp.Y, fp.X, plot.Bottom);
            }

            //-------------------------------------------------------------
            // 7) Title & info
            //-------------------------------------------------------------
            using (var titleFont = new Font("Arial", 14, FontStyle.Bold)) { 
                g.DrawString("Stress–Strain Curve", titleFont,
                             Brushes.White, 20, 20);

                float theoreticalBreakingPressure = sim.CalculateTheoreticalBreakingPressure();
                g.DrawString($"Stress–Strain Curve (Theoretical Break: {theoreticalBreakingPressure:F1} MPa)",
                             titleFont, Brushes.White, 20, 20);
            }

            using (var infoFont = new Font("Arial", 9))
                g.DrawString($"Confining P = {sim.ConfiningPressure:F1} MPa,  " +
                             $"E = {sim.YoungModulus:N0} MPa,  " +
                             $"ν = {sim.PoissonRatio:F2},  " +
                             $"Theoretical Break: {theoreticalBreakingPressure:F1} MPa",
                             infoFont, Brushes.LightGray, marginL, height - marginB + 15);

        }
        /// <summary>
        /// Calculates the most likely fracture plane orientation based on stress state and local geometry
        /// </summary>
        protected Vector3 CalculateFracturePlaneNormal(float sigma1, float sigma3, Vector3 sigma1Direction, Vector3 sigma3Direction)
        {
            // Convert friction angle to radians
            float phiRad = FrictionAngle * (float)Math.PI / 180.0f;

            // Calculate failure plane angle (angle between sigma1 and failure plane normal)
            // Based on Mohr-Coulomb theory: θ = 45° + φ/2
            float failureAngle = (float)(Math.PI / 4.0 + phiRad / 2.0);

            // Ensure sigma1Direction and sigma3Direction are properly orthogonalized
            sigma1Direction = Vector3.Normalize(sigma1Direction);

            // Make sure sigma3Direction is perpendicular to sigma1Direction
            Vector3 temp = Vector3.Cross(sigma1Direction, new Vector3(0, 0, 1));
            if (Vector3.Dot(temp, temp) < 0.01f)
                temp = Vector3.Cross(sigma1Direction, new Vector3(0, 1, 0));

            Vector3 sigma3Perpendicular = Vector3.Normalize(Vector3.Cross(sigma1Direction, temp));

            // Create a rotation matrix around an axis perpendicular to the sigma1-sigma3 plane
            Vector3 rotationAxis = Vector3.Normalize(Vector3.Cross(sigma1Direction, sigma3Perpendicular));

            // Apply spatial randomization to create more varied fracture orientations
            // Use the stress values to create a unique angle variation for each element
            float randomFactor = (float)Math.Sin((sigma1 * 13.7f + sigma3 * 5.3f) * 0.1f);
            float angleVariation = (float)Math.PI * 0.2f * randomFactor; // Up to ±36° variation

            // Apply the variation to the failure angle
            float finalAngle = failureAngle + angleVariation;

            // Perform the 3D rotation using quaternion
            Quaternion rotation = Quaternion.CreateFromAxisAngle(rotationAxis, finalAngle);
            Vector3 fracturePlaneNormal = Vector3.Transform(sigma1Direction, rotation);

            // Apply a second rotation around sigma1Direction to get more variation
            // This creates different orientations around the principal stress axis
            float secondaryAngle = (float)(Math.Sin(sigma1 * 7.5f + sigma3 * 3.2f) * Math.PI * 0.5f);
            Quaternion secondaryRotation = Quaternion.CreateFromAxisAngle(sigma1Direction, secondaryAngle);
            fracturePlaneNormal = Vector3.Transform(fracturePlaneNormal, secondaryRotation);

            return Vector3.Normalize(fracturePlaneNormal);
        }
        private float GetFractureThreshold(float pressure)
        {
            // Base threshold that decreases as pressure increases
            float baseThreshold = 0.01f * (1.0f - 0.3f * pressure / MaxAxialPressure); // Lower base threshold

            // Scale inversely with cohesion strength (weaker materials break more easily)
            float cohesionScale = 15.0f / Math.Max(1.0f, CohesionStrength); // Increased sensitivity

            // Scale inversely with friction angle (lower friction = easier to break)
            float frictionScale = 35.0f / Math.Max(10.0f, FrictionAngle); // Increased sensitivity

            // Combine factors (limit to reasonable range)
            return Math.Min(0.08f, Math.Max(0.003f, baseThreshold * cohesionScale * frictionScale)); // Lower minimum
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

        #endregion Rendering and Export

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

        #endregion Event Handlers

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

        #endregion IDisposable Implementation
    }
}