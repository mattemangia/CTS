using SharpDX;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Linq;

namespace CTS.SharpDXIntegration
{
    public enum MeshExportMode
    {
        VoxelMesh,
        SurfaceMesh,
        BinarySTL
    }

    public class MeshExportOptions
    {
        public MeshExportMode Mode { get; set; } = MeshExportMode.SurfaceMesh;
        public bool ExportGrayscale { get; set; } = true;
        public bool ExportMaterials { get; set; } = true;
        public bool ExcludeExterior { get; set; } = true;
        public int SurfaceFacetCount { get; set; } = 100000;
        public float MinParticleRadius { get; set; } = 5.0f;
        public int DownsampleFactor { get; set; } = 1;
        public float VoxelSize { get; set; } = 1.0f;

        // Clipping options
        public bool UseClippingPlane { get; set; } = false;
        public Vector3 ClippingPlaneNormal { get; set; } = Vector3.UnitX;
        public float ClippingPlaneDistance { get; set; } = 0.5f;
        public bool ClippingPlaneMirror { get; set; } = false;

        // Cutting plane parameters
        public bool CutXEnabled { get; set; } = false;
        public bool CutYEnabled { get; set; } = false;
        public bool CutZEnabled { get; set; } = false;
        public float CutXDirection { get; set; } = 1.0f;
        public float CutYDirection { get; set; } = 1.0f;
        public float CutZDirection { get; set; } = 1.0f;
        public float CutXPosition { get; set; } = 0.5f;
        public float CutYPosition { get; set; } = 0.5f;
        public float CutZPosition { get; set; } = 0.5f;
        public bool ApplyPlaneCut { get; set; } = true;
    }

    // Chunk data for streaming output
    public struct ChunkData
    {
        public List<Vector3> Vertices;
        public List<Triangle> Triangles;
        public int ChunkIndex;
    }

    public struct Triangle
    {
        public int V1, V2, V3;
        public Vector3 Normal;

        public Triangle(int v1, int v2, int v3, Vector3 normal)
        {
            V1 = v1;
            V2 = v2;
            V3 = v3;
            Normal = normal;
        }
        public static void WriteMaterialFile(string objPath)
        {
            string mtlPath = Path.Combine(Path.GetDirectoryName(objPath), Path.GetFileNameWithoutExtension(objPath) + ".mtl");

            using (StreamWriter writer = new StreamWriter(mtlPath, false, Encoding.ASCII))
            {
                writer.WriteLine("# Material file for VoxelMesh");
                writer.WriteLine("newmtl default");
                writer.WriteLine("Ka 0.2 0.2 0.2");
                writer.WriteLine("Kd 0.8 0.8 0.8");
                writer.WriteLine("Ks 0.0 0.0 0.0");
                writer.WriteLine("d 1.0");
                writer.WriteLine("illum 2");
            }
        }
    }
    
    

    public static class VoxelMeshExporter
    {
        private const int CHUNK_SIZE = 32; // Optimized chunk size
        private static readonly int ProcessorCount = Environment.ProcessorCount;

        // Vector3 constants for SharpDX compatibility
        private static readonly Vector3 Forward = -Vector3.UnitZ;
        private static readonly Vector3 Backward = Vector3.UnitZ;
        private static readonly Vector3 Left = -Vector3.UnitX;
        private static readonly Vector3 Right = Vector3.UnitX;
        private static readonly Vector3 Up = Vector3.UnitY;
        private static readonly Vector3 Down = -Vector3.UnitY;

        public static async Task ExportVisibleVoxelsAsync(
            string outputPath,
            ChunkedVolume grayVol, ChunkedLabelVolume labelVol,
            int minThreshold, int maxThreshold,
            bool[] labelVisibility,
            int sliceX, int sliceY, int sliceZ,
            bool showSlices,
            MeshExportOptions options,
            Action<int> progressCallback = null)
        {
            string ext = Path.GetExtension(outputPath).ToLowerInvariant();
            if (ext == ".stl") options.Mode = MeshExportMode.BinarySTL;
            else if (ext == ".obj") options.Mode = options.Mode == MeshExportMode.VoxelMesh
                                                      ? MeshExportMode.VoxelMesh
                                                      : MeshExportMode.SurfaceMesh;

            var startTime = DateTime.Now;
            Logger.Log($"[VoxelMeshExporter] Starting memory-optimized export with mode: {options.Mode}");

            // For STL exports, enforce minimum downsampling and face limits
            if (options.Mode == MeshExportMode.BinarySTL)
            {
                if (options.DownsampleFactor < 2)
                {
                    Logger.Log("[VoxelMeshExporter] WARNING: STL export with DownsampleFactor < 2 may create very large files. Setting to 2.");
                    options.DownsampleFactor = 2;
                }

                if (options.SurfaceFacetCount > 1000000)
                {
                    Logger.Log("[VoxelMeshExporter] WARNING: SurfaceFacetCount > 1M may create very large STL files. Setting to 1M.");
                    options.SurfaceFacetCount = 1000000;
                }
                else if (options.SurfaceFacetCount == 0)
                {
                    options.SurfaceFacetCount = 500000; // Default to 500K for STL
                }
            }

            try
            {
                switch (options.Mode)
                {
                    case MeshExportMode.BinarySTL:
                        await ExportSTLMemoryOptimizedAsync(outputPath, grayVol, labelVol, minThreshold, maxThreshold,
                            labelVisibility, sliceX, sliceY, sliceZ, showSlices, options, progressCallback);
                        break;

                    case MeshExportMode.VoxelMesh:
                        await ExportVoxelMeshMemoryOptimizedAsync(outputPath, grayVol, labelVol, minThreshold, maxThreshold,
                            labelVisibility, sliceX, sliceY, sliceZ, showSlices, options, progressCallback);
                        break;

                    default: // MeshExportMode.SurfaceMesh
                        await ExportOBJMemoryOptimizedAsync(outputPath, grayVol, labelVol, minThreshold, maxThreshold,
                            labelVisibility, sliceX, sliceY, sliceZ, showSlices, options, progressCallback);
                        break;
                }

                var elapsed = (DateTime.Now - startTime).TotalSeconds;
                var fileInfo = new FileInfo(outputPath);
                Logger.Log($"[VoxelMeshExporter] Export completed in {elapsed:F2} seconds: {outputPath}");
                Logger.Log($"[VoxelMeshExporter] File size: {fileInfo.Length / (1024.0 * 1024.0):F2} MB");
            }
            catch (Exception ex)
            {
                Logger.Log($"[VoxelMeshExporter] Export failed: {ex.Message}");
                throw;
            }
        }

