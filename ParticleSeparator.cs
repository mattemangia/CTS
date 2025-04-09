using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ILGPU;
using ILGPU.Runtime;
using System.Threading;
using ILGPU.Runtime.CPU;

namespace CTSegmenter
{
    public class ParticleSeparator : IDisposable
    {
        // Constants for array handling
        private const int MAX_PARTICLES = 50000000; // Adjusted for .NET 4.8.1 limits

        private MainForm mainForm;
        private Material selectedMaterial;
        private bool useGpu;
        private Context gpuContext;
        private Accelerator accelerator;
        private bool gpuInitialized = false;

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

        // Result of separation
        public class SeparationResult
        {
            public int[,,] LabelVolume { get; set; }
            public List<Particle> Particles { get; set; }
            public int CurrentSlice { get; set; }
            public bool Is3D { get; set; }
        }

        // Constructor
        public ParticleSeparator(MainForm mainForm, Material selectedMaterial, bool useGpu)
        {
            this.mainForm = mainForm;
            this.selectedMaterial = selectedMaterial;
            this.useGpu = useGpu;

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

            // Extract the slice data
            progress?.Report(10);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte label = mainForm.volumeLabels[x, y, slice];
                    // Only include voxels with the selected material
                    sliceData[x, y] = (label == selectedMaterial.ID) ? (byte)1 : (byte)0;
                }
            }

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

