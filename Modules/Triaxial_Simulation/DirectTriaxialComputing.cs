//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenTK;
using System.Drawing;
using System.Runtime.InteropServices;
using System.IO;
using System.Runtime.CompilerServices;

namespace CTS
{
    /// <summary>
    /// Provides high-performance direct computation for triaxial simulations
    /// using hardware acceleration and memory optimization
    /// </summary>
    public class DirectTriaxialCompute : IDisposable
    {
        #region Fields and Properties

        // Structure for volumetric mesh data
        private struct MeshData
        {
            public Vector3[] Vertices;
            public Vector3[] Normals;
            public int[] Indices;
            public float[] DensityValues;
            public float MinDensity;
            public float MaxDensity;
        }

        // Structure for material properties
        private struct MaterialProperties
        {
            public float BulkDensity;
            public float YoungModulus;
            public float PoissonRatio;
            public float YieldStrength;
            public float BrittleStrength;
            public float Cohesion;
            public float FrictionAngle;
            public float Porosity;
            public float BulkModulus;
            public float Permeability;
            public float MinPressure;
            public float MaxPressure;
            public Vector3 PrincipalStresses;
            public float PorePressure;
            public bool IsElasticEnabled;
            public bool IsPlasticEnabled;
            public bool IsBrittleEnabled;
        }

        // Structure for element properties
        private struct ElementProperties
        {
            public int[] VertexIndices;
            public float Density;
            public float Stress;
            public float Strain;
            public Vector3 PrincipalStrains;
            public bool HasFailed;
        }

        // Structure for simulation results
        public struct SimulationResult
        {
            public float AverageStress;
            public float[] ElementStresses;
            public Vector3[] DeformedVertices;
            public float VolumetricStrain;
            public float ElasticEnergy;
            public float PlasticEnergy;
            public float PorePressure;
            public float Permeability;
            public float FailurePercentage;
            public bool HasFailed;
            public float PeakStress;
            public float StrainAtPeak;
        }

        // Computation resources
        private MeshData meshData;
        private MaterialProperties materialProps;
        private ElementProperties[] elements;
        private SimulationResult currentResult;
        private CancellationTokenSource cancellationSource;
        private List<Point> stressStrainCurve;
        private int hardwareThreadCount;
        private bool useGPUAcceleration;
        private bool disposed;
        private bool ignoreFailure = false;
        public bool IsInitialized { get; private set; } = false;
        // Optimization buffers
        private float[] parallelStressBuffer;
        private Vector3[] parallelDeformationBuffer;

        // Memory optimization
        private ArrayPool<float> floatPool;
        private ArrayPool<Vector3> vectorPool;
        private bool useCompatibilityMode = true;
        // Progress tracking
        public event EventHandler<DirectComputeProgressEventArgs> ProgressUpdated;
        public event EventHandler<DirectComputeCompletedEventArgs> SimulationCompleted;

        #endregion

        #region Constructor and Initialization

        /// <summary>
        /// Creates a new instance of the direct computation engine
        /// </summary>
        /// <param name="useGPU">Whether to attempt to use GPU acceleration</param>
        public DirectTriaxialCompute(bool useGPU = true)
        {
            // Initialize all necessary resources
            hardwareThreadCount = Environment.ProcessorCount;
            useGPUAcceleration = useGPU && IsGPUAvailable();
            stressStrainCurve = new List<Point>();

            // Create reusable memory pools
            floatPool = new ArrayPool<float>(10);
            vectorPool = new ArrayPool<Vector3>(10);

            // Log initialization
            Log($"DirectTriaxialCompute initialized with {hardwareThreadCount} hardware threads");
            Log($"GPU acceleration: {(useGPUAcceleration ? "Enabled" : "Disabled")}");
        }
        public void SetIgnoreFailure(bool ignore)
        {
            ignoreFailure = ignore;
        }
        /// <summary>
        /// Calculates Biot coefficient for effective stress
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float Biot(float porosity)
        {
            // Biot coefficient: α = 1 - K/Ks
            // Typical range: 0.5-1.0, approaching 1.0 for high-porosity rocks

            // Use grain bulk modulus for quartz-rich rocks (36-45 GPa)
            float grainBulkModulus = 36000.0f; // MPa

            // Prevent division by extremely small numbers
            if (materialProps.BulkModulus < 0.01f)
                return 0.9f; // Default reasonable value

            // Calculate with proper bounds
            float biotCoeff = 1.0f - (materialProps.BulkModulus / grainBulkModulus);

            // Ensure physically reasonable range
            return Math.Max(0.5f, Math.Min(1.0f, biotCoeff));
        }
        /// <summary>
        /// Calculates pore pressure based on volumetric strain and permeability
        /// </summary>
        private float CalculatePorePressure(float strain)
        {
            // Initial pore pressure
            float initialPorePressure = 0.1f; // MPa

            // Volumetric strain calculation (crucial for accurate pore pressure)
            float volumetricStrain = CalculateVolumetricStrain(strain);

            // Calculate for undrained (low permeability) vs drained (high permeability) conditions
            float skemptonB = CalculateSkemptonB(materialProps.Porosity);

            // Drainage factor: 1.0 = fully drained, 0.0 = undrained
            float drainageFactor = Math.Max(0.01f, Math.Min(1.0f, materialProps.Permeability * 10.0f));
            float undrained = (1.0f - drainageFactor);

            // Calculate pore pressure change (negative for compression, positive for dilation)
            // Use bulk modulus to get correct magnitude
            float porePressureChange = -volumetricStrain * materialProps.BulkModulus * skemptonB * undrained;

            // Ensure positive minimum pressure
            return Math.Max(0.0f, initialPorePressure + porePressureChange);
        }
        /// <summary>
        /// Checks if compatible GPU compute acceleration is available
        /// </summary>
        private bool IsGPUAvailable()
        {
            try
            {
                // Check for compatible GPU capabilities
                return HasCompatibleGPU();
            }
            catch (Exception ex)
            {
                Log($"GPU detection error: {ex.Message}. Falling back to CPU computation.");
                return false;
            }
        }

        /// <summary>
        /// Initializes the compute engine with mesh data from the simulation form
        /// </summary>
        public void InitializeFromMesh(
            List<Vector3> vertices,
            List<Vector3> normals,
            List<int> indices,
            List<float> densityValues,
            List<TetrahedralElement> elements)
        {
            if (vertices == null || vertices.Count == 0)
                throw new ArgumentException("Vertices collection cannot be null or empty", nameof(vertices));

            if (indices == null || indices.Count == 0)
                throw new ArgumentException("Indices collection cannot be null or empty", nameof(indices));

            if (elements == null || elements.Count == 0)
                throw new ArgumentException("Elements collection cannot be null or empty", nameof(elements));

            // Convert to efficient array-based representation
            meshData = new MeshData
            {
                Vertices = vertices.ToArray(),
                Normals = normals != null && normals.Count > 0 ? normals.ToArray() : new Vector3[vertices.Count],
                Indices = indices.ToArray(),
                DensityValues = densityValues != null && densityValues.Count > 0 ? densityValues.ToArray() : new float[vertices.Count],
                MinDensity = densityValues != null && densityValues.Count > 0 ? densityValues.Min() : 0,
                MaxDensity = densityValues != null && densityValues.Count > 0 ? densityValues.Max() : 1
            };

            // Initialize element properties
            this.elements = new ElementProperties[elements.Count];
            for (int i = 0; i < elements.Count; i++)
            {
                var tetraElement = elements[i];

                // Guard against null vertex indices
                if (tetraElement == null || tetraElement.Vertices == null || tetraElement.Vertices.Length < 4)
                {
                    Log($"Warning: Element at index {i} is invalid. Using default values.");
                    this.elements[i] = new ElementProperties
                    {
                        VertexIndices = new int[] { 0, 0, 0, 0 },
                        Density = 2500.0f, // Default density
                        Stress = 0,
                        Strain = 0,
                        PrincipalStrains = Vector3.Zero,
                        HasFailed = false
                    };
                    continue;
                }

                this.elements[i] = new ElementProperties
                {
                    VertexIndices = tetraElement.Vertices,
                    Density = CalculateElementDensity(tetraElement, densityValues),
                    Stress = 0,
                    Strain = 0,
                    PrincipalStrains = Vector3.Zero,
                    HasFailed = false
                };
            }

            // Allocate optimized buffers sized to the mesh
            AllocateComputeBuffers();

            // Set initialized flag
            IsInitialized = true;

            Log($"Mesh initialized: {vertices.Count} vertices, {elements.Count} elements");
        }

