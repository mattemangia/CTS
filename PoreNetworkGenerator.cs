using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Drawing;
using System.Windows.Forms;
using System.Numerics;
using ILGPU;
using ILGPU.Runtime;
using System.IO;
using ILGPU.Runtime.CPU;
using SharpDX;

namespace CTSegmenter
{
    public class PoreNetworkGenerator:IDisposable
    {
        private Context context;
        private Accelerator accelerator;
        private bool gpuInitialized = false;
        private bool disposed = false;

        public PoreNetworkGenerator()
        {
            InitializeGPU();
        }

        private void InitializeGPU()
        {
            try
            {
                context = Context.Create(builder => builder.Default().EnableAlgorithms());

                // Try to use GPU first
                foreach (var device in context.Devices)
                {
                    try
                    {
                        if (device.AcceleratorType != AcceleratorType.CPU)
                        {
                            accelerator = device.CreateAccelerator(context);
                            Logger.Log($"[PoreNetworkGenerator] Using GPU accelerator: {device.Name}");
                            gpuInitialized = true;
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[PoreNetworkGenerator] Failed to initialize GPU device: {ex.Message}");
                    }
                }

                // Fall back to CPU if no GPU available
                var cpuDevice = context.GetCPUDevice(0);
                accelerator = cpuDevice.CreateAccelerator(context);
                Logger.Log("[PoreNetworkGenerator] Using CPU accelerator");
                gpuInitialized = true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[PoreNetworkGenerator] Failed to initialize GPU: {ex.Message}");
                gpuInitialized = false;
            }
        }

        public async Task<PoreNetworkModel> GenerateNetworkFromSeparationResult(
    ParticleSeparator.SeparationResult separationResult,
    double pixelSize,
    IProgress<int> progress,
    bool useGpu)
        {
            PoreNetworkModel model = new PoreNetworkModel();
            model.PixelSize = pixelSize;

            // Create a Random instance with a fixed seed for reproducible results
            Random random = new Random(42);

            // Convert particles to pores
            progress?.Report(10);
            await Task.Run(() =>
            {
                // Extract pores from particles with consistent calculation
                foreach (var particle in separationResult.Particles)
                {
                    double volume = particle.VoxelCount * Math.Pow(pixelSize, 3); // m³
                    double radius = Math.Pow(3 * volume / (4 * Math.PI), 1.0 / 3.0); // m

                    // Use the CPU method for surface area for consistency
                    double surfaceArea = CalculateSurfaceArea(particle, separationResult.LabelVolume,pixelSize);

                    Pore pore = new Pore
                    {
                        Id = particle.Id,
                        Volume = volume * 1e18, // Convert to µm³
                        Area = surfaceArea * 1e12, // Convert to µm²
                        Radius = radius * 1e6, // Convert to µm
                        Center = new Point3D
                        {
                            X = particle.Center.X * pixelSize * 1e6,
                            Y = particle.Center.Y * pixelSize * 1e6,
                            Z = particle.Center.Z * pixelSize * 1e6
                        }
                    };
                    model.Pores.Add(pore);
                }
            });

            // Generate throats with consistent algorithm regardless of GPU setting
            progress?.Report(50);
            await Task.Run(() =>
            {
                // IMPORTANT: Always use the same algorithm regardless of GPU flag
                // for consistent results
                int maxConnections = Math.Min(6, model.Pores.Count - 1);
                GenerateConsistentThroats(model, maxConnections, random); // Passing random parameter

                // Calculate network properties
                CalculateNetworkProperties(model);
            });

            progress?.Report(100);
            return model;
        }

        private void GenerateConsistentThroats(PoreNetworkModel model, int maxConnections, Random random)
        {
            model.Throats.Clear();

            // Skip if we have too few pores
            if (model.Pores.Count < 2)
                return;

            // Calculate all pore distances once and store them
            var distances = new Dictionary<(int, int), double>();

            for (int i = 0; i < model.Pores.Count; i++)
            {
                for (int j = i + 1; j < model.Pores.Count; j++)
                {
                    Pore pore1 = model.Pores[i];
                    Pore pore2 = model.Pores[j];
                    double distance = Distance(pore1.Center, pore2.Center);
                    distances[(pore1.Id, pore2.Id)] = distance;
                }
            }

            // For each pore, find closest neighbors
            foreach (var pore1 in model.Pores)
            {
                // Get all other pores with their distances
                var otherPores = model.Pores
                    .Where(p => p.Id != pore1.Id)
                    .Select(p => (
                        Pore: p,
                        Distance: distances.ContainsKey((Math.Min(pore1.Id, p.Id), Math.Max(pore1.Id, p.Id))) ?
                            distances[(Math.Min(pore1.Id, p.Id), Math.Max(pore1.Id, p.Id))] :
                            distances[(Math.Max(pore1.Id, p.Id), Math.Min(pore1.Id, p.Id))]
                    ))
                    .OrderBy(pair => pair.Distance)
                    .Take(maxConnections);

                foreach (var (pore2, distance) in otherPores)
                {
                    // Avoid duplicate throats (only add if pore1.Id < pore2.Id)
                    if (pore1.Id < pore2.Id)
                    {
                        // Calculate throat properties
                        double radius = (pore1.Radius + pore2.Radius) * 0.25; // 25% of average
                        double length = Math.Max(0.1, distance - pore1.Radius - pore2.Radius);
                        double volume = Math.PI * radius * radius * length;

                        Throat throat = new Throat
                        {
                            Id = model.Throats.Count + 1,
                            PoreId1 = pore1.Id,
                            PoreId2 = pore2.Id,
                            Radius = radius,
                            Length = length,
                            Volume = volume
                        };

                        model.Throats.Add(throat);

                        // Update connection counts
                        pore1.ConnectionCount++;
                        pore2.ConnectionCount++;
                    }
                }
            }
        }

        

        /// <summary>
        /// Calculates the surface area of a particle using GPU acceleration if available
        /// </summary>
        /// <param name="particle">The particle to calculate the surface area for</param>
        /// <param name="labelVolume">The 3D volume containing labeled particles</param>
        /// <param name="pixelSize">The size of each voxel in meters</param>
        /// <returns>Surface area in square meters</returns>
        private double CalculateSurfaceAreaGPU(ParticleSeparator.Particle particle, int[,,] labelVolume, double pixelSize)
        {
            // Fall back to CPU method if GPU isn't initialized
            if (!gpuInitialized)
            {
                Logger.Log("[PoreNetworkGenerator] GPU not initialized, using CPU for surface area calculation");
                return CalculateSurfaceArea(particle, labelVolume, pixelSize);
            }

            try
            {
                // Get dimensions of the volume
                int width = labelVolume.GetLength(0);
                int height = labelVolume.GetLength(1);
                int depth = labelVolume.GetLength(2);

                // Define bounding box for processing (only process the particle's region)
                int minX = Math.Max(0, particle.Bounds.MinX);
                int minY = Math.Max(0, particle.Bounds.MinY);
                int minZ = Math.Max(0, particle.Bounds.MinZ);
                int maxX = Math.Min(width - 1, particle.Bounds.MaxX);
                int maxY = Math.Min(height - 1, particle.Bounds.MaxY);
                int maxZ = Math.Min(depth - 1, particle.Bounds.MaxZ);

                // Calculate dimensions of the subvolume
                int boxWidth = maxX - minX + 1;
                int boxHeight = maxY - minY + 1;
                int boxDepth = maxZ - minZ + 1;

                // If the bounding box is too small, use the CPU method (avoids overhead)
                if (boxWidth * boxHeight * boxDepth < 1000)
                {
                    return CalculateSurfaceArea(particle, labelVolume, pixelSize);
                }

                // Extract the subvolume to a 1D array for GPU processing
                int[] flatSubvolume = new int[boxWidth * boxHeight * boxDepth];
                int idx = 0;

                // Copy data from 3D volume to 1D array
                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int y = minY; y <= maxY; y++)
                    {
                        for (int x = minX; x <= maxX; x++)
                        {
                            flatSubvolume[idx++] = labelVolume[x, y, z];
                        }
                    }
                }

                // Store bounding box offsets for kernel processing
                int[] boxOffsets = new int[] { minX, minY, minZ };

                // Create device arrays
                using (var deviceSubvolume = accelerator.Allocate1D(flatSubvolume))
                using (var deviceResult = accelerator.Allocate1D<int>(1))
                using (var deviceOffsets = accelerator.Allocate1D(boxOffsets))
                {
                    // Initialize result to zero with a kernel
                    var initKernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<int>>(
                        (index, result) => { result[index] = 0; });

                    initKernel(new Index1D(1), deviceResult.View);

                    // Define kernel to count boundary voxels
                    var countBoundaryKernel = accelerator.LoadAutoGroupedStreamKernel<
                        Index1D, ArrayView<int>, ArrayView<int>, ArrayView<int>, int, int, int, int>(
                        (index, volume, result, offsets, w, h, d, targetLabel) =>
                        {
                            // Skip if out of range
                            if (index >= volume.Length)
                                return;

                            // Convert flat index to 3D coordinates within the box
                            int x = index % w;
                            int y = (index / w) % h;
                            int z = index / (w * h);

                            // Get value at this position
                            int value = volume[index];

                            // Only process voxels that belong to our target particle
                            if (value == targetLabel)
                            {
                                bool isBoundary = false;

                                // Check 6-connected neighbors
                                // -X neighbor
                                if (x > 0 && volume[(z * h + y) * w + (x - 1)] != targetLabel)
                                    isBoundary = true;
                                // +X neighbor
                                else if (x < w - 1 && volume[(z * h + y) * w + (x + 1)] != targetLabel)
                                    isBoundary = true;
                                // -Y neighbor
                                else if (y > 0 && volume[(z * h + (y - 1)) * w + x] != targetLabel)
                                    isBoundary = true;
                                // +Y neighbor
                                else if (y < h - 1 && volume[(z * h + (y + 1)) * w + x] != targetLabel)
                                    isBoundary = true;
                                // -Z neighbor
                                else if (z > 0 && volume[((z - 1) * h + y) * w + x] != targetLabel)
                                    isBoundary = true;
                                // +Z neighbor
                                else if (z < d - 1 && volume[((z + 1) * h + y) * w + x] != targetLabel)
                                    isBoundary = true;

                                // If this is a boundary voxel, increment counter
                                if (isBoundary)
                                {
                                    Atomic.Add(ref result[0], 1);
                                }
                            }
                        });

                    // Execute kernel
                    countBoundaryKernel(flatSubvolume.Length, deviceSubvolume.View, deviceResult.View, deviceOffsets.View,
                        boxWidth, boxHeight, boxDepth, particle.Id);

                    // Get result
                    int[] resultArray = deviceResult.GetAsArray1D();
                    int boundaryVoxelCount = resultArray[0];

                    // Calculate surface area (average of 1.5 faces per boundary voxel)
                    double surfaceArea = boundaryVoxelCount * 1.5 * pixelSize * pixelSize;

                    Logger.Log($"[PoreNetworkGenerator] GPU calculated surface area: {boundaryVoxelCount} boundary voxels, {surfaceArea} m²");
                    return surfaceArea;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[PoreNetworkGenerator] GPU surface area calculation failed: {ex.Message}");
                Logger.Log($"[PoreNetworkGenerator] Stack trace: {ex.StackTrace}");

                // Fall back to CPU method
                return CalculateSurfaceArea(particle, labelVolume, pixelSize);
            }
        }