            // Find the maximum label (number of components)
            int maxLabel = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    maxLabel = Math.Max(maxLabel, labeledSlice[x, y]);
                }
            }

            // Create particles
            progress?.Report(60);
            List<Particle> particles = new List<Particle>();
            Dictionary<int, int> voxelCounts = new Dictionary<int, int>();
            Dictionary<int, (int sumX, int sumY)> centers = new Dictionary<int, (int, int)>();
            Dictionary<int, (int minX, int minY, int maxX, int maxY)> bounds =
                new Dictionary<int, (int, int, int, int)>();

            // Count voxels and compute centers
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int label = labeledSlice[x, y];
                    if (label > 0)
                    {
                        // Update voxel count
                        if (!voxelCounts.ContainsKey(label))
                        {
                            voxelCounts[label] = 0;
                            centers[label] = (0, 0);
                            bounds[label] = (x, y, x, y);
                        }

                        voxelCounts[label]++;

                        // Update center
                        var center = centers[label];
                        centers[label] = (center.sumX + x, center.sumY + y);

                        // Update bounds
                        var bound = bounds[label];
                        bounds[label] = (
                            Math.Min(bound.minX, x),
                            Math.Min(bound.minY, y),
                            Math.Max(bound.maxX, x),
                            Math.Max(bound.maxY, y)
                        );
                    }
                }
            }

            // Create the particles
            foreach (var entry in voxelCounts)
            {
                int label = entry.Key;
                int voxelCount = entry.Value;
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

            // Create a 3D volume for consistent interface
            int[,,] labelVolume = new int[width, height, 1];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    labelVolume[x, y, 0] = labeledSlice[x, y];
                }
            }

            progress?.Report(100);

            return new SeparationResult
            {
                LabelVolume = labelVolume,
                Particles = particles,
                CurrentSlice = 0,
                Is3D = false
            };
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

                // Extract the volume data in slices to reduce memory pressure
                progress?.Report(10);
                for (int z = 0; z < depth; z++)
                {
                    if (z % 10 == 0)
                    {
                        progress?.Report(10 + (z * 15) / depth);
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            byte label = mainForm.volumeLabels[x, y, z];
                            // Only include voxels with the selected material
                            volumeData[x, y, z] = (label == selectedMaterial.ID) ? (byte)1 : (byte)0;
                        }
                    }
                }

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

                // Count particles and analyze
                progress?.Report(75);

                // First pass: find unique labels to avoid repeatedly scanning the whole volume
                HashSet<int> uniqueLabels = new HashSet<int>();
                for (int z = 0; z < depth; z++)
                {
                    if (z % 10 == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report(75 + (z * 5) / depth);
                    }

                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int label = labeledVolume[x, y, z];
                            if (label > 0)
                            {
                                uniqueLabels.Add(label);
                            }
                        }
                    }
                }

                Logger.Log($"[Separate3D] Found {uniqueLabels.Count} unique labels");

                // Initialize tracking data structures for all labels at once
                Dictionary<int, int> voxelCounts = new Dictionary<int, int>(uniqueLabels.Count);
                Dictionary<int, (int sumX, int sumY, int sumZ)> centers =
                    new Dictionary<int, (int, int, int)>(uniqueLabels.Count);
                Dictionary<int, (int minX, int minY, int minZ, int maxX, int maxY, int maxZ)> bounds =
                    new Dictionary<int, (int, int, int, int, int, int)>(uniqueLabels.Count);

                // Initialize structures for all unique labels at once
                foreach (int label in uniqueLabels)
                {
                    voxelCounts[label] = 0;
                    centers[label] = (0, 0, 0);
                    bounds[label] = (int.MaxValue, int.MaxValue, int.MaxValue, 0, 0, 0);
                }

                // Analyze particles with optimized slice-by-slice processing
                progress?.Report(80);
                for (int z = 0; z < depth; z++)
                {
                    if (z % 10 == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report(80 + (z * 15) / depth);
                    }

                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int label = labeledVolume[x, y, z];
                            if (label > 0)
                            {
                                // Update voxel count
                                voxelCounts[label]++;

                                // Update center of mass
                                var center = centers[label];
                                centers[label] = (center.sumX + x, center.sumY + y, center.sumZ + z);

                                // Update bounds
                                var bound = bounds[label];
                                bounds[label] = (
                                    Math.Min(bound.minX, x),
                                    Math.Min(bound.minY, y),
                                    Math.Min(bound.minZ, z),
                                    Math.Max(bound.maxX, x),
                                    Math.Max(bound.maxY, y),
                                    Math.Max(bound.maxZ, z)
                                );
                            }
                        }
                    }
                }

                // Create particles
                progress?.Report(95);
                List<Particle> particles = new List<Particle>(uniqueLabels.Count);

                foreach (var entry in voxelCounts)
                {
                    int label = entry.Key;
                    int voxelCount = entry.Value;
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
                        Center = new Point3D
                        {
                            X = center.sumX / voxelCount,
                            Y = center.sumY / voxelCount,
                            Z = center.sumZ / voxelCount
                        },
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

                progress?.Report(100);
                Logger.Log($"[Separate3D] Identified {particles.Count} particles");

                return new SeparationResult
                {
                    LabelVolume = labeledVolume,
                    Particles = particles,
                    CurrentSlice = depth / 2, // Start with middle slice for viewing
                    Is3D = true
                };
            }
            catch (OutOfMemoryException ex)
            {
                Logger.Log($"[Separate3D] Out of memory: {ex.Message}");
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);

                // Try with large volume method
                return Separate3DLarge(conservative, progress, cancellationToken);
            }
        }


        // For large volumes, use a chunking approach
        private SeparationResult Separate3DLarge(bool conservative, IProgress<int> progress, CancellationToken cancellationToken)
        {
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();

            // Use a smaller chunk size for very large volumes
            int chunkSize = 128;
            int numChunksX = (width + chunkSize - 1) / chunkSize;
            int numChunksY = (height + chunkSize - 1) / chunkSize;
            int numChunksZ = (depth + chunkSize - 1) / chunkSize;
            int totalChunks = numChunksX * numChunksY * numChunksZ;

            // Process each chunk and merge results
            int[,,] globalLabels = new int[width, height, depth];
            UnionFind unionFind = new UnionFind();
            int nextGlobalLabel = 1;
            Dictionary<(int chunkX, int chunkY, int chunkZ, int localLabel), int> labelMap =
                new Dictionary<(int, int, int, int), int>();

            int chunksDone = 0;
            progress?.Report(0);

            // First pass: process each chunk separately
            for (int chunkZ = 0; chunkZ < numChunksZ; chunkZ++)
            {
                for (int chunkY = 0; chunkY < numChunksY; chunkY++)
                {
                    for (int chunkX = 0; chunkX < numChunksX; chunkX++)
                    {
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

                        // Process chunk
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
                            labelMap[(chunkX, chunkY, chunkZ, localLabel)] = nextGlobalLabel++;
                            unionFind.MakeSet(labelMap[(chunkX, chunkY, chunkZ, localLabel)]);
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

                        chunksDone++;
                        progress?.Report((chunksDone * 50) / totalChunks);
                    }
                }
            }

            // Second pass: merge connected components across chunk boundaries
            progress?.Report(50);

            for (int z = 0; z < depth; z++)
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
                    progress?.Report(50 + (z * 25) / depth);
                }
            }

            // Third pass: apply final labels
            Dictionary<int, int> finalLabelMap = new Dictionary<int, int>();
            int finalLabelCount = 0;

            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int label = globalLabels[x, y, z];
                        if (label > 0)
                        {
                            int root = unionFind.Find(label);
                            if (!finalLabelMap.ContainsKey(root))
                            {
                                finalLabelMap[root] = ++finalLabelCount;
                            }

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
            }

            // Analyze particles
            List<Particle> particles = new List<Particle>();
            Dictionary<int, int> voxelCounts = new Dictionary<int, int>();
            Dictionary<int, (int sumX, int sumY, int sumZ)> centers = new Dictionary<int, (int, int, int)>();
            Dictionary<int, (int minX, int minY, int minZ, int maxX, int maxY, int maxZ)> bounds =
                new Dictionary<int, (int, int, int, int, int, int)>();

            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int label = globalLabels[x, y, z];
                        if (label > 0)
                        {
                            // Update voxel count
                            if (!voxelCounts.ContainsKey(label))
                            {
                                voxelCounts[label] = 0;
                                centers[label] = (0, 0, 0);
                                bounds[label] = (int.MaxValue, int.MaxValue, int.MaxValue, 0, 0, 0);
                            }

                            voxelCounts[label]++;

                            // Update center
                            var center = centers[label];
                            centers[label] = (center.sumX + x, center.sumY + y, center.sumZ + z);

                            // Update bounds
                            var bound = bounds[label];
                            bounds[label] = (
                                Math.Min(bound.minX, x),
                                Math.Min(bound.minY, y),
                                Math.Min(bound.minZ, z),
                                Math.Max(bound.maxX, x),
                                Math.Max(bound.maxY, y),
                                Math.Max(bound.maxZ, z)
                            );
                        }
                    }
                }
            }

            // Create particles
            foreach (var entry in voxelCounts)
            {
                int label = entry.Key;
                int voxelCount = entry.Value;
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
                    Center = new Point3D
                    {
                        X = center.sumX / voxelCount,
                        Y = center.sumY / voxelCount,
                        Z = center.sumZ / voxelCount
                    },
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

            progress?.Report(100);

            return new SeparationResult
            {
                LabelVolume = globalLabels,
                Particles = particles,
                CurrentSlice = depth / 2, // Start with middle slice for viewing
                Is3D = true
            };
        }

        private class UnionFind
        {
            private Dictionary<int, int> parent;
            private Dictionary<int, int> rank;

            public UnionFind()
            {
                parent = new Dictionary<int, int>();
                rank = new Dictionary<int, int>();
            }

            public void MakeSet(int x)
            {
                if (!parent.ContainsKey(x))
                {
                    parent[x] = x;
                    rank[x] = 0;
                }
            }

            public int Find(int x)
            {
                if (!parent.ContainsKey(x))
                {
                    MakeSet(x);
                    return x;
                }

                if (parent[x] != x)
                {
                    parent[x] = Find(parent[x]); // Path compression
                }

                return parent[x];
            }

            public void Union(int x, int y)
            {
                int rootX = Find(x);
                int rootY = Find(y);

                if (rootX == rootY)
                    return;

                // Union by rank
                if (!rank.ContainsKey(rootX)) rank[rootX] = 0;
                if (!rank.ContainsKey(rootY)) rank[rootY] = 0;

                if (rank[rootX] < rank[rootY])
                {
                    parent[rootX] = rootY;
                }
                else if (rank[rootX] > rank[rootY])
                {
                    parent[rootY] = rootX;
                }
                else
                {
                    parent[rootY] = rootX;
                    rank[rootX]++;
                }
            }
        }

        // CPU implementation of 2D connected component labeling
        private int[,] LabelConnectedComponents2DCpu(byte[,] data, CancellationToken cancellationToken)
        {
            int width = data.GetLength(0);
            int height = data.GetLength(1);

            int[,] labels = new int[width, height];
            UnionFind unionFind = new UnionFind();
            int nextLabel = 1;

            // First pass: assign initial labels and record equivalences
            for (int y = 0; y < height; y++)
            {
                if (y % 100 == 0) cancellationToken.ThrowIfCancellationRequested();

                for (int x = 0; x < width; x++)
                {
                    if (data[x, y] == 0)
                        continue;

                    // Check neighbors (8-connectivity)
                    List<int> neighbors = new List<int>();

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
                        labels[x, y] = nextLabel++;
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

            // Second pass: apply union-find equivalences
            Dictionary<int, int> finalLabelMap = new Dictionary<int, int>();
            int finalLabelCount = 0;

            for (int y = 0; y < height; y++)
            {
                if (y % 100 == 0) cancellationToken.ThrowIfCancellationRequested();

                for (int x = 0; x < width; x++)
                {
                    if (labels[x, y] == 0)
                        continue;

                    int root = unionFind.Find(labels[x, y]);

                    if (!finalLabelMap.ContainsKey(root))
                    {
                        finalLabelMap[root] = ++finalLabelCount;
                    }

                    labels[x, y] = finalLabelMap[root];
                }
            }

            return labels;
        }

        // CPU implementation of 3D connected component labeling
        private int[,,] LabelConnectedComponents3DCpu(byte[,,] data, CancellationToken cancellationToken)
        {
            int width = data.GetLength(0);
            int height = data.GetLength(1);
            int depth = data.GetLength(2);

            int[,,] labels = new int[width, height, depth];
            UnionFind unionFind = new UnionFind();
            int nextLabel = 1;

            // First pass: assign initial labels and record equivalences
            for (int z = 0; z < depth; z++)
            {
                if (z % 10 == 0) cancellationToken.ThrowIfCancellationRequested();

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (data[x, y, z] == 0)
                            continue;

                        // Check 6-connected neighbors
                        List<int> neighbors = new List<int>();

                        if (x > 0 && data[x - 1, y, z] != 0)
                            neighbors.Add(labels[x - 1, y, z]);

                        if (y > 0 && data[x, y - 1, z] != 0)
                            neighbors.Add(labels[x, y - 1, z]);

                        if (z > 0 && data[x, y, z - 1] != 0)
                            neighbors.Add(labels[x, y, z - 1]);

                        neighbors.RemoveAll(n => n == 0);

                        if (neighbors.Count == 0)
                        {
                            // New component
                            labels[x, y, z] = nextLabel++;
                            unionFind.MakeSet(labels[x, y, z]);
                        }
                        else
                        {
                            // Use the minimum neighbor label
                            labels[x, y, z] = neighbors.Min();

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
            }

            // Second pass: apply union-find equivalences
            Dictionary<int, int> finalLabelMap = new Dictionary<int, int>();
            int finalLabelCount = 0;

            for (int z = 0; z < depth; z++)
            {
                if (z % 10 == 0) cancellationToken.ThrowIfCancellationRequested();

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (labels[x, y, z] == 0)
                            continue;

                        int root = unionFind.Find(labels[x, y, z]);

                        if (!finalLabelMap.ContainsKey(root))
                        {
                            finalLabelMap[root] = ++finalLabelCount;
                        }

                        labels[x, y, z] = finalLabelMap[root];
                    }
                }
            }

            return labels;
        }

        // GPU kernel methods for 2D connected component labeling
        static void InitLabelsKernel2D(
    Index2D index,
    ArrayView2D<byte, Stride2D.DenseY> input,
    ArrayView2D<int, Stride2D.DenseY> output)
        {
            int x = index.X;
            int y = index.Y;

            output[x, y] = input[x, y] == 0 ? 0 : -1;
        }


        static void PropagateLabelsKernel2D(
    Index2D index,
    ArrayView2D<int, Stride2D.DenseY> labels,
    ArrayView1D<int, Stride1D.Dense> changes)
        {
            int x = index.X;
            int y = index.Y;

            if (labels[x, y] <= 0)
                return;

            long width = labels.Extent.X;
            long height = labels.Extent.Y;

            int currentLabel = labels[x, y];
            int minLabel = currentLabel;

            // Check 8-connected neighbors
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                        continue;

                    int nx = x + dx;
                    int ny = y + dy;

                    if (nx >= 0 && nx < width && ny >= 0 && ny < height && labels[nx, ny] > 0)
                    {
                        minLabel = Math.Min(minLabel, labels[nx, ny]);
                    }
                }
            }

            if (minLabel < currentLabel)
            {
                labels[x, y] = minLabel;
                ILGPU.Atomic.Add(ref changes[0], 1);
            }
        }

        private int[,] LabelConnectedComponents2DGpu(byte[,] data)
        {
            if (!gpuInitialized || accelerator == null)
            {
                throw new InvalidOperationException("GPU acceleration not available. Initialize GPU first.");
            }

            int width = data.GetLength(0);
            int height = data.GetLength(1);

            try
            {
                // Convert to 1D array for GPU processing
                byte[] flatData = new byte[width * height];
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        flatData[y * width + x] = data[x, y];

                // Create GPU buffers using correct ILGPU 1.5.1 syntax
                var inputBuffer = accelerator.Allocate1D<byte>(flatData.Length);
                inputBuffer.CopyFromCPU(flatData);

                var labelsBuffer = accelerator.Allocate1D<int>(width * height);

                var changesBuffer = accelerator.Allocate1D<int>(1);
                int[] zeros = new int[1] { 0 };
                changesBuffer.CopyFromCPU(zeros);

                // Initialize labels with -1 for foreground pixels
                var initKernel = accelerator.LoadAutoGroupedStreamKernel<
                    Index1D,           // index
                    ArrayView<byte>,   // input
                    ArrayView<int>,    // output
                    int                // width
                >(
                    (Index1D index, ArrayView<byte> input, ArrayView<int> output, int w) =>
                    {
                        if (index >= input.Length)
                            return;

                        output[index] = input[index] > 0 ? -1 : 0;
                    });

                // Execute initialization kernel
                initKernel(flatData.Length, inputBuffer.View, labelsBuffer.View, width);
                accelerator.Synchronize();

                // Get data back for initial labeling
                int[] labelsArray = new int[width * height];
                labelsBuffer.CopyToCPU(labelsArray);

                // Assign initial labels
                int nextLabel = 1;
                for (int i = 0; i < labelsArray.Length; i++)
                {
                    if (labelsArray[i] == -1)
                        labelsArray[i] = nextLabel++;
                }

                // Copy back to GPU
                labelsBuffer.CopyFromCPU(labelsArray);

                // Define the propagation kernel
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

                        // Convert 1D index to 2D coordinates
                        int x = (int)(index % w);
                        int y = (int)(index / w);

                        if (labels[index] <= 0)
                            return;

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
                            Atomic.Add(ref changes[0], 1);
                        }
                    });

                // Iteratively propagate labels
                bool hasChanges = true;
                int[] changesArray = new int[1];

                for (int iter = 0; iter < width + height && hasChanges; iter++)
                {
                    // Reset changes counter
                    changesBuffer.CopyFromCPU(zeros);

                    // Execute propagation kernel
                    propagateKernel(width * height, labelsBuffer.View, changesBuffer.View, width, height);
                    accelerator.Synchronize();

                    // Check if any changes occurred
                    changesBuffer.CopyToCPU(changesArray);
                    hasChanges = changesArray[0] > 0;
                }

                // Get final results
                labelsBuffer.CopyToCPU(labelsArray);

                // Convert back to 2D array
                int[,] result = new int[width, height];
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        result[x, y] = labelsArray[y * width + x];

                // Clean up
                inputBuffer.Dispose();
                labelsBuffer.Dispose();
                changesBuffer.Dispose();

                return result;
            }
            catch (Exception ex)
            {
                Logger.Log($"[ParticleSeparator] GPU processing error: {ex.Message}");
                throw; // Rethrow so we can handle it at a higher level
            }
        }

        // GPU kernel methods for 3D connected component labeling




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

            // Check if we should process this on GPU or fall back to CPU
            // Increased threshold and added adaptive size check based on available memory
            long maxGpuVoxels = GetMaxGpuVoxels();
            if (totalVoxels > maxGpuVoxels)
            {
                Logger.Log($"[ParticleSeparator] Volume too large for direct GPU processing: {totalVoxels} voxels. Falling back to chunked processing.");
                return ProcessLargeVolumeWithChunks(data, cancellationToken);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Convert to 1D array for GPU
                byte[] flatData = new byte[width * height * depth];
                int strideXY = width * height;

                // Process in Z-slices to reduce memory pressure
                for (int z = 0; z < depth; z++)
                {
                    if (z % 10 == 0) cancellationToken.ThrowIfCancellationRequested();

                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                            flatData[z * strideXY + y * width + x] = data[x, y, z];
                }

                // Use smaller buffers with pinned memory for better performance
                using (var inputBuffer = accelerator.Allocate1D<byte>(flatData.Length))
                using (var labelsBuffer = accelerator.Allocate1D<int>(flatData.Length))
                using (var changesBuffer = accelerator.Allocate1D<int>(1))
                {
                    // Copy data to GPU
                    inputBuffer.CopyFromCPU(flatData);

                    // Zero-initialize the changes buffer
                    int[] zeros = new int[1] { 0 };
                    changesBuffer.CopyFromCPU(zeros);

                    cancellationToken.ThrowIfCancellationRequested();

                    // Initialize kernel
                    var initKernel = accelerator.LoadAutoGroupedStreamKernel<
                        Index1D,           // index
                        ArrayView<byte>,   // input
                        ArrayView<int>     // output
                    >(
                        (Index1D index, ArrayView<byte> input, ArrayView<int> output) =>
                        {
                            if (index < input.Length)
                            {
                                output[index] = input[index] > 0 ? -1 : 0;
                            }
                        });

                    // Execute init kernel
                    initKernel(flatData.Length, inputBuffer.View, labelsBuffer.View);
                    accelerator.Synchronize();

                    cancellationToken.ThrowIfCancellationRequested();

                    // Get data back for initial labeling
                    int[] labelsArray = new int[flatData.Length];
                    labelsBuffer.CopyToCPU(labelsArray);

                    // Assign initial labels
                    int nextLabel = 1;
                    for (int i = 0; i < labelsArray.Length; i++)
                    {
                        if (i % (width * height * 10) == 0)
                            cancellationToken.ThrowIfCancellationRequested();

                        if (labelsArray[i] == -1)
                            labelsArray[i] = nextLabel++;
                    }

                    // Copy back to GPU
                    labelsBuffer.CopyFromCPU(labelsArray);

                    // Propagation kernel for 3D
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

                            // Convert 1D index to 3D coordinates
                            int x = (int)(index % w);
                            int y = (int)((index / w) % h);
                            int z = (int)(index / (w * h));

                            if (labels[index] <= 0)
                                return;

                            int currentLabel = labels[index];
                            int minLabel = currentLabel;
                            bool foundSmaller = false;

                            // Check 6-connected neighbors
                            int StrideXY = w * h;

                            // X-1
                            if (x > 0)
                            {
                                int neighborIdx = z * StrideXY + y * w + (x - 1);
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
                                int neighborIdx = z * StrideXY + y * w + (x + 1);
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
                                int neighborIdx = z * StrideXY + (y - 1) * w + x;
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
                                int neighborIdx = z * StrideXY + (y + 1) * w + x;
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
                                int neighborIdx = (z - 1) * StrideXY + y * w + x;
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
                                int neighborIdx = (z + 1) * StrideXY + y * w + x;
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
                                Atomic.Add(ref changes[0], 1);
                            }
                        });

                    cancellationToken.ThrowIfCancellationRequested();

                    // Iteratively propagate labels
                    bool hasChanges = true;
                    int[] changesArray = new int[1];
                    int maxIterations = Math.Min(width + height + depth, 1000); // Add max iteration limit

                    for (int iter = 0; iter < maxIterations && hasChanges; iter++)
                    {
                        if (iter % 10 == 0) cancellationToken.ThrowIfCancellationRequested();

                        // Reset changes counter
                        changesBuffer.CopyFromCPU(zeros);

                        // Execute propagation kernel
                        propagateKernel(flatData.Length, labelsBuffer.View, changesBuffer.View, width, height, depth);
                        accelerator.Synchronize();

                        // Check if any changes occurred
                        changesBuffer.CopyToCPU(changesArray);
                        hasChanges = changesArray[0] > 0;

                        // Log progress of large operations
                        if (iter % 20 == 0)
                            Logger.Log($"[GPU] Label propagation iteration {iter}, changes: {changesArray[0]}");
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    // Get final results
                    labelsBuffer.CopyToCPU(labelsArray);

                    // Convert back to 3D array
                    int[,,] result = new int[width, height, depth];

                    // Process in chunks to reduce GC pressure
                    int chunkSize = 10; // Process 10 slices at a time
                    for (int zChunk = 0; zChunk < depth; zChunk += chunkSize)
                    {
                        int endZ = Math.Min(zChunk + chunkSize, depth);
                        for (int z = zChunk; z < endZ; z++)
                        {
                            if (z % 10 == 0) cancellationToken.ThrowIfCancellationRequested();

                            for (int y = 0; y < height; y++)
                                for (int x = 0; x < width; x++)
                                    result[x, y, z] = labelsArray[z * strideXY + y * width + x];
                        }

                        // Force garbage collection every few chunks for very large volumes
                        if (totalVoxels > 100_000_000 && zChunk % 50 == 0)
                        {
                            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
                        }
                    }

                    // Free memory to avoid holding onto large arrays
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

        // Add this new helper method to estimate available GPU memory
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

        // Add this new method to process large volumes with a tiled approach
        private int[,,] ProcessLargeVolumeWithChunks(byte[,,] data, CancellationToken cancellationToken)
        {
            int width = data.GetLength(0);
            int height = data.GetLength(1);
            int depth = data.GetLength(2);

            // Create the result volume
            int[,,] result = new int[width, height, depth];

            // Process in slabs along Z axis
            int slabSize = 64; // Process 64 Z-slices at a time
            int nextGlobalLabel = 1;
            Dictionary<int, int> equivalenceMap = new Dictionary<int, int>();

            // First pass: label each slab independently
            for (int startZ = 0; startZ < depth; startZ += slabSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int endZ = Math.Min(startZ + slabSize, depth);
                int slabDepth = endZ - startZ;

                // Extract slab
                byte[,,] slab = new byte[width, height, slabDepth];
                for (int z = 0; z < slabDepth; z++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            slab[x, y, z] = data[x, y, startZ + z];
                        }
                    }
                }

                // Process this slab with CPU method (more reliable for chunks)
                int[,,] slabLabels = LabelConnectedComponents3DCpu(slab, cancellationToken);

                // Create label mapping for this slab
                Dictionary<int, int> slabMap = new Dictionary<int, int>();

                // Copy labels to result and create mapping
                for (int z = 0; z < slabDepth; z++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int label = slabLabels[x, y, z];
                            if (label > 0)
                            {
                                // Map to a global label
                                if (!slabMap.TryGetValue(label, out int globalLabel))
                                {
                                    globalLabel = nextGlobalLabel++;
                                    slabMap[label] = globalLabel;
                                }

                                result[x, y, startZ + z] = globalLabel;
                            }
                        }
                    }
                }

                // Clear memory
                slab = null;
                slabLabels = null;
                GC.Collect();

                Logger.Log($"[ProcessLargeVolumeWithChunks] Processed slab {startZ}-{endZ} with {slabMap.Count} components");
            }

            // Second pass: merge components across slab boundaries
            UnionFind unionFind = new UnionFind();

            // Initialize all labels in the union find
            for (int i = 1; i < nextGlobalLabel; i++)
            {
                unionFind.MakeSet(i);
            }

            // Find connections between slabs
            for (int z = 1; z < depth; z++)
            {
                if (z % 10 == 0) cancellationToken.ThrowIfCancellationRequested();

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int currentLabel = result[x, y, z];
                        int prevLabel = result[x, y, z - 1];

                        // If both voxels have labels and they're from different components
                        if (currentLabel > 0 && prevLabel > 0)
                        {
                            unionFind.Union(currentLabel, prevLabel);
                        }
                    }
                }
            }

            // Final pass: apply merged labels
            int[,,] mergedResult = new int[width, height, depth];
            Dictionary<int, int> finalLabels = new Dictionary<int, int>();
            int finalLabelCount = 0;

            for (int z = 0; z < depth; z++)
            {
                if (z % 10 == 0) cancellationToken.ThrowIfCancellationRequested();

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int label = result[x, y, z];
                        if (label > 0)
                        {
                            int root = unionFind.Find(label);

                            if (!finalLabels.TryGetValue(root, out int finalLabel))
                            {
                                finalLabel = ++finalLabelCount;
                                finalLabels[root] = finalLabel;
                            }

                            mergedResult[x, y, z] = finalLabel;
                        }
                    }
                }
            }

            // Clean up
            result = null;
            GC.Collect();

            Logger.Log($"[ProcessLargeVolumeWithChunks] Completed with {finalLabelCount} final components");
            return mergedResult;
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

            // Flatten the 3D volume into a 1D array for RLE compression
            int[] flatData = new int[width * height * depth];
            int index = 0;

            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        flatData[index++] = volume[x, y, z];
                    }
                }
            }

            // Compress using RLE
            List<(int value, int count)> rleData = new List<(int, int)>();
            int currentValue = flatData[0];
            int currentCount = 1;

            for (int i = 1; i < flatData.Length; i++)
            {
                if (flatData[i] == currentValue)
                {
                    currentCount++;
                }
                else
                {
                    rleData.Add((currentValue, currentCount));
                    currentValue = flatData[i];
                    currentCount = 1;
                }
            }

            // Add the last run
            rleData.Add((currentValue, currentCount));

            // Write the RLE data
            writer.Write(rleData.Count);
            foreach (var (value, count) in rleData)
            {
                writer.Write(value);
                writer.Write(count);
            }
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

                // Read label volume
                int[,,] labelVolume = ReadRleCompressedVolume(reader, width, height, depth);

                // Read particles
                List<Particle> particles = new List<Particle>();
                for (int i = 0; i < particleCount; i++)
                {
                    Particle particle = new Particle
                    {
                        Id = reader.ReadInt32(),
                        VoxelCount = reader.ReadInt32(),
                        VolumeMicrometers = reader.ReadDouble(),
                        VolumeMillimeters = reader.ReadDouble(),

                        Center = new Point3D
                        {
                            X = reader.ReadInt32(),
                            Y = reader.ReadInt32(),
                            Z = reader.ReadInt32()
                        },

                        Bounds = new BoundingBox
                        {
                            MinX = reader.ReadInt32(),
                            MinY = reader.ReadInt32(),
                            MinZ = reader.ReadInt32(),
                            MaxX = reader.ReadInt32(),
                            MaxY = reader.ReadInt32(),
                            MaxZ = reader.ReadInt32()
                        }
                    };

                    particles.Add(particle);
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

            // Decompress RLE data into the volume
            int index = 0;
            foreach (var (value, count) in rleData)
            {
                for (int i = 0; i < count; i++)
                {
                    int z = index / (width * height);
                    int remainder = index % (width * height);
                    int y = remainder / width;
                    int x = remainder % width;

                    volume[x, y, z] = value;
                    index++;
                }
            }

            return volume;
        }

        public void Dispose()
        {
            accelerator?.Dispose();
            gpuContext?.Dispose();
        }
    }
}