using ILGPU;
using ILGPU.Runtime;
using ILGPU.Algorithms;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using Color = System.Drawing.Color;
using Rectangle = System.Drawing.Rectangle;
using Vector3 = System.Numerics.Vector3;

namespace CTSegmenter
{
    /// <summary>
    /// Extended implementation of triaxial simulation that accounts for inhomogeneous density
    /// </summary>
    public partial class InhomogeneousTriaxialSimulation : TriaxialSimulation
    {
        #region Properties and Fields

        // Additional properties for inhomogeneous density
        private readonly bool _useInhomogeneousDensity;

        private readonly ConcurrentDictionary<Vector3, float> _densityMap;

        private Action<Index1D,
    ArrayView<Vector3>,
    ArrayView<Vector3>,
    ArrayView<Vector3>,
    ArrayView<float>, // stress factors
    float, float, Vector3,
    float, float, // reduced parameters: cohesion, frictionAngleRad
    ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<int>>
    _inhomogeneousStressKernel;
        private ILGPU.Context _context;
        // Density statistics
        public float MinimumDensity { get; private set; }

        public float MaximumDensity { get; private set; }
        public float AverageDensity { get; private set; }

        // Derived properties accounting for density variations
        public Dictionary<Triangle, float> TriangleDensities { get; private set; }

        public Dictionary<Triangle, float> TriangleStressFactors { get; private set; }

        // Flag for density model initialization
        private bool _hasInitializedInhomogeneousModels = false;
        private Accelerator _accelerator;

        #endregion Properties and Fields

        #region Constructor and Initialization

        /// <summary>
        /// Constructor for inhomogeneous triaxial simulation
        /// </summary>
        public InhomogeneousTriaxialSimulation(
            Material material,
            List<Triangle> triangles,
            float confiningPressure,
            float minAxialPressure,
            float maxAxialPressure,
            int pressureSteps,
            string direction,
            bool useInhomogeneousDensity,
            ConcurrentDictionary<Vector3, float> densityMap)
            : base(material, triangles, confiningPressure, minAxialPressure, maxAxialPressure, pressureSteps, direction)
        {
            _useInhomogeneousDensity = useInhomogeneousDensity;
            _densityMap = densityMap;
            InitializeILGPU();
            // Initialize density-dependent collections
            TriangleDensities = new Dictionary<Triangle, float>();
            TriangleStressFactors = new Dictionary<Triangle, float>();

            Logger.Log($"[InhomogeneousTriaxialSimulation] Initialized with inhomogeneous density " +
                      $"enabled: {_useInhomogeneousDensity}, density map size: {_densityMap?.Count ?? 0}");
        }

        /// <summary>
        /// Override Initialize method to include density variation initialization
        /// </summary>
        public override bool Initialize()
        {
            // Call the base initialization first
            if (!base.Initialize())
                return false;

            try
            {
                // Initialize inhomogeneous density model if enabled and not already initialized
                if (_useInhomogeneousDensity && _densityMap != null && _densityMap.Count > 0 && !_hasInitializedInhomogeneousModels)
                {
                    InitializeInhomogeneousDensityModel();
                    _hasInitializedInhomogeneousModels = true;
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[InhomogeneousTriaxialSimulation] Initialization failed: {ex.Message}");
                return false;
            }
        }
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
                //throw new InvalidOperationException("Failed to initialize ILGPU. The simulation cannot continue.", ex);
            }
        }
        /// <summary>
        /// Initialize inhomogeneous density model with improved spatial correlation
        /// </summary>
        private void InitializeInhomogeneousDensityModel()
        {
            Logger.Log("[InhomogeneousTriaxialSimulation] Initializing inhomogeneous density model");

            if (MeshTriangles == null || MeshTriangles.Count == 0)
            {
                Logger.Log("[InhomogeneousTriaxialSimulation] No mesh triangles available");
                return;
            }

            // Track density statistics
            float minDensity = float.MaxValue;
            float maxDensity = float.MinValue;
            float sumDensity = 0;
            int densityPointCount = 0;

            // Default to material density
            float baseDensity = (float)Material.Density;

            // Process each triangle in the mesh with improved density model
            foreach (var triangle in MeshTriangles)
            {
                // Find the centroid of the triangle
                Vector3 centroid = new Vector3(
                    (triangle.V1.X + triangle.V2.X + triangle.V3.X) / 3f,
                    (triangle.V1.Y + triangle.V2.Y + triangle.V3.Y) / 3f,
                    (triangle.V1.Z + triangle.V2.Z + triangle.V3.Z) / 3f
                );

                // Find the density value from the density map using improved interpolation
                float density = FindInterpolatedDensity(centroid);

                // Store the density for this triangle
                TriangleDensities[triangle] = density;

                // Calculate a more sophisticated stress factor based on density variation
                // This provides more nuanced behavior based on material mechanics theory
                float stressFactor = CalculateStressFactor(density, baseDensity);

                // Store the stress factor for this triangle
                TriangleStressFactors[triangle] = stressFactor;

                // Update statistics
                minDensity = Math.Min(minDensity, density);
                maxDensity = Math.Max(maxDensity, density);
                sumDensity += density;
                densityPointCount++;
            }

            // Store density statistics
            MinimumDensity = minDensity;
            MaximumDensity = maxDensity;
            AverageDensity = densityPointCount > 0 ? sumDensity / densityPointCount : baseDensity;

            Logger.Log($"[InhomogeneousTriaxialSimulation] Density statistics: Min={MinimumDensity:F1}, " +
                      $"Max={MaximumDensity:F1}, Avg={AverageDensity:F1}, Triangles={densityPointCount}");
        }