        /// <summary>
        /// CPU version of surface area calculation
        /// </summary>
        private double CalculateSurfaceArea(ParticleSeparator.Particle particle, int[,,] labelVolume, double pixelSize)
        {
            int width = labelVolume.GetLength(0);
            int height = labelVolume.GetLength(1);
            int depth = labelVolume.GetLength(2);

            int boundaryVoxelCount = 0;

            // Check the particle's bounding box only
            for (int z = Math.Max(0, particle.Bounds.MinZ); z <= Math.Min(depth - 1, particle.Bounds.MaxZ); z++)
            {
                for (int y = Math.Max(0, particle.Bounds.MinY); y <= Math.Min(height - 1, particle.Bounds.MaxY); y++)
                {
                    for (int x = Math.Max(0, particle.Bounds.MinX); x <= Math.Min(width - 1, particle.Bounds.MaxX); x++)
                    {
                        if (labelVolume[x, y, z] == particle.Id)
                        {
                            // Check 6-connected neighbors
                            bool isBoundary = false;

                            // Check X direction
                            if (x > 0 && labelVolume[x - 1, y, z] != particle.Id) isBoundary = true;
                            else if (x < width - 1 && labelVolume[x + 1, y, z] != particle.Id) isBoundary = true;

                            // Check Y direction
                            else if (y > 0 && labelVolume[x, y - 1, z] != particle.Id) isBoundary = true;
                            else if (y < height - 1 && labelVolume[x, y + 1, z] != particle.Id) isBoundary = true;

                            // Check Z direction
                            else if (z > 0 && labelVolume[x, y, z - 1] != particle.Id) isBoundary = true;
                            else if (z < depth - 1 && labelVolume[x, y, z + 1] != particle.Id) isBoundary = true;

                            if (isBoundary)
                                boundaryVoxelCount++;
                        }
                    }
                }
            }

            // Calculate surface area using the correct pixel size
            double surfaceArea = boundaryVoxelCount * 1.5 * pixelSize * pixelSize;
            Logger.Log($"[PoreNetworkGenerator] CPU calculated surface area: {boundaryVoxelCount} boundary voxels, {surfaceArea} m²");
            return surfaceArea;
        }