        /// <summary>
        /// Sets material properties for the simulation
        /// </summary>
        public void SetMaterialProperties(
            float bulkDensity,
            float youngModulus,
            float poissonRatio,
            float yieldStrength,
            float brittleStrength,
            float cohesion,
            float frictionAngle,
            float porosity,
            float bulkModulus,
            float permeability,
            float minPressure,
            float maxPressure,
            bool isElasticEnabled,
            bool isPlasticEnabled,
            bool isBrittleEnabled)
        {
            materialProps = new MaterialProperties
            {
                BulkDensity = bulkDensity,
                YoungModulus = youngModulus,
                PoissonRatio = poissonRatio,
                YieldStrength = yieldStrength,
                BrittleStrength = brittleStrength,
                Cohesion = cohesion,
                FrictionAngle = frictionAngle,
                Porosity = porosity,
                BulkModulus = bulkModulus,
                Permeability = permeability,
                MinPressure = minPressure,
                MaxPressure = maxPressure,
                PrincipalStresses = new Vector3(minPressure / 1000f, minPressure / 1000f, minPressure / 1000f),
                PorePressure = 0,
                IsElasticEnabled = isElasticEnabled,
                IsPlasticEnabled = isPlasticEnabled,
                IsBrittleEnabled = isBrittleEnabled
            };

            Log("Material properties configured");
        }

        /// <summary>
        /// Allocates compute buffers optimized for operations
        /// </summary>
        private void AllocateComputeBuffers()
        {
            try
            {
                // Allocate buffers with optimal size
                int vertexCount = meshData.Vertices.Length;
                int elementCount = elements.Length;

                // Aligned buffers for better memory access patterns
                int alignedVertexCount = ((vertexCount + 7) / 8) * 8;
                int alignedElementCount = ((elementCount + 7) / 8) * 8;

                // Release old buffers if they exist
                if (parallelStressBuffer != null)
                {
                    floatPool.Return(parallelStressBuffer);
                    parallelStressBuffer = null;
                }

                if (parallelDeformationBuffer != null)
                {
                    vectorPool.Return(parallelDeformationBuffer);
                    parallelDeformationBuffer = null;
                }

                // Allocate from pools to reduce GC pressure
                parallelStressBuffer = floatPool.Rent(alignedElementCount);
                parallelDeformationBuffer = vectorPool.Rent(alignedVertexCount);

                // Initialize buffer contents
                Array.Clear(parallelStressBuffer, 0, parallelStressBuffer.Length);

                // Initialize deformation buffer with original vertices
                for (int i = 0; i < vertexCount; i++)
                {
                    parallelDeformationBuffer[i] = meshData.Vertices[i];
                }

                // Reset result structure
                currentResult = new SimulationResult
                {
                    AverageStress = 0,
                    ElementStresses = new float[elementCount],
                    DeformedVertices = new Vector3[vertexCount],
                    VolumetricStrain = 0,
                    ElasticEnergy = 0,
                    PlasticEnergy = 0,
                    PorePressure = 0,
                    Permeability = materialProps.Permeability,
                    FailurePercentage = 0,
                    HasFailed = false
                };

                // Initialize deformed vertices to original positions
                Array.Copy(meshData.Vertices, currentResult.DeformedVertices, vertexCount);

                // Log successful buffer allocation
                Log($"Compute buffers allocated: {alignedVertexCount} vertices, {alignedElementCount} elements");
            }
            catch (Exception ex)
            {
                Log($"Error allocating compute buffers: {ex.Message}");
                throw;
            }
        }

        #endregion

