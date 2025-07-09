//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static CTS.ParticleSeparator;

namespace CTS
{
    public class ParticleSeparator : IDisposable
    {
        // Constants for array and processing handling
        private const int CHUNK_SIZE = 128; // Optimized chunk size for processing
        private const int MAX_ITERATIONS = 1000; // Maximum iterations for label propagation
        private const int PARTICLE_BATCH_SIZE = 100000; // Process particles in batches

        private MainForm mainForm;
        private Material selectedMaterial;
        private bool useGpu;
        private Context gpuContext;
        private Accelerator accelerator;
        private bool gpuInitialized = false;
        private int maxThreads;

        // Class to represent a particle
        public class Particle
        {
            public int Id { get; set; }
            public int VoxelCount { get; set; }
            public double VolumeMicrometers { get; set; }
            public double VolumeMillimeters { get; set; }
            public Point3D Center { get; set; }
            public BoundingBox Bounds { get; set; }

            public override string ToString()
            {
                return $"ID: {Id}, Volume: {VoxelCount} voxels ({VolumeMicrometers:F2} µm³)";
            }
        }

        public class Point3D
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }

            public override string ToString()
            {
                return $"({X}, {Y}, {Z})";
            }
        }

        public class BoundingBox
        {
            public int MinX { get; set; }
            public int MinY { get; set; }
            public int MinZ { get; set; }
            public int MaxX { get; set; }
            public int MaxY { get; set; }
            public int MaxZ { get; set; }

            public int Width => MaxX - MinX + 1;
            public int Height => MaxY - MinY + 1;
            public int Depth => MaxZ - MinZ + 1;

            public override string ToString()
            {
                return $"Min: ({MinX}, {MinY}, {MinZ}), Max: ({MaxX}, {MaxY}, {MaxZ})";
            }
        }

        // Optimized result class with on-demand particle analysis
        public class SeparationResult
        {
            public int[,,] LabelVolume { get; set; }
            private List<Particle> _particles;
            public List<Particle> Particles
            {
                get
                {
                    // If particles haven't been analyzed yet, analyze them now
                    if (_particles == null && LabelVolume != null && _analyzeParticlesFunc != null)
                    {
                        _particles = _analyzeParticlesFunc();
                    }
                    return _particles;
                }
                set
                {
                    _particles = value;
                }
            }
            public int CurrentSlice { get; set; }
            public bool Is3D { get; set; }

            // Function to analyze particles on demand
            private Func<List<Particle>> _analyzeParticlesFunc;

            // Setup lazy loading of particles
            public void SetParticleAnalysisFunction(Func<List<Particle>> analysisFunc)
            {
                _analyzeParticlesFunc = analysisFunc;
            }
        }

        // Constructor
        public ParticleSeparator(MainForm mainForm, Material selectedMaterial, bool useGpu)
        {
            this.mainForm = mainForm;
            this.selectedMaterial = selectedMaterial;
            this.useGpu = useGpu;
            this.maxThreads = Environment.ProcessorCount;

            if (useGpu)
            {
                InitializeGpu();
            }
        }

        private void InitializeGpu()
        {
            try
            {
                // Initialize ILGPU with simpler approach for version 1.5.1
                gpuContext = Context.Create(builder => builder.Default().EnableAlgorithms());

                // Try to create an accelerator
                foreach (var device in gpuContext.Devices)
                {
                    try
                    {
                        if (device.AcceleratorType != AcceleratorType.CPU)
                        {
                            accelerator = device.CreateAccelerator(gpuContext);
                            Logger.Log($"[ParticleSeparator] Using GPU accelerator: {device.Name}");
                            gpuInitialized = true;
                            return;
                        }
                    }
                    catch (Exception deviceEx)
                    {
                        Logger.Log($"[ParticleSeparator] Could not initialize device {device.Name}: {deviceEx.Message}");
                    }
                }

                // If no GPU device worked, fall back to CPU
                try
                {
                    var cpuDevice = gpuContext.GetCPUDevice(0);
                    accelerator = cpuDevice.CreateAccelerator(gpuContext);
                    Logger.Log("[ParticleSeparator] Falling back to CPU accelerator");
                    gpuInitialized = true;
                }
                catch (Exception cpuEx)
                {
                    Logger.Log($"[ParticleSeparator] Could not initialize CPU accelerator: {cpuEx.Message}");
                    gpuInitialized = false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ParticleSeparator] Error initializing GPU: {ex.Message}");
                gpuContext = null;
                accelerator = null;
                gpuInitialized = false;
            }
        }

        public SeparationResult SeparateParticles(bool process3D, bool conservative, int currentSlice, IProgress<int> progress, CancellationToken cancellationToken)
        {
            if (process3D)
            {
                return Separate3D(conservative, progress, cancellationToken);
            }
            else
            {
                return Separate2D(currentSlice, conservative, progress, cancellationToken);
            }
        }
        private SeparationResult Separate2D(int slice, bool conservative, IProgress<int> progress, CancellationToken cancellationToken)
        {
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();

            // Create a 2D array to hold the slice data
            byte[,] sliceData = new byte[width, height];

            // Extract the slice data in parallel
            progress?.Report(10);

            Parallel.For(0, height, new ParallelOptions { MaxDegreeOfParallelism = maxThreads, CancellationToken = cancellationToken }, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    byte label = mainForm.volumeLabels[x, y, slice];
                    // Only include voxels with the selected material
                    sliceData[x, y] = (label == selectedMaterial.ID) ? (byte)1 : (byte)0;
                }
            });

            // Check for cancellation
            cancellationToken.ThrowIfCancellationRequested();

            // Perform connected component labeling
            progress?.Report(30);
            int[,] labeledSlice;

            if (useGpu && gpuInitialized)
            {
                // Use GPU for labeling
                labeledSlice = LabelConnectedComponents2DGpu(sliceData);
            }
            else
            {
                // Use CPU for labeling
                labeledSlice = LabelConnectedComponents2DCpu(sliceData, cancellationToken);
            }

            // Check for cancellation
            cancellationToken.ThrowIfCancellationRequested();

            // Create a 3D volume for consistent interface
            int[,,] labelVolume = new int[width, height, 1];

            Parallel.For(0, height, new ParallelOptions { MaxDegreeOfParallelism = maxThreads, CancellationToken = cancellationToken }, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    labelVolume[x, y, 0] = labeledSlice[x, y];
                }
            });

            progress?.Report(60);

            // Create a function for deferred particle analysis
            Func<List<Particle>> analyzeParticlesFunc = () =>
            {
                Logger.Log("[ParticleSeparator] Analyzing 2D particles on demand");

                // Use concurrent collections for thread safety
                ConcurrentDictionary<int, int> voxelCounts = new ConcurrentDictionary<int, int>();
                ConcurrentDictionary<int, (int sumX, int sumY)> centers = new ConcurrentDictionary<int, (int, int)>();
                ConcurrentDictionary<int, (int minX, int minY, int maxX, int maxY)> bounds =
                    new ConcurrentDictionary<int, (int, int, int, int)>();

                // First pass: find all unique labels (in parallel)
                var uniqueLabels = new ConcurrentBag<int>();

                Parallel.For(0, height, new ParallelOptions { MaxDegreeOfParallelism = maxThreads }, y =>
                {
                    for (int x = 0; x < width; x++)
                    {
                        int label = labeledSlice[x, y];
                        if (label > 0)
                        {
                            // Add to unique labels if not already added
                            if (!voxelCounts.ContainsKey(label))
                            {
                                voxelCounts.TryAdd(label, 0);
                                centers.TryAdd(label, (0, 0));
                                bounds.TryAdd(label, (x, y, x, y));
                                uniqueLabels.Add(label);
                            }
                        }
                    }
                });

                // Count voxels and compute centers (in parallel)
                Parallel.ForEach(uniqueLabels, new ParallelOptions { MaxDegreeOfParallelism = maxThreads }, label =>
                {
                    int count = 0;
                    int sumX = 0;
                    int sumY = 0;
                    int minX = int.MaxValue;
                    int minY = int.MaxValue;
                    int maxX = int.MinValue;
                    int maxY = int.MinValue;

                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            if (labeledSlice[x, y] == label)
                            {
                                count++;
                                sumX += x;
                                sumY += y;
                                minX = Math.Min(minX, x);
                                minY = Math.Min(minY, y);
                                maxX = Math.Max(maxX, x);
                                maxY = Math.Max(maxY, y);
                            }
                        }
                    }

                    voxelCounts[label] = count;
                    centers[label] = (sumX, sumY);
                    bounds[label] = (minX, minY, maxX, maxY);
                });

                // Create the particles from the collected data
                List<Particle> particles = new List<Particle>();

                foreach (var label in uniqueLabels)
                {
                    int voxelCount = voxelCounts[label];
                    var center = centers[label];
                    var bound = bounds[label];

                    // Filter out small particles if conservative approach is selected
                    if (conservative && voxelCount < 10)
                        continue;

                    Particle particle = new Particle
                    {
                        Id = label,
                        VoxelCount = voxelCount,
                        VolumeMicrometers = voxelCount * mainForm.pixelSize * mainForm.pixelSize * 1e12,
                        VolumeMillimeters = voxelCount * mainForm.pixelSize * mainForm.pixelSize * 1e6,
                        Center = new Point3D
                        {
                            X = center.sumX / voxelCount,
                            Y = center.sumY / voxelCount,
                            Z = slice
                        },
                        Bounds = new BoundingBox
                        {
                            MinX = bound.minX,
                            MinY = bound.minY,
                            MinZ = slice,
                            MaxX = bound.maxX,
                            MaxY = bound.maxY,
                            MaxZ = slice
                        }
                    };

                    particles.Add(particle);
                }

                Logger.Log($"[ParticleSeparator] 2D particle analysis completed: {particles.Count} particles");
                return particles;
            };

            var result = new SeparationResult
            {
                LabelVolume = labelVolume,
                Particles = null, // Will be computed on demand
                CurrentSlice = 0,
                Is3D = false
            };

            // Set up deferred processing
            result.SetParticleAnalysisFunction(analyzeParticlesFunc);

            progress?.Report(100);
            return result;
        }
        private SeparationResult Separate3D(bool conservative, IProgress<int> progress, CancellationToken cancellationToken)
        {
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();

            // Check memory limits - estimate how many voxels we can process
            long totalVoxels = (long)width * height * depth;
            Logger.Log($"[Separate3D] Processing volume with {totalVoxels} voxels");

            // For very large volumes, always use chunked processing
            bool useChunkedProcessing = totalVoxels > 250_000_000;
            if (useChunkedProcessing)
            {
                Logger.Log($"[Separate3D] Volume is very large ({totalVoxels} voxels), using optimized chunked processing");
                return Separate3DLarge(conservative, progress, cancellationToken);
            }

            try
            {
                // Create the volume data with memory-efficient approach
                progress?.Report(5);
                byte[,,] volumeData = null;

                try
                {
                    Logger.Log("[Separate3D] Allocating volume data array");
                    volumeData = new byte[width, height, depth];
                }
                catch (OutOfMemoryException)
                {
                    Logger.Log("[Separate3D] Failed to allocate volume array, falling back to chunked processing");
                    return Separate3DLarge(conservative, progress, cancellationToken);
                }

                // Extract the volume data in parallel slices
                progress?.Report(10);

                Parallel.For(0, depth, new ParallelOptions { MaxDegreeOfParallelism = maxThreads, CancellationToken = cancellationToken }, z =>
                {
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            byte label = mainForm.volumeLabels[x, y, z];
                            // Only include voxels with the selected material
                            volumeData[x, y, z] = (label == selectedMaterial.ID) ? (byte)1 : (byte)0;
                        }
                    }

                    if (z % 10 == 0)
                    {
                        int progressValue = 10 + (z * 15) / depth;
                        progress?.Report(progressValue);
                    }
                });

                // Perform connected component labeling
                progress?.Report(25);
                int[,,] labeledVolume = null;

                try
                {
                    if (useGpu && gpuInitialized)
                    {
                        // Use GPU for labeling
                        Logger.Log("[Separate3D] Using GPU for connected component labeling");
                        labeledVolume = LabelConnectedComponents3DGpu(volumeData, cancellationToken);
                    }
                    else
                    {
                        // Use CPU for labeling
                        Logger.Log("[Separate3D] Using CPU for connected component labeling");
                        labeledVolume = LabelConnectedComponents3DCpu(volumeData, cancellationToken);
                    }
                }
                catch (OutOfMemoryException ex)
                {
                    Logger.Log($"[Separate3D] Out of memory during labeling: {ex.Message}");
                    // Free up memory ASAP
                    volumeData = null;
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);

                    // Try with chunked processing
                    return Separate3DLarge(conservative, progress, cancellationToken);
                }

                // Free up memory before particle analysis
                volumeData = null;
                GC.Collect();

                // Check for cancellation
                cancellationToken.ThrowIfCancellationRequested();

                // Create result with lazy particle analysis
                progress?.Report(75);

                // Create a function for deferred particle analysis
                Func<List<Particle>> analyzeParticlesFunc = () =>
                {
                    Logger.Log("[ParticleSeparator] Starting on-demand 3D particle analysis");
                    List<Particle> particles = new List<Particle>();

                    try
                    {
                        // First pass: find unique labels to avoid repeatedly scanning the whole volume
                        ConcurrentDictionary<int, byte> uniqueLabelsDict = new ConcurrentDictionary<int, byte>();

                        // Use LabelVolumeHelper instead of GetLength
                        int labelWidth = LabelVolumeHelper.GetWidth(labeledVolume);
                        int labelHeight = LabelVolumeHelper.GetHeight(labeledVolume);
                        int labelDepth = LabelVolumeHelper.GetDepth(labeledVolume);

                        Parallel.For(0, labelDepth, new ParallelOptions { MaxDegreeOfParallelism = maxThreads }, z =>
                        {
                            for (int y = 0; y < labelHeight; y++)
                            {
                                for (int x = 0; x < labelWidth; x++)
                                {
                                    // Use LabelVolumeHelper.GetLabel instead of direct indexing
                                    int label = LabelVolumeHelper.GetLabel(labeledVolume, x, y, z);
                                    if (label > 0)
                                    {
                                        uniqueLabelsDict.TryAdd(label, 1);
                                    }
                                }
                            }
                        });

                        var uniqueLabels = uniqueLabelsDict.Keys.ToList();
                        Logger.Log($"[ParticleSeparator] Found {uniqueLabels.Count} unique particles to analyze");

                        // Process particles in batches to avoid excessive memory usage
                        for (int batchStart = 0; batchStart < uniqueLabels.Count; batchStart += PARTICLE_BATCH_SIZE)
                        {
                            int batchEnd = Math.Min(batchStart + PARTICLE_BATCH_SIZE, uniqueLabels.Count);
                            Logger.Log($"[ParticleSeparator] Processing particle batch {batchStart}-{batchEnd} of {uniqueLabels.Count}");

                            // Create dictionaries for this batch
                            ConcurrentDictionary<int, int> voxelCounts = new ConcurrentDictionary<int, int>();
                            ConcurrentDictionary<int, (int sumX, int sumY, int sumZ)> centers =
                                new ConcurrentDictionary<int, (int, int, int)>();
                            ConcurrentDictionary<int, (int minX, int minY, int minZ, int maxX, int maxY, int maxZ)> bounds =
                                new ConcurrentDictionary<int, (int, int, int, int, int, int)>();

                            // Get labels for this batch
                            var batchLabels = uniqueLabels.GetRange(batchStart, batchEnd - batchStart);

                            // Initialize dictionaries for this batch
                            foreach (int label in batchLabels)
                            {
                                voxelCounts[label] = 0;
                                centers[label] = (0, 0, 0);
                                bounds[label] = (int.MaxValue, int.MaxValue, int.MaxValue, 0, 0, 0);
                            }

                            // Scan volume for these specific labels
                            Parallel.For(0, labelDepth, new ParallelOptions { MaxDegreeOfParallelism = maxThreads }, z =>
                            {
                                for (int y = 0; y < labelHeight; y++)
                                {
                                    for (int x = 0; x < labelWidth; x++)
                                    {
                                        // Use LabelVolumeHelper.GetLabel instead of direct indexing
                                        int label = LabelVolumeHelper.GetLabel(labeledVolume, x, y, z);
                                        if (label > 0 && batchLabels.Contains(label))
                                        {
                                            // Update voxel count atomically
                                            voxelCounts.AddOrUpdate(
                                                label,
                                                1,
                                                (key, oldCount) => oldCount + 1);

                                            // Update center sums atomically
                                            centers.AddOrUpdate(
                                                label,
                                                (x, y, z),
                                                (key, oldSums) => (oldSums.sumX + x, oldSums.sumY + y, oldSums.sumZ + z));

                                            // Update bounds atomically
                                            bounds.AddOrUpdate(
                                                label,
                                                (x, y, z, x, y, z),
                                                (key, oldBounds) => (
                                                    Math.Min(oldBounds.minX, x),
                                                    Math.Min(oldBounds.minY, y),
                                                    Math.Min(oldBounds.minZ, z),
                                                    Math.Max(oldBounds.maxX, x),
                                                    Math.Max(oldBounds.maxY, y),
                                                    Math.Max(oldBounds.maxZ, z)
                                                ));
                                        }
                                    }
                                }
                            });

                            // Create particles for this batch
                            foreach (int label in batchLabels)
                            {
                                int voxelCount = voxelCounts[label];
                                var center = centers[label];
                                var bound = bounds[label];

                                // Filter out small particles if conservative approach is selected
                                if (conservative && voxelCount < 20)
                                    continue;

                                // Calculate volumes
                                double pixelVolume = mainForm.pixelSize * mainForm.pixelSize * mainForm.pixelSize;
                                double volumeMicrometers = voxelCount * pixelVolume * 1e18; // m³ to µm³
                                double volumeMillimeters = voxelCount * pixelVolume * 1e9;  // m³ to mm³

                                Particle particle = new Particle
                                {
                                    Id = label,
                                    VoxelCount = voxelCount,
                                    VolumeMicrometers = volumeMicrometers,
                                    VolumeMillimeters = volumeMillimeters,
                                    Center = voxelCount > 0 ? new Point3D
                                    {
                                        X = center.sumX / voxelCount,
                                        Y = center.sumY / voxelCount,
                                        Z = center.sumZ / voxelCount
                                    } : new Point3D(),
                                    Bounds = new BoundingBox
                                    {
                                        MinX = bound.minX,
                                        MinY = bound.minY,
                                        MinZ = bound.minZ,
                                        MaxX = bound.maxX,
                                        MaxY = bound.maxY,
                                        MaxZ = bound.maxZ
                                    }
                                };

                                particles.Add(particle);
                            }

                            // Clean up batch-specific resources
                            voxelCounts = null;
                            centers = null;
                            bounds = null;
                            GC.Collect();
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ParticleSeparator] Error during deferred particle analysis: {ex.Message}");
                    }

                    Logger.Log($"[ParticleSeparator] 3D particle analysis completed: {particles.Count} particles");
                    return particles;
                };

                SeparationResult result = new SeparationResult
                {
                    LabelVolume = labeledVolume,
                    Particles = null, // Will be computed on demand
                    CurrentSlice = depth / 2, // Start with middle slice for viewing
                    Is3D = true
                };

                // Set up deferred processing
                result.SetParticleAnalysisFunction(analyzeParticlesFunc);

                progress?.Report(100);
                Logger.Log($"[Separate3D] Created result with deferred particle analysis");

                return result;
            }
            catch (OutOfMemoryException ex)
            {
                Logger.Log($"[Separate3D] Out of memory: {ex.Message}");
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);

                // Try with large volume method
                return Separate3DLarge(conservative, progress, cancellationToken);
            }
        }

        // For large volumes, use a chunking approach with optimized parallel processing
        private SeparationResult Separate3DLarge(bool conservative, IProgress<int> progress, CancellationToken cancellationToken)
        {
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();

            // Use an optimized chunk size for better memory locality
            int chunkSize = CHUNK_SIZE;
            int numChunksX = (width + chunkSize - 1) / chunkSize;
            int numChunksY = (height + chunkSize - 1) / chunkSize;
            int numChunksZ = (depth + chunkSize - 1) / chunkSize;
            int totalChunks = numChunksX * numChunksY * numChunksZ;

            // Process each chunk and merge results
            int[,,] globalLabels = new int[width, height, depth];
            UnionFind unionFind = new UnionFind();
            int nextGlobalLabel = 1;

            // Use concurrent dictionary for thread safety
            ConcurrentDictionary<(int chunkX, int chunkY, int chunkZ, int localLabel), int> labelMap =
                new ConcurrentDictionary<(int, int, int, int), int>();

            int chunksDone = 0;
            progress?.Report(0);

            // Create a thread-safe counter for label assignment
            var labelCounter = new AtomicCounter(1);

            // First pass: process each chunk in parallel
            Parallel.For(0, numChunksZ, new ParallelOptions { MaxDegreeOfParallelism = maxThreads, CancellationToken = cancellationToken }, chunkZ =>
            {
                for (int chunkY = 0; chunkY < numChunksY; chunkY++)
                {
                    for (int chunkX = 0; chunkX < numChunksX; chunkX++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // Determine chunk bounds
                        int startX = chunkX * chunkSize;
                        int startY = chunkY * chunkSize;
                        int startZ = chunkZ * chunkSize;
                        int endX = Math.Min(startX + chunkSize, width);
                        int endY = Math.Min(startY + chunkSize, height);
                        int endZ = Math.Min(startZ + chunkSize, depth);
                        int chunkWidth = endX - startX;
                        int chunkHeight = endY - startY;
                        int chunkDepth = endZ - startZ;

                        // Extract chunk data
                        byte[,,] chunkData = new byte[chunkWidth, chunkHeight, chunkDepth];
                        for (int z = 0; z < chunkDepth; z++)
                        {
                            for (int y = 0; y < chunkHeight; y++)
                            {
                                for (int x = 0; x < chunkWidth; x++)
                                {
                                    int globalX = startX + x;
                                    int globalY = startY + y;
                                    int globalZ = startZ + z;
                                    byte label = mainForm.volumeLabels[globalX, globalY, globalZ];
                                    chunkData[x, y, z] = (label == selectedMaterial.ID) ? (byte)1 : (byte)0;
                                }
                            }
                        }

                        // Check for cancellation
                        cancellationToken.ThrowIfCancellationRequested();

                        // Process chunk - use CPU as we're already parallelizing chunks
                        int[,,] chunkLabels = LabelConnectedComponents3DCpu(chunkData, cancellationToken);

                        // Map chunk labels to global labels
                        HashSet<int> chunkUniqueLabels = new HashSet<int>();
                        for (int z = 0; z < chunkDepth; z++)
                        {
                            for (int y = 0; y < chunkHeight; y++)
                            {
                                for (int x = 0; x < chunkWidth; x++)
                                {
                                    if (chunkLabels[x, y, z] > 0)
                                    {
                                        chunkUniqueLabels.Add(chunkLabels[x, y, z]);
                                    }
                                }
                            }
                        }

                        foreach (int localLabel in chunkUniqueLabels)
                        {
                            int newGlobalLabel = labelCounter.GetAndIncrement();
                            labelMap.TryAdd((chunkX, chunkY, chunkZ, localLabel), newGlobalLabel);
                            unionFind.MakeSet(newGlobalLabel);
                        }

                        // Apply global labels
                        for (int z = 0; z < chunkDepth; z++)
                        {
                            for (int y = 0; y < chunkHeight; y++)
                            {
                                for (int x = 0; x < chunkWidth; x++)
                                {
                                    int localLabel = chunkLabels[x, y, z];
                                    int globalX = startX + x;
                                    int globalY = startY + y;
                                    int globalZ = startZ + z;

                                    if (localLabel > 0)
                                    {
                                        globalLabels[globalX, globalY, globalZ] =
                                            labelMap[(chunkX, chunkY, chunkZ, localLabel)];
                                    }
                                }
                            }
                        }

                        Interlocked.Increment(ref chunksDone);

                        if (chunksDone % 4 == 0)
                        {
                            int progressValue = (chunksDone * 50) / totalChunks;
                            progress?.Report(progressValue);
                        }
                    }
                }
            });

            // Second pass: merge connected components across chunk boundaries
            progress?.Report(50);

            // Process in slices for better memory locality
            Parallel.For(0, depth, new ParallelOptions { MaxDegreeOfParallelism = maxThreads, CancellationToken = cancellationToken }, z =>
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int label = globalLabels[x, y, z];
                        if (label > 0)
                        {
                            // Check 6-connected neighbors
                            if (x > 0)
                            {
                                int neighborLabel = globalLabels[x - 1, y, z];
                                if (neighborLabel > 0 && neighborLabel != label)
                                {
                                    unionFind.Union(label, neighborLabel);
                                }
                            }

                            if (y > 0)
                            {
                                int neighborLabel = globalLabels[x, y - 1, z];
                                if (neighborLabel > 0 && neighborLabel != label)
                                {
                                    unionFind.Union(label, neighborLabel);
                                }
                            }

                            if (z > 0)
                            {
                                int neighborLabel = globalLabels[x, y, z - 1];
                                if (neighborLabel > 0 && neighborLabel != label)
                                {
                                    unionFind.Union(label, neighborLabel);
                                }
                            }
                        }
                    }
                }

                // Check for cancellation and report progress
                if (z % 10 == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(50 + (z * 25) / depth);
                }
            });

            // Third pass: apply final labels with a parallel approach
            ConcurrentDictionary<int, int> finalLabelMap = new ConcurrentDictionary<int, int>();
            var finalLabelCounter = new AtomicCounter(1);

            // First collect all roots
            ConcurrentBag<int> roots = new ConcurrentBag<int>();

            Parallel.For(0, depth, new ParallelOptions { MaxDegreeOfParallelism = maxThreads, CancellationToken = cancellationToken }, z =>
            {
                var localRoots = new HashSet<int>();

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int label = globalLabels[x, y, z];
                        if (label > 0)
                        {
                            int root = unionFind.Find(label);
                            localRoots.Add(root);
                        }
                    }
                }

                foreach (var root in localRoots)
                {
                    roots.Add(root);
                }

                if (z % 10 == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            });

            // Create the final label map - we need to do this sequentially to get consistent labels
            foreach (var root in new HashSet<int>(roots))
            {
                finalLabelMap.TryAdd(root, finalLabelCounter.GetAndIncrement());
            }

            // Now apply the final labels in parallel
            Parallel.For(0, depth, new ParallelOptions { MaxDegreeOfParallelism = maxThreads, CancellationToken = cancellationToken }, z =>
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int label = globalLabels[x, y, z];
                        if (label > 0)
                        {
                            int root = unionFind.Find(label);
                            globalLabels[x, y, z] = finalLabelMap[root];
                        }
                    }
                }

                // Check for cancellation every slice
                if (z % 10 == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(75 + (z * 20) / depth);
                }
            });

            // Create deferred particle analysis function
            Func<List<Particle>> analyzeParticlesFunc = () =>
            {
                Logger.Log("[ParticleSeparator] Starting on-demand particle analysis for large volume");
                List<Particle> particles = new List<Particle>();

                try
                {
                    // Find all unique labels in the volume
                    ConcurrentDictionary<int, byte> uniqueLabelsDict = new ConcurrentDictionary<int, byte>();

                    Parallel.For(0, depth, new ParallelOptions { MaxDegreeOfParallelism = maxThreads }, z =>
                    {
                        HashSet<int> localLabels = new HashSet<int>();
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                int label = globalLabels[x, y, z];
                                if (label > 0)
                                {
                                    localLabels.Add(label);
                                }
                            }
                        }

                        foreach (int label in localLabels)
                        {
                            uniqueLabelsDict.TryAdd(label, 1);
                        }
                    });

                    var uniqueLabels = uniqueLabelsDict.Keys.ToList();
                    Logger.Log($"[ParticleSeparator] Found {uniqueLabels.Count} unique particles in large volume");

                    // Process in batches to handle very large numbers of particles
                    for (int batchStart = 0; batchStart < uniqueLabels.Count; batchStart += PARTICLE_BATCH_SIZE)
                    {
                        int batchEnd = Math.Min(batchStart + PARTICLE_BATCH_SIZE, uniqueLabels.Count);
                        Logger.Log($"[ParticleSeparator] Processing large volume particle batch {batchStart}-{batchEnd} of {uniqueLabels.Count}");

                        var batchLabels = uniqueLabels.GetRange(batchStart, batchEnd - batchStart);

                        // Prepare counters and accumulators for this batch
                        ConcurrentDictionary<int, int> voxelCounts = new ConcurrentDictionary<int, int>();
                        ConcurrentDictionary<int, (int sumX, int sumY, int sumZ)> centers =
                            new ConcurrentDictionary<int, (int, int, int)>();
                        ConcurrentDictionary<int, (int minX, int minY, int minZ, int maxX, int maxY, int maxZ)> bounds =
                            new ConcurrentDictionary<int, (int, int, int, int, int, int)>();

                        // Initialize dictionaries for this batch
                        foreach (int label in batchLabels)
                        {
                            voxelCounts[label] = 0;
                            centers[label] = (0, 0, 0);
                            bounds[label] = (int.MaxValue, int.MaxValue, int.MaxValue, 0, 0, 0);
                        }

                        // Process the volume in chunks for better cache coherence
                        int slabSize = 16; // Process 16 slices at a time
                        for (int slabStart = 0; slabStart < depth; slabStart += slabSize)
                        {
                            int slabEnd = Math.Min(slabStart + slabSize, depth);

                            Parallel.For(slabStart, slabEnd, new ParallelOptions { MaxDegreeOfParallelism = maxThreads }, z =>
                            {
                                for (int y = 0; y < height; y++)
                                {
                                    for (int x = 0; x < width; x++)
                                    {
                                        int label = globalLabels[x, y, z];
                                        if (label > 0 && batchLabels.Contains(label))
                                        {
                                            // Update voxel count
                                            voxelCounts.AddOrUpdate(
                                                label,
                                                1,
                                                (key, oldCount) => oldCount + 1);

                                            // Update center coordinates
                                            centers.AddOrUpdate(
                                                label,
                                                (x, y, z),
                                                (key, oldSum) => (oldSum.sumX + x, oldSum.sumY + y, oldSum.sumZ + z));

                                            // Update bounding box
                                            bounds.AddOrUpdate(
                                                label,
                                                (x, y, z, x, y, z),
                                                (key, oldBounds) => (
                                                    Math.Min(oldBounds.minX, x),
                                                    Math.Min(oldBounds.minY, y),
                                                    Math.Min(oldBounds.minZ, z),
                                                    Math.Max(oldBounds.maxX, x),
                                                    Math.Max(oldBounds.maxY, y),
                                                    Math.Max(oldBounds.maxZ, z)
                                                ));
                                        }
                                    }
                                }
                            });
                        }

                        // Create particles for this batch
                        foreach (int label in batchLabels)
                        {
                            int voxelCount = voxelCounts[label];
                            var center = centers[label];
                            var bound = bounds[label];

                            // Filter out small particles if conservative approach is selected
                            if (conservative && voxelCount < 20)
                                continue;

                            // Calculate volumes
                            double pixelVolume = mainForm.pixelSize * mainForm.pixelSize * mainForm.pixelSize;
                            double volumeMicrometers = voxelCount * pixelVolume * 1e18; // m³ to µm³
                            double volumeMillimeters = voxelCount * pixelVolume * 1e9;  // m³ to mm³

                            Particle particle = new Particle
                            {
                                Id = label,
                                VoxelCount = voxelCount,
                                VolumeMicrometers = volumeMicrometers,
                                VolumeMillimeters = volumeMillimeters,
                                Center = voxelCount > 0 ? new Point3D
                                {
                                    X = center.sumX / voxelCount,
                                    Y = center.sumY / voxelCount,
                                    Z = center.sumZ / voxelCount
                                } : new Point3D(),
                                Bounds = new BoundingBox
                                {
                                    MinX = bound.minX,
                                    MinY = bound.minY,
                                    MinZ = bound.minZ,
                                    MaxX = bound.maxX,
                                    MaxY = bound.maxY,
                                    MaxZ = bound.maxZ
                                }
                            };

                            particles.Add(particle);
                        }

                        // Clean up batch resources
                        voxelCounts = null;
                        centers = null;
                        bounds = null;
                        GC.Collect();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ParticleSeparator] Error during large volume particle analysis: {ex.Message}");
                }

                Logger.Log($"[ParticleSeparator] Large volume particle analysis completed: {particles.Count} particles");
                return particles;
            };

            // Create the result with deferred particle analysis
            SeparationResult result = new SeparationResult
            {
                LabelVolume = globalLabels,
                Particles = null, // Will be computed on demand
                CurrentSlice = depth / 2, // Start with middle slice for viewing
                Is3D = true
            };

            // Set up the deferred processing
            result.SetParticleAnalysisFunction(analyzeParticlesFunc);

            progress?.Report(100);
            Logger.Log($"[Separate3DLarge] Created result with deferred particle analysis");

            return result;
        }

        // Optimized thread-safe UnionFind implementation
        private class UnionFind
        {
            private readonly ConcurrentDictionary<int, int> parent;
            private readonly ConcurrentDictionary<int, int> rank;
            private readonly object syncLock = new object();

            public UnionFind()
            {
                parent = new ConcurrentDictionary<int, int>();
                rank = new ConcurrentDictionary<int, int>();
            }

            public void MakeSet(int x)
            {
                parent.TryAdd(x, x);
                rank.TryAdd(x, 0);
            }

            public int Find(int x)
            {
                if (!parent.TryGetValue(x, out int p))
                {
                    lock (syncLock)
                    {
                        // Double-check after acquiring lock
                        if (!parent.ContainsKey(x))
                        {
                            MakeSet(x);
                        }
                        return x;
                    }
                }

                if (p != x)
                {
                    // Path compression with atomic update
                    int root = Find(p);
                    parent.TryUpdate(x, root, p);
                    return root;
                }

                return x;
            }

            public void Union(int x, int y)
            {
                int rootX = Find(x);
                int rootY = Find(y);

                if (rootX == rootY)
                    return;

                // Need to synchronize to avoid race conditions
                lock (syncLock)
                {
                    // Re-check after lock
                    rootX = Find(x);
                    rootY = Find(y);

                    if (rootX == rootY)
                        return;

                    // Get current ranks
                    int rankX = rank.GetOrAdd(rootX, 0);
                    int rankY = rank.GetOrAdd(rootY, 0);

                    // Union by rank
                    if (rankX < rankY)
                    {
                        parent[rootX] = rootY;
                    }
                    else if (rankX > rankY)
                    {
                        parent[rootY] = rootX;
                    }
                    else
                    {
                        parent[rootY] = rootX;
                        rank[rootX] = rankX + 1;
                    }
                }
            }
        }

        // Thread-safe atomic counter for consistent label assignment
        private class AtomicCounter
        {
            private int value;

            public AtomicCounter(int initialValue)
            {
                value = initialValue;
            }

            public int GetAndIncrement()
            {
                return Interlocked.Increment(ref value) - 1;
            }

            public int Value => value;
        }

        // Optimized CPU implementation of 2D connected component labeling
        private int[,] LabelConnectedComponents2DCpu(byte[,] data, CancellationToken cancellationToken)
        {
            int width = data.GetLength(0);
            int height = data.GetLength(1);

            int[,] labels = new int[width, height];
            UnionFind unionFind = new UnionFind();
            int nextLabel = 1;

            // First pass: assign initial labels in parallel chunks
            int chunkSize = Math.Max(1, height / maxThreads);

            Parallel.For(0, (height + chunkSize - 1) / chunkSize, new ParallelOptions { MaxDegreeOfParallelism = maxThreads, CancellationToken = cancellationToken }, chunk =>
            {
                int startY = chunk * chunkSize;
                int endY = Math.Min(startY + chunkSize, height);

                for (int y = startY; y < endY; y++)
                {
                    if (y % 100 == 0) cancellationToken.ThrowIfCancellationRequested();

                    for (int x = 0; x < width; x++)
                    {
                        if (data[x, y] == 0)
                            continue;

                        // Check neighbors (8-connectivity)
                        List<int> neighbors = new List<int>(4); // Pre-allocate for efficiency

                        // Check 4-connected neighbors
                        if (x > 0 && data[x - 1, y] != 0)
                            neighbors.Add(labels[x - 1, y]);

                        if (y > 0 && data[x, y - 1] != 0)
                            neighbors.Add(labels[x, y - 1]);

                        // Add diagonal neighbors for 8-connectivity
                        if (x > 0 && y > 0 && data[x - 1, y - 1] != 0)
                            neighbors.Add(labels[x - 1, y - 1]);

                        if (x < width - 1 && y > 0 && data[x + 1, y - 1] != 0)
                            neighbors.Add(labels[x + 1, y - 1]);

                        neighbors.RemoveAll(n => n == 0);

                        if (neighbors.Count == 0)
                        {
                            // New component
                            labels[x, y] = Interlocked.Increment(ref nextLabel) - 1;
                            unionFind.MakeSet(labels[x, y]);
                        }
                        else
                        {
                            // Use the minimum neighbor label
                            labels[x, y] = neighbors.Min();

                            // Union all neighboring labels
                            for (int i = 0; i < neighbors.Count; i++)
                            {
                                for (int j = i + 1; j < neighbors.Count; j++)
                                {
                                    unionFind.Union(neighbors[i], neighbors[j]);
                                }
                            }
                        }
                    }
                }
            });

            // Second pass: apply union-find equivalences
            ConcurrentDictionary<int, int> finalLabelMap = new ConcurrentDictionary<int, int>();
            AtomicCounter finalLabelCounter = new AtomicCounter(1);

            // First collect all roots
            ConcurrentBag<int> roots = new ConcurrentBag<int>();

            Parallel.For(0, height, new ParallelOptions { MaxDegreeOfParallelism = maxThreads, CancellationToken = cancellationToken }, y =>
            {
                var localRoots = new HashSet<int>();

                for (int x = 0; x < width; x++)
                {
                    if (labels[x, y] > 0)
                    {
                        int root = unionFind.Find(labels[x, y]);
                        localRoots.Add(root);
                    }
                }

                foreach (var root in localRoots)
                {
                    roots.Add(root);
                }

                if (y % 100 == 0) cancellationToken.ThrowIfCancellationRequested();
            });

            // Create final label map (must be sequential for consistent labels)
            foreach (var root in new HashSet<int>(roots))
            {
                finalLabelMap.TryAdd(root, finalLabelCounter.GetAndIncrement());
            }

            // Apply final labels in parallel
            Parallel.For(0, height, new ParallelOptions { MaxDegreeOfParallelism = maxThreads, CancellationToken = cancellationToken }, y =>
            {
                if (y % 100 == 0) cancellationToken.ThrowIfCancellationRequested();

                for (int x = 0; x < width; x++)
                {
                    if (labels[x, y] > 0)
                    {
                        int root = unionFind.Find(labels[x, y]);
                        labels[x, y] = finalLabelMap[root];
                    }
                }
            });

            return labels;
        }

        // Optimized CPU implementation of 3D connected component labeling
        private int[,,] LabelConnectedComponents3DCpu(byte[,,] data, CancellationToken cancellationToken)
        {
            int width = data.GetLength(0);
            int height = data.GetLength(1);
            int depth = data.GetLength(2);

            int[,,] labels = new int[width, height, depth];
            UnionFind unionFind = new UnionFind();
            AtomicCounter labelCounter = new AtomicCounter(1);

            // First pass: assign initial labels and record equivalences in parallel by z-slices
            Parallel.For(0, depth, new ParallelOptions { MaxDegreeOfParallelism = maxThreads, CancellationToken = cancellationToken }, z =>
            {
                if (z % 10 == 0) cancellationToken.ThrowIfCancellationRequested();

                // Process each slice sequentially for label consistency
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (data[x, y, z] == 0)
                            continue;

                        // Check 6-connected neighbors - using array for performance
                        int[] neighbors = new int[3]; // Max 3 neighbors (x-1, y-1, z-1)
                        int neighborCount = 0;

                        if (x > 0 && data[x - 1, y, z] != 0)
                            neighbors[neighborCount++] = labels[x - 1, y, z];

                        if (y > 0 && data[x, y - 1, z] != 0)
                            neighbors[neighborCount++] = labels[x, y - 1, z];

                        if (z > 0 && data[x, y, z - 1] != 0)
                            neighbors[neighborCount++] = labels[x, y, z - 1];

                        // Filter zero labels
                        int validNeighbors = 0;
                        for (int i = 0; i < neighborCount; i++)
                        {
                            if (neighbors[i] > 0)
                                neighbors[validNeighbors++] = neighbors[i];
                        }

                        if (validNeighbors == 0)
                        {
                            // New component
                            labels[x, y, z] = labelCounter.GetAndIncrement();
                            unionFind.MakeSet(labels[x, y, z]);
                        }
                        else
                        {
                            // Find minimum non-zero label among neighbors
                            int minLabel = int.MaxValue;
                            for (int i = 0; i < validNeighbors; i++)
                            {
                                minLabel = Math.Min(minLabel, neighbors[i]);
                            }

                            labels[x, y, z] = minLabel;

                            // Union all neighboring labels
                            for (int i = 0; i < validNeighbors; i++)
                            {
                                if (neighbors[i] != minLabel)
                                {
                                    unionFind.Union(minLabel, neighbors[i]);
                                }
                            }
                        }
                    }
                }
            });

            // Second pass: collect all roots using parallel processing
            ConcurrentBag<int> roots = new ConcurrentBag<int>();

            Parallel.For(0, depth, new ParallelOptions { MaxDegreeOfParallelism = maxThreads, CancellationToken = cancellationToken }, z =>
            {
                if (z % 10 == 0) cancellationToken.ThrowIfCancellationRequested();

                var localRoots = new HashSet<int>();

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (labels[x, y, z] > 0)
                        {
                            int root = unionFind.Find(labels[x, y, z]);
                            localRoots.Add(root);
                        }
                    }
                }

                foreach (var root in localRoots)
                {
                    roots.Add(root);
                }
            });

            // Create final label map (must be sequential for consistent labels)
            ConcurrentDictionary<int, int> finalLabelMap = new ConcurrentDictionary<int, int>();
            AtomicCounter finalLabelCounter = new AtomicCounter(1);

            foreach (var root in new HashSet<int>(roots))
            {
                finalLabelMap.TryAdd(root, finalLabelCounter.GetAndIncrement());
            }

            // Third pass: apply final labels in parallel
            Parallel.For(0, depth, new ParallelOptions { MaxDegreeOfParallelism = maxThreads, CancellationToken = cancellationToken }, z =>
            {
                if (z % 10 == 0) cancellationToken.ThrowIfCancellationRequested();

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (labels[x, y, z] > 0)
                        {
                            int root = unionFind.Find(labels[x, y, z]);
                            labels[x, y, z] = finalLabelMap[root];
                        }
                    }
                }
            });

            return labels;
        }

        // Optimized GPU implementation for 2D connected component labeling
        private int[,] LabelConnectedComponents2DGpu(byte[,] data)
        {
            if (!gpuInitialized || accelerator == null)
            {
                throw new InvalidOperationException("GPU acceleration not available. Initialize GPU first.");
            }

            int width = data.GetLength(0);
            int height = data.GetLength(1);
            int totalSize = width * height;

            try
            {
                // Optimize memory by using pinned arrays and minimizing transfers
                using (var inputBuffer = accelerator.Allocate1D<byte>(totalSize))
                using (var labelsBuffer = accelerator.Allocate1D<int>(totalSize))
                using (var changesBuffer = accelerator.Allocate1D<int>(1))
                {
                    // Flatten the 2D array - this is faster than working with 2D views
                    byte[] flatData = new byte[totalSize];
                    Parallel.For(0, height, y =>
                    {
                        for (int x = 0; x < width; x++)
                        {
                            flatData[y * width + x] = data[x, y];
                        }
                    });

                    // Copy data to GPU
                    inputBuffer.CopyFromCPU(flatData);

                    // Initialize labels kernel - directly mark foreground voxels with temporary labels
                    var initKernel = accelerator.LoadAutoGroupedStreamKernel<
                        Index1D,           // index
                        ArrayView<byte>,   // input
                        ArrayView<int>,    // output
                        int                // width
                    >(
                        (Index1D index, ArrayView<byte> input, ArrayView<int> output, int w) =>
                        {
                            // Bounds check
                            if (index >= input.Length)
                                return;

                            // Binary classification - foreground vs background
                            output[index] = input[index] > 0 ? int.MaxValue : 0;
                        }
                    );

                    // Execute initialization kernel
                    initKernel(totalSize, inputBuffer.View, labelsBuffer.View, width);
                    accelerator.Synchronize();

                    // Raster scan label propagation kernel (more GPU-friendly)
                    var propagateKernel = accelerator.LoadAutoGroupedStreamKernel<
                        Index1D,           // index
                        ArrayView<int>,    // labels
                        ArrayView<int>,    // changes
                        int,               // width
                        int                // height
                    >(
                        (Index1D index, ArrayView<int> labels, ArrayView<int> changes, int w, int h) =>
                        {
                            if (index >= labels.Length)
                                return;

                            // Skip background pixels
                            if (labels[index] == 0)
                                return;

                            // Convert 1D index to 2D coordinates
                            int x = index % w;
                            int y = (int)(index / w);

                            // Find minimum label among 8-connected neighbors
                            int currentLabel = labels[index];
                            int minLabel = currentLabel;
                            bool foundSmaller = false;

                            // Check 8-connected neighbors
                            for (int dy = -1; dy <= 1; dy++)
                            {
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    if (dx == 0 && dy == 0)
                                        continue;

                                    int nx = x + dx;
                                    int ny = y + dy;

                                    if (nx >= 0 && nx < w && ny >= 0 && ny < h)
                                    {
                                        int neighborIdx = ny * w + nx;
                                        int neighborLabel = labels[neighborIdx];

                                        if (neighborLabel > 0 && neighborLabel < minLabel)
                                        {
                                            minLabel = neighborLabel;
                                            foundSmaller = true;
                                        }
                                    }
                                }
                            }

                            if (foundSmaller)
                            {
                                labels[index] = minLabel;
                                ILGPU.Atomic.Add(ref changes[0], 1);
                            }
                        }
                    );

                    // Alternate forward/backward passes for faster convergence
                    changesBuffer.MemSetToZero();
                    int iteration = 0;
                    bool hasChanges = true;
                    int[] changesArray = new int[1];
                    int maxIterations = Math.Min((width + height) * 2, MAX_ITERATIONS);

                    // Two-pass algorithm with multiple iterations
                    while (hasChanges && iteration < maxIterations)
                    {
                        // Reset changes counter
                        changesBuffer.MemSetToZero();

                        // Execute propagation kernel
                        propagateKernel(totalSize, labelsBuffer.View, changesBuffer.View, width, height);
                        accelerator.Synchronize();

                        // Check if any changes occurred
                        changesBuffer.CopyToCPU(changesArray);
                        hasChanges = changesArray[0] > 0;
                        iteration++;
                    }

                    // Relabeling kernel - compress label space
                    var relabelKernel = accelerator.LoadAutoGroupedStreamKernel<
                        Index1D,           // index
                        ArrayView<int>,    // labels
                        int,               // start value
                        int                // width
                    >(
                        (Index1D index, ArrayView<int> labels, int startValue, int w) =>
                        {
                            if (index >= labels.Length)
                                return;

                            // Skip background pixels
                            if (labels[index] == 0)
                                return;

                            // Renumber with sequential labels starting from startValue
                            // We can't do a full sequential relabeling on GPU, but we can normalize the labels
                            int x = index % w;
                            int y = (int)(index / w);

                            // Use pixel position as a unique ID for each connected component
                            // This assigns a canonical pixel for each component (top-left)
                            int minX = x;
                            int minY = y;
                            bool foundCanonical = false;
                            int label = labels[index];

                            // Look for pixels with same label to find the canonical one
                            for (int scanY = 0; scanY < y; scanY++)
                            {
                                for (int scanX = 0; scanX < w; scanX++)
                                {
                                    int scanIdx = scanY * w + scanX;

                                    if (scanIdx >= labels.Length)
                                        continue;

                                    if (labels[scanIdx] == label)
                                    {
                                        minX = scanX;
                                        minY = scanY;
                                        foundCanonical = true;
                                        break;
                                    }
                                }

                                if (foundCanonical)
                                    break;
                            }

                            // Set a unique identifier based on the canonical pixel position
                            labels[index] = minY * w + minX + startValue;
                        }
                    );

                    // Relabel connected components for consistent labels
                    relabelKernel(totalSize, labelsBuffer.View, 1, width);
                    accelerator.Synchronize();

                    // Get final results
                    int[] labelsArray = new int[totalSize];
                    labelsBuffer.CopyToCPU(labelsArray);

                    // Convert back to 2D array
                    int[,] result = new int[width, height];
                    Parallel.For(0, height, y =>
                    {
                        for (int x = 0; x < width; x++)
                        {
                            result[x, y] = labelsArray[y * width + x];
                        }
                    });

                    return result;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ParticleSeparator] GPU processing error: {ex.Message}");
                throw; // Rethrow so we can handle it at a higher level
            }
        }

        // Optimized GPU implementation for 3D connected component labeling
        private int[,,] LabelConnectedComponents3DGpu(byte[,,] data, CancellationToken cancellationToken)
        {
            if (!gpuInitialized || accelerator == null)
            {
                throw new InvalidOperationException("GPU acceleration not available. Initialize GPU first.");
            }

            int width = data.GetLength(0);
            int height = data.GetLength(1);
            int depth = data.GetLength(2);
            long totalVoxels = (long)width * height * depth;

            // Check for very large volumes
            long maxGpuVoxels = GetMaxGpuVoxels();
            if (totalVoxels > maxGpuVoxels)
            {
                Logger.Log($"[ParticleSeparator] Volume too large for direct GPU processing: {totalVoxels} voxels. Using optimized chunked GPU processing.");
                return ProcessLargeVolumeWithGPUChunks(data, cancellationToken);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Convert to 1D array for GPU (flattened for better performance)
                byte[] flatData = new byte[totalVoxels];
                int strideXY = width * height;

                // Parallelize the flattening operation
                Parallel.For(0, depth, new ParallelOptions { MaxDegreeOfParallelism = maxThreads, CancellationToken = cancellationToken }, z =>
                {
                    int zOffset = z * strideXY;
                    for (int y = 0; y < height; y++)
                    {
                        int yzOffset = zOffset + y * width;
                        for (int x = 0; x < width; x++)
                        {
                            flatData[yzOffset + x] = data[x, y, z];
                        }
                    }
                });

                cancellationToken.ThrowIfCancellationRequested();

                // Use managed memory for better efficiency
                using (var inputBuffer = accelerator.Allocate1D<byte>(flatData.Length))
                using (var labelsBuffer = accelerator.Allocate1D<int>(flatData.Length))
                using (var changesBuffer = accelerator.Allocate1D<int>(1))
                {
                    // Copy data to GPU
                    inputBuffer.CopyFromCPU(flatData);

                    // Zero-initialize the changes buffer
                    changesBuffer.MemSetToZero();

                    cancellationToken.ThrowIfCancellationRequested();

                    // Optimized initialization kernel
                    var initKernel = accelerator.LoadAutoGroupedStreamKernel<
                        Index1D,           // index
                        ArrayView<byte>,   // input
                        ArrayView<int>     // output
                    >(
                        (Index1D index, ArrayView<byte> input, ArrayView<int> output) =>
                        {
                            if (index < input.Length)
                            {
                                // Mark foreground voxels with a special value
                                output[index] = input[index] > 0 ? int.MaxValue : 0;
                            }
                        }
                    );

                    // Execute init kernel
                    initKernel(flatData.Length, inputBuffer.View, labelsBuffer.View);
                    accelerator.Synchronize();

                    cancellationToken.ThrowIfCancellationRequested();

                    // Custom label initialization kernel - assign unique label to first pixel
                    var seedLabelKernel = accelerator.LoadAutoGroupedStreamKernel<
                        Index1D,
                        ArrayView<int>,
                        int,
                        int,
                        int
                    >(
                        (Index1D index, ArrayView<int> labels, int w, int h, int d) =>
                        {
                            int x = index % w;
                            int y = (index / w) % h;
                            int z = (int)(index / (w * h));

                            // First-pass: seed with sequential labels based on index
                            if (labels[index] == int.MaxValue)
                            {
                                // Generate a unique label based on position
                                labels[index] = index + 1;
                            }
                        }
                    );

                    // Apply seed labels
                    seedLabelKernel(flatData.Length, labelsBuffer.View, width, height, depth);
                    accelerator.Synchronize();

                    cancellationToken.ThrowIfCancellationRequested();

                    // Optimized kernel for 3D propagation
                    var propagateKernel = accelerator.LoadAutoGroupedStreamKernel<
                        Index1D,           // index
                        ArrayView<int>,    // labels
                        ArrayView<int>,    // changes
                        int,               // width
                        int,               // height
                        int                // depth
                    >(
                        (Index1D index, ArrayView<int> labels, ArrayView<int> changes, int w, int h, int d) =>
                        {
                            if (index >= labels.Length)
                                return;

                            // Skip background voxels
                            if (labels[index] == 0)
                                return;

                            // Convert 1D index to 3D coordinates
                            int x = index % w;
                            int y = (index / w) % h;
                            int z = (int)(index / (w * h));

                            int currentLabel = labels[index];
                            int minLabel = currentLabel;
                            bool foundSmaller = false;

                            // Check 6-connected neighbors with optimized index calculations
                            int localStride = w * h;

                            // X-1
                            if (x > 0)
                            {
                                int neighborIdx = z * localStride + y * w + (x - 1);
                                int neighborLabel = labels[neighborIdx];

                                if (neighborLabel > 0 && neighborLabel < minLabel)
                                {
                                    minLabel = neighborLabel;
                                    foundSmaller = true;
                                }
                            }

                            // X+1
                            if (x < w - 1)
                            {
                                int neighborIdx = z * localStride + y * w + (x + 1);
                                int neighborLabel = labels[neighborIdx];

                                if (neighborLabel > 0 && neighborLabel < minLabel)
                                {
                                    minLabel = neighborLabel;
                                    foundSmaller = true;
                                }
                            }

                            // Y-1
                            if (y > 0)
                            {
                                int neighborIdx = z * localStride + (y - 1) * w + x;
                                int neighborLabel = labels[neighborIdx];

                                if (neighborLabel > 0 && neighborLabel < minLabel)
                                {
                                    minLabel = neighborLabel;
                                    foundSmaller = true;
                                }
                            }

                            // Y+1
                            if (y < h - 1)
                            {
                                int neighborIdx = z * localStride + (y + 1) * w + x;
                                int neighborLabel = labels[neighborIdx];

                                if (neighborLabel > 0 && neighborLabel < minLabel)
                                {
                                    minLabel = neighborLabel;
                                    foundSmaller = true;
                                }
                            }

                            // Z-1
                            if (z > 0)
                            {
                                int neighborIdx = (z - 1) * localStride + y * w + x;
                                int neighborLabel = labels[neighborIdx];

                                if (neighborLabel > 0 && neighborLabel < minLabel)
                                {
                                    minLabel = neighborLabel;
                                    foundSmaller = true;
                                }
                            }

                            // Z+1
                            if (z < d - 1)
                            {
                                int neighborIdx = (z + 1) * localStride + y * w + x;
                                int neighborLabel = labels[neighborIdx];

                                if (neighborLabel > 0 && neighborLabel < minLabel)
                                {
                                    minLabel = neighborLabel;
                                    foundSmaller = true;
                                }
                            }

                            if (foundSmaller)
                            {
                                labels[index] = minLabel;
                                ILGPU.Atomic.Add(ref changes[0], 1);
                            }
                        }
                    );

                    cancellationToken.ThrowIfCancellationRequested();

                    // Iteratively propagate labels with early termination
                    bool hasChanges = true;
                    int[] changesArray = new int[1];
                    int maxIterations = Math.Min(width + height + depth, MAX_ITERATIONS);

                    for (int iter = 0; iter < maxIterations && hasChanges; iter++)
                    {
                        if (iter % 10 == 0) cancellationToken.ThrowIfCancellationRequested();

                        // Reset changes counter
                        changesBuffer.MemSetToZero();

                        // Execute propagation kernel
                        propagateKernel(flatData.Length, labelsBuffer.View, changesBuffer.View, width, height, depth);
                        accelerator.Synchronize();

                        // Check if any changes occurred
                        changesBuffer.CopyToCPU(changesArray);
                        hasChanges = changesArray[0] > 0;

                        if (iter % 20 == 0 && hasChanges)
                        {
                            Logger.Log($"[GPU] Label propagation iteration {iter}, changes: {changesArray[0]}");
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    // Get final results
                    int[] labelsArray = new int[flatData.Length];
                    labelsBuffer.CopyToCPU(labelsArray);

                    // Convert back to 3D array in parallel
                    int[,,] result = new int[width, height, depth];

                    // Process in chunks to reduce GC pressure
                    int chunkSize = 10; // Process 10 slices at a time
                    Parallel.For(0, depth, new ParallelOptions { MaxDegreeOfParallelism = maxThreads, CancellationToken = cancellationToken }, z =>
                    {
                        int zOffset = z * strideXY;
                        for (int y = 0; y < height; y++)
                        {
                            int yzOffset = zOffset + y * width;
                            for (int x = 0; x < width; x++)
                            {
                                result[x, y, z] = labelsArray[yzOffset + x];
                            }
                        }

                        if (z % 10 == 0) cancellationToken.ThrowIfCancellationRequested();
                    });

                    // Free memory
                    flatData = null;
                    labelsArray = null;
                    GC.Collect();

                    return result;
                }
            }
            catch (OperationCanceledException)
            {
                throw; // Propagate cancellation
            }
            catch (Exception ex)
            {
                Logger.Log($"[ParticleSeparator] GPU processing error: {ex.Message}");

                // Clear out-of-memory error with more helpful message
                if (ex.Message.Contains("out of memory") || ex is OutOfMemoryException)
                {
                    throw new OutOfMemoryException($"GPU memory exceeded while processing volume of {totalVoxels} voxels. Try using CPU mode instead.");
                }

                throw; // Rethrow to ensure the caller knows there was a problem
            }
        }

        // Optimized method for GPU memory estimation
        private long GetMaxGpuVoxels()
        {
            // If we have memory information from the accelerator, use it
            if (accelerator != null && accelerator.MemorySize > 0)
            {
                // Calculate max voxels based on 60% of GPU memory
                // Each voxel needs 5 bytes (1 for input, 4 for label)
                long maxVoxels = (long)(accelerator.MemorySize * 0.6) / 5;
                return Math.Min(maxVoxels, 300_000_000); // Still cap at 300M voxels max
            }

            // Default values based on typical GPU sizes
            return 200_000_000; // 200 million voxels as a safer default
        }

        // Process large volumes with GPU-accelerated chunks
        private int[,,] ProcessLargeVolumeWithGPUChunks(byte[,,] data, CancellationToken cancellationToken)
        {
            int width = data.GetLength(0);
            int height = data.GetLength(1);
            int depth = data.GetLength(2);

            // Use an optimized chunk size for better memory locality
            int chunkSize = CHUNK_SIZE;
            int numChunksX = (width + chunkSize - 1) / chunkSize;
            int numChunksY = (height + chunkSize - 1) / chunkSize;
            int numChunksZ = (depth + chunkSize - 1) / chunkSize;
            int totalChunks = numChunksX * numChunksY * numChunksZ;

            Logger.Log($"[ProcessLargeVolumeWithGPUChunks] Processing volume in {numChunksX}x{numChunksY}x{numChunksZ} chunks");

            // Create the final label volume
            int[,,] globalLabels = new int[width, height, depth];
            UnionFind unionFind = new UnionFind();
            int nextGlobalLabel = 1;

            // Use concurrent dictionary for thread safety
            ConcurrentDictionary<(int chunkX, int chunkY, int chunkZ, int localLabel), int> labelMap =
                new ConcurrentDictionary<(int, int, int, int), int>();

            int chunksDone = 0;

            // Create a thread-safe counter for label assignment
            var labelCounter = new AtomicCounter(1);

            // First pass: process each chunk in parallel
            Parallel.For(0, numChunksZ, new ParallelOptions { MaxDegreeOfParallelism = maxThreads, CancellationToken = cancellationToken }, chunkZ =>
            {
                for (int chunkY = 0; chunkY < numChunksY; chunkY++)
                {
                    for (int chunkX = 0; chunkX < numChunksX; chunkX++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // Determine chunk bounds
                        int startX = chunkX * chunkSize;
                        int startY = chunkY * chunkSize;
                        int startZ = chunkZ * chunkSize;
                        int endX = Math.Min(startX + chunkSize, width);
                        int endY = Math.Min(startY + chunkSize, height);
                        int endZ = Math.Min(startZ + chunkSize, depth);
                        int chunkWidth = endX - startX;
                        int chunkHeight = endY - startY;
                        int chunkDepth = endZ - startZ;

                        // Skip empty chunks
                        bool hasData = false;
                        for (int z = 0; z < chunkDepth && !hasData; z++)
                        {
                            for (int y = 0; y < chunkHeight && !hasData; y++)
                            {
                                for (int x = 0; x < chunkWidth && !hasData; x++)
                                {
                                    if (data[startX + x, startY + y, startZ + z] > 0)
                                    {
                                        hasData = true;
                                        break;
                                    }
                                }
                            }
                        }

                        if (!hasData)
                            continue;

                        // Extract chunk
                        byte[,,] chunkData = new byte[chunkWidth, chunkHeight, chunkDepth];
                        for (int z = 0; z < chunkDepth; z++)
                        {
                            for (int y = 0; y < chunkHeight; y++)
                            {
                                for (int x = 0; x < chunkWidth; x++)
                                {
                                    chunkData[x, y, z] = data[startX + x, startY + y, startZ + z];
                                }
                            }
                        }

                        // Check for cancellation
                        cancellationToken.ThrowIfCancellationRequested();

                        // Process chunk - use CPU as we're already parallelizing chunks
                        int[,,] chunkLabels = LabelConnectedComponents3DCpu(chunkData, cancellationToken);

                        // Map chunk labels to global labels
                        HashSet<int> chunkUniqueLabels = new HashSet<int>();
                        for (int z = 0; z < chunkDepth; z++)
                        {
                            for (int y = 0; y < chunkHeight; y++)
                            {
                                for (int x = 0; x < chunkWidth; x++)
                                {
                                    if (chunkLabels[x, y, z] > 0)
                                    {
                                        chunkUniqueLabels.Add(chunkLabels[x, y, z]);
                                    }
                                }
                            }
                        }

                        foreach (int localLabel in chunkUniqueLabels)
                        {
                            int newGlobalLabel = labelCounter.GetAndIncrement();
                            labelMap.TryAdd((chunkX, chunkY, chunkZ, localLabel), newGlobalLabel);
                            unionFind.MakeSet(newGlobalLabel);
                        }

                        // Apply global labels
                        for (int z = 0; z < chunkDepth; z++)
                        {
                            for (int y = 0; y < chunkHeight; y++)
                            {
                                for (int x = 0; x < chunkWidth; x++)
                                {
                                    int localLabel = chunkLabels[x, y, z];
                                    int globalX = startX + x;
                                    int globalY = startY + y;
                                    int globalZ = startZ + z;

                                    if (localLabel > 0)
                                    {
                                        globalLabels[globalX, globalY, globalZ] =
                                            labelMap[(chunkX, chunkY, chunkZ, localLabel)];
                                    }
                                }
                            }
                        }

                        Interlocked.Increment(ref chunksDone);
                    }
                }
            });

            // Second pass: merge connected components across chunk boundaries
            Logger.Log("[ProcessLargeVolumeWithGPUChunks] Connecting components across chunk boundaries");

            // Process in slices for better memory locality
            Parallel.For(0, depth, new ParallelOptions { MaxDegreeOfParallelism = maxThreads, CancellationToken = cancellationToken }, z =>
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int label = globalLabels[x, y, z];
                        if (label > 0)
                        {
                            // Check 6-connected neighbors
                            if (x > 0)
                            {
                                int neighborLabel = globalLabels[x - 1, y, z];
                                if (neighborLabel > 0 && neighborLabel != label)
                                {
                                    unionFind.Union(label, neighborLabel);
                                }
                            }

                            if (y > 0)
                            {
                                int neighborLabel = globalLabels[x, y - 1, z];
                                if (neighborLabel > 0 && neighborLabel != label)
                                {
                                    unionFind.Union(label, neighborLabel);
                                }
                            }

                            if (z > 0)
                            {
                                int neighborLabel = globalLabels[x, y, z - 1];
                                if (neighborLabel > 0 && neighborLabel != label)
                                {
                                    unionFind.Union(label, neighborLabel);
                                }
                            }
                        }
                    }
                }

                // Check for cancellation every slice
                if (z % 10 == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            });

            // Third pass: apply final labels with a parallel approach
            ConcurrentDictionary<int, int> finalLabelMap = new ConcurrentDictionary<int, int>();
            var finalLabelCounter = new AtomicCounter(1);

            // First collect all roots
            ConcurrentBag<int> roots = new ConcurrentBag<int>();

            Parallel.For(0, depth, new ParallelOptions { MaxDegreeOfParallelism = maxThreads, CancellationToken = cancellationToken }, z =>
            {
                var localRoots = new HashSet<int>();

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int label = globalLabels[x, y, z];
                        if (label > 0)
                        {
                            int root = unionFind.Find(label);
                            localRoots.Add(root);
                        }
                    }
                }

                foreach (var root in localRoots)
                {
                    roots.Add(root);
                }

                if (z % 10 == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            });

            // Create final label map
            foreach (var root in new HashSet<int>(roots))
            {
                finalLabelMap.TryAdd(root, finalLabelCounter.GetAndIncrement());
            }

            // Now apply the final labels in parallel
            Parallel.For(0, depth, new ParallelOptions { MaxDegreeOfParallelism = maxThreads, CancellationToken = cancellationToken }, z =>
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int label = globalLabels[x, y, z];
                        if (label > 0)
                        {
                            int root = unionFind.Find(label);
                            globalLabels[x, y, z] = finalLabelMap[root];
                        }
                    }
                }

                // Check for cancellation every slice
                if (z % 10 == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            });

            Logger.Log($"[ProcessLargeVolumeWithGPUChunks] Component labeling completed with {finalLabelMap.Count} components");
            return globalLabels;
        }

        public void SaveToCsv(string filePath, List<Particle> particles)
        {
            using (StreamWriter writer = new StreamWriter(filePath))
            {
                // Write header
                writer.WriteLine("ID,VoxelCount,VolumeMicrometers,VolumeMillimeters,CenterX,CenterY,CenterZ,MinX,MinY,MinZ,MaxX,MaxY,MaxZ,Width,Height,Depth");

                // Write particles
                foreach (var particle in particles)
                {
                    writer.WriteLine(
                        $"{particle.Id},{particle.VoxelCount},{particle.VolumeMicrometers:F4},{particle.VolumeMillimeters:F6}," +
                        $"{particle.Center.X},{particle.Center.Y},{particle.Center.Z}," +
                        $"{particle.Bounds.MinX},{particle.Bounds.MinY},{particle.Bounds.MinZ}," +
                        $"{particle.Bounds.MaxX},{particle.Bounds.MaxY},{particle.Bounds.MaxZ}," +
                        $"{particle.Bounds.Width},{particle.Bounds.Height},{particle.Bounds.Depth}");
                }
            }
        }

        public void SaveToBinaryFile(string filePath, SeparationResult result)
        {
            using (FileStream fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            using (BinaryWriter writer = new BinaryWriter(fs))
            {
                // Write header
                writer.Write(result.LabelVolume.GetLength(0)); // Width
                writer.Write(result.LabelVolume.GetLength(1)); // Height
                writer.Write(result.LabelVolume.GetLength(2)); // Depth
                writer.Write(result.Particles.Count); // Number of particles
                writer.Write(result.Is3D); // Is 3D volume
                writer.Write(result.CurrentSlice); // Current slice index
                writer.Write(mainForm.pixelSize); // Pixel size for volume calculations

                // Write label volume (usually large, so use Run-Length Encoding)
                WriteRleCompressedVolume(writer, result.LabelVolume);

                // Write particles
                foreach (var particle in result.Particles)
                {
                    writer.Write(particle.Id);
                    writer.Write(particle.VoxelCount);
                    writer.Write(particle.VolumeMicrometers);
                    writer.Write(particle.VolumeMillimeters);

                    writer.Write(particle.Center.X);
                    writer.Write(particle.Center.Y);
                    writer.Write(particle.Center.Z);

                    writer.Write(particle.Bounds.MinX);
                    writer.Write(particle.Bounds.MinY);
                    writer.Write(particle.Bounds.MinZ);
                    writer.Write(particle.Bounds.MaxX);
                    writer.Write(particle.Bounds.MaxY);
                    writer.Write(particle.Bounds.MaxZ);
                }
            }
        }

        private void WriteRleCompressedVolume(BinaryWriter writer, int[,,] volume)
        {
            int width = volume.GetLength(0);
            int height = volume.GetLength(1);
            int depth = volume.GetLength(2);

            // Use a more efficient RLE approach for parallel processing
            // First, process the volume in slabs for better memory locality
            int slabSize = 16; // Process 16 slices at a time
            int numSlabs = (depth + slabSize - 1) / slabSize;

            // Collect RLE data from each slab
            var slabRleData = new List<List<(int value, int count)>>(numSlabs);

            Parallel.For(0, numSlabs, slab =>
            {
                int startZ = slab * slabSize;
                int endZ = Math.Min(startZ + slabSize, depth);

                var slabData = new List<(int value, int count)>();
                int prevValue = int.MinValue; // Use a value that won't occur in the data
                int currentCount = 0;

                // Process the slab
                for (int z = startZ; z < endZ; z++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int value = volume[x, y, z];

                            if (value == prevValue)
                            {
                                currentCount++;
                            }
                            else
                            {
                                if (prevValue != int.MinValue)
                                {
                                    slabData.Add((prevValue, currentCount));
                                }
                                prevValue = value;
                                currentCount = 1;
                            }
                        }
                    }
                }

                // Add the last run
                if (prevValue != int.MinValue)
                {
                    slabData.Add((prevValue, currentCount));
                }

                lock (slabRleData)
                {
                    slabRleData.Add(slabData);
                }
            });

            // Combine all RLE data and write
            List<(int value, int count)> rleData = new List<(int, int)>();

            // Combine all slabs
            foreach (var slabData in slabRleData)
            {
                // Optimize: if last run in rleData matches first run in slabData, combine them
                if (rleData.Count > 0 && slabData.Count > 0)
                {
                    var lastRun = rleData[rleData.Count - 1];
                    var firstRun = slabData[0];

                    if (lastRun.value == firstRun.value)
                    {
                        // Combine runs
                        rleData[rleData.Count - 1] = (lastRun.value, lastRun.count + firstRun.count);

                        // Add the rest of the slab data
                        for (int i = 1; i < slabData.Count; i++)
                        {
                            rleData.Add(slabData[i]);
                        }
                    }
                    else
                    {
                        // Add all slab data
                        rleData.AddRange(slabData);
                    }
                }
                else
                {
                    // Add all slab data
                    rleData.AddRange(slabData);
                }
            }

            // Write the RLE data
            writer.Write(rleData.Count);
            foreach (var (value, count) in rleData)
            {
                writer.Write(value);
                writer.Write(count);
            }
        }

        private static int[,,] ReadRleCompressedVolume(BinaryReader reader, int width, int height, int depth)
        {
            int[,,] volume = new int[width, height, depth];

            // Read RLE data
            int runCount = reader.ReadInt32();
            List<(int value, int count)> rleData = new List<(int, int)>(runCount);

            for (int i = 0; i < runCount; i++)
            {
                int value = reader.ReadInt32();
                int count = reader.ReadInt32();
                rleData.Add((value, count));
            }

            // For large volumes, use chunked decompression to reduce memory pressure
            const int MAX_CHUNK_SIZE = 50_000_000; // 50 million voxels per chunk
            long totalVoxels = (long)width * height * depth;

            if (totalVoxels > MAX_CHUNK_SIZE)
            {
                // Determine optimal chunk size for the z-dimension
                int zChunkSize = Math.Max(1, (int)(MAX_CHUNK_SIZE / ((long)width * height)));
                int numZChunks = (depth + zChunkSize - 1) / zChunkSize;

                Logger.Log($"[ReadRleCompressedVolume] Large volume detected, using {numZChunks} z-chunks of size {zChunkSize}");

                // Calculate the start index for each RLE run
                long[] startIndices = new long[rleData.Count];
                long currentIndex = 0;

                for (int i = 0; i < rleData.Count; i++)
                {
                    startIndices[i] = currentIndex;
                    currentIndex += rleData[i].count;
                }

                // Process each z-chunk separately
                for (int chunkIndex = 0; chunkIndex < numZChunks; chunkIndex++)
                {
                    int startZ = chunkIndex * zChunkSize;
                    int endZ = Math.Min(startZ + zChunkSize, depth);

                    long chunkStartIndex = (long)startZ * width * height;
                    long chunkEndIndex = (long)endZ * width * height;

                    // Find which RLE runs intersect with this chunk
                    List<(int runIndex, long runStartIndex, int value, int count)> chunkRuns = new List<(int, long, int, int)>();

                    for (int runIndex = 0; runIndex < rleData.Count; runIndex++)
                    {
                        long runStartIndex = startIndices[runIndex];
                        long runEndIndex = runStartIndex + rleData[runIndex].count - 1;

                        // Check if this run intersects with the current chunk
                        if (runEndIndex >= chunkStartIndex && runStartIndex < chunkEndIndex)
                        {
                            chunkRuns.Add((runIndex, runStartIndex, rleData[runIndex].value, rleData[runIndex].count));
                        }

                        // If we've gone past the end of this chunk, we can stop checking
                        if (runStartIndex >= chunkEndIndex)
                            break;
                    }

                    // Process runs for this chunk
                    Parallel.ForEach(chunkRuns, run =>
                    {
                        int runIndex = run.runIndex;
                        long runStartIndex = run.runStartIndex;
                        int value = run.value;
                        int count = run.count;

                        // Calculate overlapping region
                        long overlapStart = Math.Max(runStartIndex, chunkStartIndex);
                        long overlapEnd = Math.Min(runStartIndex + count - 1, chunkEndIndex - 1);
                        int overlapCount = (int)(overlapEnd - overlapStart + 1);

                        // Process the overlapping voxels
                        for (int i = 0; i < overlapCount; i++)
                        {
                            long voxelIndex = overlapStart + i;

                            // Convert to volume coordinates
                            int z = (int)(voxelIndex / (width * height));
                            int remainder = (int)(voxelIndex % (width * height));
                            int y = remainder / width;
                            int x = remainder % width;

                            // Set the value in the volume
                            if (x < width && y < height && z < depth)
                            {
                                volume[x, y, z] = value;
                            }
                        }
                    });
                }
            }
            else
            {
                // For smaller volumes, use the original decompression method
                int index = 0;
                foreach (var (value, count) in rleData)
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (index >= width * height * depth)
                            break;

                        int z = index / (width * height);
                        int remainder = index % (width * height);
                        int y = remainder / width;
                        int x = remainder % width;

                        volume[x, y, z] = value;
                        index++;
                    }
                }
            }

            return volume;
        }

        public static SeparationResult LoadFromBinaryFile(string filePath)
        {
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            using (BinaryReader reader = new BinaryReader(fs))
            {
                // Read header
                int width = reader.ReadInt32();
                int height = reader.ReadInt32();
                int depth = reader.ReadInt32();
                int particleCount = reader.ReadInt32();
                bool is3D = reader.ReadBoolean();
                int currentSlice = reader.ReadInt32();
                double pixelSize = reader.ReadDouble();

                Logger.Log($"[LoadFromBinaryFile] Loading separation result: {width}x{height}x{depth}, {particleCount} particles");

                // Read label volume using optimized decompression
                int[,,] labelVolume = ReadRleCompressedVolume(reader, width, height, depth);

                // Read particles with optimized batch processing
                List<Particle> particles = new List<Particle>(particleCount);

                // For very large particle counts, process in batches
                const int BATCH_SIZE = 10000;

                for (int batchStart = 0; batchStart < particleCount; batchStart += BATCH_SIZE)
                {
                    int batchEnd = Math.Min(batchStart + BATCH_SIZE, particleCount);
                    int batchSize = batchEnd - batchStart;

                    Logger.Log($"[LoadFromBinaryFile] Loading particle batch {batchStart}-{batchEnd} of {particleCount}");

                    // Pre-allocate arrays for batch data
                    int[] ids = new int[batchSize];
                    int[] voxelCounts = new int[batchSize];
                    double[] volumesMicrometers = new double[batchSize];
                    double[] volumesMillimeters = new double[batchSize];
                    int[] centersX = new int[batchSize];
                    int[] centersY = new int[batchSize];
                    int[] centersZ = new int[batchSize];
                    int[] minX = new int[batchSize];
                    int[] minY = new int[batchSize];
                    int[] minZ = new int[batchSize];
                    int[] maxX = new int[batchSize];
                    int[] maxY = new int[batchSize];
                    int[] maxZ = new int[batchSize];

                    // Read batch data sequentially
                    for (int i = 0; i < batchSize; i++)
                    {
                        ids[i] = reader.ReadInt32();
                        voxelCounts[i] = reader.ReadInt32();
                        volumesMicrometers[i] = reader.ReadDouble();
                        volumesMillimeters[i] = reader.ReadDouble();
                        centersX[i] = reader.ReadInt32();
                        centersY[i] = reader.ReadInt32();
                        centersZ[i] = reader.ReadInt32();
                        minX[i] = reader.ReadInt32();
                        minY[i] = reader.ReadInt32();
                        minZ[i] = reader.ReadInt32();
                        maxX[i] = reader.ReadInt32();
                        maxY[i] = reader.ReadInt32();
                        maxZ[i] = reader.ReadInt32();
                    }

                    // Process batch in parallel
                    var batchParticles = new Particle[batchSize];
                    Parallel.For(0, batchSize, i =>
                    {
                        batchParticles[i] = new Particle
                        {
                            Id = ids[i],
                            VoxelCount = voxelCounts[i],
                            VolumeMicrometers = volumesMicrometers[i],
                            VolumeMillimeters = volumesMillimeters[i],
                            Center = new Point3D
                            {
                                X = centersX[i],
                                Y = centersY[i],
                                Z = centersZ[i]
                            },
                            Bounds = new BoundingBox
                            {
                                MinX = minX[i],
                                MinY = minY[i],
                                MinZ = minZ[i],
                                MaxX = maxX[i],
                                MaxY = maxY[i],
                                MaxZ = maxZ[i]
                            }
                        };
                    });

                    // Add batch to result
                    particles.AddRange(batchParticles);
                }

                return new SeparationResult
                {
                    LabelVolume = labelVolume,
                    Particles = particles,
                    CurrentSlice = currentSlice,
                    Is3D = is3D
                };
            }
        }

        public void Dispose()
        {
            try
            {
                accelerator?.Dispose();
                gpuContext?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Log($"[ParticleSeparator] Error during disposal: {ex.Message}");
            }
        }
    }
    public class SeparationResult
    {
        private int[,,] _labelVolume;
        public int[,,] LabelVolume
        {
            get => _labelVolume;
            set => _labelVolume = value;
        }

        private List<Particle> _particles;
        public List<Particle> Particles
        {
            get
            {
                // If particles haven't been analyzed yet, analyze them now
                if (_particles == null && _labelVolume != null && _analyzeParticlesFunc != null)
                {
                    _particles = _analyzeParticlesFunc();
                }
                // Always return a non-null list to avoid rendering issues
                return _particles ?? new List<Particle>();
            }
            set => _particles = value;
        }

        public int CurrentSlice { get; set; }
        public bool Is3D { get; set; }

        private Func<List<Particle>> _analyzeParticlesFunc;

        public void SetParticleAnalysisFunction(Func<List<Particle>> analysisFunc)
        {
            _analyzeParticlesFunc = analysisFunc;
        }
    }
}