        private void GenerateThroatsCPU(PoreNetworkModel model)
        {
            model.Throats.Clear();

            // Skip if we have too few pores
            if (model.Pores.Count < 2)
                return;

            // For each pore, find closest neighbors to create throats
            int maxConnections = Math.Min(6, model.Pores.Count - 1);

            // Calculate distance between pores more efficiently
            Dictionary<(int, int), double> distanceCache = new Dictionary<(int, int), double>();

            for (int i = 0; i < model.Pores.Count; i++)
            {
                Pore pore1 = model.Pores[i];

                // Find closest pores using cached distances
                List<(Pore pore, double distance)> sortedPores = new List<(Pore, double)>();

                for (int j = 0; j < model.Pores.Count; j++)
                {
                    if (i == j) continue;

                    Pore pore2 = model.Pores[j];

                    // Use or calculate distance
                    double distance;
                    var key = (Math.Min(pore1.Id, pore2.Id), Math.Max(pore1.Id, pore2.Id));

                    if (!distanceCache.TryGetValue(key, out distance))
                    {
                        distance = Distance(pore1.Center, pore2.Center);
                        distanceCache[key] = distance;
                    }

                    sortedPores.Add((pore2, distance));
                }

                // Get closest pores
                var closestPores = sortedPores.OrderBy(p => p.distance).Take(maxConnections).Select(p => p.pore);

                foreach (var pore2 in closestPores)
                {
                    // Avoid duplicate throats (only add if pore1.Id < pore2.Id)
                    if (pore1.Id < pore2.Id)
                    {
                        // Calculate throat radius as weighted average of pore radii
                        double radius = (pore1.Radius + pore2.Radius) * 0.25; // 25% of average

                        // Calculate throat length
                        double distance = distanceCache[(Math.Min(pore1.Id, pore2.Id), Math.Max(pore1.Id, pore2.Id))];
                        double length = Math.Max(0.1, distance - pore1.Radius - pore2.Radius);

                        // Calculate throat volume (cylinder approximation)
                        double volume = Math.PI * radius * radius * length;

                        Throat throat = new Throat
                        {
                            Id = model.Throats.Count + 1,
                            PoreId1 = pore1.Id,
                            PoreId2 = pore2.Id,
                            Radius = radius,
                            Length = length,
                            Volume = volume
                        };

                        model.Throats.Add(throat);

                        // Update connection counts
                        pore1.ConnectionCount++;
                        pore2.ConnectionCount++;
                    }
                }
            }
        }

