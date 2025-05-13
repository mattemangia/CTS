using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CTS
{
    /// <summary>
    /// Manages caching of simulation frames to disk for efficient replay and export
    /// Now with async caching for better performance
    /// </summary>
    public class FrameCacheManager : IDisposable
    {
        private readonly string cacheDirectory;
        private readonly int width, height, depth;
        private readonly object lockObject = new object();
        private BinaryWriter metadataWriter;
        private FileStream metadataStream;
        private List<FrameMetadata> frameMetadata = new List<FrameMetadata>();
        private bool isDisposed = false;

        // Async caching components
        private readonly ConcurrentQueue<FrameData> frameQueue = new ConcurrentQueue<FrameData>();
        private readonly ManualResetEvent frameAvailableEvent = new ManualResetEvent(false);
        private readonly Thread cacheWriterThread;
        private volatile bool stopRequested = false;
        private readonly SemaphoreSlim metadataWriteSemaphore = new SemaphoreSlim(1, 1);

        // Stats for monitoring
        private int queuedFrames = 0;
        private int savedFrames = 0;
        private readonly int maxQueueSize = 50; // Limit queue size to prevent memory issues

        // Internal frame data structure for queuing
        private class FrameData
        {
            public int TimeStep;
            public float[,,] VX, VY, VZ;
            public float[,] Tomography, CrossSection;
            public float PWaveValue, SWaveValue;
            public float PWavePathProgress, SWavePathProgress;
            public float[] PWaveTimeSeries, SWaveTimeSeries;
            public string FileName;
        }

        public class FrameMetadata
        {
            public int TimeStep { get; set; }
            public string FileName { get; set; }
            public float PWaveValue { get; set; }
            public float SWaveValue { get; set; }
            public float PWavePathProgress { get; set; }
            public float SWavePathProgress { get; set; }
        }

        public FrameCacheManager(string baseDir, int width, int height, int depth, string sessionId = null)
        {
            this.width = width;
            this.height = height;
            this.depth = depth;

            // Create unique cache directory
            string dirName = sessionId ?? $"cache_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
            cacheDirectory = Path.Combine(baseDir, dirName);
            Directory.CreateDirectory(cacheDirectory);

            // Initialize metadata file
            string metadataPath = Path.Combine(cacheDirectory, "metadata.dat");
            metadataStream = new FileStream(metadataPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            metadataWriter = new BinaryWriter(metadataStream);

            // Write header
            metadataWriter.Write("ACSIM"); // Magic string
            metadataWriter.Write(1); // Version
            metadataWriter.Write(width);
            metadataWriter.Write(height);
            metadataWriter.Write(depth);
            metadataWriter.Flush();

            // Start background cache writer thread
            cacheWriterThread = new Thread(CacheWriterThreadProc)
            {
                Name = "FrameCacheWriter",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal // Lower priority to not interfere with simulation
            };
            cacheWriterThread.Start();

            Logger.Log($"[FrameCacheManager] Created cache directory: {cacheDirectory}");
        }

        public string CacheDirectory => cacheDirectory;
        public int FrameCount => frameMetadata.Count;
        public int QueuedFrames => queuedFrames;
        public int SavedFrames => savedFrames;

        // Original method signature kept for compatibility, but now async
        public void SaveFrame(int timeStep,
            float[,,] vx, float[,,] vy, float[,,] vz,
            float[,] tomography, float[,] crossSection,
            float pWaveValue, float sWaveValue,
            float pWaveProgress, float sWaveProgress,
            float[] pWaveTimeSeries = null, float[] sWaveTimeSeries = null)
        {
            if (isDisposed || stopRequested) return;

            // Check queue size to prevent memory issues
            if (queuedFrames > maxQueueSize)
            {
                // Log warning but don't block - drop frame if necessary
                if (queuedFrames > maxQueueSize * 2)
                {
                    Logger.Log($"[FrameCacheManager] Warning: Frame queue overflow. Dropping frame {timeStep}");
                    return;
                }

                // Wait briefly if queue is getting full
                int waitCount = 0;
                while (queuedFrames > maxQueueSize && waitCount < 10)
                {
                    Thread.Sleep(1);
                    waitCount++;
                }
            }

            // Create deep copies of arrays to avoid race conditions
            var frameData = new FrameData
            {
                TimeStep = timeStep,
                VX = CloneArray3D(vx),
                VY = CloneArray3D(vy),
                VZ = CloneArray3D(vz),
                Tomography = CloneArray2D(tomography),
                CrossSection = CloneArray2D(crossSection),
                PWaveValue = pWaveValue,
                SWaveValue = sWaveValue,
                PWavePathProgress = pWaveProgress,
                SWavePathProgress = sWaveProgress,
                PWaveTimeSeries = pWaveTimeSeries?.Clone() as float[],
                SWaveTimeSeries = sWaveTimeSeries?.Clone() as float[],
                FileName = $"frame_{timeStep:D8}.dat"
            };

            // Queue the frame for async saving
            frameQueue.Enqueue(frameData);
            Interlocked.Increment(ref queuedFrames);
            frameAvailableEvent.Set();

            // Add metadata immediately for consistent ordering
            lock (lockObject)
            {
                var metadata = new FrameMetadata
                {
                    TimeStep = timeStep,
                    FileName = frameData.FileName,
                    PWaveValue = pWaveValue,
                    SWaveValue = sWaveValue,
                    PWavePathProgress = pWaveProgress,
                    SWavePathProgress = sWaveProgress
                };
                frameMetadata.Add(metadata);
            }

            if (timeStep % 100 == 0)
            {
                Logger.Log($"[FrameCacheManager] Queued frame {timeStep} (Queue: {queuedFrames}, Saved: {savedFrames})");
            }
        }

        private void CacheWriterThreadProc()
        {
            Logger.Log("[FrameCacheManager] Cache writer thread started");

            while (!stopRequested)
            {
                // Wait for frames to be available
                frameAvailableEvent.WaitOne(100);

                while (frameQueue.TryDequeue(out FrameData frameData))
                {
                    try
                    {
                        SaveFrameToDisk(frameData);
                        Interlocked.Decrement(ref queuedFrames);
                        Interlocked.Increment(ref savedFrames);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[FrameCacheManager] Error saving frame {frameData.TimeStep}: {ex.Message}");
                    }
                }

                // Reset event if queue is empty
                if (frameQueue.IsEmpty)
                {
                    frameAvailableEvent.Reset();
                }
            }

            // Process any remaining frames before exiting
            while (frameQueue.TryDequeue(out FrameData frameData))
            {
                try
                {
                    SaveFrameToDisk(frameData);
                    Interlocked.Decrement(ref queuedFrames);
                    Interlocked.Increment(ref savedFrames);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[FrameCacheManager] Error saving final frame {frameData.TimeStep}: {ex.Message}");
                }
            }

            Logger.Log("[FrameCacheManager] Cache writer thread ended");
        }

        private void SaveFrameToDisk(FrameData frameData)
        {
            string framePath = Path.Combine(cacheDirectory, frameData.FileName);

            // Write frame data
            using (var stream = new FileStream(framePath, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                // Write frame header
                writer.Write("FRAME"); // Magic string
                writer.Write(frameData.TimeStep);

                // Write 3D fields
                WriteArray3D(writer, frameData.VX);
                WriteArray3D(writer, frameData.VY);
                WriteArray3D(writer, frameData.VZ);

                // Write 2D fields
                WriteArray2D(writer, frameData.Tomography);
                WriteArray2D(writer, frameData.CrossSection);

                // Write scalar values
                writer.Write(frameData.PWaveValue);
                writer.Write(frameData.SWaveValue);
                writer.Write(frameData.PWavePathProgress);
                writer.Write(frameData.SWavePathProgress);

                // Write time series if available
                if (frameData.PWaveTimeSeries != null)
                {
                    writer.Write(frameData.PWaveTimeSeries.Length);
                    foreach (float val in frameData.PWaveTimeSeries)
                        writer.Write(val);
                }
                else
                {
                    writer.Write(0);
                }

                if (frameData.SWaveTimeSeries != null)
                {
                    writer.Write(frameData.SWaveTimeSeries.Length);
                    foreach (float val in frameData.SWaveTimeSeries)
                        writer.Write(val);
                }
                else
                {
                    writer.Write(0);
                }
            }

            // Update metadata file asynchronously
            Task.Run(async () =>
            {
                await metadataWriteSemaphore.WaitAsync();
                try
                {
                    metadataWriter.Write(frameData.TimeStep);
                    metadataWriter.Write(frameData.FileName);
                    metadataWriter.Write(frameData.PWaveValue);
                    metadataWriter.Write(frameData.SWaveValue);
                    metadataWriter.Write(frameData.PWavePathProgress);
                    metadataWriter.Write(frameData.SWavePathProgress);
                    metadataWriter.Flush();
                }
                finally
                {
                    metadataWriteSemaphore.Release();
                }
            });
        }

        // Helper methods for deep copying arrays
        private float[,,] CloneArray3D(float[,,] source)
        {
            if (source == null) return null;
            int w = source.GetLength(0);
            int h = source.GetLength(1);
            int d = source.GetLength(2);
            float[,,] clone = new float[w, h, d];
            Buffer.BlockCopy(source, 0, clone, 0, w * h * d * sizeof(float));
            return clone;
        }

        private float[,] CloneArray2D(float[,] source)
        {
            if (source == null) return null;
            int w = source.GetLength(0);
            int h = source.GetLength(1);
            float[,] clone = new float[w, h];
            Buffer.BlockCopy(source, 0, clone, 0, w * h * sizeof(float));
            return clone;
        }

        public CachedFrame LoadFrame(int frameIndex)
        {
            if (isDisposed || frameIndex < 0 || frameIndex >= frameMetadata.Count)
                return null;

            // Wait for any pending saves to complete if necessary
            if (frameIndex >= savedFrames)
            {
                int waitCount = 0;
                while (frameIndex >= savedFrames && waitCount < 100) // 10 second timeout
                {
                    Thread.Sleep(100);
                    waitCount++;
                }

                if (frameIndex >= savedFrames)
                {
                    Logger.Log($"[FrameCacheManager] Frame {frameIndex} not yet saved");
                    return null;
                }
            }

            lock (lockObject)
            {
                try
                {
                    var metadata = frameMetadata[frameIndex];
                    string framePath = Path.Combine(cacheDirectory, metadata.FileName);

                    if (!File.Exists(framePath))
                    {
                        Logger.Log($"[FrameCacheManager] Frame file not found: {framePath}");
                        return null;
                    }

                    using (var stream = new FileStream(framePath, FileMode.Open, FileAccess.Read))
                    using (var reader = new BinaryReader(stream))
                    {
                        // Read and validate header
                        string magic = reader.ReadString();
                        if (magic != "FRAME")
                        {
                            Logger.Log($"[FrameCacheManager] Invalid frame file: {framePath}");
                            return null;
                        }

                        int timeStep = reader.ReadInt32();

                        // Read 3D fields
                        var vx = ReadArray3D(reader);
                        var vy = ReadArray3D(reader);
                        var vz = ReadArray3D(reader);

                        // Read 2D fields
                        var tomography = ReadArray2D(reader);
                        var crossSection = ReadArray2D(reader);

                        // Read scalar values
                        float pWaveValue = reader.ReadSingle();
                        float sWaveValue = reader.ReadSingle();
                        float pWaveProgress = reader.ReadSingle();
                        float sWaveProgress = reader.ReadSingle();

                        // Read time series
                        float[] pWaveTimeSeries = null;
                        float[] sWaveTimeSeries = null;

                        int pLength = reader.ReadInt32();
                        if (pLength > 0)
                        {
                            pWaveTimeSeries = new float[pLength];
                            for (int i = 0; i < pLength; i++)
                                pWaveTimeSeries[i] = reader.ReadSingle();
                        }

                        int sLength = reader.ReadInt32();
                        if (sLength > 0)
                        {
                            sWaveTimeSeries = new float[sLength];
                            for (int i = 0; i < sLength; i++)
                                sWaveTimeSeries[i] = reader.ReadSingle();
                        }

                        return new CachedFrame
                        {
                            TimeStep = timeStep,
                            VX = vx,
                            VY = vy,
                            VZ = vz,
                            Tomography = tomography,
                            CrossSection = crossSection,
                            PWaveValue = pWaveValue,
                            SWaveValue = sWaveValue,
                            PWavePathProgress = pWaveProgress,
                            SWavePathProgress = sWaveProgress,
                            PWaveTimeSeries = pWaveTimeSeries,
                            SWaveTimeSeries = sWaveTimeSeries
                        };
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[FrameCacheManager] Error loading frame {frameIndex}: {ex.Message}");
                    return null;
                }
            }
        }

        public void LoadMetadata()
        {
            string metadataPath = Path.Combine(cacheDirectory, "metadata.dat");
            if (!File.Exists(metadataPath))
                return;

            lock (lockObject)
            {
                frameMetadata.Clear();

                using (var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read))
                using (var reader = new BinaryReader(stream))
                {
                    // Read header
                    string magic = reader.ReadString();
                    int version = reader.ReadInt32();
                    int w = reader.ReadInt32();
                    int h = reader.ReadInt32();
                    int d = reader.ReadInt32();

                    // Read entries
                    while (stream.Position < stream.Length)
                    {
                        try
                        {
                            var metadata = new FrameMetadata
                            {
                                TimeStep = reader.ReadInt32(),
                                FileName = reader.ReadString(),
                                PWaveValue = reader.ReadSingle(),
                                SWaveValue = reader.ReadSingle(),
                                PWavePathProgress = reader.ReadSingle(),
                                SWavePathProgress = reader.ReadSingle()
                            };
                            frameMetadata.Add(metadata);
                        }
                        catch
                        {
                            break;
                        }
                    }
                }

                Logger.Log($"[FrameCacheManager] Loaded {frameMetadata.Count} frame metadata entries");
            }
        }

        // Existing serialization methods remain unchanged
        private void WriteArray3D(BinaryWriter writer, float[,,] array)
        {
            int w = array.GetLength(0);
            int h = array.GetLength(1);
            int d = array.GetLength(2);

            writer.Write(w);
            writer.Write(h);
            writer.Write(d);

            for (int z = 0; z < d; z++)
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        writer.Write(array[x, y, z]);
        }

        private void WriteArray2D(BinaryWriter writer, float[,] array)
        {
            int w = array.GetLength(0);
            int h = array.GetLength(1);

            writer.Write(w);
            writer.Write(h);

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    writer.Write(array[x, y]);
        }

        private float[,,] ReadArray3D(BinaryReader reader)
        {
            int w = reader.ReadInt32();
            int h = reader.ReadInt32();
            int d = reader.ReadInt32();

            float[,,] array = new float[w, h, d];

            for (int z = 0; z < d; z++)
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        array[x, y, z] = reader.ReadSingle();

            return array;
        }

        private float[,] ReadArray2D(BinaryReader reader)
        {
            int w = reader.ReadInt32();
            int h = reader.ReadInt32();

            float[,] array = new float[w, h];

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    array[x, y] = reader.ReadSingle();

            return array;
        }

        public void WaitForAllFramesToSave(int timeoutSeconds = 60)
        {
            var startTime = DateTime.Now;
            while (queuedFrames > 0)
            {
                if ((DateTime.Now - startTime).TotalSeconds > timeoutSeconds)
                {
                    Logger.Log($"[FrameCacheManager] Timeout waiting for frames to save. {queuedFrames} still queued.");
                    break;
                }
                Thread.Sleep(100);
            }
        }

        public void Dispose()
        {
            if (isDisposed)
                return;

            lock (lockObject)
            {
                isDisposed = true;
                stopRequested = true;

                // Signal the writer thread to stop
                frameAvailableEvent.Set();

                // Wait for writer thread to finish (max 5 seconds)
                if (!cacheWriterThread.Join(5000))
                {
                    Logger.Log("[FrameCacheManager] Warning: Cache writer thread did not terminate gracefully");
                }

                metadataWriter?.Dispose();
                metadataStream?.Dispose();
                frameAvailableEvent?.Dispose();
                metadataWriteSemaphore?.Dispose();

                Logger.Log($"[FrameCacheManager] Disposed (cache at: {cacheDirectory}, saved {savedFrames} frames)");
            }
        }
    }

    public class CachedFrame
    {
        public int TimeStep { get; set; }
        public float[,,] VX { get; set; }
        public float[,,] VY { get; set; }
        public float[,,] VZ { get; set; }
        public float[,] Tomography { get; set; }
        public float[,] CrossSection { get; set; }
        public float PWaveValue { get; set; }
        public float SWaveValue { get; set; }
        public float PWavePathProgress { get; set; }
        public float SWavePathProgress { get; set; }
        public float[] PWaveTimeSeries { get; set; }
        public float[] SWaveTimeSeries { get; set; }
    }
}