        private static async Task ExportSTLMemoryOptimizedAsync(
            string outputPath,
            ChunkedVolume grayVol,
            ChunkedLabelVolume labelVol,
            int minThreshold,
            int maxThreshold,
            bool[] labelVisibility,
            int sliceX, int sliceY, int sliceZ,
            bool showSlices,
            MeshExportOptions options,
            Action<int> progressCallback = null)
        {
            int step = Math.Max(1, options.DownsampleFactor);
            var chunks = CreateChunks(grayVol, step);

            using (var writer = new FastSTLWriter(outputPath))
            {
                int totalChunks = chunks.Count;
                int processedChunks = 0;
                int maxTriangles = options.SurfaceFacetCount;
                int lastReportedProgress = -1;
                var progressLock = new object();

                // Calculate approximate decimation factor
                long estimatedTotalTriangles = EstimateTriangleCount(grayVol, labelVol, minThreshold, maxThreshold,
                    labelVisibility, sliceX, sliceY, sliceZ, showSlices, options, step);

                int decimationFactor = 1;
                if (estimatedTotalTriangles > maxTriangles)
                {
                    decimationFactor = Math.Max(1, (int)(estimatedTotalTriangles / maxTriangles));
                    Logger.Log($"[STL Export] Estimated {estimatedTotalTriangles} triangles, using decimation factor {decimationFactor}");
                }

                // Process chunks with limited concurrency
                var semaphore = new SemaphoreSlim(ProcessorCount);
                var tasks = new List<Task>();
                var triangleCount = 0;

                for (int i = 0; i < chunks.Count; i++)
                {
                    if (triangleCount >= maxTriangles)
                        break;

                    var chunk = chunks[i];
                    var chunkIndex = i;

                    await semaphore.WaitAsync();

                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var chunkTriangles = ProcessChunkForSTL(chunk, grayVol, labelVol, minThreshold, maxThreshold,
                                labelVisibility, sliceX, sliceY, sliceZ, showSlices, options, step);

                            // Apply decimation at chunk level
                            if (decimationFactor > 1)
                            {
                                var decimatedTriangles = new List<TriangleWithVertices>();
                                for (int t = 0; t < chunkTriangles.Count; t += decimationFactor)
                                {
                                    decimatedTriangles.Add(chunkTriangles[t]);
                                }
                                chunkTriangles = decimatedTriangles;
                            }

                            // Write triangles in batches
                            lock (writer)
                            {
                                if (triangleCount + chunkTriangles.Count <= maxTriangles)
                                {
                                    foreach (var tri in chunkTriangles)
                                    {
                                        writer.WriteTriangle(tri.Normal, tri.Vertices[0], tri.Vertices[1], tri.Vertices[2]);
                                    }
                                    triangleCount += chunkTriangles.Count;
                                }
                                else
                                {
                                    // Write only what we can fit
                                    int remaining = maxTriangles - triangleCount;
                                    if (remaining > 0)
                                    {
                                        for (int t = 0; t < remaining; t++)
                                        {
                                            var tri = chunkTriangles[t];
                                            writer.WriteTriangle(tri.Normal, tri.Vertices[0], tri.Vertices[1], tri.Vertices[2]);
                                        }
                                        triangleCount = maxTriangles;
                                    }
                                }
                            }

                            int currentProgress = Interlocked.Increment(ref processedChunks);
                            int progressPercentage = (currentProgress * 100) / totalChunks;

                            // Only report progress when it changes and only from one thread
                            if (progressPercentage != lastReportedProgress &&
                                progressPercentage % 5 == 0) // Report every 5%
                            {
                                lock (progressLock)
                                {
                                    if (progressPercentage != lastReportedProgress)
                                    {
                                        lastReportedProgress = progressPercentage;
                                        progressCallback?.Invoke(progressPercentage);
                                    }
                                }
                            }
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }));

                    // Prevent too many tasks from accumulating
                    if (tasks.Count >= ProcessorCount * 2)
                    {
                        await Task.WhenAny(tasks);
                        tasks.RemoveAll(t => t.IsCompleted);
                    }
                }

                await Task.WhenAll(tasks);

                // Report final progress
                if (lastReportedProgress != 100)
                {
                    progressCallback?.Invoke(100);
                }

