using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Numerics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.OpenCL;

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

        // Density statistics
        public float MinimumDensity { get; private set; }
        public float MaximumDensity { get; private set; }
        public float AverageDensity { get; private set; }

        // Derived properties accounting for density variations
        public Dictionary<Triangle, float> TriangleDensities { get; private set; }
        public Dictionary<Triangle, float> TriangleStressFactors { get; private set; }

        // Flag for density model initialization
        private bool _hasInitializedInhomogeneousModels = false;

        #endregion

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
        /// Initialize inhomogeneous density model
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

            // Process each triangle in the mesh
            foreach (var triangle in MeshTriangles)
            {
                // Find the centroid of the triangle
                Vector3 centroid = new Vector3(
                    (triangle.V1.X + triangle.V2.X + triangle.V3.X) / 3f,
                    (triangle.V1.Y + triangle.V2.Y + triangle.V3.Y) / 3f,
                    (triangle.V1.Z + triangle.V2.Z + triangle.V3.Z) / 3f
                );

                // Find the nearest density value from the density map
                float density = FindNearestDensity(centroid);

                // Store the density for this triangle
                TriangleDensities[triangle] = density;

                // Calculate a stress factor based on density variation
                // (denser regions typically handle more stress)
                float stressFactor = density / baseDensity;

                // Modify the stress factor to provide a reasonable variation
                // Square root relationship provides a more moderate effect
                stressFactor = (float)Math.Sqrt(stressFactor);

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
        /// Find the nearest density value from the density map
        /// </summary>
        private float FindNearestDensity(Vector3 position)
        {
            if (_densityMap == null || _densityMap.Count == 0)
                return (float)Material.Density;

            // Check if exact position exists
            if (_densityMap.TryGetValue(position, out float exactDensity))
                return exactDensity;

            // Find the nearest neighbor
            const float searchRadius = 5.0f; // Adjust search radius as needed
            float minDistance = float.MaxValue;
            float nearestDensity = (float)Material.Density;

            foreach (var entry in _densityMap)
            {
                Vector3 densityPos = entry.Key;
                float distance = Vector3.Distance(position, densityPos);

                if (distance < minDistance && distance < searchRadius)
                {
                    minDistance = distance;
                    nearestDensity = entry.Value;
                }
            }

            return nearestDensity;
        }

        #endregion

        #region Simulation Override Methods

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
                }
                else
                {
                    stressFactors[i] = 1.0f; // Default factor if not density-enabled or no factor found
                }
            }

            // Convert friction angle from degrees to radians
            float frictionAngleRad = FrictionAngle * (float)Math.PI / 180f;

            try
            {
                // CRITICAL: Make sure all arrays have the same length
                using (var b1 = _accelerator.Allocate1D<Vector3>(n))
                using (var b2 = _accelerator.Allocate1D<Vector3>(n))
                using (var b3 = _accelerator.Allocate1D<Vector3>(n))
                using (var bfac = _accelerator.Allocate1D<float>(n))
                using (var bv = _accelerator.Allocate1D<float>(n))
                using (var bs1 = _accelerator.Allocate1D<float>(n))
                using (var bs2 = _accelerator.Allocate1D<float>(n))
                using (var bs3 = _accelerator.Allocate1D<float>(n))
                using (var bf = _accelerator.Allocate1D<int>(n))
                {
                    // Copy data to device
                    b1.CopyFromCPU(v1);
                    b2.CopyFromCPU(v2);
                    b3.CopyFromCPU(v3);
                    bfac.CopyFromCPU(stressFactors);

                    // Execute stress kernel with error handling
                    Logger.Log($"[InhomogeneousTriaxialSimulation] Running kernel with {n} triangles");

                    // Launch kernel with the CORRECT number of threads
                    ComputeInhomogeneousStressKernel(
                        n, // This should match the array lengths
                        b1.View, b2.View, b3.View,
                        bfac.View,
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

                    // Copy results back to host
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
                throw;
            }

            int fcount = 0;
            for (int i = 0; i < n; i++)
            {
                var tri = _simulationTriangles[i]; // Use protected field
                tri.VonMisesStress = vm[i];
                tri.Stress1 = s1[i];
                tri.Stress2 = s2[i];
                tri.Stress3 = s3[i];

                // Apply the proper Mohr-Coulomb criterion on the host side as well
                tri.FractureProbability = CalculateFractureProbability(s1[i], s3[i], CohesionStrength, FrictionAngle);

                // Use a more reasonable threshold for fracture detection
                bool fracturePredicted = frac[i] == 1;
                bool hostFractureCheck = tri.FractureProbability > 0.75f;

                tri.IsFractured = fracturePredicted || hostFractureCheck;

                if (tri.IsFractured) fcount++;
                _simulationTriangles[i] = tri; // Update the protected field, not MeshTriangles
            }

            await Task.Delay(10, CancellationToken.None);

            // More sensitive detection criterion - only require 2% of triangles to be fractured
            float fracturePercentage = (float)fcount / n;

            // Log the fracture percentage for debugging
            if (fracturePercentage > 0.01f)
            {
                Logger.Log($"[InhomogeneousTriaxialSimulation] Fracture percentage: {fracturePercentage:P2} at pressure {axialPressure} MPa");
            }

            return fracturePercentage > 0.02f; // Only 2% required for fracture detection
        }

        /// <summary>
        /// ILGPU kernel for inhomogeneous density stress calculation
        /// </summary>
        private static void ComputeInhomogeneousStressKernel(
            Index1D idx,
            ArrayView<Vector3> v1Arr,
            ArrayView<Vector3> v2Arr,
            ArrayView<Vector3> v3Arr,
            ArrayView<float> stressFactors,
            float pConf,                  // Confining pressure [MPa]
            float pAxial,                 // Applied axial pressure [MPa]
            Vector3 axis,                 // Test axis (unit)
            float cohesion,               // Cohesion strength [MPa]
            float frictionAngleRad,       // Friction angle [radians]
            ArrayView<float> vmArr,       // Von‑Mises σₑ [MPa]
            ArrayView<float> s1Arr,       // σ₁
            ArrayView<float> s2Arr,       // σ₂
            ArrayView<float> s3Arr,       // σ₃
            ArrayView<int> fracArr)       // 1 = failed, 0 = intact
        {
            // IMPORTANT: Add bounds check to prevent index out of range errors
            if (idx >= v1Arr.Length || idx >= v2Arr.Length || idx >= v3Arr.Length ||
                idx >= stressFactors.Length || idx >= vmArr.Length ||
                idx >= s1Arr.Length || idx >= s2Arr.Length ||
                idx >= s3Arr.Length || idx >= fracArr.Length)
            {
                return; // Skip this thread if any index would be out of bounds
            }

            // Get the stress factor for this triangle
            float stressFactor = stressFactors[idx];

            // Geometry helpers
            Vector3 v1 = v1Arr[idx];
            Vector3 v2 = v2Arr[idx];
            Vector3 v3 = v3Arr[idx];

            Vector3 e1 = v2 - v1;
            Vector3 e2 = v3 - v1;
            Vector3 n = Vector3.Normalize(Vector3.Cross(e1, e2));

            // Orientation factor ∈ [0,1]
            float align = Math.Abs(Vector3.Dot(n, axis));

            // Construct the stress tensor with density-adjusted stresses
            // Apply the stress factor to modify the stresses based on local density
            float sigma1_raw = pAxial * stressFactor;
            float sigmaT_raw = pConf * stressFactor;

            // Interpolate based on orientation
            float sigma_n = sigma1_raw * align + sigmaT_raw * (1f - align);
            float sigma_t = sigmaT_raw * align + sigma1_raw * (1f - align);

            // Assemble diagonalized representation
            float σ1 = Math.Max(sigma_n, sigma_t);
            float σ3 = Math.Min(sigma_n, sigma_t);
            float σ2 = sigma_t;

            // Von‑Mises equivalent stress
            float vm = 0.5f * ((σ1 - σ2) * (σ1 - σ2) +
                              (σ2 - σ3) * (σ2 - σ3) +
                              (σ3 - σ1) * (σ3 - σ1));
            vm = MathF.Sqrt(vm);

            // Full Mohr–Coulomb failure check
            float sinPhi = MathF.Sin(frictionAngleRad);
            float cosPhi = MathF.Cos(frictionAngleRad);
            float rhs = (2f * cohesion * cosPhi + (σ1 + σ3) * sinPhi) / (1f - sinPhi);
            bool failed = (σ1 - σ3) >= rhs;

            // Store results
            vmArr[idx] = vm;
            s1Arr[idx] = σ1;
            s2Arr[idx] = σ2;
            s3Arr[idx] = σ3;
            fracArr[idx] = failed ? 1 : 0;
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

            // Calculate the criterion threshold
            float numerator = (2.0f * cohesion * cosPhi) + ((stress1 + stress3) * sinPhi);
            float denominator = 1.0f - sinPhi;
            float thresholdValue = numerator / denominator;

            // Calculate the stress difference
            float stressDiff = stress1 - stress3;

            // Calculate the ratio of actual stress to threshold
            float ratio = stressDiff / thresholdValue;

            // Apply a more sensitive sigmoid function with center at 0.8
            float probability = 1.0f / (1.0f + (float)Math.Exp(-12 * (ratio - 0.8f)));

            return Math.Max(0f, Math.Min(1f, probability));
        }

        /// <summary>
        /// Override RenderResults to add density visualization options
        /// </summary>
        public override void RenderResults(Graphics g, int width, int height, RenderMode renderMode = RenderMode.Stress)
        {
            // Replace Custom1 with FailureProbability (or another suitable existing mode)
            if (renderMode == RenderMode.FailureProbability && _useInhomogeneousDensity)
            {
                RenderDensityDistribution(g, width, height);
                return;
            }

            // Call the base rendering method
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

                    // Position in top-right corner
                    g.FillRectangle(backBrush, width - textSize.Width - 15, 10, textSize.Width + 10, textSize.Height + 5);
                    g.DrawString(message, font, textBrush, width - textSize.Width - 10, 12);

                    // Add density range if we have it
                    if (MinimumDensity < MaximumDensity)
                    {
                        string rangeText = $"Density: {MinimumDensity:F0}-{MaximumDensity:F0} kg/m³";
                        SizeF rangeSize = g.MeasureString(rangeText, font);
                        g.FillRectangle(backBrush, width - rangeSize.Width - 15, 15 + textSize.Height, rangeSize.Width + 10, rangeSize.Height + 5);
                        g.DrawString(rangeText, font, textBrush, width - rangeSize.Width - 10, 17 + textSize.Height);
                    }
                }
            }
        }

        /// <summary>
        /// Render density distribution visualization
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
            float scale = Math.Min(width, height) / 200.0f;
            float centerX = width / 2.0f;
            float centerY = height / 2.0f;
            float maxCoord = FindMaxCoordinate();

            // Default rotation angles
            float rotationX = 0.5f;
            float rotationY = 0.5f;

            // Create a list to hold all triangles with their depth and density
            var trianglesToDraw = new List<(Triangle Triangle, float Depth, float Density)>();

            // Prepare triangles with depth and density info
            foreach (var tri in MeshTriangles)
            {
                float depth = (tri.V1.Z + tri.V2.Z + tri.V3.Z) / 3.0f;
                float density = (float)Material.Density; // Default

                if (TriangleDensities.TryGetValue(tri, out float triangleDensity))
                {
                    density = triangleDensity;
                }

                trianglesToDraw.Add((tri, depth, density));
            }

            // Sort triangles by depth (back to front)
            trianglesToDraw.Sort((a, b) => -a.Depth.CompareTo(b.Depth));

            // Draw each triangle with color based on density
            foreach (var (tri, _, density) in trianglesToDraw)
            {
                // Project vertices
                PointF p1 = ProjectVertex(tri.V1, centerX, centerY, scale, maxCoord, rotationX, rotationY);
                PointF p2 = ProjectVertex(tri.V2, centerX, centerY, scale, maxCoord, rotationX, rotationY);
                PointF p3 = ProjectVertex(tri.V3, centerX, centerY, scale, maxCoord, rotationX, rotationY);

                // Get color based on density
                float normalizedDensity = (density - MinimumDensity) / (MaximumDensity - MinimumDensity);
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
                              $"Density Range: {MinimumDensity:F0} - {MaximumDensity:F0} kg/m³\n" +
                              $"Average Density: {AverageDensity:F0} kg/m³";
                g.DrawString(info, infoFont, textBrush, 20, 50);
            }

            // Draw color scale
            DrawDensityColorScale(g, width - 80, height / 3, 30, height / 3);
        }

        /// <summary>
        /// Draw density color scale legend
        /// </summary>
        private void DrawDensityColorScale(Graphics g, int x, int y, int width, int height)
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

            // Draw min/max values
            using (Font font = new Font("Arial", 8))
            using (SolidBrush brush = new SolidBrush(Color.White))
            {
                g.DrawString("Density (kg/m³)", font, brush, x - 10, y - 15);
                g.DrawString($"{MaximumDensity:F0}", font, brush, x + width + 5, y);
                g.DrawString($"{AverageDensity:F0}", font, brush, x + width + 5, y + height / 2);
                g.DrawString($"{MinimumDensity:F0}", font, brush, x + width + 5, y + height - 10);
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
        /// Get a color from a heatmap gradient based on a value
        /// </summary>
        private Color GetHeatMapColor(float value, float min, float max)
        {
            try
            {
                // Safety checks to prevent invalid inputs
                if (float.IsNaN(value) || float.IsInfinity(value))
                    value = min;

                if (float.IsNaN(min) || float.IsInfinity(min))
                    min = 0;

                if (float.IsNaN(max) || float.IsInfinity(max))
                    max = 1;

                // If min >= max, use a safe default
                if (min >= max)
                {
                    min = 0;
                    max = 1;
                }

                // Normalize value to 0-1 range with safety bounds
                float normalized = Math.Max(0f, Math.Min(1f, (value - min) / (max - min)));

                // Create a safe function to ensure color values are in the valid range
                int SafeColorValue(float val) => Math.Max(0, Math.Min(255, (int)(val * 255)));

                // Create a heatmap gradient: blue -> cyan -> green -> yellow -> red
                if (normalized < 0.25f)
                {
                    // Blue to cyan
                    float t = normalized / 0.25f;
                    return Color.FromArgb(
                        255, // alpha
                        0,   // red
                        SafeColorValue(t), // green (0 -> 255)
                        255  // blue
                    );
                }
                else if (normalized < 0.5f)
                {
                    // Cyan to green
                    float t = (normalized - 0.25f) / 0.25f;
                    return Color.FromArgb(
                        255, // alpha
                        0,   // red
                        255, // green
                        SafeColorValue(1 - t) // blue (255 -> 0)
                    );
                }
                else if (normalized < 0.75f)
                {
                    // Green to yellow
                    float t = (normalized - 0.5f) / 0.25f;
                    return Color.FromArgb(
                        255, // alpha
                        SafeColorValue(t), // red (0 -> 255)
                        255, // green
                        0    // blue
                    );
                }
                else
                {
                    // Yellow to red
                    float t = (normalized - 0.75f) / 0.25f;
                    return Color.FromArgb(
                        255, // alpha
                        255, // red
                        SafeColorValue(1 - t), // green (255 -> 0)
                        0    // blue
                    );
                }
            }
            catch (Exception ex)
            {
                // If any error occurs, return a safe default color (medium gray)
                Logger.Log($"[InhomogeneousTriaxialSimulation] Error in GetHeatMapColor: {ex.Message}");
                return Color.DarkGray;
            }
        }

        #endregion
    }
}