        private void GenerateThroatsGPU(PoreNetworkModel model)
        {
            if (!gpuInitialized)
            {
                Logger.Log("[PoreNetworkGenerator] GPU not initialized, falling back to CPU");
                GenerateThroatsCPU(model);
                return;
            }

            try
            {
                // Log detailed GPU info for debugging
                Logger.Log($"[PoreNetworkGenerator] Using GPU: {accelerator.Name}");
                Logger.Log($"[PoreNetworkGenerator] Processing {model.Pores.Count} pores");

                model.Throats.Clear();

                // Skip if we have too few pores
                if (model.Pores.Count < 2)
                    return;

                int poreCount = model.Pores.Count;
                int maxConnections = Math.Min(6, poreCount - 1);

                // Create arrays for distance calculation
                float[] positions = new float[poreCount * 3]; // x, y, z for each pore
                float[] radii = new float[poreCount];
                int[] ids = new int[poreCount];

                for (int i = 0; i < poreCount; i++)
                {
                    Pore pore = model.Pores[i];
                    positions[i * 3] = (float)pore.Center.X;
                    positions[i * 3 + 1] = (float)pore.Center.Y;
                    positions[i * 3 + 2] = (float)pore.Center.Z;
                    radii[i] = (float)pore.Radius;
                    ids[i] = pore.Id;
                }

                // Use simpler approach: calculate distances on CPU
                float[,] distances = new float[poreCount, poreCount];

                for (int i = 0; i < poreCount; i++)
                {
                    for (int j = 0; j < poreCount; j++)
                    {
                        if (i == j)
                            distances[i, j] = float.MaxValue;
                        else
                        {
                            float dx = positions[i * 3] - positions[j * 3];
                            float dy = positions[i * 3 + 1] - positions[j * 3 + 1];
                            float dz = positions[i * 3 + 2] - positions[j * 3 + 2];
                            distances[i, j] = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        }
                    }
                }

                // Find closest pores per each pore
                List<(int, int, float)> connections = new List<(int, int, float)>();
                HashSet<(int, int)> processedPairs = new HashSet<(int, int)>();

                for (int i = 0; i < poreCount; i++)
                {
                    // Create list of (index, distance) pairs
                    List<(int, float)> neighbors = new List<(int, float)>();
                    for (int j = 0; j < poreCount; j++)
                    {
                        if (i != j)
                            neighbors.Add((j, distances[i, j]));
                    }

                    // Sort by distance and take closest
                    var closest = neighbors.OrderBy(n => n.Item2).Take(maxConnections);

                    foreach (var (j, dist) in closest)
                    {
                        int id1 = Math.Min(ids[i], ids[j]);
                        int id2 = Math.Max(ids[i], ids[j]);
                        var pair = (id1, id2);

                        if (!processedPairs.Contains(pair))
                        {
                            connections.Add((i, j, dist));
                            processedPairs.Add(pair);
                        }
                    }
                }

                // Create throats from connections
                foreach (var (i, j, dist) in connections)
                {
                    Pore pore1 = model.Pores.First(p => p.Id == ids[i]);
                    Pore pore2 = model.Pores.First(p => p.Id == ids[j]);

                    // Calculate throat properties
                    double radius = (pore1.Radius + pore2.Radius) * 0.25;
                    double length = Math.Max(0.1, dist - pore1.Radius - pore2.Radius);
                    double volume = Math.PI * radius * radius * length;

                    // Create throat
                    Throat throat = new Throat
                    {
                        Id = model.Throats.Count + 1,
                        PoreId1 = pore1.Id,
                        PoreId2 = pore2.Id,
                        Radius = radius,
                        Length = length,
                        Volume = volume
                    };

                    model.Throats.Add(throat);

                    // Update connection counts
                    pore1.ConnectionCount++;
                    pore2.ConnectionCount++;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[PoreNetworkGenerator] GPU throat generation failed: {ex.Message}");
                Logger.Log($"[PoreNetworkGenerator] Stack trace: {ex.StackTrace}");

                // Fall back to CPU implementation
                GenerateThroatsCPU(model);
            }
        }

        // Helper method to process connections from GPU results
        private void ProcessConnections(PoreNetworkModel model, int[] connections, float[] distanceMatrix,
                                      int poreCount, int maxConnections)
        {
            HashSet<(int, int)> processedPairs = new HashSet<(int, int)>();

            for (int i = 0; i < poreCount; i++)
            {
                Pore pore1 = model.Pores[i];

                for (int j = 0; j < maxConnections; j++)
                {
                    int connIdx = i * maxConnections + j;
                    if (connIdx >= connections.Length)
                        continue;

                    int otherIdx = connections[connIdx];

                    // Skip invalid indices
                    if (otherIdx < 0 || otherIdx >= poreCount)
                        continue;

                    Pore pore2 = model.Pores[otherIdx];

                    // Use smaller ID first to avoid duplicates
                    int id1 = Math.Min(pore1.Id, pore2.Id);
                    int id2 = Math.Max(pore1.Id, pore2.Id);

                    var pair = (id1, id2);
                    if (processedPairs.Contains(pair))
                        continue;

                    processedPairs.Add(pair);

                    // Calculate throat properties
                    float dist = distanceMatrix[i * poreCount + otherIdx];
                    double radius = (pore1.Radius + pore2.Radius) * 0.25;
                    double length = Math.Max(0.1, dist - pore1.Radius - pore2.Radius);
                    double volume = Math.PI * radius * radius * length;

                    // Create throat
                    Throat throat = new Throat
                    {
                        Id = model.Throats.Count + 1,
                        PoreId1 = id1,
                        PoreId2 = id2,
                        Radius = radius,
                        Length = length,
                        Volume = volume
                    };

                    model.Throats.Add(throat);

                    // Update connection counts
                    pore1.ConnectionCount++;
                    pore2.ConnectionCount++;
                }
            }
        }

        // Pure CPU processing as another fallback
        private void ProcessConnectionsCPU(PoreNetworkModel model, float[] distanceMatrix, int poreCount, int maxConnections)
        {
            Logger.Log("[PoreNetworkGenerator] Using CPU fallback for neighbor finding");

            HashSet<(int, int)> processedPairs = new HashSet<(int, int)>();

            for (int i = 0; i < poreCount; i++)
            {
                Pore pore1 = model.Pores[i];

                // Sort distances for this pore
                List<(int idx, float dist)> neighbors = new List<(int, float)>();
                for (int j = 0; j < poreCount; j++)
                {
                    if (i != j)
                    {
                        neighbors.Add((j, distanceMatrix[i * poreCount + j]));
                    }
                }

                // Sort and take closest
                var closest = neighbors.OrderBy(n => n.dist).Take(maxConnections);

                foreach (var (j, dist) in closest)
                {
                    Pore pore2 = model.Pores[j];

                    int id1 = Math.Min(pore1.Id, pore2.Id);
                    int id2 = Math.Max(pore1.Id, pore2.Id);

                    var pair = (id1, id2);
                    if (processedPairs.Contains(pair))
                        continue;

                    processedPairs.Add(pair);

                    // Calculate throat properties
                    double radius = (pore1.Radius + pore2.Radius) * 0.25;
                    double length = Math.Max(0.1, dist - pore1.Radius - pore2.Radius);
                    double volume = Math.PI * radius * radius * length;

                    // Create throat
                    Throat throat = new Throat
                    {
                        Id = model.Throats.Count + 1,
                        PoreId1 = id1,
                        PoreId2 = id2,
                        Radius = radius,
                        Length = length,
                        Volume = volume
                    };

                    model.Throats.Add(throat);

                    // Update connection counts
                    pore1.ConnectionCount++;
                    pore2.ConnectionCount++;
                }
            }
        }



        private void CalculateNetworkProperties(PoreNetworkModel model)
        {
            // Calculate total volumes
            model.TotalPoreVolume = model.Pores.Sum(p => p.Volume);
            model.TotalThroatVolume = model.Throats.Sum(t => t.Volume);

            // Find the bounding box of all pores to determine the sample volume
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            foreach (var pore in model.Pores)
            {
                minX = Math.Min(minX, pore.Center.X - pore.Radius);
                minY = Math.Min(minY, pore.Center.Y - pore.Radius);
                minZ = Math.Min(minZ, pore.Center.Z - pore.Radius);

                maxX = Math.Max(maxX, pore.Center.X + pore.Radius);
                maxY = Math.Max(maxY, pore.Center.Y + pore.Radius);
                maxZ = Math.Max(maxZ, pore.Center.Z + pore.Radius);
            }

            // Calculate the volume of the sample based on the bounding box
            double totalVolume = (maxX - minX) * (maxY - minY) * (maxZ - minZ);

            // If bounding box calculation fails (e.g., with only one pore), use a fallback
            if (totalVolume <= 0 && model.Pores.Count > 0)
            {
                // Estimate volume based on average pore size and count
                double avgRadius = model.Pores.Average(p => p.Radius);
                double estimatedSideLength = avgRadius * 4 * Math.Pow(model.Pores.Count, 1.0 / 3.0);
                totalVolume = Math.Pow(estimatedSideLength, 3);
            }

            // Calculate porosity as void volume / total volume
            // Ensure it never exceeds 100%
            model.Porosity = totalVolume > 0 ?
                Math.Min(1.0, (model.TotalPoreVolume + model.TotalThroatVolume) / totalVolume) : 0;
        }


        private double Distance(Point3D p1, Point3D p2)
        {
            return Math.Sqrt(
                Math.Pow(p2.X - p1.X, 2) +
                Math.Pow(p2.Y - p1.Y, 2) +
                Math.Pow(p2.Z - p1.Z, 2)
            );
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources
                    accelerator?.Dispose();
                    context?.Dispose();
                }

                // Set the flag to prevent redundant calls
                disposed = true;
            }
        }

        ~PoreNetworkGenerator()
        {
            Dispose(false);
        }
    }
}