                Logger.Log($"[STL Export] Wrote {writer.TriangleCount} triangles");
            }
        }

        private static async Task ExportOBJMemoryOptimizedAsync(
            string outputPath,
            ChunkedVolume grayVol,
            ChunkedLabelVolume labelVol,
            int minThreshold,
            int maxThreshold,
            bool[] labelVisibility,
            int sliceX, int sliceY, int sliceZ,
            bool showSlices,
            MeshExportOptions options,
            Action<int> progressCallback = null)
        {
            int step = Math.Max(1, options.DownsampleFactor);
            var chunks = CreateChunks(grayVol, step);

            // Two-pass approach: first collect vertices, then write triangles
            var vertexMap = new ConcurrentDictionary<Vector3, int>();
            var vertexCounter = 0;

            using (var writer = new FastOBJWriter(outputPath))
            {
                int totalChunks = chunks.Count;
                int processedChunks = 0;
                int lastReportedProgress = -1;
                var progressLock = new object();

                // First pass: collect and write vertices
                await Task.Run(() =>
                {
                    Parallel.ForEach(chunks, new ParallelOptions { MaxDegreeOfParallelism = ProcessorCount }, chunk =>
                    {
                        var chunkVertices = CollectChunkVertices(chunk, grayVol, labelVol, minThreshold, maxThreshold,
                            labelVisibility, sliceX, sliceY, sliceZ, showSlices, options, step);

                        // Add unique vertices to map
                        foreach (var vertex in chunkVertices)
                        {
                            vertexMap.GetOrAdd(vertex, v =>
                            {
                                int index = Interlocked.Increment(ref vertexCounter);
                                lock (writer)
                                {
                                    writer.WriteVertex(v);
                                }
                                return index;
                            });
                        }

                        int currentProgress = Interlocked.Increment(ref processedChunks);
                        int progressPercentage = (currentProgress * 50) / totalChunks;

                        // Only report progress when it changes significantly
                        if (progressPercentage != lastReportedProgress &&
                            progressPercentage % 5 == 0) // Report every 5%
                        {
                            lock (progressLock)
                            {
                                if (progressPercentage != lastReportedProgress)
                                {
                                    lastReportedProgress = progressPercentage;
                                    progressCallback?.Invoke(progressPercentage);
                                }
                            }
                        }
                    });
                });

                processedChunks = 0;

                // Second pass: write triangles
                await Task.Run(() =>
                {
                    Parallel.ForEach(chunks, new ParallelOptions { MaxDegreeOfParallelism = ProcessorCount }, chunk =>
                    {
                        var faceBuffer = new StringBuilder();
                        var chunkTriangles = ProcessChunkForOBJ(chunk, vertexMap, grayVol, labelVol, minThreshold, maxThreshold,
                            labelVisibility, sliceX, sliceY, sliceZ, showSlices, options, step);

                        foreach (var triangle in chunkTriangles)
                        {
                            faceBuffer.AppendLine($"f {triangle.V1} {triangle.V2} {triangle.V3}");

                            if (faceBuffer.Length > 8192)
                            {
                                lock (writer)
                                {
                                    writer.QueueFaces(faceBuffer);
                                    faceBuffer.Clear();
                                }
                            }
                        }

                        if (faceBuffer.Length > 0)
                        {
                            lock (writer)
                            {
                                writer.QueueFaces(faceBuffer);
                            }
                        }

                        int currentProgress = Interlocked.Increment(ref processedChunks);
                        int progressPercentage = 50 + (currentProgress * 50) / totalChunks;

                        // Only report progress when it changes significantly
                        if (progressPercentage != lastReportedProgress &&
                            progressPercentage % 5 == 0) // Report every 5%
                        {
                            lock (progressLock)
                            {
                                if (progressPercentage != lastReportedProgress)
                                {
                                    lastReportedProgress = progressPercentage;
                                    progressCallback?.Invoke(progressPercentage);
                                }
                            }
                        }
                    });
                });

                // Report final progress
                if (lastReportedProgress != 100)
                {
                    progressCallback?.Invoke(100);
                }
            }

            Triangle.WriteMaterialFile(outputPath);
        }

        private static async Task ExportVoxelMeshMemoryOptimizedAsync(
            string outputPath,
            ChunkedVolume grayVol,
            ChunkedLabelVolume labelVol,
            int minThreshold,
            int maxThreshold,
            bool[] labelVisibility,
            int sliceX, int sliceY, int sliceZ,
            bool showSlices,
            MeshExportOptions options,
            Action<int> progressCallback = null)
        {
            var chunks = CreateChunks(grayVol, 1);

            using (var writer = new FastOBJWriter(outputPath))
            {
                int totalChunks = chunks.Count;
                int processedChunks = 0;
                int globalVertexCounter = 0;
                int lastReportedProgress = -1;
                var progressLock = new object();

                await Task.Run(() =>
                {
                    Parallel.ForEach(chunks, new ParallelOptions { MaxDegreeOfParallelism = ProcessorCount }, chunk =>
                    {
                        var buffer = new StringBuilder();
                        int localVertexStart = 0;
                        int verticesWritten = 0;

                        lock (writer)
                        {
                            localVertexStart = globalVertexCounter;
                        }

                        for (int z = chunk.StartZ; z < chunk.EndZ; z++)
                        {
                            for (int y = chunk.StartY; y < chunk.EndY; y++)
                            {
                                for (int x = chunk.StartX; x < chunk.EndX; x++)
                                {
                                    if (ShouldIncludeVoxel(x, y, z, grayVol, labelVol, minThreshold, maxThreshold,
                                                         labelVisibility, sliceX, sliceY, sliceZ, showSlices, options))
                                    {
                                        // Write cube vertices
                                        lock (writer)
                                        {
                                            writer.WriteVertex(new Vector3(x, y, z));
                                            writer.WriteVertex(new Vector3(x + 1, y, z));
                                            writer.WriteVertex(new Vector3(x + 1, y + 1, z));
                                            writer.WriteVertex(new Vector3(x, y + 1, z));
                                            writer.WriteVertex(new Vector3(x, y, z + 1));
                                            writer.WriteVertex(new Vector3(x + 1, y, z + 1));
                                            writer.WriteVertex(new Vector3(x + 1, y + 1, z + 1));
                                            writer.WriteVertex(new Vector3(x, y + 1, z + 1));
                                        }

                                        // Write cube faces with indices relative to the global vertex counter
                                        int b = localVertexStart + verticesWritten + 1; // OBJ indices start at 1
                                        buffer.AppendLine($"f {b} {b + 1} {b + 2}");
                                        buffer.AppendLine($"f {b} {b + 2} {b + 3}");
                                        buffer.AppendLine($"f {b + 4} {b + 5} {b + 6}");
                                        buffer.AppendLine($"f {b + 4} {b + 6} {b + 7}");
                                        buffer.AppendLine($"f {b} {b + 4} {b + 5}");
                                        buffer.AppendLine($"f {b} {b + 5} {b + 1}");
                                        buffer.AppendLine($"f {b + 1} {b + 5} {b + 6}");
                                        buffer.AppendLine($"f {b + 1} {b + 6} {b + 2}");
                                        buffer.AppendLine($"f {b + 2} {b + 6} {b + 7}");
                                        buffer.AppendLine($"f {b + 2} {b + 7} {b + 3}");
                                        buffer.AppendLine($"f {b + 3} {b + 7} {b + 4}");
                                        buffer.AppendLine($"f {b + 3} {b + 4} {b}");

                                        verticesWritten += 8;

                                        // Flush to file periodically to keep memory usage low
                                        if (buffer.Length > 8192)
                                        {
                                            lock (writer)
                                            {
                                                writer.QueueFaces(buffer);
                                                buffer.Clear();
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        // Write chunk data to file atomically
                        lock (writer)
                        {
                            globalVertexCounter += verticesWritten;
                            if (buffer.Length > 0)
                            {
                                writer.QueueFaces(buffer);
                            }
                        }

                        int currentProgress = Interlocked.Increment(ref processedChunks);
                        int progressPercentage = (currentProgress * 100) / totalChunks;

                        // Only report progress when it changes significantly
                        if (progressPercentage != lastReportedProgress &&
                            progressPercentage % 5 == 0) // Report every 5%
                        {
                            lock (progressLock)
                            {
                                if (progressPercentage != lastReportedProgress)
                                {
                                    lastReportedProgress = progressPercentage;
                                    progressCallback?.Invoke(progressPercentage);
                                }
                            }
                        }
                    });
                });

                // Report final progress
                if (lastReportedProgress != 100)
                {
                    progressCallback?.Invoke(100);
                }
            }

            Triangle.WriteMaterialFile(outputPath);
        }

        private static List<Chunk> CreateChunks(ChunkedVolume volume, int step)
        {
            var chunks = new List<Chunk>();

            for (int z = 0; z < volume.Depth; z += CHUNK_SIZE)
            {
                for (int y = 0; y < volume.Height; y += CHUNK_SIZE)
                {
                    for (int x = 0; x < volume.Width; x += CHUNK_SIZE)
                    {
                        chunks.Add(new Chunk
                        {
                            StartX = x,
                            StartY = y,
                            StartZ = z,
                            EndX = Math.Min(x + CHUNK_SIZE, volume.Width - step),
                            EndY = Math.Min(y + CHUNK_SIZE, volume.Height - step),
                            EndZ = Math.Min(z + CHUNK_SIZE, volume.Depth - step)
                        });
                    }
                }
            }

            return chunks;
        }

        private static long EstimateTriangleCount(
            ChunkedVolume grayVol,
            ChunkedLabelVolume labelVol,
            int minThreshold,
            int maxThreshold,
            bool[] labelVisibility,
            int sliceX, int sliceY, int sliceZ,
            bool showSlices,
            MeshExportOptions options,
            int step)
        {
            // Sample the volume to estimate triangle count
            long triangleCount = 0;
            int sampleStep = Math.Max(8, step * 2); // Sample at lower resolution

            for (int z = 0; z < grayVol.Depth - step; z += sampleStep)
            {
                for (int y = 0; y < grayVol.Height - step; y += sampleStep)
                {
                    for (int x = 0; x < grayVol.Width - step; x += sampleStep)
                    {
                        if (ShouldIncludeVoxel(x, y, z, grayVol, labelVol, minThreshold, maxThreshold,
                                             labelVisibility, sliceX, sliceY, sliceZ, showSlices, options))
                        {
                            if (IsOnSurface(x, y, z, step, grayVol, labelVol, minThreshold, maxThreshold,
                                          labelVisibility, sliceX, sliceY, sliceZ, showSlices, options))
                            {
                                // Estimate 2 triangles per exposed face, max 12 triangles per voxel
                                triangleCount += 6; // Average estimate
                            }
                        }
                    }
                }
            }

            // Scale up estimate based on sampling ratio
            double sampleRatio = Math.Pow((double)sampleStep / step, 3);
            return (long)(triangleCount * sampleRatio);
        }

        private static List<TriangleWithVertices> ProcessChunkForSTL(
            Chunk chunk,
            ChunkedVolume grayVol,
            ChunkedLabelVolume labelVol,
            int minThreshold,
            int maxThreshold,
            bool[] labelVisibility,
            int sliceX, int sliceY, int sliceZ,
            bool showSlices,
            MeshExportOptions options,
            int step)
        {
            var triangles = new List<TriangleWithVertices>();

            for (int z = chunk.StartZ; z < chunk.EndZ; z += step)
            {
                for (int y = chunk.StartY; y < chunk.EndY; y += step)
                {
                    for (int x = chunk.StartX; x < chunk.EndX; x += step)
                    {
                        if (ShouldIncludeVoxel(x, y, z, grayVol, labelVol, minThreshold, maxThreshold,
                                             labelVisibility, sliceX, sliceY, sliceZ, showSlices, options))
                        {
                            if (IsOnSurface(x, y, z, step, grayVol, labelVol, minThreshold, maxThreshold,
                                          labelVisibility, sliceX, sliceY, sliceZ, showSlices, options))
                            {
                                AddExposedFacesToList(x, y, z, step, triangles, grayVol, labelVol,
                                    minThreshold, maxThreshold, labelVisibility, sliceX, sliceY, sliceZ, showSlices, options);
                            }
                        }
                    }
                }
            }

            return triangles;
        }

        private static HashSet<Vector3> CollectChunkVertices(
            Chunk chunk,
            ChunkedVolume grayVol,
            ChunkedLabelVolume labelVol,
            int minThreshold,
            int maxThreshold,
            bool[] labelVisibility,
            int sliceX, int sliceY, int sliceZ,
            bool showSlices,
            MeshExportOptions options,
            int step)
        {
            var vertices = new HashSet<Vector3>();

            for (int z = chunk.StartZ; z < chunk.EndZ; z += step)
            {
                for (int y = chunk.StartY; y < chunk.EndY; y += step)
                {
                    for (int x = chunk.StartX; x < chunk.EndX; x += step)
                    {
                        if (ShouldIncludeVoxel(x, y, z, grayVol, labelVol, minThreshold, maxThreshold,
                                             labelVisibility, sliceX, sliceY, sliceZ, showSlices, options))
                        {
                            if (IsOnSurface(x, y, z, step, grayVol, labelVol, minThreshold, maxThreshold,
                                          labelVisibility, sliceX, sliceY, sliceZ, showSlices, options))
                            {
                                AddExposedFaceVertices(x, y, z, step, vertices, grayVol, labelVol,
                                    minThreshold, maxThreshold, labelVisibility, sliceX, sliceY, sliceZ, showSlices, options);
                            }
                        }
                    }
                }
            }

            return vertices;
        }

        private static List<Triangle> ProcessChunkForOBJ(
            Chunk chunk,
            ConcurrentDictionary<Vector3, int> vertexMap,
            ChunkedVolume grayVol,
            ChunkedLabelVolume labelVol,
            int minThreshold,
            int maxThreshold,
            bool[] labelVisibility,
            int sliceX, int sliceY, int sliceZ,
            bool showSlices,
            MeshExportOptions options,
            int step)
        {
            var triangles = new List<Triangle>();

            for (int z = chunk.StartZ; z < chunk.EndZ; z += step)
            {
                for (int y = chunk.StartY; y < chunk.EndY; y += step)
                {
                    for (int x = chunk.StartX; x < chunk.EndX; x += step)
                    {
                        if (ShouldIncludeVoxel(x, y, z, grayVol, labelVol, minThreshold, maxThreshold,
                                             labelVisibility, sliceX, sliceY, sliceZ, showSlices, options))
                        {
                            if (IsOnSurface(x, y, z, step, grayVol, labelVol, minThreshold, maxThreshold,
                                          labelVisibility, sliceX, sliceY, sliceZ, showSlices, options))
                            {
                                AddExposedFacesToTriangles(x, y, z, step, triangles, vertexMap, grayVol, labelVol,
                                    minThreshold, maxThreshold, labelVisibility, sliceX, sliceY, sliceZ, showSlices, options);
                            }
                        }
                    }
                }
            }

            return triangles;
        }

        private static void AddExposedFacesToList(
            int x, int y, int z, int step,
            List<TriangleWithVertices> triangles,
            ChunkedVolume grayVol,
            ChunkedLabelVolume labelVol,
            int minThreshold,
            int maxThreshold,
            bool[] labelVisibility,
            int sliceX, int sliceY, int sliceZ,
            bool showSlices,
            MeshExportOptions options)
        {
            // Check each face
            CheckAndAddFaceToList(x, y, z, step, Forward, triangles, grayVol, labelVol, minThreshold, maxThreshold,
                            labelVisibility, sliceX, sliceY, sliceZ, showSlices, options);
            CheckAndAddFaceToList(x, y, z, step, Backward, triangles, grayVol, labelVol, minThreshold, maxThreshold,
                            labelVisibility, sliceX, sliceY, sliceZ, showSlices, options);
            CheckAndAddFaceToList(x, y, z, step, Left, triangles, grayVol, labelVol, minThreshold, maxThreshold,
                            labelVisibility, sliceX, sliceY, sliceZ, showSlices, options);
            CheckAndAddFaceToList(x, y, z, step, Right, triangles, grayVol, labelVol, minThreshold, maxThreshold,
                            labelVisibility, sliceX, sliceY, sliceZ, showSlices, options);
            CheckAndAddFaceToList(x, y, z, step, Up, triangles, grayVol, labelVol, minThreshold, maxThreshold,
                            labelVisibility, sliceX, sliceY, sliceZ, showSlices, options);
            CheckAndAddFaceToList(x, y, z, step, Down, triangles, grayVol, labelVol, minThreshold, maxThreshold,
                            labelVisibility, sliceX, sliceY, sliceZ, showSlices, options);
        }

        private static void AddExposedFaceVertices(
            int x, int y, int z, int step,
            HashSet<Vector3> vertices,
            ChunkedVolume grayVol,
            ChunkedLabelVolume labelVol,
            int minThreshold,
            int maxThreshold,
            bool[] labelVisibility,
            int sliceX, int sliceY, int sliceZ,
            bool showSlices,
            MeshExportOptions options)
        {
            // Check each face
            CheckAndAddFaceVertices(x, y, z, step, Forward, vertices, grayVol, labelVol, minThreshold, maxThreshold,
                            labelVisibility, sliceX, sliceY, sliceZ, showSlices, options);
            CheckAndAddFaceVertices(x, y, z, step, Backward, vertices, grayVol, labelVol, minThreshold, maxThreshold,
                            labelVisibility, sliceX, sliceY, sliceZ, showSlices, options);
            CheckAndAddFaceVertices(x, y, z, step, Left, vertices, grayVol, labelVol, minThreshold, maxThreshold,
                            labelVisibility, sliceX, sliceY, sliceZ, showSlices, options);
            CheckAndAddFaceVertices(x, y, z, step, Right, vertices, grayVol, labelVol, minThreshold, maxThreshold,
                            labelVisibility, sliceX, sliceY, sliceZ, showSlices, options);
            CheckAndAddFaceVertices(x, y, z, step, Up, vertices, grayVol, labelVol, minThreshold, maxThreshold,
                            labelVisibility, sliceX, sliceY, sliceZ, showSlices, options);
            CheckAndAddFaceVertices(x, y, z, step, Down, vertices, grayVol, labelVol, minThreshold, maxThreshold,
                            labelVisibility, sliceX, sliceY, sliceZ, showSlices, options);
        }

        private static void AddExposedFacesToTriangles(
            int x, int y, int z, int step,
            List<Triangle> triangles,
            ConcurrentDictionary<Vector3, int> vertexMap,
            ChunkedVolume grayVol,
            ChunkedLabelVolume labelVol,
            int minThreshold,
            int maxThreshold,
            bool[] labelVisibility,
            int sliceX, int sliceY, int sliceZ,
            bool showSlices,
            MeshExportOptions options)
        {
            // Check each face
            CheckAndAddFaceTriangle(x, y, z, step, Forward, triangles, vertexMap, grayVol, labelVol, minThreshold, maxThreshold,
                            labelVisibility, sliceX, sliceY, sliceZ, showSlices, options);
            CheckAndAddFaceTriangle(x, y, z, step, Backward, triangles, vertexMap, grayVol, labelVol, minThreshold, maxThreshold,
                            labelVisibility, sliceX, sliceY, sliceZ, showSlices, options);
            CheckAndAddFaceTriangle(x, y, z, step, Left, triangles, vertexMap, grayVol, labelVol, minThreshold, maxThreshold,
                            labelVisibility, sliceX, sliceY, sliceZ, showSlices, options);
            CheckAndAddFaceTriangle(x, y, z, step, Right, triangles, vertexMap, grayVol, labelVol, minThreshold, maxThreshold,
                            labelVisibility, sliceX, sliceY, sliceZ, showSlices, options);
            CheckAndAddFaceTriangle(x, y, z, step, Up, triangles, vertexMap, grayVol, labelVol, minThreshold, maxThreshold,
                            labelVisibility, sliceX, sliceY, sliceZ, showSlices, options);
            CheckAndAddFaceTriangle(x, y, z, step, Down, triangles, vertexMap, grayVol, labelVol, minThreshold, maxThreshold,
                            labelVisibility, sliceX, sliceY, sliceZ, showSlices, options);
        }

        private static void CheckAndAddFaceToList(
            int x, int y, int z, int step, Vector3 faceNormal,
            List<TriangleWithVertices> triangles,
            ChunkedVolume grayVol,
            ChunkedLabelVolume labelVol,
            int minThreshold,
            int maxThreshold,
            bool[] labelVisibility,
            int sliceX, int sliceY, int sliceZ,
            bool showSlices,
            MeshExportOptions options)
        {
            // Calculate neighbor position
            int nx = x + (int)(faceNormal.X * step);
            int ny = y + (int)(faceNormal.Y * step);
            int nz = z + (int)(faceNormal.Z * step);

            // Check if face is exposed
            if (nx < 0 || nx >= grayVol.Width || ny < 0 || ny >= grayVol.Height || nz < 0 || nz >= grayVol.Depth ||
                !ShouldIncludeVoxel(nx, ny, nz, grayVol, labelVol, minThreshold, maxThreshold,
                                  labelVisibility, sliceX, sliceY, sliceZ, showSlices, options))
            {
                // Get face vertices
                Vector3[] vertices = GetFaceVertices(x, y, z, step, faceNormal);

                // Add two triangles for the quad
                triangles.Add(new TriangleWithVertices { Normal = faceNormal, Vertices = new[] { vertices[0], vertices[1], vertices[2] } });
                triangles.Add(new TriangleWithVertices { Normal = faceNormal, Vertices = new[] { vertices[0], vertices[2], vertices[3] } });
            }
        }

        private static void CheckAndAddFaceVertices(
            int x, int y, int z, int step, Vector3 faceNormal,
            HashSet<Vector3> vertices,
            ChunkedVolume grayVol,
            ChunkedLabelVolume labelVol,
            int minThreshold,
            int maxThreshold,
            bool[] labelVisibility,
            int sliceX, int sliceY, int sliceZ,
            bool showSlices,
            MeshExportOptions options)
        {
            // Calculate neighbor position
            int nx = x + (int)(faceNormal.X * step);
            int ny = y + (int)(faceNormal.Y * step);
            int nz = z + (int)(faceNormal.Z * step);

            // Check if face is exposed
            if (nx < 0 || nx >= grayVol.Width || ny < 0 || ny >= grayVol.Height || nz < 0 || nz >= grayVol.Depth ||
                !ShouldIncludeVoxel(nx, ny, nz, grayVol, labelVol, minThreshold, maxThreshold,
                                  labelVisibility, sliceX, sliceY, sliceZ, showSlices, options))
            {
                // Get face vertices
                Vector3[] faceVerts = GetFaceVertices(x, y, z, step, faceNormal);
                foreach (var vert in faceVerts)
                {
                    vertices.Add(vert);
                }
            }
        }

        private static void CheckAndAddFaceTriangle(
            int x, int y, int z, int step, Vector3 faceNormal,
            List<Triangle> triangles,
            ConcurrentDictionary<Vector3, int> vertexMap,
            ChunkedVolume grayVol,
            ChunkedLabelVolume labelVol,
            int minThreshold,
            int maxThreshold,
            bool[] labelVisibility,
            int sliceX, int sliceY, int sliceZ,
            bool showSlices,
            MeshExportOptions options)
        {
            // Calculate neighbor position
            int nx = x + (int)(faceNormal.X * step);
            int ny = y + (int)(faceNormal.Y * step);
            int nz = z + (int)(faceNormal.Z * step);

            // Check if face is exposed
            if (nx < 0 || nx >= grayVol.Width || ny < 0 || ny >= grayVol.Height || nz < 0 || nz >= grayVol.Depth ||
                !ShouldIncludeVoxel(nx, ny, nz, grayVol, labelVol, minThreshold, maxThreshold,
                                  labelVisibility, sliceX, sliceY, sliceZ, showSlices, options))
            {
                // Get face vertices
                Vector3[] vertices = GetFaceVertices(x, y, z, step, faceNormal);

                // Get vertex indices
                int[] indices = new int[4];
                for (int i = 0; i < 4; i++)
                {
                    if (!vertexMap.TryGetValue(vertices[i], out indices[i]))
                        throw new InvalidOperationException("Vertex not found in map");
                }

                // Add two triangles for the quad
                triangles.Add(new Triangle(indices[0], indices[1], indices[2], faceNormal));
                triangles.Add(new Triangle(indices[0], indices[2], indices[3], faceNormal));
            }
        }

        private static Vector3[] GetFaceVertices(int x, int y, int z, int step, Vector3 normal)
        {
            // Define face vertices based on normal direction
            if (normal == Forward) // -Z
            {
                return new[] {
                    new Vector3(x, y, z),
                    new Vector3(x+step, y, z),
                    new Vector3(x+step, y+step, z),
                    new Vector3(x, y+step, z)
                };
            }
            else if (normal == Backward) // +Z
            {
                return new[] {
                    new Vector3(x, y, z+step),
                    new Vector3(x, y+step, z+step),
                    new Vector3(x+step, y+step, z+step),
                    new Vector3(x+step, y, z+step)
                };
            }
            else if (normal == Left) // -X
            {
                return new[] {
                    new Vector3(x, y, z),
                    new Vector3(x, y+step, z),
                    new Vector3(x, y+step, z+step),
                    new Vector3(x, y, z+step)
                };
            }
            else if (normal == Right) // +X
            {
                return new[] {
                    new Vector3(x+step, y, z),
                    new Vector3(x+step, y, z+step),
                    new Vector3(x+step, y+step, z+step),
                    new Vector3(x+step, y+step, z)
                };
            }
            else if (normal == Down) // -Y
            {
                return new[] {
                    new Vector3(x, y, z),
                    new Vector3(x, y, z+step),
                    new Vector3(x+step, y, z+step),
                    new Vector3(x+step, y, z)
                };
            }
            else // Up (+Y)
            {
                return new[] {
                    new Vector3(x, y+step, z),
                    new Vector3(x+step, y+step, z),
                    new Vector3(x+step, y+step, z+step),
                    new Vector3(x, y+step, z+step)
                };
            }
        }

        private static bool IsOnSurface(
            int x, int y, int z, int step,
            ChunkedVolume grayVol,
            ChunkedLabelVolume labelVol,
            int minThreshold,
            int maxThreshold,
            bool[] labelVisibility,
            int sliceX, int sliceY, int sliceZ,
            bool showSlices,
            MeshExportOptions options)
        {
            // Check if at volume boundary
            if (x <= 0 || x >= grayVol.Width - step ||
                y <= 0 || y >= grayVol.Height - step ||
                z <= 0 || z >= grayVol.Depth - step)
                return true;

            // Check 6 neighbors
            if (!ShouldIncludeVoxel(x - step, y, z, grayVol, labelVol, minThreshold, maxThreshold,
                                  labelVisibility, sliceX, sliceY, sliceZ, showSlices, options)) return true;
            if (!ShouldIncludeVoxel(x + step, y, z, grayVol, labelVol, minThreshold, maxThreshold,
                                  labelVisibility, sliceX, sliceY, sliceZ, showSlices, options)) return true;
            if (!ShouldIncludeVoxel(x, y - step, z, grayVol, labelVol, minThreshold, maxThreshold,
                                  labelVisibility, sliceX, sliceY, sliceZ, showSlices, options)) return true;
            if (!ShouldIncludeVoxel(x, y + step, z, grayVol, labelVol, minThreshold, maxThreshold,
                                  labelVisibility, sliceX, sliceY, sliceZ, showSlices, options)) return true;
            if (!ShouldIncludeVoxel(x, y, z - step, grayVol, labelVol, minThreshold, maxThreshold,
                                  labelVisibility, sliceX, sliceY, sliceZ, showSlices, options)) return true;
            if (!ShouldIncludeVoxel(x, y, z + step, grayVol, labelVol, minThreshold, maxThreshold,
                                  labelVisibility, sliceX, sliceY, sliceZ, showSlices, options)) return true;

            return false;
        }

        private static bool ShouldIncludeVoxel(
            int x, int y, int z,
            ChunkedVolume grayVol,
            ChunkedLabelVolume labelVol,
            int minThreshold,
            int maxThreshold,
            bool[] labelVisibility,
            int sliceX, int sliceY, int sliceZ,
            bool showSlices,
            MeshExportOptions options)
        {
            // Bounds check
            if (x < 0 || x >= grayVol.Width || y < 0 || y >= grayVol.Height || z < 0 || z >= grayVol.Depth)
                return false;

            // Apply orthoslice clipping
            if (showSlices && (x > sliceX || y > sliceY || z > sliceZ))
                return false;

            // Apply existing cutting planes
            if (options.ApplyPlaneCut && IsCutByPlane(x, y, z, options, grayVol))
                return false;

            // Apply rotating clipping plane
            if (options.UseClippingPlane && IsCutByClippingPlane(x, y, z, options, grayVol))
                return false;

            bool include = false;

            // Check grayscale volume
            if (options.ExportGrayscale)
            {
                byte gVal = grayVol[x, y, z];
                if (gVal >= minThreshold && gVal <= maxThreshold)
                {
                    include = true;
                }
            }

            // Check materials
            if (options.ExportMaterials && labelVol != null)
            {
                byte label = labelVol[x, y, z];
                if (label > 0 && label < labelVisibility.Length && labelVisibility[label])
                {
                    // Exclude exterior if requested
                    if (options.ExcludeExterior && label == 0)
                    {
                        // Skip exterior material
                    }
                    else
                    {
                        include = true;
                    }
                }
            }

            return include;
        }

        private static bool IsCutByPlane(int x, int y, int z, MeshExportOptions options, ChunkedVolume grayVol)
        {
            if (!options.ApplyPlaneCut) return false;

            // Calculate cut positions in voxel coordinates
            int cutXPos = (int)(options.CutXPosition * grayVol.Width);
            int cutYPos = (int)(options.CutYPosition * grayVol.Height);
            int cutZPos = (int)(options.CutZPosition * grayVol.Depth);

            // Check X cutting plane
            if (options.CutXEnabled)
            {
                if (options.CutXDirection > 0) // Forward cut
                {
                    if (x > cutXPos) return true;
                }
                else // Backward cut
                {
                    if (x < cutXPos) return true;
                }
            }

            // Check Y cutting plane
            if (options.CutYEnabled)
            {
                if (options.CutYDirection > 0) // Forward cut
                {
                    if (y > cutYPos) return true;
                }
                else // Backward cut
                {
                    if (y < cutYPos) return true;
                }
            }

            // Check Z cutting plane
            if (options.CutZEnabled)
            {
                if (options.CutZDirection > 0) // Forward cut
                {
                    if (z > cutZPos) return true;
                }
                else // Backward cut
                {
                    if (z < cutZPos) return true;
                }
            }

            return false;
        }

        private static bool IsCutByClippingPlane(int x, int y, int z, MeshExportOptions options, ChunkedVolume grayVol)
        {
            if (!options.UseClippingPlane) return false;

            // Convert to normalized coordinates (0-1)
            float nx = (float)x / grayVol.Width;
            float ny = (float)y / grayVol.Height;
            float nz = (float)z / grayVol.Depth;

            // Calculate distance from point to plane
            Vector3 point = new Vector3(nx, ny, nz);
            Vector3 planePoint = options.ClippingPlaneNormal * options.ClippingPlaneDistance;
            float distance = Vector3.Dot(options.ClippingPlaneNormal, point - planePoint);

            // Check which side of the plane the point is on
            if (options.ClippingPlaneMirror)
            {
                return distance < 0; // Cut the negative side
            }
            else
            {
                return distance > 0; // Cut the positive side
            }
        }
    }

    // Helper structures
    public struct Chunk
    {
        public int StartX, StartY, StartZ;
        public int EndX, EndY, EndZ;
    }

    public struct TriangleWithVertices
    {
        public Vector3[] Vertices;
        public Vector3 Normal;
    }
}