using ILGPU;
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
       ArrayView<System.Numerics.Vector3>,
       ArrayView<System.Numerics.Vector3>,
       ArrayView<System.Numerics.Vector3>,
       ArrayView<float>, // stress factors
       float, float, System.Numerics.Vector3,
       float, float, float, // cohesion, sinPhi, cosPhi
       ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<int>>
       _inhomogeneousStressKernel;

        // Density statistics
        public float MinimumDensity { get; private set; }

        public float MaximumDensity { get; private set; }
        public float AverageDensity { get; private set; }

        // Derived properties accounting for density variations
        public Dictionary<Triangle, float> TriangleDensities { get; private set; }

        public Dictionary<Triangle, float> TriangleStressFactors { get; private set; }

        // Flag for density model initialization
        private bool _hasInitializedInhomogeneousModels = false;

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

            // Calculate stress factor with power law relationship
            float stressFactor = (float)Math.Pow(relativeDensity, exponent - 1.0f);

            // Add small spatial variation for more realistic heterogeneity
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

        #endregion Constructor and Initialization

        #region Simulation Override Methods

        /// <summary>
        /// Override RunSimulationStep to use density-based calculations
        /// </summary>
        public override async Task<bool> RunSimulationStep(float axialPressure)
        {
            // Get the count from the protected field, not the read-only property
            int n = _simulationTriangles.Count;
            var v1 = new Vector3[n];
            var v2 = new Vector3[n];
            var v3 = new Vector3[n];
            var vm = new float[n];
            var s1 = new float[n];
            var s2 = new float[n];
            var s3 = new float[n];
            var frac = new int[n];
            var displacements = new Vector3[n]; // Add displacement arrays

            // Also prepare density-based stress factors
            var stressFactors = new float[n];

            for (int i = 0; i < n; i++)
            {
                var t = _simulationTriangles[i]; // Use protected field
                v1[i] = t.V1;
                v2[i] = t.V2;
                v3[i] = t.V3;

                // Apply density-based stress factor if enabled
                if (_useInhomogeneousDensity && TriangleStressFactors.TryGetValue(t, out float factor))
                {
                    stressFactors[i] = factor;

                    // Calculate displacement field based on inhomogeneous properties
                    float localDisplacementMagnitude = CalculateDisplacementMagnitude(axialPressure) / factor;
                    displacements[i] = CalculateDisplacementVector(t, TestDirection, localDisplacementMagnitude);
                }
                else
                {
                    stressFactors[i] = 1.0f; // Default factor if not density-enabled or no factor found

                    // Standard displacement calculation
                    float displacementMagnitude = CalculateDisplacementMagnitude(axialPressure);
                    displacements[i] = CalculateDisplacementVector(t, TestDirection, displacementMagnitude);
                }
            }

            // Pre-calculate sin and cos values on CPU
            float frictionAngleRad = FrictionAngle * (float)Math.PI / 180f;
            float sinPhiValue = (float)Math.Sin(frictionAngleRad);
            float cosPhiValue = (float)Math.Cos(frictionAngleRad);

            try
            {
                // Perform enhanced CPU-based calculation with full physics model
                Logger.Log($"[InhomogeneousTriaxialSimulation] Performing enhanced CPU-based calculation with {n} triangles");

                Parallel.For(0, n, i =>
                {
                    // Apply density-based stress factor
                    float stressFactor = stressFactors[i];

                    // Scale material properties based on density variation
                    float localYoungModulus = YoungModulus * stressFactor;
                    float localCohesion = CohesionStrength * (float)Math.Sqrt(stressFactor);

                    // Calculate face normal
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

                    // Calculate full 3D stress tensor components with inhomogeneous properties
                    float sigmaXX, sigmaYY, sigmaZZ, sigmaXY, sigmaXZ, sigmaYZ;

                    // Apply confining and axial pressure with density scaling
                    float scaledPConf = ConfiningPressure * stressFactor;
                    float scaledPAxial = axialPressure * stressFactor;

                    // Create local heterogeneity patterns for realistic stress distribution
                    Vector3 centroid = (v1[i] + v2[i] + v3[i]) / 3.0f;
                    float localVariation = 1.0f + 0.1f * (float)Math.Sin(centroid.X * 0.5f + centroid.Y * 0.7f + centroid.Z * 0.9f);

                    // Set up stress tensor based on test direction and confining pressure
                    if (TestDirection.X > 0.7f) // X-direction test
                    {
                        sigmaXX = scaledPAxial * localVariation;
                        sigmaYY = scaledPConf;
                        sigmaZZ = scaledPConf;
                        // Shear components - small values representing imperfections
                        sigmaXY = scaledPConf * 0.05f * (float)Math.Sin(centroid.X * 0.1f);
                        sigmaXZ = scaledPConf * 0.05f * (float)Math.Cos(centroid.Z * 0.1f);
                        sigmaYZ = scaledPConf * 0.03f * (float)Math.Sin(centroid.Y * 0.1f);
                    }
                    else if (TestDirection.Y > 0.7f) // Y-direction test
                    {
                        sigmaXX = scaledPConf;
                        sigmaYY = scaledPAxial * localVariation;
                        sigmaZZ = scaledPConf;
                        // Shear components
                        sigmaXY = scaledPConf * 0.05f * (float)Math.Sin(centroid.Y * 0.1f);
                        sigmaXZ = scaledPConf * 0.03f * (float)Math.Cos(centroid.X * 0.1f);
                        sigmaYZ = scaledPConf * 0.05f * (float)Math.Sin(centroid.Z * 0.1f);
                    }
                    else if (TestDirection.Z > 0.7f) // Z-direction test
                    {
                        sigmaXX = scaledPConf;
                        sigmaYY = scaledPConf;
                        sigmaZZ = scaledPAxial * localVariation;
                        // Shear components
                        sigmaXY = scaledPConf * 0.03f * (float)Math.Sin(centroid.X * 0.1f);
                        sigmaXZ = scaledPConf * 0.05f * (float)Math.Cos(centroid.Z * 0.1f);
                        sigmaYZ = scaledPConf * 0.05f * (float)Math.Sin(centroid.Y * 0.1f);
                    }
                    else // Arbitrary direction - blend stress components
                    {
                        // Weighted components based on direction cosines
                        float tx = TestDirection.X * TestDirection.X;
                        float ty = TestDirection.Y * TestDirection.Y;
                        float tz = TestDirection.Z * TestDirection.Z;

                        // Normal stresses
                        sigmaXX = scaledPConf + (scaledPAxial - scaledPConf) * tx * localVariation;
                        sigmaYY = scaledPConf + (scaledPAxial - scaledPConf) * ty * localVariation;
                        sigmaZZ = scaledPConf + (scaledPAxial - scaledPConf) * tz * localVariation;

                        // Shear stresses - proportional to direction products
                        sigmaXY = (scaledPAxial - scaledPConf) * TestDirection.X * TestDirection.Y * 0.5f;
                        sigmaXZ = (scaledPAxial - scaledPConf) * TestDirection.X * TestDirection.Z * 0.5f;
                        sigmaYZ = (scaledPAxial - scaledPConf) * TestDirection.Y * TestDirection.Z * 0.5f;
                    }

                    // Calculate stress tensor invariants
                    float I1 = sigmaXX + sigmaYY + sigmaZZ;
                    float I2 = sigmaXX * sigmaYY + sigmaYY * sigmaZZ + sigmaZZ * sigmaXX -
                              sigmaXY * sigmaXY - sigmaXZ * sigmaXZ - sigmaYZ * sigmaYZ;

                    // Calculate normal stress on the triangle plane
                    float nx = normal.X;
                    float ny = normal.Y;
                    float nz = normal.Z;
                    float normalStress = nx * nx * sigmaXX + ny * ny * sigmaYY + nz * nz * sigmaZZ +
                                       2 * nx * ny * sigmaXY + 2 * nx * nz * sigmaXZ + 2 * ny * nz * sigmaYZ;

                    // Mean and deviatoric stress components
                    float meanStress = I1 / 3.0f;

                    // Calculate deviatoric stress components
                    float sxx = sigmaXX - meanStress;
                    float syy = sigmaYY - meanStress;
                    float szz = sigmaZZ - meanStress;

                    // J2 invariant (second invariant of the deviatoric stress tensor)
                    float J2 = (sxx * sxx + syy * syy + szz * szz) / 2.0f +
                              sigmaXY * sigmaXY + sigmaXZ * sigmaXZ + sigmaYZ * sigmaYZ;

                    // Calculate principal stresses using invariants
                    float rootJ2 = (float)Math.Sqrt(J2);

                    // Principal stresses (approximate solution)
                    float sigma1 = meanStress + rootJ2 * 1.73f; // ~sqrt(3)
                    float sigma3 = meanStress - rootJ2 * 1.73f;
                    float sigma2 = 3.0f * meanStress - sigma1 - sigma3; // Must sum to 3*meanStress

                    // Apply local stress amplification from heterogeneity
                    float stressAmplification = localVariation * stressFactor;
                    sigma1 *= stressAmplification;

                    // Ensure proper ordering
                    if (sigma1 < sigma2) { float temp = sigma1; sigma1 = sigma2; sigma2 = temp; }
                    if (sigma2 < sigma3) { float temp = sigma2; sigma2 = sigma3; sigma3 = temp; }
                    if (sigma1 < sigma2) { float temp = sigma1; sigma1 = sigma2; sigma2 = temp; }

                    // Von Mises stress - correct formula from tensor invariants
                    float vonMises = (float)Math.Sqrt(3.0f * J2) * stressAmplification;

                    // Enhanced Mohr-Coulomb criterion with density effects
                    float criterion;

                    if (sigma3 < 0) // Tensile condition
                    {
                        // Tension cutoff model - more conservative criterion in tension
                        criterion = (2.0f * localCohesion * cosPhiValue) / (1.0f - sinPhiValue);
                        criterion *= (1.0f - Math.Abs(sigma3) / (localCohesion * 2.0f)); // Reduce strength in tension
                    }
                    else // Compressive condition
                    {
                        criterion = (2.0f * localCohesion * cosPhiValue + (sigma1 + sigma3) * sinPhiValue) / (1.0f - sinPhiValue);
                    }

                    // Apply heterogeneity factor to criterion
                    float heterogeneityFactor = 1.0f / (0.9f + 0.2f * stressFactor); // Stronger materials have lower variability
                    int failed = (sigma1 - sigma3 >= criterion * heterogeneityFactor) ? 1 : 0;

                    // Store results
                    vm[i] = vonMises;
                    s1[i] = sigma1;
                    s2[i] = sigma2;
                    s3[i] = sigma3;
                    frac[i] = failed;
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"[InhomogeneousTriaxialSimulation] Execution error: {ex.Message}");
                return false;
            }

            int fcount = 0;
            float sumVonMises = 0;
            float maxVonMises = 0;

            for (int i = 0; i < n; i++)
            {
                var tri = _simulationTriangles[i]; // Use protected field

                // Update triangle stress values
                tri.VonMisesStress = vm[i];
                tri.Stress1 = s1[i];
                tri.Stress2 = s2[i];
                tri.Stress3 = s3[i];

                // Add nonuniform displacement based on density distribution
                tri.Displacement = displacements[i];

                // Improved fracture probability calculation with density effects
                float fractureProb;

                if (_useInhomogeneousDensity && TriangleDensities.TryGetValue(tri, out float density))
                {
                    // Enhance fracture calculation with density factor
                    float densityFactor = (float)Math.Pow(AverageDensity / Math.Max(density, 1.0f), 0.75f);
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
                fractureProb = Math.Min(fractureProb, 1.0f); // Clamp to valid range

                tri.FractureProbability = fractureProb;

                // Determine if fracture occurs with more nuanced criteria
                bool fracturePredicted = frac[i] == 1;
                bool hostFractureCheck = fractureProb > 0.75f;
                tri.IsFractured = fracturePredicted || hostFractureCheck;

                if (tri.IsFractured) fcount++;

                // Track stress statistics
                sumVonMises += vm[i];
                maxVonMises = Math.Max(maxVonMises, vm[i]);

                // Update triangle
                _simulationTriangles[i] = tri;
            }

            await Task.Delay(10, CancellationToken.None);

            // Non-linear fracture detection criteria based on pressure level and material properties
            float pressureRatio = axialPressure / MaxAxialPressure;
            float fracturePercentage = (float)fcount / n;
            float fractureThreshold = GetInhomogeneousFractureThreshold(axialPressure);

            Logger.Log($"[InhomogeneousTriaxialSimulation] Pressure: {axialPressure:F2} MPa, " +
                       $"Fracture: {fracturePercentage:P2}, Threshold: {fractureThreshold:P2}");

            return fracturePercentage > fractureThreshold;

            // Log stress statistics
            if (fracturePercentage > 0.005f || axialPressure > MaxAxialPressure * 0.8f)
            {
                Logger.Log($"[InhomogeneousTriaxialSimulation] Pressure: {axialPressure:F2} MPa, " +
                           $"Fracture: {fracturePercentage:P2}, Avg VM: {sumVonMises / n:F2}, Max VM: {maxVonMises:F2}");
            }

            return fracturePercentage > fractureThreshold;
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
            // Calculate base strain using the base class method
            float baseStrain = base.CalculateStrain(pressure);

            // If we're not using inhomogeneous density, return the base calculation
            if (!_useInhomogeneousDensity || TriangleDensities.Count == 0)
                return baseStrain;

            // Calculate average strain considering density variations
            float sumStrain = 0;
            int count = 0;

            foreach (var entry in TriangleDensities)
            {
                Triangle triangle = entry.Key;
                float density = entry.Value;

                // Density-dependent elastic modulus - lower density = lower modulus = higher strain
                float densityRatio = Math.Max(0.1f, density / AverageDensity);

                // Calculate local strain (inversely proportional to density)
                float localStrain = baseStrain / (float)Math.Pow(densityRatio, 0.7f);

                // Add position-dependent variation
                Vector3 centroid = (triangle.V1 + triangle.V2 + triangle.V3) / 3.0f;
                float positionFactor = 1.0f + 0.1f * (float)Math.Sin(centroid.X * 0.3f + centroid.Y * 0.2f + centroid.Z * 0.4f);

                sumStrain += localStrain * positionFactor;
                count++;
            }

            // Return the average strain, or base strain if no valid calculations
            return (count > 0) ? sumStrain / count : baseStrain;
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
            // Start with base threshold
            float baseThreshold = 0.015f;

            // Scale based on density contrast (higher contrast = easier fracture)
            float densityContrastFactor = 1.0f;
            if (MinimumDensity > 0 && MaximumDensity > MinimumDensity)
            {
                float densityRatio = MaximumDensity / MinimumDensity;
                densityContrastFactor = (float)Math.Sqrt(densityRatio);
            }

            // Scale based on material strength properties
            float strengthFactor = 10.0f / Math.Max(CohesionStrength, 1.0f);

            // Return calculated threshold with reasonable limits
            return Math.Min(0.08f, Math.Max(0.005f, baseThreshold * densityContrastFactor * strengthFactor));
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

        #endregion Simulation Override Methods
    }
}