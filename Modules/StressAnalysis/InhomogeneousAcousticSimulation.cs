using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Numerics;
using System.Threading.Tasks;

namespace CTSegmenter
{
    /// <summary>
    /// Extended implementation of acoustic velocity simulation that accounts for inhomogeneous density
    /// </summary>
    public class InhomogeneousAcousticSimulation : AcousticVelocitySimulation, IDensityVisualizable
    {
        // Additional properties for inhomogeneous density
        private readonly bool _useInhomogeneousDensity;

        private readonly ConcurrentDictionary<Vector3, float> _densityMap;
        public float[,,] _detailedDensityModel;
        private bool _hasInitializedInhomogeneousModels = false;

        // Statistics for density variation
        public float MinimumDensity { get; private set; }

        public float MaximumDensity { get; private set; }
        public float AverageDensity { get; private set; }

        public Dictionary<Triangle, float> TriangleDensities
        {
            get
            {
                Dictionary<Triangle, float> result = new Dictionary<Triangle, float>();

                if (_densityMap != null && MeshTriangles != null)
                {
                    Logger.Log($"[InhomogeneousAcousticSimulation] Building triangle densities from {_densityMap.Count} density points");

                    // Convert all triangles with their corresponding densities
                    foreach (var tri in MeshTriangles)
                    {
                        // Calculate triangle center
                        Vector3 center = new Vector3(
                            (tri.V1.X + tri.V2.X + tri.V3.X) / 3,
                            (tri.V1.Y + tri.V2.Y + tri.V3.Y) / 3,
                            (tri.V1.Z + tri.V2.Z + tri.V3.Z) / 3
                        );

                        // Find density for this triangle from our map
                        float density = FindNearestDensity(center);

                        // If no specific density found, use material density
                        if (density <= 0)
                            density = (float)Material.Density;

                        result[tri] = density;
                    }
                }
                else
                {
                    // If no density map available, use material density for all triangles
                    foreach (var tri in MeshTriangles)
                    {
                        result[tri] = (float)Material.Density;
                    }
                }

                return result;
            }
        }

        /// <summary>
        /// Constructor for inhomogeneous density simulation
        /// </summary>
        public InhomogeneousAcousticSimulation(
            Material material,
            List<Triangle> triangles,
            float confiningPressure,
            string waveType,
            int timeSteps,
            float frequency,
            float amplitude,
            float energy,
            string direction,
            bool useExtendedSimulationTime,
            bool useInhomogeneousDensity,
            ConcurrentDictionary<Vector3, float> densityMap,
            SimulationResult previousTriaxialResult = null,
            MainForm mainForm = null)
            : base(material, triangles, confiningPressure, waveType, timeSteps, frequency,
                  amplitude, energy, direction, useExtendedSimulationTime, previousTriaxialResult, mainForm)
        {
            _useInhomogeneousDensity = useInhomogeneousDensity;
            _densityMap = densityMap;

            // Log initialization
            Logger.Log($"[InhomogeneousAcousticSimulation] Initialized with inhomogeneous density " +
                      $"enabled: {_useInhomogeneousDensity}, density map size: {_densityMap?.Count ?? 0}");
        }

