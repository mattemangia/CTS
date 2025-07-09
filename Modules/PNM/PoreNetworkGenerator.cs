//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CTS
{
    public class PoreNetworkGenerator : IDisposable
    {
        private Context context;
        private Accelerator accelerator;
        private bool gpuInitialized = false;
        private bool disposed = false;
        private readonly int _degreeOfParallelism;

        public PoreNetworkGenerator()
        {
            // Initialize with optimal thread count based on available processors
            _degreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1);
            Logger.Log($"[PoreNetworkGenerator] Initialized with {_degreeOfParallelism} worker threads");

            // Initialize GPU in the background to avoid UI blocking
            Task.Run(() => InitializeGPU());
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
            bool useGpu,
            double maxThroatLengthFactor = 3.0,
            double minOverlapFactor = 0.1,
            bool enforceFlowPath = true,
            CancellationToken cancellationToken = default)
        {
            // Report initial progress
            progress?.Report(0);

            PoreNetworkModel model = new PoreNetworkModel();
            model.PixelSize = pixelSize;

            // Create a Random instance with a fixed seed for reproducible results
            Random random = new Random(42);

            try
            {
                // Step 1: Convert particles to pores (parallelized)
                progress?.Report(5);

                // Use ConcurrentBag for thread-safe collection
                var pores = new ConcurrentBag<Pore>();

                await Task.Run(() =>
                {
                    // Process particles in parallel
                    Parallel.ForEach(
                        separationResult.Particles,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = _degreeOfParallelism,
                            CancellationToken = cancellationToken
                        },
                        particle =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            double volume = particle.VoxelCount * Math.Pow(pixelSize, 3); // m³
                            double radius = Math.Pow(3 * volume / (4 * Math.PI), 1.0 / 3.0); // m

                            // Use the CPU method for surface area for consistency
                            double surfaceArea = CalculateSurfaceArea(particle, separationResult.LabelVolume, pixelSize);

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
                            pores.Add(pore);
                        }
                    );
                }, cancellationToken);

                // Transfer from concurrent bag to model list and sort by ID for deterministic ordering
                model.Pores = pores.OrderBy(p => p.Id).ToList();
                progress?.Report(30);

                // Step 2: Calculate distance matrix and connectivity in parallel
                var distances = new ConcurrentDictionary<(int, int), double>();
                var canConnect = new ConcurrentDictionary<(int, int), bool>();

                await Task.Run(() =>
                {
                    // Calculate average pore radius for scaling connections
                    double avgPoreRadius = model.Pores.Count > 0 ? model.Pores.Average(p => p.Radius) : 10.0;
                    // Maximum throat length based on petrophysical factor
                    double maxThroatLength = avgPoreRadius * maxThroatLengthFactor;

                    // Using partitioner for better load balancing
                    var rangePartitioner = Partitioner.Create(0, model.Pores.Count);

                    Parallel.ForEach(
                        rangePartitioner,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = _degreeOfParallelism,
                            CancellationToken = cancellationToken
                        },
                        range =>
                        {
                            for (int i = range.Item1; i < range.Item2; i++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();

                                for (int j = i + 1; j < model.Pores.Count; j++)
                                {
                                    Pore pore1 = model.Pores[i];
                                    Pore pore2 = model.Pores[j];

                                    // Calculate center-to-center distance
                                    double distance = Distance(pore1.Center, pore2.Center);
                                    distances[(pore1.Id, pore2.Id)] = distance;

                                    // Calculate if pores can connect based on petrophysical criteria
                                    bool connectible = CanPoresConnect(pore1, pore2, distance, maxThroatLength, minOverlapFactor);
                                    canConnect[(pore1.Id, pore2.Id)] = connectible;
                                }
                            }
                        }
                    );
                }, cancellationToken);

                progress?.Report(50);

                // Step 3: Generate throats using calculated distances
                var throatList = new List<Throat>();
                var connectionCounts = new Dictionary<int, int>();

                // Initialize connection counts
                foreach (var pore in model.Pores)
                {
                    connectionCounts[pore.Id] = 0;
                }

                // Generate throats deterministically
                int maxConnections = Math.Min(6, model.Pores.Count - 1);

                foreach (var pore1 in model.Pores)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Get all potentially connected pores with their distances
                    var connectiblePores = model.Pores
                        .Where(p => p.Id != pore1.Id)
                        .Select(p =>
                        {
                            double distance;
                            bool canConnectValue;
                            var minId = Math.Min(pore1.Id, p.Id);
                            var maxId = Math.Max(pore1.Id, p.Id);
                            var key = (minId, maxId);

                            distances.TryGetValue(key, out distance);
                            canConnect.TryGetValue(key, out canConnectValue);

                            return new { Pore = p, Distance = distance, CanConnect = canConnectValue };
                        })
                        .Where(pair => pair.CanConnect)  // Only include pores that can connect petrophysically
                        .OrderBy(pair => pair.Distance)
                        .ThenBy(pair => pair.Pore.Id) // Secondary sort by ID for deterministic ordering
                        .Take(maxConnections);

                    foreach (var pair in connectiblePores)
                    {
                        Pore pore2 = pair.Pore;
                        double distance = pair.Distance;

                        // Avoid duplicate throats (only add if pore1.Id < pore2.Id)
                        if (pore1.Id < pore2.Id)
                        {
                            // Check if we've already reached the max connections for either pore
                            if (connectionCounts[pore1.Id] >= maxConnections ||
                                connectionCounts[pore2.Id] >= maxConnections)
                                continue;

                            // Calculate throat properties
                            double radius = CalculateThroatRadius(pore1.Radius, pore2.Radius);
                            double length = Math.Max(0.1, distance - pore1.Radius - pore2.Radius);
                            double volume = Math.PI * radius * radius * length;

                            Throat throat = new Throat
                            {
                                Id = throatList.Count + 1,
                                PoreId1 = pore1.Id,
                                PoreId2 = pore2.Id,
                                Radius = radius,
                                Length = length,
                                Volume = volume
                            };

                            throatList.Add(throat);

                            // Update connection counts
                            connectionCounts[pore1.Id]++;
                            connectionCounts[pore2.Id]++;
                        }
                    }
                }

                // Update connection counts in the model
                foreach (var pore in model.Pores)
                {
                    pore.ConnectionCount = connectionCounts[pore.Id];
                }

                // Transfer throats to model
                model.Throats = throatList;
                progress?.Report(70);

                // Step 4: When enforceFlowPath is enabled, ensure connectivity along main flow axis
                if (enforceFlowPath && model.Pores.Count > 0)
                {
                    await Task.Run(() =>
                    {
                        EnsureFlowPathConnectivity(model, distances);
                    }, cancellationToken);
                }

                progress?.Report(85);

                // Step 5: Calculate network properties
                await Task.Run(() =>
                {
                    CalculateNetworkProperties(model);

                    // Calculate tortuosity for the network
                    model.Tortuosity = CalculateTortuosity(model);
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Logger.Log("[PoreNetworkGenerator] Generation was cancelled by user");
                throw; // Re-throw to properly handle cancellation
            }
            catch (Exception ex)
            {
                Logger.Log($"[PoreNetworkGenerator] Error generating network: {ex.Message}\n{ex.StackTrace}");
                throw; // Re-throw to properly handle errors
            }

            progress?.Report(100);
            return model;
        }

        private bool CanPoresConnect(Pore pore1, Pore pore2, double distance, double maxThroatLength, double minOverlapFactor)
        {
            // Check if distance is within maximum throat length
            if (distance > maxThroatLength)
                return false;

            // Calculate connectivity based on pore size and distance
            double sumOfRadii = pore1.Radius + pore2.Radius;
            double overlap = Math.Max(0, sumOfRadii - distance);
            double overlapFactor = overlap / Math.Min(pore1.Radius, pore2.Radius);

            // Must have minimum overlap to connect
            return overlapFactor >= minOverlapFactor;
        }

        private void EnsureFlowPathConnectivity(PoreNetworkModel model, ConcurrentDictionary<(int, int), double> distances)
        {
            // Sort pores by Z coordinate (typical flow direction)
            var sortedPores = model.Pores.OrderBy(p => p.Center.Z).ToList();

            // No need to process if fewer than 2 pores
            if (sortedPores.Count < 2)
                return;

            // Create a simple graph representation
            Dictionary<int, List<int>> graph = new Dictionary<int, List<int>>();
            foreach (var pore in model.Pores)
            {
                graph[pore.Id] = new List<int>();
            }

            // Fill in existing connections
            foreach (var throat in model.Throats)
            {
                graph[throat.PoreId1].Add(throat.PoreId2);
                graph[throat.PoreId2].Add(throat.PoreId1);
            }

            // Find inlet and outlet zones (top 10% and bottom 10% of pores)
            int boundaryCount = Math.Max(1, (int)(sortedPores.Count * 0.1));
            var inletPores = sortedPores.Take(boundaryCount).ToList();
            var outletPores = sortedPores.Skip(sortedPores.Count - boundaryCount).ToList();

            // Check if any inlet pore can reach any outlet pore using parallel BFS
            bool isConnected = false;

            // Check connectivity using BFS from each inlet pore
            foreach (var inlet in inletPores)
            {
                if (isConnected) break; // Skip if already found a path

                // Simple BFS to check connectivity
                HashSet<int> visited = new HashSet<int>();
                Queue<int> queue = new Queue<int>();
                queue.Enqueue(inlet.Id);
                visited.Add(inlet.Id);

                while (queue.Count > 0 && !isConnected)
                {
                    int current = queue.Dequeue();

                    // Check if we've reached an outlet
                    if (outletPores.Any(p => p.Id == current))
                    {
                        isConnected = true;
                        break;
                    }

                    // Add all unvisited neighbors
                    foreach (int neighbor in graph[current])
                    {
                        if (!visited.Contains(neighbor))
                        {
                            visited.Add(neighbor);
                            queue.Enqueue(neighbor);
                        }
                    }
                }
            }

            // If not connected, create necessary connections
            if (!isConnected)
            {
                // Identify the largest clusters
                var clusters = FindDisconnectedClusters(model, graph);
                ConnectLargestClusters(model, clusters, distances);
            }
        }

        private List<List<int>> FindDisconnectedClusters(PoreNetworkModel model, Dictionary<int, List<int>> graph)
        {
            HashSet<int> visited = new HashSet<int>();
            List<List<int>> clusters = new List<List<int>>();

            foreach (var pore in model.Pores)
            {
                if (!visited.Contains(pore.Id))
                {
                    // New cluster found
                    List<int> cluster = new List<int>();
                    Queue<int> queue = new Queue<int>();
                    queue.Enqueue(pore.Id);
                    visited.Add(pore.Id);

                    while (queue.Count > 0)
                    {
                        int current = queue.Dequeue();
                        cluster.Add(current);

                        foreach (int neighbor in graph[current])
                        {
                            if (!visited.Contains(neighbor))
                            {
                                visited.Add(neighbor);
                                queue.Enqueue(neighbor);
                            }
                        }
                    }

                    clusters.Add(cluster);
                }
            }

            // Sort clusters by size (largest first)
            return clusters.OrderByDescending(c => c.Count).ToList();
        }

        // Connects the largest clusters to ensure flow path
        private void ConnectLargestClusters(PoreNetworkModel model, List<List<int>> clusters, ConcurrentDictionary<(int, int), double> distances)
        {
            // Need at least 2 clusters to connect
            if (clusters.Count < 2)
                return;

            // Find the closest pair of pores between the two largest clusters
            var largestCluster = clusters[0];
            var secondCluster = clusters[1];

            double minDistance = double.MaxValue;
            Pore bestPore1 = null;
            Pore bestPore2 = null;

            // Find closest pair
            foreach (int id1 in largestCluster)
            {
                Pore pore1 = model.Pores.First(p => p.Id == id1);

                foreach (int id2 in secondCluster)
                {
                    Pore pore2 = model.Pores.First(p => p.Id == id2);

                    var key = (Math.Min(id1, id2), Math.Max(id1, id2));
                    double distance;

                    if (distances.TryGetValue(key, out distance) && distance < minDistance)
                    {
                        minDistance = distance;
                        bestPore1 = pore1;
                        bestPore2 = pore2;
                    }
                }
            }

            // Create a throat connecting the clusters
            if (bestPore1 != null && bestPore2 != null)
            {
                double radius = CalculateThroatRadius(bestPore1.Radius, bestPore2.Radius);
                double length = Math.Max(0.1, minDistance - bestPore1.Radius - bestPore2.Radius);
                double volume = Math.PI * radius * radius * length;

                Throat throat = new Throat
                {
                    Id = model.Throats.Count + 1,
                    PoreId1 = bestPore1.Id,
                    PoreId2 = bestPore2.Id,
                    Radius = radius,
                    Length = length,
                    Volume = volume
                };

                model.Throats.Add(throat);

                // Update connection counts
                bestPore1.ConnectionCount++;
                bestPore2.ConnectionCount++;
            }

            // Recursively connect additional clusters if necessary
            if (clusters.Count > 2)
            {
                // After connecting the two largest, treat them as one
                var mergedCluster = largestCluster.Concat(secondCluster).ToList();
                var remainingClusters = clusters.Skip(2).ToList();
                remainingClusters.Insert(0, mergedCluster);

                ConnectLargestClusters(model, remainingClusters, distances);
            }
        }

        private double CalculateThroatRadius(double radius1, double radius2)
        {
            // Use the petrophysical relationship for throat radius
            // Typically 0.3-0.7 times the smaller pore radius in real rocks
            double minRadius = Math.Min(radius1, radius2);
            return minRadius * 0.4; // Standard factor in petrophysical models
        }

        /// <summary>
        /// Calculate tortuosity of the pore network model
        /// </summary>
        /// <param name="model">The pore network model</param>
        /// <returns>Geometric tortuosity value</returns>
        private double CalculateTortuosity(PoreNetworkModel model)
        {
            if (model.Pores.Count < 2 || model.Throats.Count == 0) return 1.0;

            /* ---------- grafo non orientato pesato sui throats -------------------- */
            var graph = new Dictionary<int, List<(int poreId, double dist)>>(model.Pores.Count);
            foreach (var p in model.Pores) graph[p.Id] = new List<(int, double)>();

            foreach (var th in model.Throats)
            {
                var p1 = model.Pores.First(p => p.Id == th.PoreId1);
                var p2 = model.Pores.First(p => p.Id == th.PoreId2);
                double d = Distance(p1.Center, p2.Center);          // µm

                graph[th.PoreId1].Add((th.PoreId2, d));
                graph[th.PoreId2].Add((th.PoreId1, d));
            }

            /* ---------- τ asse-per-asse ------------------------------------------ */
            double τx = CalculateAxisTortuosity(model, graph, "X");
            double τy = CalculateAxisTortuosity(model, graph, "Y");
            double τz = CalculateAxisTortuosity(model, graph, "Z");

            var finite = new List<double>();
            if (!double.IsNaN(τx) && !double.IsInfinity(τx)) finite.Add(τx);
            if (!double.IsNaN(τy) && !double.IsInfinity(τy)) finite.Add(τy);
            if (!double.IsNaN(τz) && !double.IsInfinity(τz)) finite.Add(τz);

            double τavg;
            if (finite.Count > 0) τavg = finite.Average();
            else τavg = double.PositiveInfinity;

            Logger.Log($"[PoreNetworkGenerator] Calculated tortuosity: X={τx:F3}, Y={τy:F3}, Z={τz:F3}, Avg={τavg:F3}");
            return τavg;
        }
        /// <summary>
        /// Calculate tortuosity along a specific axis
        /// </summary>
        /// <param name="model">The pore network model</param>
        /// <param name="graph">The network graph representation</param>
        /// <param name="axis">The axis to calculate tortuosity along ("X", "Y", or "Z")</param>
        /// <returns>Tortuosity value along the specified axis</returns>
        private double CalculateAxisTortuosity(
    PoreNetworkModel model,
    Dictionary<int, List<(int poreId, double dist)>> graph,
    string axis)
        {
            /* ---------- inlet / outlet ------------------------------------------- */
            List<Pore> inlet, outlet;
            SelectBoundaryPoresForAxis(model, axis, out inlet, out outlet);
            if (inlet.Count == 0 || outlet.Count == 0) return double.PositiveInfinity;

            /* ---------- lunghezza retta del campione (µm) ------------------------ */
            double straight;
            switch (axis)
            {
                case "X": straight = model.Pores.Max(p => p.Center.X) - model.Pores.Min(p => p.Center.X); break;
                case "Y": straight = model.Pores.Max(p => p.Center.Y) - model.Pores.Min(p => p.Center.Y); break;
                default: straight = model.Pores.Max(p => p.Center.Z) - model.Pores.Min(p => p.Center.Z); break;
            }
            if (straight < 1e-6) return 1.0;

            /* ---------- Dijkstra da ogni inlet (caching) ------------------------- */
            var cache = new Dictionary<int, Dictionary<int, double>>();
            var paths = new List<double>();

            foreach (var pin in inlet)
            {
                if (!cache.TryGetValue(pin.Id, out var distMap))
                {
                    distMap = CalculateShortestPaths(model, graph, pin.Id);
                    cache[pin.Id] = distMap;
                }

                foreach (var pout in outlet)
                {
                    if (distMap.TryGetValue(pout.Id, out double d) &&
                        d >= straight && d < straight * 15)        // filtra scorciatoie o cicli infiniti
                    {
                        paths.Add(d);
                    }
                }
            }

            if (paths.Count == 0) return double.PositiveInfinity;

            paths.Sort();
            double median = paths[paths.Count / 2];
            double tau = median / straight;

            /* ---------- limite superiore dinamico in base alla connettività ------ */
            double avgConn = model.Pores.Average(p => p.ConnectionCount);
            double maxTau;                                     // (≈ 5 se conn. ≥4) … (≈ 11 se conn. ≈1)
            if (avgConn >= 4.0) maxTau = 5.0;
            else if (avgConn >= 3.0) maxTau = 6.5;
            else if (avgConn >= 2.0) maxTau = 8.0;
            else maxTau = 11.0;

            if (tau < 1.0) tau = 1.0;
            else if (tau > maxTau) tau = maxTau;

            return tau;
        }

        /// <summary>
        /// Calculate shortest paths from a source pore to all other pores using Dijkstra's algorithm
        /// </summary>
        /// <param name="model">The pore network model</param>
        /// <param name="graph">The network graph representation</param>
        /// <param name="sourceId">The source pore ID</param>
        /// <returns>Dictionary mapping pore IDs to their shortest path distances from the source</returns>
        private static Dictionary<int, double> CalculateShortestPaths(
    PoreNetworkModel model,
    Dictionary<int, List<(int poreId, double dist)>> graph,
    int sourceId)
        {
            var dist = new Dictionary<int, double>(model.Pores.Count);
            var visited = new HashSet<int>();

            foreach (var p in model.Pores) dist[p.Id] = double.PositiveInfinity;
            dist[sourceId] = 0.0;

            while (visited.Count < model.Pores.Count)
            {
                /* trova il nodo non visitato con distanza minima */
                double minD = double.PositiveInfinity;
                int minId = -1;

                foreach (var kv in dist)
                    if (!visited.Contains(kv.Key) && kv.Value < minD)
                    {
                        minD = kv.Value;
                        minId = kv.Key;
                    }

                if (minId == -1 || double.IsPositiveInfinity(minD)) break;  // resto irraggiungibile
                visited.Add(minId);

                /* rilassamento archi uscenti */
                foreach (var edge in graph[minId])
                {
                    int nid = edge.poreId;
                    double nd = minD + edge.dist;
                    if (nd < dist[nid]) dist[nid] = nd;
                }
            }

            return dist;
        }

        private static void SelectBoundaryPoresForAxis(
    PoreNetworkModel model,
    string axis,          // "X"  "Y"  "Z"
    out List<Pore> inletPores,
    out List<Pore> outletPores)
        {
            if (model == null || model.Pores.Count == 0)
                throw new ArgumentException("Empty pore network", nameof(model));

            double avgRadius = model.Pores.Average(p => p.Radius);   // µm
            double layer = 2.0 * avgRadius;                      // µm

            Func<Pore, double> coord;
            switch (axis)
            {
                case "X": coord = p => p.Center.X; break;
                case "Y": coord = p => p.Center.Y; break;
                default: coord = p => p.Center.Z; break;           // "Z"
            }

            double minC = model.Pores.Min(coord);
            double maxC = model.Pores.Max(coord);

            inletPores = model.Pores.Where(p => coord(p) - minC <= layer).ToList();
            outletPores = model.Pores.Where(p => maxC - coord(p) <= layer).ToList();
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
            return surfaceArea;
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