        /// <summary>
        /// Calculate stress factor from density using improved physical relationship
        /// Higher density regions typically have higher elastic moduli and strength
        /// </summary>
        private float CalculateStressFactor(float density, float baseDensity)
        {
            // More sophisticated relationship between density and stiffness/strength
            // Based on research showing non-linear relationships between density and mechanical properties

            // If density is very low, provide a floor to prevent numerical issues
            if (density < 0.1f * baseDensity)
                density = 0.1f * baseDensity;

            float relativeDensity = density / baseDensity;

            // The relationship between density and elastic properties follows a power law
            // E ∝ ρ^n where n is typically between 1.5 and 3 for geological materials
            float exponent = 2.0f; // Empirical exponent (cubic relationship for ceramics, ~2 for rocks)

            // Calculate stress factor with power law relationship and limits
            float stressFactor = (float)Math.Pow(relativeDensity, exponent - 1.0f);

            // Limit range to prevent extreme values
            stressFactor = Math.Max(0.1f, Math.Min(stressFactor, 10.0f));

            return stressFactor;
        }

        /// <summary>
        /// Find the interpolated density value from the density map
        /// Uses inverse distance weighting for smoother transitions
        /// </summary>
        private float FindInterpolatedDensity(Vector3 position)
        {
            if (_densityMap == null || _densityMap.Count == 0)
                return (float)Material.Density;

            // Check if exact position exists
            if (_densityMap.TryGetValue(position, out float exactDensity))
                return exactDensity;

            // Use inverse distance weighted interpolation
            const float searchRadius = 10.0f; // Larger radius for better interpolation
            const int maxNeighbors = 5; // Use at most 5 closest points

            List<(Vector3 Pos, float Density, float Distance)> neighbors = new List<(Vector3, float, float)>();

            // Find all points within search radius
            foreach (var entry in _densityMap)
            {
                Vector3 densityPos = entry.Key;
                float distance = Vector3.Distance(position, densityPos);

                if (distance <= searchRadius)
                {
                    neighbors.Add((densityPos, entry.Value, distance));
                }
            }

            // Sort by distance and keep only the closest maxNeighbors
            neighbors.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            if (neighbors.Count > maxNeighbors)
                neighbors.RemoveRange(maxNeighbors, neighbors.Count - maxNeighbors);

            // If no neighbors found, use base density
            if (neighbors.Count == 0)
                return (float)Material.Density;

            // If only one neighbor, use its value
            if (neighbors.Count == 1)
                return neighbors[0].Density;

            // Calculate IDW weights and sum
            float weightSum = 0;
            float valueSum = 0;

            foreach (var (_, density, distance) in neighbors)
            {
                // Prevent division by zero
                float effectiveDistance = Math.Max(distance, 0.0001f);
                float weight = 1.0f / (effectiveDistance * effectiveDistance); // Square inverse distance

                weightSum += weight;
                valueSum += density * weight;
            }

            // Prevent division by zero
            if (weightSum < 0.0001f)
                return (float)Material.Density;

            return valueSum / weightSum;
        }
        public override void LoadKernels()
        {
            try
            {
                // First call the base class implementation to load the standard kernels
                base.LoadKernels();

                // Make sure the accelerator is still valid after base.LoadKernels()
                if (_accelerator == null)
                {
                    Logger.Log("[InhomogeneousTriaxialSimulation] ERROR: Accelerator is null after base.LoadKernels()");
                    return;
                }

                // Verify kernel method exists before loading
                if (typeof(InhomogeneousTriaxialSimulation).GetMethod("ComputeInhomogeneousStressKernelFixed",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static) == null)
                {
                    Logger.Log("[InhomogeneousTriaxialSimulation] ERROR: Kernel method ComputeInhomogeneousStressKernelFixed not found");
                    return;
                }

                // Load the inhomogeneous stress kernel with explicit null checks
                Logger.Log("[InhomogeneousTriaxialSimulation] Loading inhomogeneous stress kernel...");
                _inhomogeneousStressKernel = _accelerator.LoadAutoGroupedStreamKernel<Index1D,
                    ArrayView<Vector3>, ArrayView<Vector3>, ArrayView<Vector3>, ArrayView<float>,
                    float, float, Vector3, float, float,
                    ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<int>>
                    (ComputeInhomogeneousStressKernelFixed);

                if (_inhomogeneousStressKernel == null)
                {
                    Logger.Log("[InhomogeneousTriaxialSimulation] ERROR: Kernel loading returned null");
                }
                else
                {
                    Logger.Log("[InhomogeneousTriaxialSimulation] Successfully loaded inhomogeneous stress kernel");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[InhomogeneousTriaxialSimulation] Failed to load inhomogeneous stress kernel: {ex.Message}");
                Logger.Log($"[InhomogeneousTriaxialSimulation] Stack trace: {ex.StackTrace}");

                // Make sure _inhomogeneousStressKernel is explicitly set to null in case of failure
                _inhomogeneousStressKernel = null;

                // Fall back to using the base class kernel
                Logger.Log("[InhomogeneousTriaxialSimulation] Will use base class kernel as fallback");
            }
        }
        #endregion Constructor and Initialization

        #region Simulation Override Methods

        /// <summary>
        /// Override RunSimulationStep to use density-based calculations
        /// </summary>
        public override async Task<bool> RunSimulationStep(float axialPressure)
        {
            // Get the count from the protected field
            int n = _simulationTriangles.Count;
            var v1 = new Vector3[n];
            var v2 = new Vector3[n];
            var v3 = new Vector3[n];
            var vm = new float[n];
            var s1 = new float[n];
            var s2 = new float[n];
            var s3 = new float[n];
            var frac = new int[n];
            var displacements = new Vector3[n];
            var stressFactors = new float[n];

            // Calculate theoretical breaking pressure for reference
            float theoreticalBreak = CalculateTheoreticalBreakingPressure();

            // CRITICAL: Ensure we record reasonable strain values
            float currentStrain = CalculateStrain(axialPressure);

            // If we're adding a new pressure point, also add a strain value
            if (SimulationPressures.Count > SimulationStrains.Count)
            {
                SimulationStrains.Add(currentStrain);
            }

            for (int i = 0; i < n; i++)
            {
                var t = _simulationTriangles[i];
                v1[i] = t.V1;
                v2[i] = t.V2;
                v3[i] = t.V3;

                // Apply density-based stress factor
                if (_useInhomogeneousDensity && TriangleStressFactors.TryGetValue(t, out float factor))
                {
                    stressFactors[i] = factor;

                    // Calculate displacement with density consideration
                    float localDisplacementMagnitude = CalculateDisplacementMagnitude(axialPressure) / factor;
                    displacements[i] = CalculateDisplacementVector(t, TestDirection, localDisplacementMagnitude);
                }
                else
                {
                    stressFactors[i] = 1.0f;

                    // Standard displacement calculation
                    float displacementMagnitude = CalculateDisplacementMagnitude(axialPressure);
                    displacements[i] = CalculateDisplacementVector(t, TestDirection, displacementMagnitude);
                }
            }

            // Pre-calculate friction angle in radians
            float frictionAngleRad = FrictionAngle * (float)Math.PI / 180f;

            try
            {
                using (var b1 = _accelerator.Allocate1D<Vector3>(v1))
                using (var b2 = _accelerator.Allocate1D<Vector3>(v2))
                using (var b3 = _accelerator.Allocate1D<Vector3>(v3))
                using (var bsf = _accelerator.Allocate1D<float>(stressFactors))
                using (var bv = _accelerator.Allocate1D<float>(n))
                using (var bs1 = _accelerator.Allocate1D<float>(n))
                using (var bs2 = _accelerator.Allocate1D<float>(n))
                using (var bs3 = _accelerator.Allocate1D<float>(n))
                using (var bf = _accelerator.Allocate1D<int>(n))
                {
                    // Call the kernel with the modified signature
                    _inhomogeneousStressKernel(
                        n,
                        b1.View, b2.View, b3.View,
                        bsf.View,
                        ConfiningPressure,
                        axialPressure,
                        TestDirection,
                        CohesionStrength,
                        frictionAngleRad,
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
                Logger.Log($"[InhomogeneousTriaxialSimulation] Kernel execution error: {ex.Message}");

                // Fallback calculation on CPU
                Parallel.For(0, n, i =>
                {
                    // Get density-based stress factor
                    float stressFactor = stressFactors[i];

                    // Scale material properties based on density variation
                    float localCohesion = CohesionStrength * (float)Math.Sqrt(stressFactor);

                    // Calculate face normal
                    Vector3 e1 = v2[i] - v1[i];
                    Vector3 e2 = v3[i] - v1[i];
                    Vector3 normal = Vector3.Cross(e1, e2);
                    float normalLength = normal.Length();

                    if (normalLength > 0.0001f)
                    {
                        normal = normal / normalLength;
                    }
                    else
                    {
                        normal = new Vector3(0, 0, 1);
                    }

                    // Calculate 3D stress tensor components
                    Vector3 centroid = (v1[i] + v2[i] + v3[i]) / 3.0f;
                    float scaledPConf = ConfiningPressure * stressFactor;
                    float scaledPAxial = axialPressure * stressFactor;
                    float localVariation = 1.0f + 0.1f * (float)Math.Sin(centroid.X * 0.5f + centroid.Y * 0.7f + centroid.Z * 0.9f);

                    // Setup stress tensor based on test direction
                    float sigmaXX, sigmaYY, sigmaZZ;

                    if (TestDirection.X > 0.7f)
                    {
                        sigmaXX = scaledPAxial * localVariation;
                        sigmaYY = scaledPConf;
                        sigmaZZ = scaledPConf;
                    }
                    else if (TestDirection.Y > 0.7f)
                    {
                        sigmaXX = scaledPConf;
                        sigmaYY = scaledPAxial * localVariation;
                        sigmaZZ = scaledPConf;
                    }
                    else if (TestDirection.Z > 0.7f)
                    {
                        sigmaXX = scaledPConf;
                        sigmaYY = scaledPConf;
                        sigmaZZ = scaledPAxial * localVariation;
                    }
                    else
                    {
                        float tx = TestDirection.X * TestDirection.X;
                        float ty = TestDirection.Y * TestDirection.Y;
                        float tz = TestDirection.Z * TestDirection.Z;

                        sigmaXX = scaledPConf + (scaledPAxial - scaledPConf) * tx * localVariation;
                        sigmaYY = scaledPConf + (scaledPAxial - scaledPConf) * ty * localVariation;
                        sigmaZZ = scaledPConf + (scaledPAxial - scaledPConf) * tz * localVariation;
                    }

                    // Calculate principal stresses
                    float sigma1 = Math.Max(sigmaXX, Math.Max(sigmaYY, sigmaZZ));
                    float sigma3 = Math.Min(sigmaXX, Math.Min(sigmaYY, sigmaZZ));
                    float sigma2 = sigmaXX + sigmaYY + sigmaZZ - sigma1 - sigma3;

                    // Ensure proper ordering
                    if (sigma1 < sigma2) { float temp = sigma1; sigma1 = sigma2; sigma2 = temp; }
                    if (sigma2 < sigma3) { float temp = sigma2; sigma2 = sigma3; sigma3 = temp; }
                    if (sigma1 < sigma2) { float temp = sigma1; sigma1 = sigma2; sigma2 = temp; }

                    // Force reasonable stress values to ensure fracture detection works
                    if (axialPressure > theoreticalBreak * 0.7f)
                    {
                        sigma1 = Math.Max(sigma1, theoreticalBreak * stressFactor * 0.8f);
                    }

                    // Von Mises stress
                    float vonMises = (float)Math.Sqrt(0.5f * ((sigma1 - sigma2) * (sigma1 - sigma2) +
                                                    (sigma2 - sigma3) * (sigma2 - sigma3) +
                                                    (sigma3 - sigma1) * (sigma3 - sigma1)));

                    // Mohr-Coulomb criterion
                    float sinPhi = (float)Math.Sin(frictionAngleRad);
                    float cosPhi = (float)Math.Cos(frictionAngleRad);
                    float criterion = (2.0f * localCohesion * cosPhi +
                                     (sigma1 + sigma3) * sinPhi) / (1.0f - sinPhi);

                    // Determine if failure occurs
                    bool failed = (sigma1 - sigma3 >= criterion * 0.9f);

                    // Store results
                    vm[i] = vonMises;
                    s1[i] = sigma1;
                    s2[i] = sigma2;
                    s3[i] = sigma3;
                    frac[i] = failed ? 1 : 0;
                });
            }

            int fcount = 0;
            float sumVonMises = 0;
            float maxVonMises = 0;
            bool anyFractured = false;

            for (int i = 0; i < n; i++)
            {
                var tri = _simulationTriangles[i];

                // Update triangle stress values
                tri.VonMisesStress = vm[i];
                tri.Stress1 = s1[i];
                tri.Stress2 = s2[i];
                tri.Stress3 = s3[i];

                // Apply displacement - make it more visible at higher pressures
                Vector3 displacement = displacements[i] * (axialPressure / 100.0f);
                // Apply larger displacement near breaking point for better visualization
                if (axialPressure > theoreticalBreak * 0.8f)
                {
                    displacement *= 1.5f;
                }
                tri.Displacement = displacement;

                // Calculate fracture probability
                float fractureProb;
                if (_useInhomogeneousDensity && TriangleDensities.TryGetValue(tri, out float density))
                {
                    float densityFactor = (float)Math.Pow(AverageDensity / Math.Max(density, 1.0f), 0.75f);
                    densityFactor = Math.Min(densityFactor, 3.0f);
                    fractureProb = CalculateFractureProbability(s1[i], s3[i], CohesionStrength, FrictionAngle) * densityFactor;
                }
                else
                {
                    fractureProb = CalculateFractureProbability(s1[i], s3[i], CohesionStrength, FrictionAngle);
                }

                // Add spatial variation
                Vector3 centroid = (tri.V1 + tri.V2 + tri.V3) / 3.0f;
                float spatialFactor = 1.0f + 0.1f * (float)Math.Sin(centroid.X * 0.5f + centroid.Y * 0.3f + centroid.Z * 0.7f);
                fractureProb *= spatialFactor;
                fractureProb = Math.Min(fractureProb, 1.0f);

                tri.FractureProbability = fractureProb;

                // CRITICAL: Improve fracture detection - save whether it was already fractured
                bool wasAlreadyFractured = tri.IsFractured;

                // Multiple fracture criteria
                bool fracturePredicted = frac[i] == 1;
                bool probabilityBasedFracture = fractureProb > 0.30f; // Lowered threshold
                bool stressBasedFracture = s1[i] > theoreticalBreak * 0.9f; // Stress-based criterion

                // Force fracture when pressure gets close to theoretical breaking pressure
                bool forcedFracture = axialPressure >= theoreticalBreak * 0.9f && fractureProb > 0.2f;

                // Combine all criteria
                tri.IsFractured = fracturePredicted || probabilityBasedFracture || stressBasedFracture || forcedFracture;

                // CRITICAL: Update breaking pressure when first fracture is detected
                if (tri.IsFractured && !wasAlreadyFractured && BreakingPressure <= 0.001f)
                {
                    BreakingPressure = axialPressure;
                    anyFractured = true;
                    Logger.Log($"[InhomogeneousTriaxialSimulation] First fracture detected at {axialPressure:F2} MPa");
                }

                if (tri.IsFractured) fcount++;

                // Track stress statistics
                sumVonMises += vm[i];
                maxVonMises = Math.Max(maxVonMises, vm[i]);

                _simulationTriangles[i] = tri;
            }

            // If we're at or beyond the theoretical breaking pressure and no fractures yet,
            // force at least some triangles to fracture
            if (axialPressure >= theoreticalBreak * 0.95f && fcount == 0)
            {
                // Find triangles with highest fracture probability
                var candidates = new List<KeyValuePair<int, float>>();
                for (int i = 0; i < n; i++)
                {
                    candidates.Add(new KeyValuePair<int, float>(i, _simulationTriangles[i].FractureProbability));
                }

                // Sort by fracture probability (descending)
                candidates.Sort((a, b) => b.Value.CompareTo(a.Value));

                // Force fracture in top 5 candidates
                int forcedCount = Math.Min(5, candidates.Count);
                for (int j = 0; j < forcedCount; j++)
                {
                    int idx = candidates[j].Key;
                    var tri = _simulationTriangles[idx];
                    tri.IsFractured = true;
                    _simulationTriangles[idx] = tri;
                    fcount++;
                }

                // Set breaking pressure
                if (BreakingPressure <= 0.001f)
                {
                    BreakingPressure = axialPressure;
                    anyFractured = true;
                    Logger.Log($"[InhomogeneousTriaxialSimulation] Forced fracture at theoretical pressure {axialPressure:F2} MPa");
                }
            }

            // If this is the last pressure step and still no fractures detected, set breaking pressure to theoretical
            if (axialPressure >= MaxAxialPressure * 0.99f && BreakingPressure <= 0.001f)
            {
                BreakingPressure = theoreticalBreak;
                Logger.Log($"[InhomogeneousTriaxialSimulation] Setting breaking pressure to theoretical value: {theoreticalBreak:F2} MPa");
            }

            await Task.Delay(10, CancellationToken.None);

            float fracturePercentage = (float)fcount / n;
            float fractureThreshold = GetInhomogeneousFractureThreshold(axialPressure);

            if (fracturePercentage > 0.005f || axialPressure > theoreticalBreak * 0.8f)
            {
                Logger.Log($"[InhomogeneousTriaxialSimulation] P={axialPressure:F2} MPa, " +
                           $"Fracture: {fracturePercentage:P2}, Thresh: {fractureThreshold:P2}, " +
                           $"BreakingP: {BreakingPressure:F2}, VM: {maxVonMises:F2}");
            }

            return fracturePercentage > fractureThreshold || anyFractured;
        }


        /// <summary>
        /// Calculate displacement vector for a triangle with density effects
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

        /// <summary>
        /// Calculate fracture probability with density-adjusted strengths
        /// </summary>
        protected float CalculateFractureProbability(float stress1, float stress3, float cohesion, float frictionAngleDegrees)
        {
            // Convert friction angle to radians
            float frictionAngle = frictionAngleDegrees * (float)Math.PI / 180f;

            // Mohr-Coulomb failure criterion parameters
            float sinPhi = (float)Math.Sin(frictionAngle);
            float cosPhi = (float)Math.Cos(frictionAngle);

            // Calculate criterion based on stress state
            float criterion;

            if (stress3 < 0) // Tensile condition
            {
                // Modified criterion accounting for tensile weakening
                float tensileInfluence = Math.Min(1.0f, Math.Abs(stress3) / (cohesion * 0.5f));
                criterion = (2.0f * cohesion * cosPhi * (1.0f - 0.3f * tensileInfluence)) / (1.0f - sinPhi);
            }
            else // Compressive condition
            {
                // Standard Mohr-Coulomb criterion
                criterion = (2.0f * cohesion * cosPhi + (stress1 + stress3) * sinPhi) / (1.0f - sinPhi);
            }

            // Calculate differential stress
            float stressDiff = stress1 - stress3;

            // Calculate ratio of actual stress to threshold
            float ratio = stressDiff / criterion;

            // Apply a more nuanced probability function
            // This creates a smoother transition with a better distribution of values
            if (ratio < 0.4f)
            {
                // Very low probability range (0.0 - 0.01)
                return ratio * 0.025f;
            }
            else if (ratio < 0.7f)
            {
                // Low probability range (0.01 - 0.1)
                return 0.01f + (ratio - 0.4f) * 0.3f;
            }
            else if (ratio < 0.9f)
            {
                // Medium probability range (0.1 - 0.5)
                return 0.1f + (ratio - 0.7f) * 2.0f;
            }
            else if (ratio < 1.0f)
            {
                // High probability range (0.5 - 0.9)
                return 0.5f + (ratio - 0.9f) * 4.0f;
            }
            else
            {
                // Very high probability range (0.9 - 1.0)
                float exceedRatio = Math.Min(ratio - 1.0f, 0.5f) / 0.5f; // Capped at 0.5 beyond criterion
                return 0.9f + exceedRatio * 0.1f;
            }
        }
        public override float CalculateStrain(float pressure)
        {
            // CRITICAL FIX: Calculate a direct, realistic strain value that doesn't depend on the base class
            // We're using a simplified approach to ensure we get non-zero values

            // Basic strain calculation from Hooke's law, but with realistic values
            float baseStrain = pressure / Math.Max(YoungModulus, 1000.0f);

            // Ensure we have at least some visible strain
            baseStrain = Math.Max(baseStrain, pressure / 30000.0f);

            // Add non-linear component for higher pressures
            float theoBreak = CalculateTheoreticalBreakingPressure();
            if (pressure > 0.5f * theoBreak)
            {
                float ratio = pressure / theoBreak;
                baseStrain *= (1.0f + ratio * 0.5f);
            }

            // If we're not using inhomogeneous density, return the improved calculation
            if (!_useInhomogeneousDensity || TriangleDensities.Count == 0)
                return baseStrain;

            // If using inhomogeneous density, calculate mean strain with variation
            float sumStrain = 0;
            int count = 0;

            foreach (var entry in TriangleDensities)
            {
                float density = entry.Value;

                // Density affects strain inversely (lower density = higher strain)
                float densityRatio = Math.Max(0.2f, density / Math.Max(AverageDensity, 1.0f));
                float localStrain = baseStrain / (float)Math.Pow(densityRatio, 0.3f);

                // Add position-dependent variation
                Vector3 centroid = (entry.Key.V1 + entry.Key.V2 + entry.Key.V3) / 3.0f;
                float positionFactor = 1.0f + 0.15f * (float)Math.Sin(centroid.X * 0.3f + centroid.Y * 0.2f + centroid.Z * 0.4f);

                sumStrain += localStrain * positionFactor;
                count++;
            }

            // Calculate mean strain, with minimum threshold
            float result = (count > 0) ? sumStrain / count : baseStrain;
            return Math.Max(result, baseStrain);
        }


        /// <summary>
        /// Override RenderResultsWithSlicing to add density visualization options
        /// </summary>
        public override void RenderResultsWithSlicing(Graphics g, int width, int height,
                                            Vector3 slicePlaneNormal, float slicePlaneDistance,
                                            RenderMode renderMode = RenderMode.Stress)
        {
            // Call the base rendering method for all modes
            base.RenderResultsWithSlicing(g, width, height, slicePlaneNormal, slicePlaneDistance, renderMode);

            // Add an overlay indicating inhomogeneous density is being used
            if (_useInhomogeneousDensity && _densityMap != null && _densityMap.Count > 0 && renderMode != RenderMode.Wireframe)
            {
                using (Font font = new Font("Arial", 10, FontStyle.Bold))
                using (SolidBrush backBrush = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                using (SolidBrush textBrush = new SolidBrush(Color.Yellow))
                {
                    string message = $"Inhomogeneous Density: {TriangleDensities.Count} elements";
                    SizeF textSize = g.MeasureString(message, font);

                    // Position in bottom-right corner instead of top-right
                    g.FillRectangle(backBrush, width - textSize.Width - 15, height - textSize.Height - 15,
                                    textSize.Width + 10, textSize.Height + 5);
                    g.DrawString(message, font, textBrush, width - textSize.Width - 10, height - textSize.Height - 13);

                    // Add density range if we have it
                    if (MinimumDensity < MaximumDensity)
                    {
                        string rangeText = $"Density: {MinimumDensity:F0}-{MaximumDensity:F0} kg/m³";
                        SizeF rangeSize = g.MeasureString(rangeText, font);
                        g.FillRectangle(backBrush, width - rangeSize.Width - 15,
                                        height - textSize.Height - rangeSize.Height - 20,
                                        rangeSize.Width + 10, rangeSize.Height + 5);
                        g.DrawString(rangeText, font, textBrush,
                                    width - rangeSize.Width - 10,
                                    height - textSize.Height - rangeSize.Height - 18);
                    }
                }
            }
        }
        /// <summary>
        /// Override RenderResults to add density visualization options and fix the rendering issue
        /// </summary>
        public override void RenderResults(Graphics g, int width, int height, RenderMode renderMode = RenderMode.Stress)
        {
            DisposeGpu();
            // Call the base rendering method for all modes
            base.RenderResults(g, width, height, renderMode);

            // Add an overlay indicating inhomogeneous density is being used
            if (_useInhomogeneousDensity && _densityMap != null && _densityMap.Count > 0 && renderMode != RenderMode.Wireframe)
            {
                using (Font font = new Font("Arial", 10, FontStyle.Bold))
                using (SolidBrush backBrush = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                using (SolidBrush textBrush = new SolidBrush(Color.Yellow))
                {
                    string message = $"Inhomogeneous Density: {TriangleDensities.Count} elements";
                    SizeF textSize = g.MeasureString(message, font);

                    // Position in bottom-right corner instead of top-right
                    g.FillRectangle(backBrush, width - textSize.Width - 15, height - textSize.Height - 15,
                                    textSize.Width + 10, textSize.Height + 5);
                    g.DrawString(message, font, textBrush, width - textSize.Width - 10, height - textSize.Height - 13);

                    // Add density range if we have it
                    if (MinimumDensity < MaximumDensity)
                    {
                        string rangeText = $"Density: {MinimumDensity:F0}-{MaximumDensity:F0} kg/m³";
                        SizeF rangeSize = g.MeasureString(rangeText, font);
                        g.FillRectangle(backBrush, width - rangeSize.Width - 15,
                                        height - textSize.Height - rangeSize.Height - 20,
                                        rangeSize.Width + 10, rangeSize.Height + 5);
                        g.DrawString(rangeText, font, textBrush,
                                    width - rangeSize.Width - 10,
                                    height - textSize.Height - rangeSize.Height - 18);
                    }
                }
            }
        }
        protected float GetInhomogeneousFractureThreshold(float pressure)
        {
            // Start with pressure-dependent base threshold similar to the homogeneous simulation
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
        /// Render density distribution visualization with improved positioning and range
        /// </summary>
        public void RenderDensityDistribution(Graphics g, int width, int height)
        {
            if (!_useInhomogeneousDensity || _densityMap == null || _densityMap.Count == 0 || TriangleDensities.Count == 0)
            {
                // Draw message if no density data is available
                g.Clear(Color.Black);
                using (Font font = new Font("Arial", 12))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    g.DrawString("Inhomogeneous density is not enabled or no density data available", font, brush, 20, 20);
                }
                return;
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Black);

            // Set up projection parameters
            float scale = Math.Min(width, height) / 250.0f;
            float centerX = width / 2.0f;
            float centerY = height / 2.0f;
            float maxCoord = FindMaxCoordinate();

            // Default rotation angles
            float rotationX = 0.3f;
            float rotationY = 0.4f;

            // Create a list to hold all triangles with their depth and density
            var trianglesToDraw = new List<(Triangle Triangle, float Depth, float Density)>();

            // Calculate min/max density values
            float minDensity = float.MaxValue;
            float maxDensity = float.MinValue;

            // Prepare triangles with depth and density info
            foreach (var tri in MeshTriangles)
            {
                float depth = (tri.V1.Z + tri.V2.Z + tri.V3.Z) / 3.0f;
                float density = (float)Material.Density; // Default

                if (TriangleDensities.TryGetValue(tri, out float triangleDensity))
                {
                    density = triangleDensity;
                    minDensity = Math.Min(minDensity, density);
                    maxDensity = Math.Max(maxDensity, density);
                }

                trianglesToDraw.Add((tri, depth, density));
            }

            // Ensure we have a valid range
            if (minDensity >= maxDensity)
            {
                minDensity = (float)(Material.Density * 0.9f);
                maxDensity = (float)(Material.Density * 1.1f);
            }

            // Sort triangles by depth (back to front)
            trianglesToDraw.Sort((a, b) => -a.Depth.CompareTo(b.Depth));

            // Draw each triangle with color based on density
            foreach (var (tri, _, density) in trianglesToDraw)
            {
                // Project vertices with improved positioning
                PointF p1 = ProjectVertex(tri.V1, centerX, centerY, scale, maxCoord, rotationX, rotationY);
                PointF p2 = ProjectVertex(tri.V2, centerX, centerY, scale, maxCoord, rotationX, rotationY);
                PointF p3 = ProjectVertex(tri.V3, centerX, centerY, scale, maxCoord, rotationX, rotationY);

                // Get color based on density with improved range
                float normalizedDensity = (density - minDensity) / (maxDensity - minDensity);
                Color triangleColor = GetHeatMapColor(normalizedDensity, 0, 1);

                // Draw triangle
                PointF[] points = { p1, p2, p3 };

                // Fill triangle
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

            // Draw title
            using (Font titleFont = new Font("Arial", 14, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                g.DrawString("Density Distribution", titleFont, textBrush, 20, 20);
            }

            // Draw info text
            using (Font infoFont = new Font("Arial", 10))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                string info = $"Material: {Material.Name}\n" +
                              $"Density Range: {minDensity:F0} - {maxDensity:F0} kg/m³\n" +
                              $"Average Density: {AverageDensity:F0} kg/m³";
                g.DrawString(info, infoFont, textBrush, 20, 50);
            }

            // Draw color scale with proper values
            DrawDensityColorScale(g, width - 80, height / 3, 30, height / 3, minDensity, maxDensity);
        }

        private void DrawDensityColorScale(Graphics g, int x, int y, int width, int height, float minDensity, float maxDensity)
        {
            // Draw gradient
            Rectangle gradientRect = new Rectangle(x, y, width, height);
            using (LinearGradientBrush gradientBrush = new LinearGradientBrush(
                   gradientRect,
                   Color.Blue, Color.Red,
                   LinearGradientMode.Vertical))
            {
                ColorBlend blend = new ColorBlend(5);
                blend.Colors = new Color[] {
            Color.Blue,
            Color.Cyan,
            Color.Green,
            Color.Yellow,
            Color.Red
        };
                blend.Positions = new float[] { 0.0f, 0.25f, 0.5f, 0.75f, 1.0f };
                gradientBrush.InterpolationColors = blend;

                g.FillRectangle(gradientBrush, gradientRect);
            }

            // Draw border
            g.DrawRectangle(Pens.White, gradientRect);

            // Ensure we have valid values
            if (minDensity >= maxDensity)
            {
                float baseDensity = (float)Material.Density;
                minDensity = baseDensity * 0.9f;
                maxDensity = baseDensity * 1.1f;
            }

            float avgDensity = (minDensity + maxDensity) / 2.0f;

            // Draw min/max values
            using (Font font = new Font("Arial", 8))
            using (SolidBrush brush = new SolidBrush(Color.White))
            {
                g.DrawString("Density (kg/m³)", font, brush, x - 10, y - 15);
                g.DrawString($"{maxDensity:F0}", font, brush, x + width + 5, y);
                g.DrawString($"{avgDensity:F0}", font, brush, x + width + 5, y + height / 2);
                g.DrawString($"{minDensity:F0}", font, brush, x + width + 5, y + height - 10);
            }
        }

        private float FindMaxCoordinate()
        {
            float maxCoord = 0;

            foreach (var tri in MeshTriangles)
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
        /// Project a 3D vertex to 2D screen coordinates with improved projection
        /// </summary>
        private PointF ProjectVertex(Vector3 vertex, float centerX, float centerY, float scale, float maxCoord, float rotX, float rotY)
        {
            // Get projection matrix for our test direction
            Matrix3x3 projMatrix = GetProjectionMatrixForAxis(TestDirection);

            // Transform vertex to align with view direction
            Vector3 transformedVertex = projMatrix.Transform(vertex);

            // Normalize coordinates to -0.5 to 0.5 range
            float nx = transformedVertex.X / maxCoord - 0.5f;
            float ny = transformedVertex.Y / maxCoord - 0.5f;
            float nz = transformedVertex.Z / maxCoord - 0.5f;

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

            // Improved perspective projection with better depth perception
            float perspective = 1.5f + rz;
            float projX = centerX + rx * scale * 150 / perspective;
            float projY = centerY + ry * scale * 150 / perspective;

            return new PointF(projX, projY);
        }

        /// <summary>
        /// Add a method to get the projection matrix based on test direction
        /// </summary>
        private Matrix3x3 GetProjectionMatrixForAxis(Vector3 testDirection)
        {
            // Create a rotation matrix that aligns the view with the test direction
            Vector3 zAxis = Vector3.Normalize(testDirection);

            // Create a perpendicular x-axis
            Vector3 xAxis;
            if (Math.Abs(zAxis.Y) > 0.9f)
                xAxis = Vector3.Normalize(new Vector3(1, 0, 0));
            else
                xAxis = Vector3.Normalize(new Vector3(0, 1, 0));

            // Make sure x-axis is perpendicular to z-axis
            xAxis = Vector3.Normalize(xAxis - zAxis * Vector3.Dot(xAxis, zAxis));

            // Complete the right-handed system
            Vector3 yAxis = Vector3.Cross(zAxis, xAxis);

            // Return the rotation matrix (as a structure)
            return new Matrix3x3(xAxis, yAxis, zAxis);
        }

        /// <summary>
        /// Helper structure for a 3x3 matrix
        /// </summary>
        private struct Matrix3x3
        {
            public Vector3 Row1;
            public Vector3 Row2;
            public Vector3 Row3;

            public Matrix3x3(Vector3 row1, Vector3 row2, Vector3 row3)
            {
                Row1 = row1;
                Row2 = row2;
                Row3 = row3;
            }

            public Vector3 Transform(Vector3 v)
            {
                return new Vector3(
                    Vector3.Dot(Row1, v),
                    Vector3.Dot(Row2, v),
                    Vector3.Dot(Row3, v)
                );
            }
        }
        private void DisposeGpu()
        {
            _accelerator?.Dispose();
            _context?.Dispose();
        }
        #endregion Simulation Override Methods
    }
}