        /// <summary>
        /// Override the base Initialize method to include density variation initialization
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
                Logger.Log($"[InhomogeneousAcousticSimulation] Initialization failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Create a detailed density model from the density map
        /// </summary>
        private void InitializeInhomogeneousDensityModel()
        {
            Logger.Log("[InhomogeneousAcousticSimulation] Initializing inhomogeneous density model");

            // Create a new detailed density model with the same dimensions as the base velocity model
            _detailedDensityModel = new float[_gridSizeX, _gridSizeY, _gridSizeZ];

            // Default to material density
            float baseDensity = (float)Material.Density;

            // Fill with the base material density first
            for (int x = 0; x < _gridSizeX; x++)
            {
                for (int y = 0; y < _gridSizeY; y++)
                {
                    for (int z = 0; z < _gridSizeZ; z++)
                    {
                        _detailedDensityModel[x, y, z] = baseDensity;
                    }
                }
            }

            // Apply the density map to the model using nearest-point mapping
            if (_densityMap != null && _densityMap.Count > 0)
            {
                // Track density statistics
                float minDensity = float.MaxValue;
                float maxDensity = float.MinValue;
                float sumDensity = 0;
                int densityPointCount = 0;

                // Find the bounds of the mesh for normalization
                Vector3 minBounds = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 maxBounds = new Vector3(float.MinValue, float.MinValue, float.MinValue);

                foreach (var key in _densityMap.Keys)
                {
                    minBounds.X = Math.Min(minBounds.X, key.X);
                    minBounds.Y = Math.Min(minBounds.Y, key.Y);
                    minBounds.Z = Math.Min(minBounds.Z, key.Z);

                    maxBounds.X = Math.Max(maxBounds.X, key.X);
                    maxBounds.Y = Math.Max(maxBounds.Y, key.Y);
                    maxBounds.Z = Math.Max(maxBounds.Z, key.Z);
                }

                // Use parallel processing for better performance
                Parallel.For(0, _gridSizeX, x =>
                {
                    for (int y = 0; y < _gridSizeY; y++)
                    {
                        for (int z = 0; z < _gridSizeZ; z++)
                        {
                            // Map grid coordinates to mesh coordinates
                            Vector3 gridPos = MapGridToMeshCoordinates(x, y, z, minBounds, maxBounds);

                            // Find the nearest density point in the map
                            float density = FindNearestDensity(gridPos);

                            if (density > 0)
                            {
                                _detailedDensityModel[x, y, z] = density;

                                // Update statistics (using lock to avoid race conditions)
                                lock (this)
                                {
                                    minDensity = Math.Min(minDensity, density);
                                    maxDensity = Math.Max(maxDensity, density);
                                    sumDensity += density;
                                    densityPointCount++;
                                }
                            }
                        }
                    }
                });

                // Store density statistics
                MinimumDensity = minDensity;
                MaximumDensity = maxDensity;
                AverageDensity = densityPointCount > 0 ? sumDensity / densityPointCount : baseDensity;

                Logger.Log($"[InhomogeneousAcousticSimulation] Density statistics: Min={MinimumDensity:F1}, " +
                          $"Max={MaximumDensity:F1}, Avg={AverageDensity:F1}, Points={densityPointCount}");

                // Now update the base density model used by the simulation with our detailed model
                UpdateBaseDensityModel();
            }
        }

        /// <summary>
        /// Map grid coordinates to mesh coordinates
        /// </summary>
        private Vector3 MapGridToMeshCoordinates(int x, int y, int z, Vector3 minBounds, Vector3 maxBounds)
        {
            // Simple linear mapping from grid space to mesh space
            float normalizedX = (float)x / _gridSizeX;
            float normalizedY = (float)y / _gridSizeY;
            float normalizedZ = (float)z / _gridSizeZ;

            return new Vector3(
                minBounds.X + normalizedX * (maxBounds.X - minBounds.X),
                minBounds.Y + normalizedY * (maxBounds.Y - minBounds.Y),
                minBounds.Z + normalizedZ * (maxBounds.Z - minBounds.Z)
            );
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

            // Find the nearest neighbor (simple approach)
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

        /// <summary>
        /// Update the base density model with our detailed model
        /// </summary>
        private void UpdateBaseDensityModel()
        {
            if (_densityModel == null || _detailedDensityModel == null)
                return;

            Logger.Log("[InhomogeneousAcousticSimulation] Updating base density model with inhomogeneous values");

            // Copy our detailed density model to the base model
            for (int x = 0; x < _gridSizeX; x++)
            {
                for (int y = 0; y < _gridSizeY; y++)
                {
                    for (int z = 0; z < _gridSizeZ; z++)
                    {
                        _densityModel[x, y, z] = _detailedDensityModel[x, y, z];
                    }
                }
            }

            // Now update the velocity model based on the new density values
            UpdateVelocityModelWithDensity();
        }

        /// <summary>
        /// Update the velocity model based on density variations
        /// </summary>
        private void UpdateVelocityModelWithDensity()
        {
            if (_velocityModel == null || _densityModel == null)
                return;

            Logger.Log("[InhomogeneousAcousticSimulation] Updating velocity model based on density variations");

            // Get base velocity values
            float basePWaveVelocity = PWaveVelocity;
            float baseSWaveVelocity = SWaveVelocity;
            float baseDensity = (float)Material.Density;

            // Get direction components for directional scaling
            Vector3 dir = Vector3.Normalize(TestDirection);

            // Update velocities based on local density and direction
            Parallel.For(0, _gridSizeX, x =>
            {
                for (int y = 0; y < _gridSizeY; y++)
                {
                    for (int z = 0; z < _gridSizeZ; z++)
                    {
                        float density = _densityModel[x, y, z];
                        if (density > 0)
                        {
                            // Physical relationship for velocity based on density: v ~ 1/√ρ
                            // For elastic waves in solids, higher density generally means lower velocity
                            float densityRatio = (float)Math.Sqrt(baseDensity / density);

                            // Apply directional scaling - more pronounced effect along test direction
                            float dirX = Math.Abs(dir.X);
                            float dirY = Math.Abs(dir.Y);
                            float dirZ = Math.Abs(dir.Z);

                            // Calculate position-dependent influence based on coordinates and test direction
                            float positionFactor = 1.0f;

                            // Create directional density influence
                            if (dirX > 0.8f) // X is dominant direction
                            {
                                positionFactor = 1.0f + 0.2f * (float)Math.Sin(x * 0.2f);
                            }
                            else if (dirY > 0.8f) // Y is dominant direction
                            {
                                positionFactor = 1.0f + 0.2f * (float)Math.Sin(y * 0.2f);
                            }
                            else if (dirZ > 0.8f) // Z is dominant direction
                            {
                                positionFactor = 1.0f + 0.2f * (float)Math.Sin(z * 0.2f);
                            }
                            else // Mixed direction
                            {
                                positionFactor = 1.0f + 0.1f * (
                                    dirX * (float)Math.Sin(x * 0.2f) +
                                    dirY * (float)Math.Sin(y * 0.2f) +
                                    dirZ * (float)Math.Sin(z * 0.2f)
                                );
                            }

                            // Apply final scaling with enhanced physical effect
                            float finalRatio = densityRatio * positionFactor;

                            // Scale velocity by appropriate ratio for P or S waves
                            if (_isPWave)
                            {
                                _velocityModel[x, y, z] = basePWaveVelocity * finalRatio;
                            }
                            else
                            {
                                _velocityModel[x, y, z] = baseSWaveVelocity * finalRatio;
                            }
                        }
                    }
                }
            });

            // Log the updated velocity range
            float minVel = float.MaxValue;
            float maxVel = float.MinValue;

            for (int x = 0; x < _gridSizeX; x++)
            {
                for (int y = 0; y < _gridSizeY; y++)
                {
                    for (int z = 0; z < _gridSizeZ; z++)
                    {
                        float vel = _velocityModel[x, y, z];
                        minVel = Math.Min(minVel, vel);
                        maxVel = Math.Max(maxVel, vel);
                    }
                }
            }

            Logger.Log($"[InhomogeneousAcousticSimulation] Updated velocity range: {minVel:F1} to {maxVel:F1} m/s");

            // Ensure source-receiver path reflects density variations appropriately
            EnhanceSourceReceiverPath();
        }

        /// <summary>
        /// Ensures that the source-receiver path and source plane properly reflect density variations
        /// </summary>
        private void EnhanceSourceReceiverPath()
        {
            // Ensure the source and receiver have appropriate velocity values
            _velocityModel[_sourceX, _sourceY, _sourceZ] = (_isPWave ? PWaveVelocity : SWaveVelocity) * 1.2f;
            _velocityModel[_receiverX, _receiverY, _receiverZ] = (_isPWave ? PWaveVelocity : SWaveVelocity) * 1.2f;

            // Create source plane transducers with density-appropriate velocities
            List<int[]> sourcePlane = GenerateSourcePlane(TestDirection, _gridSizeX, _gridSizeY, _gridSizeZ);

            // Base velocity
            float baseVelocity = _isPWave ? PWaveVelocity : SWaveVelocity;

            // Ensure each source point has appropriate velocity
            foreach (var point in sourcePlane)
            {
                int x = point[0];
                int y = point[1];
                int z = point[2];

                if (x >= 0 && x < _gridSizeX && y >= 0 && y < _gridSizeY && z >= 0 && z < _gridSizeZ)
                {
                    // Get density at this point
                    float density = _densityModel[x, y, z];

                    // Calculate velocity with density consideration
                    float densityFactor = density > 0 ?
                        (float)Math.Sqrt((float)Material.Density / density) : 1.0f;

                    // Apply to source point with boost for better propagation
                    _velocityModel[x, y, z] = baseVelocity * densityFactor * 1.1f;
                }
            }

            // Enhanced path between source and receiver
            int pathSteps = (int)Math.Ceiling(Math.Sqrt(
                Math.Pow(_receiverX - _sourceX, 2) +
                Math.Pow(_receiverY - _sourceY, 2) +
                Math.Pow(_receiverZ - _sourceZ, 2)));

            Logger.Log($"[InhomogeneousAcousticSimulation] Enhancing path between source and receiver with {pathSteps} steps");

            for (int i = 0; i <= pathSteps; i++)
            {
                float t = i / (float)pathSteps;
                int x = (int)Math.Round(_sourceX + (_receiverX - _sourceX) * t);
                int y = (int)Math.Round(_sourceY + (_receiverY - _sourceY) * t);
                int z = (int)Math.Round(_sourceZ + (_receiverZ - _sourceZ) * t);

                if (x >= 0 && x < _gridSizeX && y >= 0 && y < _gridSizeY && z >= 0 && z < _gridSizeZ)
                {
                    // Get density at this point
                    float density = _densityModel[x, y, z];

                    // Calculate density factor
                    float densityFactor = density > 0 ?
                        (float)Math.Sqrt((float)Material.Density / density) : 1.0f;

                    // Enhance path based on position, with highest enhancement in middle
                    float positionBoost = 1.0f + 0.2f * (1.0f - Math.Abs(t - 0.5f) * 2.0f);

                    // Apply both density and position factors
                    _velocityModel[x, y, z] = baseVelocity * densityFactor * positionBoost;
                }
            }
        }
        /// <summary>
        /// Override RenderResults to add density visualization options
        /// </summary>
        public override void RenderResults(Graphics g, int width, int height, RenderMode renderMode = RenderMode.Stress)
        {
            // First call the base class rendering
            base.RenderResults(g, width, height, renderMode);

            // Now add density information ONLY if this specific rendering mode doesn't already add it
            if (_useInhomogeneousDensity && _densityMap != null && _densityMap.Count > 0)
            {
                // Determine position based on rendering mode to avoid overlaps
                int x, y;

                switch (renderMode)
                {
                    case RenderMode.Stress: // wave field - top-right corner
                        x = width - 220;
                        y = 10;
                        break;

                    case RenderMode.Strain: // time series - top-right corner
                        x = width - 220;
                        y = 10;
                        break;

                    case RenderMode.FailureProbability: // velocity distribution - top-right corner
                        x = width - 220;
                        y = 10;
                        break;

                    case RenderMode.Displacement: // wave slices - fixed position in the data area
                        x = 20;
                        y = 40;
                        break;

                    default:
                        x = 20;
                        y = 20;
                        break;
                }

                using (Font font = new Font("Arial", 10, FontStyle.Bold))
                using (SolidBrush backBrush = new SolidBrush(Color.FromArgb(180, 0, 0, 0))) // Darker background for better contrast
                using (SolidBrush textBrush = new SolidBrush(Color.Yellow))
                {
                    string message = $"Inhomogeneous Density: {_densityMap.Count} points";
                    SizeF textSize = g.MeasureString(message, font);

                    // Draw background rectangle with padding
                    g.FillRectangle(backBrush, x - 5, y - 2, textSize.Width + 10, textSize.Height + 4);
                    g.DrawString(message, font, textBrush, x, y);

                    // Add density range on next line with proper spacing
                    if (MinimumDensity < MaximumDensity)
                    {
                        string rangeText = $"Density: {AverageDensity:F1} kg/m³";
                        SizeF rangeSize = g.MeasureString(rangeText, font);

                        // Draw on next line with proper spacing
                        g.FillRectangle(backBrush, x - 5, y + textSize.Height + 3, rangeSize.Width + 10, rangeSize.Height + 4);
                        g.DrawString(rangeText, font, textBrush, x, y + textSize.Height + 5);
                    }
                }
            }
        }

        /// <summary>
        /// Render a visualization of the density distribution
        /// </summary>
        public void RenderDensityDistribution(Graphics g, int width, int height)
        {
            g.Clear(Color.Black);

            if (!_useInhomogeneousDensity || _densityMap == null || _densityMap.Count == 0 || _detailedDensityModel == null)
            {
                using (Font font = new Font("Arial", 12))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    g.DrawString("Inhomogeneous density is not enabled or no density data available", font, brush, 20, 20);
                }
                return;
            }

            // Set up plot area
            int margin = 50;
            int plotWidth = width - 2 * margin;
            int plotHeight = height - 2 * margin;

            // Create 2D slices of the density model
            int sliceHeight = (plotHeight - 2 * margin) / 3;

            // Draw X-Z slice (top)
            DrawDensityModelSlice(g, "Y", margin, margin, plotWidth, sliceHeight, MinimumDensity, MaximumDensity);

            // Draw Y-Z slice (middle)
            DrawDensityModelSlice(g, "X", margin, margin + sliceHeight + margin / 2, plotWidth, sliceHeight, MinimumDensity, MaximumDensity);

            // Draw histogram (bottom)
            DrawDensityHistogram(g, margin, margin + 2 * (sliceHeight + margin / 2), plotWidth, sliceHeight, MinimumDensity, MaximumDensity);

            // Draw title
            using (Font titleFont = new Font("Arial", 14, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                string title = "Inhomogeneous Density Distribution";
                g.DrawString(title, titleFont, textBrush, (width - g.MeasureString(title, titleFont).Width) / 2, 10);
            }

            // Draw color scale
            DrawColorScale(g, width - margin, margin + plotHeight / 2, 20, plotHeight / 2, "Density (kg/m³)");
        }


        /// <summary>
        /// Draw a 2D slice of the density model with wave propagation visualization
        /// </summary>
        private void DrawDensityModelSlice(Graphics g, string sliceAxis, int x, int y, int width, int height,
                                          float minDensity, float maxDensity)
        {
            if (width <= 0 || height <= 0 || width > 10000 || height > 10000)
            {
                using (Font font = new Font("Arial", 10))
                using (SolidBrush brush = new SolidBrush(Color.Red))
                {
                    g.DrawString("Invalid slice dimensions", font, brush, x, y);
                }
                return;
            }

            if (_detailedDensityModel == null)
            {
                using (Font font = new Font("Arial", 10))
                using (SolidBrush brush = new SolidBrush(Color.Red))
                {
                    g.DrawString("No density model available", font, brush, x, y);
                }
                return;
            }

            // Get slice index (middle of the model, or at source/receiver if close)
            int sliceIndex;
            string title;

            if (sliceAxis == "X")
            {
                sliceIndex = _gridSizeX / 2;
                // Check if source or receiver are near this slice
                if (Math.Abs(_sourceX - sliceIndex) < _gridSizeX / 4)
                    sliceIndex = _sourceX;
                else if (Math.Abs(_receiverX - sliceIndex) < _gridSizeX / 4)
                    sliceIndex = _receiverX;
                title = "Y-Z Slice (X=" + sliceIndex + ")";
            }
            else if (sliceAxis == "Y")
            {
                sliceIndex = _gridSizeY / 2;
                if (Math.Abs(_sourceY - sliceIndex) < _gridSizeY / 4)
                    sliceIndex = _sourceY;
                else if (Math.Abs(_receiverY - sliceIndex) < _gridSizeY / 4)
                    sliceIndex = _receiverY;
                title = "X-Z Slice (Y=" + sliceIndex + ")";
            }
            else // Z
            {
                sliceIndex = _gridSizeZ / 2;
                if (Math.Abs(_sourceZ - sliceIndex) < _gridSizeZ / 4)
                    sliceIndex = _sourceZ;
                else if (Math.Abs(_receiverZ - sliceIndex) < _gridSizeZ / 4)
                    sliceIndex = _receiverZ;
                title = "X-Y Slice (Z=" + sliceIndex + ")";
            }

            try
            {
                // Create bitmap for the slice
                using (Bitmap slice = new Bitmap(width, height))
                {
                    // Get dimensions for the slice
                    int dim1, dim2;
                    if (sliceAxis == "X")
                    {
                        dim1 = _gridSizeY;
                        dim2 = _gridSizeZ;
                    }
                    else if (sliceAxis == "Y")
                    {
                        dim1 = _gridSizeX;
                        dim2 = _gridSizeZ;
                    }
                    else // Z
                    {
                        dim1 = _gridSizeX;
                        dim2 = _gridSizeY;
                    }

                    // Scale factors to fit the slice in the drawing area
                    float scaleX = width / (float)dim1;
                    float scaleY = height / (float)dim2;

                    // Draw each pixel of the slice
                    using (Graphics sliceG = Graphics.FromImage(slice))
                    {
                        sliceG.Clear(Color.Black);

                        // Draw density variations first
                        for (int i = 0; i < dim1; i++)
                        {
                            for (int j = 0; j < dim2; j++)
                            {
                                // Get the density at this position
                                float density;
                                if (sliceAxis == "X")
                                {
                                    density = _detailedDensityModel[sliceIndex, i, j];
                                }
                                else if (sliceAxis == "Y")
                                {
                                    density = _detailedDensityModel[i, sliceIndex, j];
                                }
                                else // Z
                                {
                                    density = _detailedDensityModel[i, j, sliceIndex];
                                }

                                // Skip very low densities
                                if (density < 100)
                                    continue;

                                // Normalize and get color
                                float normalizedValue = (density - minDensity) / (maxDensity - minDensity);
                                normalizedValue = Math.Max(0, Math.Min(1, normalizedValue));
                                Color color = GetHeatMapColor(normalizedValue, 0, 1);

                                // Calculate pixel position
                                int pixelX = (int)(i * scaleX);
                                int pixelY = (int)(j * scaleY);
                                int pixelWidth = Math.Max(1, (int)scaleX + 1);
                                int pixelHeight = Math.Max(1, (int)scaleY + 1);

                                // Draw density rectangle
                                using (SolidBrush brush = new SolidBrush(color))
                                {
                                    sliceG.FillRectangle(brush, pixelX, pixelY, pixelWidth, pixelHeight);
                                }
                            }
                        }

                        // Also overlay wave field information if available
                        float[,,] waveField = _isPWave ? PWaveField : SWaveField;
                        if (waveField != null)
                        {
                            // Find max amplitude for normalization
                            float maxAmplitude = 0.000001f;

                            if (sliceAxis == "X" && sliceIndex < waveField.GetLength(0))
                            {
                                for (int yi = 0; yi < Math.Min(dim1, waveField.GetLength(1)); yi++)
                                    for (int zi = 0; zi < Math.Min(dim2, waveField.GetLength(2)); zi++)
                                        maxAmplitude = Math.Max(maxAmplitude, Math.Abs(waveField[sliceIndex, yi, zi]));
                            }
                            else if (sliceAxis == "Y" && sliceIndex < waveField.GetLength(1))
                            {
                                for (int xi = 0; xi < Math.Min(dim1, waveField.GetLength(0)); xi++)
                                    for (int zi = 0; zi < Math.Min(dim2, waveField.GetLength(2)); zi++)
                                        maxAmplitude = Math.Max(maxAmplitude, Math.Abs(waveField[xi, sliceIndex, zi]));
                            }
                            else if (sliceAxis == "Z" && sliceIndex < waveField.GetLength(2))
                            {
                                for (int xi = 0; xi < Math.Min(dim1, waveField.GetLength(0)); xi++)
                                    for (int yi = 0; yi < Math.Min(dim2, waveField.GetLength(1)); yi++)
                                        maxAmplitude = Math.Max(maxAmplitude, Math.Abs(waveField[xi, yi, sliceIndex]));
                            }

                            // Draw wave contours
                            for (int i = 0; i < dim1; i++)
                            {
                                for (int j = 0; j < dim2; j++)
                                {
                                    float waveValue = 0;
                                    bool hasWaveValue = false;

                                    // Get wave value at this position
                                    if (sliceAxis == "X" && sliceIndex < waveField.GetLength(0) &&
                                        i < waveField.GetLength(1) && j < waveField.GetLength(2))
                                    {
                                        waveValue = waveField[sliceIndex, i, j];
                                        hasWaveValue = true;
                                    }
                                    else if (sliceAxis == "Y" && i < waveField.GetLength(0) &&
                                            sliceIndex < waveField.GetLength(1) && j < waveField.GetLength(2))
                                    {
                                        waveValue = waveField[i, sliceIndex, j];
                                        hasWaveValue = true;
                                    }
                                    else if (sliceAxis == "Z" && i < waveField.GetLength(0) &&
                                            j < waveField.GetLength(1) && sliceIndex < waveField.GetLength(2))
                                    {
                                        waveValue = waveField[i, j, sliceIndex];
                                        hasWaveValue = true;
                                    }

                                    if (hasWaveValue && Math.Abs(waveValue) > maxAmplitude * 0.3f)
                                    {
                                        // Calculate pixel position
                                        int pixelX = (int)(i * scaleX);
                                        int pixelY = (int)(j * scaleY);

                                        // Draw wave highlight - more visible for stronger waves
                                        float intensity = Math.Min(1f, Math.Abs(waveValue) / maxAmplitude);

                                        // Draw contour as a small circle
                                        int dotSize = 1 + (int)(3 * intensity);
                                        using (SolidBrush waveBrush = new SolidBrush(Color.FromArgb(
                                            (int)(180 * intensity), Color.White)))
                                        {
                                            sliceG.FillEllipse(waveBrush, pixelX - dotSize / 2, pixelY - dotSize / 2,
                                                             dotSize, dotSize);
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Draw the slice
                    g.DrawImage(slice, x, y, width, height);

                    // Draw a border
                    using (Pen borderPen = new Pen(Color.Gray, 1))
                    {
                        g.DrawRectangle(borderPen, x, y, width, height);
                    }

                    // Draw title
                    using (Font font = new Font("Arial", 10))
                    using (SolidBrush textBrush = new SolidBrush(Color.White))
                    {
                        g.DrawString(title, font, textBrush, x + width / 2 - 60, y - 15);
                    }

                    // Draw source and receiver positions if they are in this slice
                    PointF sourcePoint = new PointF();
                    PointF receiverPoint = new PointF();
                    bool sourceVisible = false;
                    bool receiverVisible = false;

                    // Calculate positions in visualization
                    if (sliceAxis == "X" && sliceIndex == _sourceX)
                    {
                        sourcePoint.X = x + (_sourceY / (float)dim1) * width;
                        sourcePoint.Y = y + (_sourceZ / (float)dim2) * height;
                        sourceVisible = true;

                        using (Brush sourceBrush = new SolidBrush(Color.Yellow))
                        {
                            g.FillEllipse(sourceBrush, sourcePoint.X - 4, sourcePoint.Y - 4, 8, 8);
                        }
                    }

                    if (sliceAxis == "X" && sliceIndex == _receiverX)
                    {
                        receiverPoint.X = x + (_receiverY / (float)dim1) * width;
                        receiverPoint.Y = y + (_receiverZ / (float)dim2) * height;
                        receiverVisible = true;

                        using (Brush receiverBrush = new SolidBrush(Color.Cyan))
                        {
                            g.FillEllipse(receiverBrush, receiverPoint.X - 4, receiverPoint.Y - 4, 8, 8);
                        }
                    }

                    if (sliceAxis == "Y" && sliceIndex == _sourceY)
                    {
                        sourcePoint.X = x + (_sourceX / (float)dim1) * width;
                        sourcePoint.Y = y + (_sourceZ / (float)dim2) * height;
                        sourceVisible = true;

                        using (Brush sourceBrush = new SolidBrush(Color.Yellow))
                        {
                            g.FillEllipse(sourceBrush, sourcePoint.X - 4, sourcePoint.Y - 4, 8, 8);
                        }
                    }

                    if (sliceAxis == "Y" && sliceIndex == _receiverY)
                    {
                        receiverPoint.X = x + (_receiverX / (float)dim1) * width;
                        receiverPoint.Y = y + (_receiverZ / (float)dim2) * height;
                        receiverVisible = true;

                        using (Brush receiverBrush = new SolidBrush(Color.Cyan))
                        {
                            g.FillEllipse(receiverBrush, receiverPoint.X - 4, receiverPoint.Y - 4, 8, 8);
                        }
                    }

                    if (sliceAxis == "Z" && sliceIndex == _sourceZ)
                    {
                        sourcePoint.X = x + (_sourceX / (float)dim1) * width;
                        sourcePoint.Y = y + (_sourceY / (float)dim2) * height;
                        sourceVisible = true;

                        using (Brush sourceBrush = new SolidBrush(Color.Yellow))
                        {
                            g.FillEllipse(sourceBrush, sourcePoint.X - 4, sourcePoint.Y - 4, 8, 8);
                        }
                    }

                    if (sliceAxis == "Z" && sliceIndex == _receiverZ)
                    {
                        receiverPoint.X = x + (_receiverX / (float)dim1) * width;
                        receiverPoint.Y = y + (_receiverY / (float)dim2) * height;
                        receiverVisible = true;

                        using (Brush receiverBrush = new SolidBrush(Color.Cyan))
                        {
                            g.FillEllipse(receiverBrush, receiverPoint.X - 4, receiverPoint.Y - 4, 8, 8);
                        }
                    }

                    // Draw the direction line if both source and receiver are on this slice
                    if (sourceVisible && receiverVisible)
                    {
                        using (Pen dirPen = new Pen(Color.Yellow, 1))
                        {
                            dirPen.DashStyle = DashStyle.Dash;
                            g.DrawLine(dirPen, sourcePoint, receiverPoint);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Handle any exceptions during rendering
                using (Font font = new Font("Arial", 10))
                using (SolidBrush brush = new SolidBrush(Color.Red))
                {
                    g.DrawString($"Error rendering slice: {ex.Message}", font, brush, x, y);
                }
                Logger.Log($"[InhomogeneousAcousticSimulation] Error rendering density slice: {ex.Message}");
            }
        }

        /// <summary>
        /// Draw a histogram of the density distribution
        /// </summary>
        private void DrawDensityHistogram(Graphics g, int x, int y, int width, int height, float minDensity, float maxDensity)
        {
            try
            {
                if (_detailedDensityModel == null)
                {
                    using (Font font = new Font("Arial", 10))
                    using (SolidBrush brush = new SolidBrush(Color.Red))
                    {
                        g.DrawString("No density model available", font, brush, x, y);
                    }
                    return;
                }

                // Number of bins for the histogram
                int numBins = 20;

                // Calculate bin width
                float binWidth = (maxDensity - minDensity) / numBins;
                if (binWidth <= 0) binWidth = 1.0f;  // Fallback if min=max

                // Count densities in each bin
                int[] bins = new int[numBins];
                int totalCells = 0;

                for (int gx = 0; gx < _gridSizeX; gx++)
                {
                    for (int gy = 0; gy < _gridSizeY; gy++)
                    {
                        for (int gz = 0; gz < _gridSizeZ; gz++)
                        {
                            float density = _detailedDensityModel[gx, gy, gz];
                            if (density > 100) // Ignore very low density
                            {
                                int binIndex = (int)((density - minDensity) / binWidth);
                                binIndex = Math.Max(0, Math.Min(binIndex, numBins - 1));
                                bins[binIndex]++;
                                totalCells++;
                            }
                        }
                    }
                }

                // Find the maximum bin count - default to 1 if all bins are empty
                int maxCount = 1;
                foreach (int count in bins)
                {
                    if (count > maxCount) maxCount = count;
                }

                // If no data was found, we should indicate this to the user
                if (totalCells == 0)
                {
                    using (Font font = new Font("Arial", 12))
                    using (SolidBrush brush = new SolidBrush(Color.Yellow))
                    {
                        g.DrawString("No density data available", font, brush, x + width / 2 - 70, y + height / 2 - 10);
                    }
                    return;
                }

                // Scale factor for bin height
                float scale = height * 0.9f / maxCount;  // Leave some margin at the top

                // Clear the area first
                using (SolidBrush bgBrush = new SolidBrush(Color.Black))
                {
                    g.FillRectangle(bgBrush, x, y, width, height);
                }

                // Draw histogram bars
                float barWidth = (float)width / numBins;

                for (int i = 0; i < numBins; i++)
                {
                    // Calculate bar position and size
                    float barX = x + i * barWidth;
                    float barHeight = bins[i] * scale;
                    float barY = y + height - barHeight;

                    // Get color based on bin value (same as the colormap)
                    float normalizedValue = (i + 0.5f) / numBins;
                    Color color = GetHeatMapColor(normalizedValue, 0, 1);

                    // Ensure minimum height for visibility when count > 0
                    if (bins[i] > 0)
                    {
                        barHeight = Math.Max(barHeight, 2);
                    }

                    using (SolidBrush brush = new SolidBrush(color))
                    {
                        g.FillRectangle(brush, barX, barY, barWidth, barHeight);
                    }

                    // Draw outline
                    using (Pen pen = new Pen(Color.FromArgb(50, Color.White), 1))
                    {
                        g.DrawRectangle(pen, barX, barY, barWidth, barHeight);
                    }
                }

                // Draw axes
                using (Pen axisPen = new Pen(Color.White, 1))
                {
                    // X axis
                    g.DrawLine(axisPen, x, y + height, x + width, y + height);

                    // Y axis
                    g.DrawLine(axisPen, x, y, x, y + height);
                }

                // Draw tick marks and labels
                using (Pen axisPen = new Pen(Color.Gray, 1))
                using (Font font = new Font("Arial", 8))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    // X axis ticks
                    int numTicks = 5;
                    for (int i = 0; i <= numTicks; i++)
                    {
                        float tickX = x + i * width / numTicks;
                        float value = minDensity + i * (maxDensity - minDensity) / numTicks;

                        g.DrawLine(axisPen, tickX, y + height, tickX, y + height + 5);
                        g.DrawString($"{value:F0}", font, textBrush, tickX - 15, y + height + 5);
                    }

                    // Mark mean value
                    if (AverageDensity > 0)
                    {
                        float meanX = x + (AverageDensity - minDensity) / (maxDensity - minDensity) * width;
                        if (meanX >= x && meanX <= x + width)
                        {
                            using (Pen meanPen = new Pen(Color.Red, 1))
                            {
                                meanPen.DashStyle = DashStyle.Dash;
                                g.DrawLine(meanPen, meanX, y, meanX, y + height);
                            }

                            g.DrawString("Mean", font, new SolidBrush(Color.Red), meanX - 15, y - 15);
                        }
                    }
                }

                // Draw title
                using (Font titleFont = new Font("Arial", 10))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    string title = "Density Histogram";
                    g.DrawString(title, titleFont, textBrush, x + width / 2 - 50, y - 15);
                }
            }
            catch (Exception ex)
            {
                // Handle any exceptions during rendering
                using (Font font = new Font("Arial", 10))
                using (SolidBrush brush = new SolidBrush(Color.Red))
                {
                    g.DrawString($"Error rendering histogram: {ex.Message}", font, brush, x, y);
                }
                Logger.Log($"[InhomogeneousAcousticSimulation] Error rendering density histogram: {ex.Message}");
            }
        }

        /// <summary>
        /// Draw a color scale with title
        /// </summary>
        private void DrawColorScale(Graphics g, int x, int y, int width, int height, string label)
        {
            // Ensure minimum dimensions
            height = Math.Max(10, height);

            // Draw gradient from min to max
            using (LinearGradientBrush gradientBrush = new LinearGradientBrush(
                new Rectangle(x, y, width, height),
                Color.Blue, Color.Red, 90f))
            {
                ColorBlend colorBlend = new ColorBlend(5);
                colorBlend.Colors = new[] { Color.Blue, Color.Cyan, Color.Green, Color.Yellow, Color.Red };
                colorBlend.Positions = new[] { 0f, 0.25f, 0.5f, 0.75f, 1f };
                gradientBrush.InterpolationColors = colorBlend;

                g.FillRectangle(gradientBrush, x, y, width, height);
                g.DrawRectangle(new Pen(Color.Gray, 1), x, y, width, height);
            }

            // Draw labels
            using (Font font = new Font("Arial", 8))
            using (SolidBrush brush = new SolidBrush(Color.White))
            {
                g.DrawString(label, font, brush, x - 20, y - 15);

                // Min label at bottom
                g.DrawString($"{MinimumDensity:F0}", font, brush, x - 30, y + height - 8);

                // Max label at top
                g.DrawString($"{MaximumDensity:F0}", font, brush, x - 30, y);

                // Mean label in middle
                g.DrawString($"{AverageDensity:F0}", font, brush, x - 30, y + height / 2 - 4);
            }
        }

        /// <summary>
        /// Get a color from the heatmap based on normalized value
        /// </summary>
        private Color GetHeatMapColor(float value, float min, float max)
        {
            // Normalize value to 0-1 range with bounds checking
            float normalized = Math.Max(0, Math.Min(1, (value - min) / (max - min)));

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
    }
}