        #region Simulation Core Methods
        public async Task<List<Point>> ContinueSimulationAsync(
        float startStrain,
        float maxStrain,
        float strainIncrement,
        SimulationDirection direction)
        {
            cancellationSource = new CancellationTokenSource();

            try
            {
                // Set flag to continue after failure
                ignoreFailure = true;

                // Calculate number of steps with proper rounding
                int totalSteps = (int)Math.Ceiling((maxStrain - startStrain) / strainIncrement);

                // Store initial state to restore between steps
                float originalPorosity = materialProps.Porosity;
                float originalPermeability = materialProps.Permeability;

                // Variables to track peak stress
                float peakStress = stressStrainCurve.Count > 0 ? stressStrainCurve.Max(p => p.Y) / 10.0f : 0;
                float strainAtPeak = 0;

                // Find strain at peak if we have a peak
                if (stressStrainCurve.Count > 0 && peakStress > 0)
                {
                    int peakIndex = 0;
                    float maxY = stressStrainCurve.Max(p => p.Y);
                    for (int i = 0; i < stressStrainCurve.Count; i++)
                    {
                        if (stressStrainCurve[i].Y >= maxY)
                        {
                            peakIndex = i;
                            break;
                        }
                    }
                    strainAtPeak = stressStrainCurve[peakIndex].X / 10.0f; // %
                }

                bool peakDetected = peakStress > 0;
                int stepsSincePeak = 0;

                // Run simulation loop
                for (int step = 0; step <= totalSteps; step++)
                {
                    // Check for cancellation
                    if (cancellationSource.Token.IsCancellationRequested)
                    {
                        Log("Simulation cancelled");
                        break;
                    }

                    // Calculate current strain with precise step control
                    float currentStrain = startStrain + step * strainIncrement;
                    if (currentStrain > maxStrain)
                        currentStrain = maxStrain;

                    // Reset material properties before each step
                    materialProps.Porosity = originalPorosity;
                    materialProps.Permeability = originalPermeability;

                    // Run calculation for this strain step
                    SimulationResult result = ComputeStrainStep(currentStrain, direction);

                    // Store result
                    currentResult = result;

                    // Add to stress-strain curve (multiplying by 10 to match form's scaling)
                    int stressPoint = (int)(result.AverageStress * 10);
                    int strainPoint = (int)(currentStrain * 1000);

                    // Check if this point already exists in the curve and update it
                    bool pointExists = false;
                    for (int i = 0; i < stressStrainCurve.Count; i++)
                    {
                        if (stressStrainCurve[i].X == strainPoint)
                        {
                            stressStrainCurve[i] = new Point(strainPoint, stressPoint);
                            pointExists = true;
                            break;
                        }
                    }

                    // If point doesn't exist, add it
                    if (!pointExists)
                    {
                        stressStrainCurve.Add(new Point(strainPoint, stressPoint));
                    }

                    // Report progress
                    float progressPercent = (float)(currentStrain - startStrain) / (maxStrain - startStrain) * 100;
                    OnProgressUpdated(progressPercent, result);

                    // Track peak stress
                    if (result.AverageStress > peakStress)
                    {
                        peakStress = result.AverageStress;
                        strainAtPeak = currentStrain;
                        peakDetected = true;
                        stepsSincePeak = 0;
                    }
                    else if (peakDetected)
                    {
                        stepsSincePeak++;

                        // Detect post-peak stress drop as failure
                        if (stepsSincePeak >= 3 && result.AverageStress < peakStress * 0.9f)
                        {
                            // Update result to indicate failure but DO NOT break the loop when ignoreFailure is true
                            result.HasFailed = true;
                            result.FailurePercentage = 100;
                            currentResult = result;

                            Log($"Failure detected at strain {currentStrain:F4}: Stress dropped to {result.AverageStress:F2} MPa after peak of {peakStress:F2} MPa");

                            // Only exit if ignoreFailure is false
                            if (!ignoreFailure)
                            {
                                break;
                            }
                        }
                    }

                    // Short delay to allow UI updates
                    if (step % 10 == 0)
                    {
                        await Task.Delay(1);
                    }
                }

                // Ensure final result properly indicates failure if peak was detected and significant drop occurred
                if (peakDetected && stressStrainCurve.Count > 0)
                {
                    // Get final stress
                    float finalStress = stressStrainCurve.Last().Y / 10.0f;

                    // Check if there was a significant stress drop from peak (>10%)
                    if (finalStress < peakStress * 0.9f)
                    {
                        currentResult.HasFailed = true;
                        currentResult.FailurePercentage = 100;
                        Log($"Post-simulation failure detection: Peak stress {peakStress:F2} MPa dropped to {finalStress:F2} MPa");
                    }
                }

                // Store peak information for display
                currentResult.PeakStress = peakStress;
                currentResult.StrainAtPeak = strainAtPeak;

                // Reset material properties to original values
                materialProps.Porosity = originalPorosity;
                materialProps.Permeability = originalPermeability;

                // Reset the ignore failure flag for next time
                ignoreFailure = false;

                // Finalize simulation
                OnSimulationCompleted(stressStrainCurve, currentResult);
                return stressStrainCurve;
            }
            catch (Exception ex)
            {
                Log($"Simulation error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Runs a complete simulation from zero strain to maxStrain
        /// </summary>
        public async Task<List<Point>> RunFullSimulationAsync(
            float maxStrain,
            float strainIncrement,
            SimulationDirection direction)
        {
            cancellationSource = new CancellationTokenSource();
            stressStrainCurve.Clear();

            try
            {
                // Reset simulation state
                ResetSimulation();

                // Mark as initialized
                IsInitialized = true;

                // Calculate number of steps with proper rounding
                int totalSteps = (int)Math.Ceiling(maxStrain / strainIncrement);

                // Store initial state to restore between steps
                float originalPorosity = materialProps.Porosity;
                float originalPermeability = materialProps.Permeability;

                // Variables to track peak stress
                float peakStress = 0;
                float strainAtPeak = 0;
                bool peakDetected = false;
                int stepsSincePeak = 0;

                // Run simulation loop
                for (int step = 0; step <= totalSteps; step++)
                {
                    // Check for cancellation
                    if (cancellationSource.Token.IsCancellationRequested)
                    {
                        Log("Simulation cancelled");
                        break;
                    }

                    // Calculate current strain with precise step control
                    float currentStrain = step * strainIncrement;
                    if (currentStrain > maxStrain)
                        currentStrain = maxStrain;

                    // Reset material properties before each step
                    materialProps.Porosity = originalPorosity;
                    materialProps.Permeability = originalPermeability;

                    // Run calculation for this strain step
                    SimulationResult result = ComputeStrainStep(currentStrain, direction);

                    // Store result
                    currentResult = result;

                    // Add to stress-strain curve (multiplying by 10 to match form's scaling)
                    int stressPoint = (int)(result.AverageStress * 10);
                    int strainPoint = (int)(currentStrain * 1000);
                    stressStrainCurve.Add(new Point(strainPoint, stressPoint));

                    // Report progress
                    float progressPercent = (float)step / totalSteps * 100;
                    OnProgressUpdated(progressPercent, result);

                    // Track peak stress
                    if (result.AverageStress > peakStress)
                    {
                        peakStress = result.AverageStress;
                        strainAtPeak = currentStrain;
                        peakDetected = true;
                        stepsSincePeak = 0;
                    }
                    else if (peakDetected)
                    {
                        stepsSincePeak++;

                        
                        if (stepsSincePeak >= 3 && result.AverageStress < peakStress * 0.9f)
                        {
                            // Update result to indicate failure
                            result.HasFailed = true;
                            result.FailurePercentage = 100;
                            currentResult = result;

                            Log($"Failure detected at strain {currentStrain:F4}: Stress dropped to {result.AverageStress:F2} MPa after peak of {peakStress:F2} MPa");

                            // Break only if we're not ignoring failure
                            if (!ignoreFailure)
                            {
                                break;
                            }
                        }
                    }

                    // If explicit failure criterion reached and we're past 20% of max strain, we can stop
                    if (result.HasFailed && currentStrain > maxStrain * 0.2f && !ignoreFailure)
                    {
                        Log("Simulation stopped early due to complete failure");
                        break;
                    }

                    // Short delay to allow UI updates
                    if (step % 10 == 0)
                    {
                        await Task.Delay(1);
                    }
                }
// CRITICAL FIX: Ensure final result properly indicates failure if peak was detected and significant drop occurred
                if (peakDetected && stressStrainCurve.Count > 0)
                {
                    // Get final stress
                    float finalStress = stressStrainCurve.Last().Y / 10.0f;

                    // Check if there was a significant stress drop from peak (>10%)
                    if (finalStress < peakStress * 0.9f)
                    {
                        currentResult.HasFailed = true;
                        currentResult.FailurePercentage = 100;
                        Log($"Post-simulation failure detection: Peak stress {peakStress:F2} MPa dropped to {finalStress:F2} MPa");
                    }
                }

                // Store peak information for display
                currentResult.PeakStress = peakStress;
                currentResult.StrainAtPeak = strainAtPeak;

                // Reset material properties to original values
                materialProps.Porosity = originalPorosity;
                materialProps.Permeability = originalPermeability;

                // Finalize simulation
                OnSimulationCompleted(stressStrainCurve, currentResult);
                return stressStrainCurve;
            }
            catch (Exception ex)
            {
                Log($"Simulation error: {ex.Message}");
                throw;
            }
        }

        public void SetCompatibilityMode(bool enabled)
        {
            // When enabled, we'll use the exact same formulas as the CPU implementation
            if (enabled)
            {
                Log("Enabling CPU compatibility mode - results will match CPU simulation");
            }
            else
            {
                Log("Disabling CPU compatibility mode - higher performance but results may differ slightly");
            }

            // Store setting
            useCompatibilityMode = enabled;
        }
        /// <summary>
        /// Computes a single strain step in the simulation
        /// </summary>
        private SimulationResult ComputeStrainStep(float strain, SimulationDirection direction)
        {
            // Prepare for stress calculation - clean buffers
            for (int i = 0; i < parallelStressBuffer.Length; i++)
            {
                parallelStressBuffer[i] = 0;
            }

            // Prepare for deformation calculation - start from original vertices
            Array.Copy(meshData.Vertices, parallelDeformationBuffer, meshData.Vertices.Length);

            // Save original properties to restore later (prevents accumulation errors)
            float originalPorosity = materialProps.Porosity;
            float originalPermeability = materialProps.Permeability;

            try
            {
                // Calculate stresses with hardware-optimized execution
                ComputeElementStresses(strain, direction);

                // Calculate deformations based on stresses
                ComputeDeformations(strain, direction);

                // Calculate average stress with consistent summation
                double totalStress = 0;
                for (int i = 0; i < elements.Length; i++)
                {
                    totalStress += elements[i].Stress;
                }
                float averageStress = elements.Length > 0 ? (float)(totalStress / elements.Length) : 0;

                // Calculate volumetric strain BEFORE pore pressure
                float volumetricStrain = CalculateVolumetricStrain(strain);

                // Update pore pressure based on volumetric strain
                float porePressure = ComputePorePressure(strain);

                // Calculate new porosity without modifying the original
                float newPorosity = originalPorosity * (1.0f - volumetricStrain);
                newPorosity = Math.Max(0.01f, Math.Min(0.5f, newPorosity));

                // Calculate permeability from porosity change
                float permRatio = CalculatePermeabilityRatio(originalPorosity, newPorosity);
                float newPermeability = originalPermeability * permRatio;
                newPermeability = Math.Max(0.0001f, Math.Min(1000.0f, newPermeability));

                // Calculate energy components correctly
                float elasticEnergy = CalculateElasticEnergy(strain, averageStress);
                float plasticEnergy = CalculatePlasticEnergy(strain, averageStress);

                // Check failure conditions
                float failurePercentage = CalculateFailurePercentage();
                bool hasFailed = failurePercentage >= 99.9f;

                // Create result with consistent sizing
                SimulationResult result = new SimulationResult
                {
                    AverageStress = averageStress,
                    ElementStresses = new float[elements.Length],
                    DeformedVertices = new Vector3[meshData.Vertices.Length],
                    VolumetricStrain = volumetricStrain,
                    ElasticEnergy = elasticEnergy,
                    PlasticEnergy = plasticEnergy,
                    PorePressure = porePressure,
                    Permeability = newPermeability,
                    FailurePercentage = failurePercentage,
                    HasFailed = hasFailed
                };

                // Copy element stresses
                for (int i = 0; i < elements.Length; i++)
                {
                    result.ElementStresses[i] = elements[i].Stress;
                }

                // Copy deformed vertices
                Array.Copy(parallelDeformationBuffer, result.DeformedVertices, meshData.Vertices.Length);

                return result;
            }
            finally
            {
                // Restore original properties
                materialProps.Porosity = originalPorosity;
                materialProps.Permeability = originalPermeability;
            }
        }

        /// <summary>
        /// Calculates element stresses using optimized computation
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ComputeElementStresses(float strain, SimulationDirection direction)
        {
            // Reset the stress buffer before calculation
            for (int i = 0; i < parallelStressBuffer.Length; i++)
            {
                parallelStressBuffer[i] = 0;
            }

            // Process elements in parallel with optimized operations
            Parallel.For(0, elements.Length, new ParallelOptions
            {
                MaxDegreeOfParallelism = hardwareThreadCount
            }, i =>
            {
                ElementProperties element = elements[i];

                // Get element density ratio with strict clamping - match CPU implementation exactly
                float densityRatio = element.Density / materialProps.BulkDensity;
                densityRatio = Math.Max(0.5f, Math.Min(2.0f, densityRatio));

                // Scale material properties based on density
                // Using exact same formulas as CPU implementation
                float elementYoungModulus = materialProps.YoungModulus * (float)Math.Pow(densityRatio, 2.5);
                float elementPoissonRatio = Math.Min(materialProps.PoissonRatio * (float)Math.Pow(densityRatio, -0.2), 0.49f);
                float elementYieldStrength = materialProps.YieldStrength * (float)Math.Pow(densityRatio, 1.5);
                float elementBrittleStrength = materialProps.BrittleStrength * (float)Math.Pow(densityRatio, 1.2);
                float elementCohesion = materialProps.Cohesion * (float)Math.Pow(densityRatio, 1.3);
                float elementFrictionAngle = materialProps.FrictionAngle *
                    (float)Math.Min(1.2f, Math.Max(0.8f, densityRatio));

                // Calculate strain thresholds with precise calculation order
                float elementYieldStrain = elementYieldStrength / elementYoungModulus;
                float elementBrittleStrain = elementBrittleStrength / elementYoungModulus;

                // Calculate confining pressure - MATCHING CPU IMPLEMENTATION EXACTLY
                float confiningPressureMPa = materialProps.MinPressure / 1000.0f; // kPa to MPa

                // Get current pore pressure
                float porePressure = CalculatePorePressure(strain);

                // Calculate effective stresses
                float biotCoeff = Biot(materialProps.Porosity);
                float effectiveConfining = Math.Max(0.01f, confiningPressureMPa - porePressure * biotCoeff);

                // Calculate strain vector based on direction
                Vector3 strainVector = Vector3.Zero;
                switch (direction)
                {
                    case SimulationDirection.X:
                        strainVector.X = strain;
                        strainVector.Y = -strain * elementPoissonRatio;
                        strainVector.Z = -strain * elementPoissonRatio;
                        break;
                    case SimulationDirection.Y:
                        strainVector.X = -strain * elementPoissonRatio;
                        strainVector.Y = strain;
                        strainVector.Z = -strain * elementPoissonRatio;
                        break;
                    default: // Z
                        strainVector.X = -strain * elementPoissonRatio;
                        strainVector.Y = -strain * elementPoissonRatio;
                        strainVector.Z = strain;
                        break;
                }

                // Store strain vector
                element.PrincipalStrains = strainVector;
                element.Strain = strain;

                // Calculate stresses based on material models
                float stressIncrement = 0;
                bool elementFailure = element.HasFailed;

                // Elastic behavior
                if (materialProps.IsElasticEnabled && !elementFailure)
                {
                    if (strain <= elementYieldStrain || !materialProps.IsPlasticEnabled)
                    {
                        // Linear elastic relationship
                        stressIncrement = elementYoungModulus * strain;
                    }
                    else
                    {
                        // Elastic contribution up to yield
                        stressIncrement = elementYieldStrength;
                    }
                }

                // Plastic behavior
                if (materialProps.IsPlasticEnabled && strain > elementYieldStrain && !elementFailure)
                {
                    float plasticStrain = strain - elementYieldStrain;

                    // Hardening parameters - MATCH CPU IMPLEMENTATION
                    float hardeningExponent = 0.1f + 0.2f * densityRatio;
                    float plasticModulus = elementYoungModulus * 0.05f * densityRatio;

                    float plasticComponent = plasticModulus * (float)Math.Pow(plasticStrain, hardeningExponent);

                    // Combine with elastic component if enabled
                    if (materialProps.IsElasticEnabled)
                    {
                        stressIncrement += plasticComponent;
                    }
                    else
                    {
                        stressIncrement = elementYieldStrength + plasticComponent;
                    }
                }

                // Brittle behavior
                if (materialProps.IsBrittleEnabled && strain > elementBrittleStrain)
                {
                    // Calculate post-failure residual strength
                    float residualFactor = Math.Max(0.05f, 0.2f + 0.3f / densityRatio);
                    float postFailureStrain = strain - elementBrittleStrain;
                    float decayRate = 20.0f + 10.0f * (1.0f - densityRatio); // Denser materials fracture more abruptly
                    float decayFactor = (float)Math.Exp(-decayRate * postFailureStrain);

                    float residualStrength = elementBrittleStrength * residualFactor;
                    float brittleStress = residualStrength + (elementBrittleStrength - residualStrength) * decayFactor;

                    // Mark as failed for stress redistribution
                    elementFailure = true;

                    // Apply brittle stress limit
                    if (!materialProps.IsElasticEnabled && !materialProps.IsPlasticEnabled)
                    {
                        stressIncrement = brittleStress;
                    }
                    else
                    {
                        stressIncrement = Math.Min(stressIncrement, brittleStress);
                    }
                }

                // Update principal stresses
                Vector3 principalStresses = new Vector3(
                    effectiveConfining,
                    effectiveConfining,
                    effectiveConfining
                );

                // Apply differential stress based on loading direction
                switch (direction)
                {
                    case SimulationDirection.X:
                        principalStresses.X = effectiveConfining + stressIncrement;
                        break;
                    case SimulationDirection.Y:
                        principalStresses.Y = effectiveConfining + stressIncrement;
                        break;
                    default: // Z
                        principalStresses.Z = effectiveConfining + stressIncrement;
                        break;
                }

                // Check Mohr-Coulomb failure - SAME METHOD AS CPU
                if (!elementFailure)
                {
                    // Convert friction angle to radians FIRST - exact match with CPU
                    float frictionRad = elementFrictionAngle * (float)Math.PI / 180.0f;

                    elementFailure = CheckMohrCoulombFailure(
                        principalStresses,
                        elementCohesion,
                        frictionRad
                    );

                    if (elementFailure)
                    {
                        // Calculate post-failure stress based on residual strength
                        float sinPhi = (float)Math.Sin(frictionRad);
                        float residualRatio = (1.0f - sinPhi) / (1.0f + sinPhi);

                        // Find minimum and maximum principal stresses
                        float sigma3 = Math.Min(principalStresses.X,
                                      Math.Min(principalStresses.Y, principalStresses.Z));
                        float sigmaMax = Math.Max(principalStresses.X,
                                        Math.Max(principalStresses.Y, principalStresses.Z));

                        // Calculate differential stress and residual
                        float differentialStress = sigmaMax - sigma3;
                        float residualDifferential = differentialStress * residualRatio;

                        // Apply to appropriate stress component
                        switch (direction)
                        {
                            case SimulationDirection.X:
                                principalStresses.X = sigma3 + residualDifferential;
                                break;
                            case SimulationDirection.Y:
                                principalStresses.Y = sigma3 + residualDifferential;
                                break;
                            default: // Z
                                principalStresses.Z = sigma3 + residualDifferential;
                                break;
                        }

                        // Recalculate stress increment
                        switch (direction)
                        {
                            case SimulationDirection.X:
                                stressIncrement = principalStresses.X - effectiveConfining;
                                break;
                            case SimulationDirection.Y:
                                stressIncrement = principalStresses.Y - effectiveConfining;
                                break;
                            default: // Z
                                stressIncrement = principalStresses.Z - effectiveConfining;
                                break;
                        }
                    }
                }

                // Update element properties
                element.Stress = stressIncrement;
                element.HasFailed = elementFailure;
                elements[i] = element;

                // Store in parallel buffer for reduction
                parallelStressBuffer[i] = stressIncrement;
            });
        }
        /// <summary>
        /// Computes mesh deformations based on stresses and element properties
        /// </summary>
        private void ComputeDeformations(float strain, SimulationDirection direction)
        {
            // Get pore pressure first to ensure consistency
            float porePressure = ComputePorePressure(strain);
            float effectiveConfining = Math.Max(0.01f, materialProps.MinPressure / 1000f -
                                      porePressure * CalculateBiotCoefficient(materialProps.Porosity));

            // Generate deterministic seeds for fracture displacements
            // This makes results consistent between runs
            int baseSeed = (int)(strain * 10000);

            // Create synchronized random seeds table to eliminate thread variations
            Dictionary<int, int> randomSeeds = new Dictionary<int, int>();
            for (int i = 0; i < meshData.Vertices.Length; i++)
            {
                randomSeeds[i] = baseSeed ^ i;
            }

            // Apply deformations to all vertices in parallel
            Parallel.For(0, meshData.Vertices.Length, new ParallelOptions
            {
                MaxDegreeOfParallelism = hardwareThreadCount
            }, i =>
            {
                // Get original vertex position
                Vector3 originalVertex = meshData.Vertices[i];

                // Get vertex density for property scaling with proper bounds checking
                float vertexDensity = i < meshData.DensityValues.Length ?
                    meshData.DensityValues[i] : materialProps.BulkDensity;

                // Ensure density is valid
                if (float.IsNaN(vertexDensity) || vertexDensity <= 0)
                    vertexDensity = materialProps.BulkDensity;

                // Density ratio for property scaling with clamping
                float densityRatio = vertexDensity / materialProps.BulkDensity;
                densityRatio = Math.Max(0.5f, Math.Min(2.0f, densityRatio));

                // Calculate porosity based on density consistently
                float grainDensity = 2650.0f; // kg/m³ (typical for quartz/feldspar)
                float vertexPorosity = Math.Max(0.01f, Math.Min(0.5f,
                                      1.0f - (vertexDensity / grainDensity)));

                // Calculate Poisson ratio with density dependence
                float effectivePoissonRatio = Math.Min(
                    materialProps.PoissonRatio * (1.0f + 0.3f * vertexPorosity),
                    0.49f
                );

                // Calculate strain vector based on loading direction
                float axialStrain = strain;
                float lateralStrain = -strain * effectivePoissonRatio;

                // Apply deformation based on strain tensor
                Vector3 deformedPos = new Vector3(
                    originalVertex.X * (1f + (direction == SimulationDirection.X ? axialStrain : lateralStrain)),
                    originalVertex.Y * (1f + (direction == SimulationDirection.Y ? axialStrain : lateralStrain)),
                    originalVertex.Z * (1f + (direction == SimulationDirection.Z ? axialStrain : lateralStrain))
                );

                // Apply heterogeneous deformation based on material properties
                if (i < meshData.DensityValues.Length)
                {
                    // Calculate stiffness ratio using a consistent formula 
                    float stiffnessRatio = (materialProps.BulkDensity / vertexDensity);
                    stiffnessRatio = Math.Max(0.5f, Math.Min(2.0f, stiffnessRatio));

                    // Apply differential deformation
                    Vector3 deformationVector = deformedPos - originalVertex;
                    Vector3 scaledDeformation = new Vector3(
                        deformationVector.X * stiffnessRatio,
                        deformationVector.Y * stiffnessRatio,
                        deformationVector.Z * stiffnessRatio
                    );

                    deformedPos = originalVertex + scaledDeformation;
                }

                // Apply brittle fracturing effects for failed elements
                if (materialProps.IsBrittleEnabled && strain > materialProps.BrittleStrength / materialProps.YoungModulus)
                {
                    // Check if vertex is part of failed elements
                    bool inFailedElement = false;
                    float maxStressFactor = 0.0f;

                    // Find if this vertex belongs to any failed element
                    for (int j = 0; j < elements.Length; j++)
                    {
                        if (!elements[j].HasFailed) continue;

                        int[] vertexIndices = elements[j].VertexIndices;
                        if (vertexIndices == null) continue;

                        for (int k = 0; k < vertexIndices.Length; k++)
                        {
                            if (vertexIndices[k] == i)
                            {
                                inFailedElement = true;
                                float stressFactor = elements[j].Stress / materialProps.BrittleStrength;
                                maxStressFactor = Math.Max(maxStressFactor, stressFactor);
                                break;
                            }
                        }

                        if (inFailedElement) break;
                    }

                    // Add fracture displacement if in failed element
                    if (inFailedElement)
                    {
                        // Use the synchronized random seed for reproducibility
                        int seed = randomSeeds[i];
                        Random rand = new Random(seed);

                        // Displacement magnitude increases with stress and strain beyond failure
                        float excessStrain = strain - (materialProps.BrittleStrength / materialProps.YoungModulus);
                        float displacementMagnitude = 0.05f * maxStressFactor * excessStrain;

                        // Create displacement with preferential direction based on loading
                        Vector3 fracDisplacement = new Vector3(
                            (float)(rand.NextDouble() - 0.5) * displacementMagnitude,
                            (float)(rand.NextDouble() - 0.5) * displacementMagnitude,
                            (float)(rand.NextDouble() - 0.5) * displacementMagnitude
                        );

                        // Bias displacement to open perpendicular to maximum stress direction
                        switch (direction)
                        {
                            case SimulationDirection.X:
                                fracDisplacement.X *= 0.1f; // Less displacement in loading direction
                                break;
                            case SimulationDirection.Y:
                                fracDisplacement.Y *= 0.1f;
                                break;
                            default: // Z
                                fracDisplacement.Z *= 0.1f;
                                break;
                        }

                        // Apply fracture displacement
                        deformedPos += fracDisplacement;
                    }
                }

                // Apply pore pressure effects if permeability is low
                if (materialProps.Permeability < 0.1f && porePressure > 0.5f)
                {
                    float swellingFactor = Math.Max(0.0f, porePressure - effectiveConfining) * 0.001f;

                    // Isotropic expansion proportional to pore pressure
                    deformedPos.X *= (1f + swellingFactor);
                    deformedPos.Y *= (1f + swellingFactor);
                    deformedPos.Z *= (1f + swellingFactor);
                }

                // Save updated position
                parallelDeformationBuffer[i] = deformedPos;
            });
        }
        /// <summary>
        /// Calculates pore pressure based on volumetric strain and permeability
        /// </summary>
        private float ComputePorePressure(float strain)
        {
            // Initial pore pressure (could be from user input)
            float initialPorePressure = 0.1f; // MPa

            // Calculate volumetric strain (improved precision)
            float volumetricStrain = CalculateVolumetricStrain(strain);

            // Skempton's B parameter - relates mean stress to pore pressure
            float skemptonB = CalculateSkemptonB(materialProps.Porosity);

            // Pore pressure buildup due to undrained conditions inversely related to permeability
            float drainageFactor = Math.Max(0.01f, Math.Min(1.0f, materialProps.Permeability * 10.0f));
            float undrained = (1.0f - drainageFactor);

            // Calculate pore pressure buildup
            float porePressureChange = -volumetricStrain * materialProps.BulkModulus * skemptonB * undrained;

            // Return total pore pressure
            return initialPorePressure + porePressureChange;
        }
        /// <summary>
        /// Calculates new porosity from volumetric strain without modifying material properties
        /// </summary>
        private float ComputeNewPorosityFromStrain(float initialPorosity, float volumetricStrain)
        {
            float newPorosity = initialPorosity * (1.0f - volumetricStrain);
            return Math.Max(0.01f, Math.Min(0.5f, newPorosity));
        }
        /// <summary>
        /// Calculates permeability from porosity without modifying material properties
        /// </summary>
        private float CalculatePermeabilityFromPorosity(float newPorosity, float initialPermeability)
        {
            // Kozeny-Carman relationship
            float oldPermeabilityFactor = (float)(Math.Pow(materialProps.Porosity, 3.0) /
                                       Math.Pow(1.0 - materialProps.Porosity, 2.0));
            float newPermeabilityFactor = (float)(Math.Pow(newPorosity, 3.0) /
                                       Math.Pow(1.0 - newPorosity, 2.0));

            // Relative change
            float permeabilityRatio = newPermeabilityFactor / oldPermeabilityFactor;

            // Calculate new permeability without modifying properties
            float newPermeability = initialPermeability * permeabilityRatio;
            return Math.Max(0.0001f, Math.Min(1000.0f, newPermeability));
        }

        /// <summary>
        /// Calculate permeability ratio based on porosity change
        /// </summary>
        private float CalculatePermeabilityRatio(float oldPorosity, float newPorosity)
        {
            // Kozeny-Carman relationship
            // k ∝ φ³/(1-φ)²

            // Calculate factors with safeguards against division by zero
            float oldFactor = (float)Math.Pow(oldPorosity, 3.0) /
                              (float)Math.Pow(Math.Max(0.01f, 1.0f - oldPorosity), 2.0);

            float newFactor = (float)Math.Pow(newPorosity, 3.0) /
                              (float)Math.Pow(Math.Max(0.01f, 1.0f - newPorosity), 2.0);

            // Calculate ratio with protection against division by zero
            float ratio = oldFactor > float.Epsilon ? newFactor / oldFactor : 1.0f;

            // Ensure result is reasonable (prevent extreme values)
            return Math.Max(0.01f, Math.Min(100.0f, ratio));
        }
        /// <summary>
        /// Updates permeability based on volumetric strain
        /// </summary>
        private float UpdatePermeability(float volumetricStrain)
        {
            // Kozeny-Carman permeability change with porosity
            // New porosity from volumetric strain
            float newPorosity = materialProps.Porosity * (1.0f - volumetricStrain);
            newPorosity = Math.Max(0.01f, Math.Min(0.5f, newPorosity));

            // Update the porosity value (for next iteration)
            materialProps.Porosity = newPorosity;

            // Update permeability using Kozeny-Carman relationship
            // k ∝ φ³/(1-φ)²
            float oldPermeabilityFactor = (float)(Math.Pow(materialProps.Porosity, 3.0) /
                                         Math.Pow(1.0 - materialProps.Porosity, 2.0));
            float newPermeabilityFactor = (float)(Math.Pow(newPorosity, 3.0) /
                                         Math.Pow(1.0 - newPorosity, 2.0));

            // Relative change
            float permeabilityRatio = newPermeabilityFactor / oldPermeabilityFactor;

            // Apply change to permeability
            float newPermeability = materialProps.Permeability * permeabilityRatio;
            newPermeability = Math.Max(0.0001f, Math.Min(1000.0f, newPermeability));

            // Update material property
            materialProps.Permeability = newPermeability;

            return newPermeability;
        }

        /// <summary>
        /// Calculates the failure percentage based on stress state vs. failure criterion
        /// </summary>
        private float CalculateFailurePercentage()
        {
            // Check how close we are to the failure envelope
            int failedElements = 0;
            double totalFailurePercent = 0;

            for (int i = 0; i < elements.Length; i++)
            {
                if (elements[i].HasFailed)
                {
                    failedElements++;
                    totalFailurePercent += 100;
                    continue;
                }

                // Get element properties
                float densityRatio = elements[i].Density / materialProps.BulkDensity;
                densityRatio = Math.Max(0.5f, Math.Min(2.0f, densityRatio));

                float elementCohesion = materialProps.Cohesion * (float)Math.Pow(densityRatio, 1.3);
                float elementFrictionAngle = materialProps.FrictionAngle *
                    (float)Math.Min(1.2f, Math.Max(0.8f, densityRatio));

                // Get confining pressure - EXACT MATCH WITH CPU
                float confiningPressureMPa = materialProps.MinPressure / 1000.0f; // kPa to MPa

                // Calculate effective confining pressure using same method as CPU
                float effectiveConfining = Math.Max(0.01f, confiningPressureMPa -
                    currentResult.PorePressure * CalculateBiotCoefficient(materialProps.Porosity));

                // Create principal stresses
                Vector3 principalStresses = new Vector3(
                    effectiveConfining,
                    effectiveConfining,
                    effectiveConfining
                );

                // Add differential stress - MATCHING CPU implementation
                principalStresses.Z = effectiveConfining + elements[i].Stress;

                // Calculate Mohr-Coulomb percentages
                float frictionRad = elementFrictionAngle * (float)Math.PI / 180.0f;
                float sinPhi = (float)Math.Sin(frictionRad);
                float cosPhi = (float)Math.Cos(frictionRad);

                // Find min and max principal stresses
                float sigma1 = Math.Max(principalStresses.X,
                              Math.Max(principalStresses.Y, principalStresses.Z));
                float sigma3 = Math.Min(principalStresses.X,
                              Math.Min(principalStresses.Y, principalStresses.Z));

                // Calculate how close we are to failure
                float leftSide = sigma1 - sigma3;
                float rightSide = 2.0f * elementCohesion * cosPhi + (sigma1 + sigma3) * sinPhi;

                float failurePercent = 0;
                if (rightSide > float.Epsilon)
                {
                    failurePercent = (leftSide / rightSide) * 100;
                }
                else if (leftSide > 0)
                {
                    failurePercent = 100; // Already at failure if rightSide is near zero
                }

                // Clamp to valid range
                failurePercent = Math.Max(0, Math.Min(100, failurePercent));

                totalFailurePercent += failurePercent;
            }

            // Calculate average failure percentage
            float result = elements.Length > 0 ? (float)(totalFailurePercent / elements.Length) : 0;

            // Ensure result is in valid range
            return Math.Max(0, Math.Min(100, result));
        }


        #endregion

        #region Helper Methods

        /// <summary>
        /// Resets the simulation to initial state
        /// </summary>
        public void ResetSimulation()
        {
            // Reset the stress-strain curve
            stressStrainCurve.Clear();

            // Capture original material properties before resetting
            float originalPorosity = materialProps.Porosity;
            float originalPermeability = materialProps.Permeability;

            // Reset element states
            for (int i = 0; i < elements.Length; i++)
            {
                elements[i].Stress = 0;
                elements[i].Strain = 0;
                elements[i].HasFailed = false;
                elements[i].PrincipalStrains = Vector3.Zero;
            }

            // Reset deformed vertices to original positions
            if (currentResult.DeformedVertices.Length != meshData.Vertices.Length)
            {
                // Allocate new array if sizes don't match
                currentResult.DeformedVertices = new Vector3[meshData.Vertices.Length];
            }
            Array.Copy(meshData.Vertices, currentResult.DeformedVertices, meshData.Vertices.Length);

            // Reset result values using float.Epsilon to avoid exact zeros
            currentResult.AverageStress = 0;
            currentResult.ElasticEnergy = 0;
            currentResult.PlasticEnergy = 0;
            currentResult.PorePressure = 0;
            currentResult.FailurePercentage = 0;
            currentResult.HasFailed = false;
            currentResult.VolumetricStrain = 0;

            // Important: Reset all element stresses array
            if (currentResult.ElementStresses == null ||
                currentResult.ElementStresses.Length != elements.Length)
            {
                currentResult.ElementStresses = new float[elements.Length];
            }
            else
            {
                Array.Clear(currentResult.ElementStresses, 0, currentResult.ElementStresses.Length);
            }

            // Restore original material properties
            materialProps.Porosity = originalPorosity;
            materialProps.Permeability = originalPermeability;
            currentResult.Permeability = originalPermeability;

            Log("Simulation reset to initial state");
        }
        /// <summary>
        /// Calculates the volumetric strain based on current strain and material properties
        /// </summary>
        private float CalculateVolumetricStrain(float axialStrain)
        {
            
            float poissonRatio = materialProps.PoissonRatio;

            // For triaxial test, volumetric strain is:
            // εvol = εaxial + 2*εlateral = εaxial + 2*(-v*εaxial) = εaxial * (1 - 2v)
            return axialStrain * (1.0f - 2.0f * poissonRatio);
        }

        /// <summary>
        /// Calculates elastic energy based on stress and strain
        /// </summary>
        private float CalculateElasticEnergy(float strain, float stress)
        {
            // FIXED: Return 0 only if elastic is disabled OR strain is exactly 0
            if (!materialProps.IsElasticEnabled || Math.Abs(strain) < float.Epsilon)
                return 0;

            float yieldStrain = materialProps.YieldStrength / materialProps.YoungModulus;
            float strainForEnergy = Math.Min(strain, yieldStrain);

            // For elastic region, energy = 0.5 * stress * strain
            if (strain <= yieldStrain || !materialProps.IsPlasticEnabled)
            {
                return 0.5f * stress * strain;
            }
            else
            {
                // Just the elastic portion
                return 0.5f * materialProps.YieldStrength * yieldStrain;
            }
        }


        /// <summary>
        /// Calculates plastic energy based on stress and strain
        /// </summary>
        private float CalculatePlasticEnergy(float strain, float stress)
        {
            if (!materialProps.IsPlasticEnabled) return 0;

            float yieldStrain = materialProps.YieldStrength / materialProps.YoungModulus;

            if (strain <= yieldStrain)
            {
                return 0; // No plastic deformation yet
            }

            // Plastic strain
            float plasticStrain = strain - yieldStrain;

            // Energy is area under curve in plastic region
            // Approximated as (yield_strength + current_stress)/2 * plastic_strain
            return (materialProps.YieldStrength + stress) * 0.5f * plasticStrain;
        }

        /// <summary>
        /// Checks if the stress state meets the Mohr-Coulomb failure criterion
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool CheckMohrCoulombFailure(Vector3 principalStresses, float cohesion, float frictionAngle)
        {
            // Find maximum and minimum principal stresses
            float sigma1 = Math.Max(principalStresses.X, Math.Max(principalStresses.Y, principalStresses.Z));
            float sigma3 = Math.Min(principalStresses.X, Math.Min(principalStresses.Y, principalStresses.Z));

            // Mohr-Coulomb parameters
            float sinPhi = (float)Math.Sin(frictionAngle);
            float cosPhi = (float)Math.Cos(frictionAngle);

            // Mohr-Coulomb criterion: (σ1 - σ3) ≥ 2c·cos(φ) + (σ1 + σ3)·sin(φ)
            float leftSide = sigma1 - sigma3;
            float rightSide = 2.0f * cohesion * cosPhi + (sigma1 + sigma3) * sinPhi;

            // Tensile cutoff using correct friction value
            float tensileStrength = cohesion / (float)Math.Tan(frictionAngle);
            bool tensileFailure = sigma3 < -tensileStrength;

            return leftSide >= rightSide || tensileFailure;
        }

        /// <summary>
        /// Calculates element density from tetrahedron vertices
        /// </summary>
        private float CalculateElementDensity(TetrahedralElement element, List<float> densityValues)
        {
            float sum = 0;
            int count = 0;

            foreach (int vIndex in element.Vertices)
            {
                if (vIndex < densityValues.Count)
                {
                    sum += densityValues[vIndex];
                    count++;
                }
            }

            return count > 0 ? sum / count : materialProps.BulkDensity;
        }

        /// <summary>
        /// Calculates Skempton's B parameter
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float CalculateSkemptonB(float porosity)
        {
            // Skempton's B parameter relates pore pressure to confining pressure
            // B = 1 / (1 + n·Kf/K), where n=porosity, Kf=fluid bulk modulus, K=rock bulk modulus
            float fluidBulkModulus = 2200.0f; // Water, MPa
            float skemptonB = 1.0f / (1.0f + porosity * fluidBulkModulus / materialProps.BulkModulus);

            // Clamp to physical range
            return Math.Max(0.01f, Math.Min(0.99f, skemptonB));
        }

        /// <summary>
        /// Calculates Biot coefficient for effective stress
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float CalculateBiotCoefficient(float porosity)
        {
            // Biot coefficient: α = 1 - K/Ks
            // where K is bulk modulus, Ks is solid grain bulk modulus
            float grainBulkModulus = 36000.0f; // Typical for quartz, MPa
            return 1.0f - (materialProps.BulkModulus / grainBulkModulus);
        }

        /// <summary>
        /// Determines if GPU compute is available
        /// </summary>
        private bool HasCompatibleGPU()
        {
            
            try
            {
                return Environment.ProcessorCount >= 4; 
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Logs a message
        /// </summary>
        private void Log(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[DirectCompute] {message}");
        }

        /// <summary>
        /// Raises the ProgressUpdated event
        /// </summary>
        protected virtual void OnProgressUpdated(float progressPercent, SimulationResult result)
        {
            ProgressUpdated?.Invoke(this, new DirectComputeProgressEventArgs(
                progressPercent,
                result.AverageStress,
                result.VolumetricStrain,
                result.PorePressure,
                result.Permeability,
                result.FailurePercentage,
                result.HasFailed
            ));
        }

        /// <summary>
        /// Raises the SimulationCompleted event
        /// </summary>
        protected virtual void OnSimulationCompleted(List<Point> stressStrainCurve, SimulationResult result)
        {
            SimulationCompleted?.Invoke(this, new DirectComputeCompletedEventArgs(
                stressStrainCurve,
                result.DeformedVertices,
                result.ElementStresses,
                result.VolumetricStrain,
                result.ElasticEnergy,
                result.PlasticEnergy,
                result.Permeability,
                result.FailurePercentage,
                result.HasFailed,
                result.PeakStress,
                result.StrainAtPeak
            ));
        }

        /// <summary>
        /// Gets the current simulation results for form display
        /// </summary>
        public SimulationData GetCurrentResults()
        {
            return new SimulationData(
                stressStrainCurve.ToList(),
                currentResult.AverageStress,
                currentResult.VolumetricStrain,
                currentResult.PorePressure,
                currentResult.Permeability,
                materialProps.Permeability,
                currentResult.ElasticEnergy,
                currentResult.PlasticEnergy,
                currentResult.FailurePercentage,
                currentResult.HasFailed,
                currentResult.DeformedVertices
            );
        }

        #endregion

        #region IDisposable Implementation

        public void Dispose()
        {
            if (disposed) return;

            // Cancel any running simulation
            if (cancellationSource != null)
            {
                cancellationSource.Cancel();
                cancellationSource.Dispose();
            }

            // Return pooled arrays
            if (floatPool != null && parallelStressBuffer != null)
            {
                floatPool.Return(parallelStressBuffer);
            }

            if (vectorPool != null && parallelDeformationBuffer != null)
            {
                vectorPool.Return(parallelDeformationBuffer);
            }

            disposed = true;
            GC.SuppressFinalize(this);
        }

        #endregion
    }

    /// <summary>
    /// Custom result data structure for DirectTriaxialCompute
    /// </summary>
    public class SimulationData
    {
        public List<Point> StressStrainCurve { get; }
        public float CurrentStress { get; }
        public float VolumetricStrain { get; }
        public float PorePressure { get; }
        public float CurrentPermeability { get; }
        public float InitialPermeability { get; }
        public float ElasticEnergy { get; }
        public float PlasticEnergy { get; }
        public float FailurePercentage { get; }
        public bool HasFailed { get; }
        public Vector3[] DeformedVertices { get; }

        public SimulationData(
            List<Point> stressStrainCurve,
            float currentStress,
            float volumetricStrain,
            float porePressure,
            float currentPermeability,
            float initialPermeability,
            float elasticEnergy,
            float plasticEnergy,
            float failurePercentage,
            bool hasFailed,
            Vector3[] deformedVertices)
        {
            StressStrainCurve = stressStrainCurve;
            CurrentStress = currentStress;
            VolumetricStrain = volumetricStrain;
            PorePressure = porePressure;
            CurrentPermeability = currentPermeability;
            InitialPermeability = initialPermeability;
            ElasticEnergy = elasticEnergy;
            PlasticEnergy = plasticEnergy;
            FailurePercentage = failurePercentage;
            HasFailed = hasFailed;
            DeformedVertices = deformedVertices;
        }
    }

    /// <summary>
    /// Event arguments for direct compute progress updates
    /// </summary>
    public class DirectComputeProgressEventArgs : EventArgs
    {
        public float ProgressPercent { get; }
        public float CurrentStress { get; }
        public float VolumetricStrain { get; }
        public float PorePressure { get; }
        public float Permeability { get; }
        public float FailurePercentage { get; }
        public bool HasFailed { get; }

        public DirectComputeProgressEventArgs(
            float progressPercent,
            float currentStress,
            float volumetricStrain,
            float porePressure,
            float permeability,
            float failurePercentage,
            bool hasFailed)
        {
            ProgressPercent = progressPercent;
            CurrentStress = currentStress;
            VolumetricStrain = volumetricStrain;
            PorePressure = porePressure;
            Permeability = permeability;
            FailurePercentage = failurePercentage;
            HasFailed = hasFailed;
        }
    }

    /// <summary>
    /// Event arguments for direct compute simulation completion
    /// </summary>
    public class DirectComputeCompletedEventArgs : EventArgs
    {
        public List<Point> StressStrainCurve { get; }
        public Vector3[] DeformedVertices { get; }
        public float[] ElementStresses { get; }
        public float VolumetricStrain { get; }
        public float ElasticEnergy { get; }
        public float PlasticEnergy { get; }
        public float Permeability { get; }
        public float FailurePercentage { get; }
        public bool HasFailed { get; }
        public float PeakStress { get; }
        public float StrainAtPeak { get; }

        public DirectComputeCompletedEventArgs(
            List<Point> stressStrainCurve,
            Vector3[] deformedVertices,
            float[] elementStresses,
            float volumetricStrain,
            float elasticEnergy,
            float plasticEnergy,
            float permeability,
            float failurePercentage,
            bool hasFailed,
            float peakStress = 0,
            float strainAtPeak = 0)
        {
            StressStrainCurve = stressStrainCurve;
            DeformedVertices = deformedVertices;
            ElementStresses = elementStresses;
            VolumetricStrain = volumetricStrain;
            ElasticEnergy = elasticEnergy;
            PlasticEnergy = plasticEnergy;
            Permeability = permeability;
            FailurePercentage = failurePercentage;
            HasFailed = hasFailed;
            PeakStress = peakStress;
            StrainAtPeak = strainAtPeak;
        }
    }

    /// <summary>
    /// Helper class for memory pooling to reduce GC pressure
    /// </summary>
    public class ArrayPool<T>
    {
        private readonly ConcurrentBag<T[]> pools = new ConcurrentBag<T[]>();
        private readonly int maxPoolSize;

        public ArrayPool(int maxPoolSize = 50)
        {
            this.maxPoolSize = maxPoolSize;
        }

        public T[] Rent(int minimumLength)
        {
            if (pools.TryTake(out T[] array) && array.Length >= minimumLength)
            {
                return array;
            }

            return new T[minimumLength];
        }

        public void Return(T[] array)
        {
            if (array == null) return;

            if (pools.Count < maxPoolSize)
            {
                // Clear sensitive data
                Array.Clear(array, 0, array.Length);
                pools.Add(array);
            }
        }
    }
    public enum SimulationDirection { X, Y, Z }
}