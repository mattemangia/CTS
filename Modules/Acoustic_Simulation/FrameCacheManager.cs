using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CTS
{
    /// <summary>
    /// Frame cache manager for acoustic simulation data with reliable file handling
    /// </summary>
    public class FrameCacheManager : IDisposable
    {
        #region Constants
        // Cache directory
        public static readonly string SINGLE_CACHE_DIR = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AcousticSimulator",
            "SimulationCache");

        // File naming patterns
        private const string METADATA_FILENAME = "metadata.dat";
        private const string FRAME_FILENAME_PATTERN = "frame_{0:D8}.dat";

        // Buffer sizes
        private const int IO_BUFFER_SIZE = 64 * 1024; // 64 KB buffer
        private const int MAX_QUEUE_SIZE = 100; // Maximum frames in queue
        #endregion

        #region Fields
        private readonly int width, height, depth;
        private readonly object metadataLock = new object();
        private readonly List<FrameMetadata> frameMetadata = new List<FrameMetadata>();
        private readonly string metadataPath;
        private readonly string cacheDirectory;
        private readonly bool readOnly;
        private bool isDisposed = false;

        // Async frame writing
        private readonly BlockingCollection<FrameData> frameQueue;
        private readonly CancellationTokenSource cancellationTokenSource;
        private readonly Task writerTask;
        private volatile int savedFrames = 0;
        private volatile int queuedFrames = 0;

        // LRU cache for loaded frames with size limit
        private readonly ConcurrentDictionary<int, WeakReference<CachedFrame>> frameCache;
        private const int MAX_CACHED_FRAMES = 10; // Small memory footprint
        #endregion

        #region Properties
        public string CacheDirectory => cacheDirectory;
        public int FrameCount => frameMetadata.Count;
        public int QueuedFrames => queuedFrames;
        public int SavedFrames => savedFrames;
        #endregion

        #region Constructors
        /// <summary>
        /// Constructor for writing (new simulation)
        /// </summary>
        public FrameCacheManager(string baseDir, int width, int height, int depth, string sessionId = null)
            : this(width, height, depth, false)
        {
            PrepareNewCache();
            WriteMetadataHeader(); // Write metadata BEFORE starting cache operations
        }

        /// <summary>
        /// Constructor for reading or writing
        /// </summary>
        public FrameCacheManager(int width, int height, int depth, bool readOnly)
        {
            this.width = width;
            this.height = height;
            this.depth = depth;
            this.readOnly = readOnly;
            this.cacheDirectory = SINGLE_CACHE_DIR;
            this.metadataPath = Path.Combine(cacheDirectory, METADATA_FILENAME);

            // Initialize frame cache
            this.frameCache = new ConcurrentDictionary<int, WeakReference<CachedFrame>>();

            // Ensure directory exists
            if (!Directory.Exists(cacheDirectory))
            {
                if (readOnly)
                    throw new DirectoryNotFoundException($"Cache directory not found: {cacheDirectory}");
                Directory.CreateDirectory(cacheDirectory);
            }

            // Start writer task if not in read-only mode
            if (!readOnly)
            {
                frameQueue = new BlockingCollection<FrameData>(MAX_QUEUE_SIZE);
                cancellationTokenSource = new CancellationTokenSource();
                writerTask = Task.Run(FrameWriterProcess, cancellationTokenSource.Token);
            }

            Logger.Log($"[FrameCacheManager] Initialized in {(readOnly ? "read-only" : "read-write")} mode");
        }
        #endregion

        #region Cache Preparation
        private void PrepareNewCache()
        {
            try
            {
                // Clean any existing files
                if (Directory.Exists(cacheDirectory))
                {
                    // Delete all frame files first
                    foreach (var file in Directory.GetFiles(cacheDirectory, "frame_*.dat"))
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[FrameCacheManager] Warning: Could not delete {file}: {ex.Message}");
                        }
                    }

                    // Delete metadata file
                    if (File.Exists(metadataPath))
                    {
                        try
                        {
                            File.Delete(metadataPath);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[FrameCacheManager] Warning: Could not delete metadata: {ex.Message}");
                        }
                    }
                }
                else
                {
                    Directory.CreateDirectory(cacheDirectory);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FrameCacheManager] Warning: Cache preparation error: {ex.Message}");
            }
        }
        #endregion

        #region Metadata Management
        /// <summary>
        /// Write the metadata header file
        /// </summary>
        private void WriteMetadataHeader()
        {
            lock (metadataLock)
            {
                try
                {
                    using (var fs = new FileStream(metadataPath, FileMode.Create, FileAccess.Write, FileShare.Read, IO_BUFFER_SIZE))
                    using (var writer = new BinaryWriter(fs))
                    {
                        // Simple header with magic bytes as raw values (not length-prefixed string)
                        byte[] magic = new byte[] { 65, 67, 83, 73, 77 }; // "ACSIM" in ASCII
                        writer.Write(magic.Length);
                        writer.Write(magic);
                        writer.Write(1); // Version
                        writer.Write(width);
                        writer.Write(height);
                        writer.Write(depth);
                        writer.Write(0); // Initial frame count placeholder
                        writer.Flush();
                    }
                    Logger.Log("[FrameCacheManager] Metadata header created");
                }
                catch (Exception ex)
                {
                    Logger.Log($"[FrameCacheManager] Error creating metadata: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Append frame metadata to the end of the metadata file
        /// </summary>
        private void AppendFrameMetadata(FrameMetadata metadata)
        {
            lock (metadataLock)
            {
                try
                {
                    using (var fs = new FileStream(metadataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, IO_BUFFER_SIZE))
                    {
                        // Update frame count first
                        fs.Position = 5 + 4 + 4 + 4 + 4 + 4; // Skip magic, version, width, height, depth
                        using (var writer = new BinaryWriter(fs))
                        {
                            writer.Write(frameMetadata.Count + 1); // Updated count

                            // Go to end of file to append the new frame metadata
                            fs.Seek(0, SeekOrigin.End);

                            // Write frame metadata
                            writer.Write(metadata.TimeStep);
                            writer.Write(metadata.FileName);
                            writer.Write(metadata.PWaveValue);
                            writer.Write(metadata.SWaveValue);
                            writer.Write(metadata.PWavePathProgress);
                            writer.Write(metadata.SWavePathProgress);
                            writer.Flush();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[FrameCacheManager] Error appending metadata: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Load the metadata file into memory
        /// </summary>
        public void LoadMetadata()
        {
            lock (metadataLock)
            {
                frameMetadata.Clear();

                if (!File.Exists(metadataPath))
                {
                    Logger.Log($"[FrameCacheManager] Metadata file not found: {metadataPath}");
                    return;
                }

                try
                {
                    using (var fs = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, IO_BUFFER_SIZE))
                    using (var reader = new BinaryReader(fs))
                    {
                        // Read header
                        int magicLength = reader.ReadInt32();
                        byte[] magicBytes = reader.ReadBytes(magicLength);
                        string magic = System.Text.Encoding.ASCII.GetString(magicBytes);

                        if (magic != "ACSIM")
                        {
                            Logger.Log($"[FrameCacheManager] Invalid metadata format: {magic}");
                            return;
                        }

                        int version = reader.ReadInt32();
                        int fileWidth = reader.ReadInt32();
                        int fileHeight = reader.ReadInt32();
                        int fileDepth = reader.ReadInt32();
                        int frameCount = reader.ReadInt32();

                        Logger.Log($"[FrameCacheManager] Metadata: v{version}, size {fileWidth}x{fileHeight}x{fileDepth}, {frameCount} frames");

                        // Read all frame entries
                        for (int i = 0; i < frameCount; i++)
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

                                // Verify the file exists
                                string framePath = Path.Combine(cacheDirectory, metadata.FileName);
                                if (File.Exists(framePath))
                                {
                                    frameMetadata.Add(metadata);
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"[FrameCacheManager] Error reading frame metadata {i}: {ex.Message}");
                                break;
                            }
                        }

                        Logger.Log($"[FrameCacheManager] Loaded {frameMetadata.Count} frame metadata entries");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[FrameCacheManager] Error loading metadata: {ex.Message}");
                }
            }
        }
        #endregion

        #region Frame Saving
        /// <summary>
        /// Save a frame to the cache
        /// </summary>
        public void SaveFrame(int timeStep,
            float[,,] vx, float[,,] vy, float[,,] vz,
            float[,] tomography, float[,] crossSection,
            float pWaveValue, float sWaveValue,
            float pWaveProgress, float sWaveProgress,
            float[] pWaveTimeSeries = null, float[] sWaveTimeSeries = null)
        {
            if (isDisposed || readOnly) return;

            try
            {
                // Create frame data
                var frameData = new FrameData
                {
                    TimeStep = timeStep,
                    VX = CloneArraySafely(vx),
                    VY = CloneArraySafely(vy),
                    VZ = CloneArraySafely(vz),
                    Tomography = CloneArraySafely(tomography),
                    CrossSection = CloneArraySafely(crossSection),
                    PWaveValue = pWaveValue,
                    SWaveValue = sWaveValue,
                    PWavePathProgress = pWaveProgress,
                    SWavePathProgress = sWaveProgress,
                    PWaveTimeSeries = CloneArraySafely(pWaveTimeSeries),
                    SWaveTimeSeries = CloneArraySafely(sWaveTimeSeries),
                    FileName = string.Format(FRAME_FILENAME_PATTERN, timeStep)
                };

                // Try to add to queue
                if (frameQueue.TryAdd(frameData, 500))
                {
                    Interlocked.Increment(ref queuedFrames);

                    // Add to metadata list
                    lock (metadataLock)
                    {
                        frameMetadata.Add(new FrameMetadata
                        {
                            TimeStep = timeStep,
                            FileName = frameData.FileName,
                            PWaveValue = pWaveValue,
                            SWaveValue = sWaveValue,
                            PWavePathProgress = pWaveProgress,
                            SWavePathProgress = sWaveProgress
                        });
                    }

                    if (timeStep % 100 == 0)
                    {
                        Logger.Log($"[FrameCacheManager] Queued frame {timeStep} (Queue: {frameQueue.Count}, Saved: {savedFrames})");
                    }
                }
                else
                {
                    Logger.Log($"[FrameCacheManager] Warning: Queue full, dropping frame {timeStep}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FrameCacheManager] Error queuing frame {timeStep}: {ex.Message}");
            }
        }

        /// <summary>
        /// Frame writer background process
        /// </summary>
        private void FrameWriterProcess()
        {
            Logger.Log("[FrameCacheManager] Frame writer process started");

            while (!cancellationTokenSource.IsCancellationRequested)
            {
                FrameData frameData = null;
                try
                {
                    // Try to get a frame from queue with timeout
                    if (frameQueue.TryTake(out frameData, 1000, cancellationTokenSource.Token))
                    {
                        // Write the frame to disk
                        WriteFrameToDisk(frameData);

                        // Update frame metadata in the index file
                        AppendFrameMetadata(new FrameMetadata
                        {
                            TimeStep = frameData.TimeStep,
                            FileName = frameData.FileName,
                            PWaveValue = frameData.PWaveValue,
                            SWaveValue = frameData.SWaveValue,
                            PWavePathProgress = frameData.PWavePathProgress,
                            SWavePathProgress = frameData.SWavePathProgress
                        });

                        Interlocked.Increment(ref savedFrames);
                        Interlocked.Decrement(ref queuedFrames);

                        if (savedFrames % 50 == 0)
                        {
                            Logger.Log($"[FrameCacheManager] Saved {savedFrames} frames (Queue: {frameQueue.Count})");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break; // Normal cancellation
                }
                catch (Exception ex)
                {
                    Logger.Log($"[FrameCacheManager] Error writing frame: {ex.Message}");

                    // Try to requeue the frame
                    if (frameData != null && !cancellationTokenSource.IsCancellationRequested)
                    {
                        if (frameQueue.TryAdd(frameData, 100))
                        {
                            Logger.Log("[FrameCacheManager] Requeued frame after error");
                        }
                    }

                    // Avoid CPU spinning on recurring errors
                    try { Thread.Sleep(100); } catch { }
                }
            }

            // Process remaining frames
            ProcessRemainingFrames();

            Logger.Log($"[FrameCacheManager] Frame writer stopped. Total saved: {savedFrames}");
        }

        /// <summary>
        /// Process any remaining frames in the queue
        /// </summary>
        private void ProcessRemainingFrames()
        {
            int remaining = frameQueue.Count;
            if (remaining > 0)
            {
                Logger.Log($"[FrameCacheManager] Processing {remaining} remaining frames");

                try
                {
                    while (frameQueue.TryTake(out FrameData frameData))
                    {
                        try
                        {
                            WriteFrameToDisk(frameData);
                            Interlocked.Increment(ref savedFrames);
                            Interlocked.Decrement(ref queuedFrames);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[FrameCacheManager] Error saving final frame: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[FrameCacheManager] Error processing remaining frames: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Write a frame to disk
        /// </summary>
        private void WriteFrameToDisk(FrameData frameData)
        {
            string framePath = Path.Combine(cacheDirectory, frameData.FileName);

            try
            {
                using (var fs = new FileStream(framePath, FileMode.Create, FileAccess.Write, FileShare.None, IO_BUFFER_SIZE))
                using (var writer = new BinaryWriter(fs))
                {
                    // Write frame header
                    writer.Write("FRAME"); // Magic string
                    writer.Write(frameData.TimeStep);

                    // Write arrays with efficient byte copy
                    WriteArray(writer, frameData.VX);
                    WriteArray(writer, frameData.VY);
                    WriteArray(writer, frameData.VZ);
                    WriteArray(writer, frameData.Tomography);
                    WriteArray(writer, frameData.CrossSection);

                    // Write scalar values
                    writer.Write(frameData.PWaveValue);
                    writer.Write(frameData.SWaveValue);
                    writer.Write(frameData.PWavePathProgress);
                    writer.Write(frameData.SWavePathProgress);

                    // Write time series
                    WriteFloatArray(writer, frameData.PWaveTimeSeries);
                    WriteFloatArray(writer, frameData.SWaveTimeSeries);

                    writer.Flush();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FrameCacheManager] Error writing frame {frameData.TimeStep}: {ex.Message}");
                throw; // Rethrow to allow requeuing
            }
        }
        #endregion

        #region Frame Loading
        /// <summary>
        /// Load a frame from cache
        /// </summary>
        public CachedFrame LoadFrame(int frameIndex, bool needFullData = true)
        {
            if (isDisposed || frameIndex < 0 || frameIndex >= frameMetadata.Count)
                return null;

            // Check cache first
            if (frameCache.TryGetValue(frameIndex, out var weakRef))
            {
                if (weakRef.TryGetTarget(out var cachedFrame))
                {
                    return cachedFrame;
                }
            }

            // Load from disk
            try
            {
                var metadata = frameMetadata[frameIndex];
                string framePath = Path.Combine(cacheDirectory, metadata.FileName);

                if (!File.Exists(framePath))
                {
                    Logger.Log($"[FrameCacheManager] Frame file not found: {framePath}");
                    return null;
                }

                CachedFrame frame = ReadFrameFromDisk(framePath, needFullData);
                if (frame != null)
                {
                    // Cache the frame with weak reference
                    frameCache[frameIndex] = new WeakReference<CachedFrame>(frame);

                    // Limit cache size
                    if (frameCache.Count > MAX_CACHED_FRAMES)
                    {
                        // Remove random entry for simplicity (approximates LRU)
                        var oldKey = frameCache.Keys.ElementAt(new Random().Next(frameCache.Count / 2));
                        frameCache.TryRemove(oldKey, out _);
                    }
                }

                return frame;
            }
            catch (Exception ex)
            {
                Logger.Log($"[FrameCacheManager] Error loading frame {frameIndex}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Read a frame from disk
        /// </summary>
        private CachedFrame ReadFrameFromDisk(string framePath, bool needFullData)
        {
            try
            {
                using (var fs = new FileStream(framePath, FileMode.Open, FileAccess.Read, FileShare.Read, IO_BUFFER_SIZE))
                using (var reader = new BinaryReader(fs))
                {
                    // Read and verify header
                    string magic = reader.ReadString();
                    if (magic != "FRAME")
                    {
                        Logger.Log($"[FrameCacheManager] Invalid frame format: {framePath}");
                        return null;
                    }

                    int timeStep = reader.ReadInt32();

                    // Create frame
                    CachedFrame frame = new CachedFrame
                    {
                        TimeStep = timeStep
                    };

                    // Read data
                    if (needFullData)
                    {
                        frame.VX = ReadArray3D(reader);
                        frame.VY = ReadArray3D(reader);
                        frame.VZ = ReadArray3D(reader);
                        frame.Tomography = ReadArray2D(reader);
                        frame.CrossSection = ReadArray2D(reader);
                    }
                    else
                    {
                        SkipArray(reader); // VX
                        SkipArray(reader); // VY
                        SkipArray(reader); // VZ
                        SkipArray(reader); // Tomography
                        SkipArray(reader); // CrossSection
                    }

                    // Read scalar values
                    frame.PWaveValue = reader.ReadSingle();
                    frame.SWaveValue = reader.ReadSingle();
                    frame.PWavePathProgress = reader.ReadSingle();
                    frame.SWavePathProgress = reader.ReadSingle();

                    // Read time series
                    if (needFullData)
                    {
                        frame.PWaveTimeSeries = ReadFloatArray(reader);
                        frame.SWaveTimeSeries = ReadFloatArray(reader);
                    }
                    else
                    {
                        SkipFloatArray(reader);
                        SkipFloatArray(reader);
                    }

                    return frame;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FrameCacheManager] Error reading frame: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get frame metadata for a specific frame
        /// </summary>
        public FrameMetadata GetFrameMetadata(int frameIndex)
        {
            if (frameIndex < 0 || frameIndex >= frameMetadata.Count)
                return null;

            return frameMetadata[frameIndex];
        }

        /// <summary>
        /// Load only time series data without full frame
        /// </summary>
        public (float[] pWave, float[] sWave) LoadTimeSeriesOnly(int frameIndex)
        {
            if (frameIndex < 0 || frameIndex >= frameMetadata.Count)
                return (null, null);

            try
            {
                var metadata = frameMetadata[frameIndex];
                return (new[] { metadata.PWaveValue }, new[] { metadata.SWaveValue });
            }
            catch
            {
                return (null, null);
            }
        }
        #endregion

        #region I/O Utilities
        /// <summary>
        /// Clone an array safely with null check
        /// </summary>
        private T CloneArraySafely<T>(T source) where T : class
        {
            if (source == null)
                return null;

            if (source is float[,,] array3D)
            {
                int dim0 = array3D.GetLength(0);
                int dim1 = array3D.GetLength(1);
                int dim2 = array3D.GetLength(2);

                float[,,] clone = new float[dim0, dim1, dim2];
                Array.Copy(array3D, clone, array3D.Length);
                return clone as T;
            }
            else if (source is float[,] array2D)
            {
                int dim0 = array2D.GetLength(0);
                int dim1 = array2D.GetLength(1);

                float[,] clone = new float[dim0, dim1];
                Array.Copy(array2D, clone, array2D.Length);
                return clone as T;
            }
            else if (source is float[] array1D)
            {
                float[] clone = new float[array1D.Length];
                Array.Copy(array1D, clone, array1D.Length);
                return clone as T;
            }

            return source; // Return original if not a known type
        }

        /// <summary>
        /// Write a 3D array
        /// </summary>
        private void WriteArray(BinaryWriter writer, float[,,] array)
        {
            if (array == null)
            {
                writer.Write(-1); // Null marker
                return;
            }

            int dim0 = array.GetLength(0);
            int dim1 = array.GetLength(1);
            int dim2 = array.GetLength(2);

            writer.Write(3); // Dimensions
            writer.Write(dim0);
            writer.Write(dim1);
            writer.Write(dim2);

            // Write all elements
            for (int z = 0; z < dim2; z++)
            {
                for (int y = 0; y < dim1; y++)
                {
                    for (int x = 0; x < dim0; x++)
                    {
                        writer.Write(array[x, y, z]);
                    }
                }
            }
        }

        /// <summary>
        /// Write a 2D array
        /// </summary>
        private void WriteArray(BinaryWriter writer, float[,] array)
        {
            if (array == null)
            {
                writer.Write(-1); // Null marker
                return;
            }

            int dim0 = array.GetLength(0);
            int dim1 = array.GetLength(1);

            writer.Write(2); // Dimensions
            writer.Write(dim0);
            writer.Write(dim1);

            // Write all elements
            for (int y = 0; y < dim1; y++)
            {
                for (int x = 0; x < dim0; x++)
                {
                    writer.Write(array[x, y]);
                }
            }
        }

        /// <summary>
        /// Write a 1D array
        /// </summary>
        private void WriteFloatArray(BinaryWriter writer, float[] array)
        {
            if (array == null)
            {
                writer.Write(0); // Length 0 for null
                return;
            }

            writer.Write(array.Length);
            for (int i = 0; i < array.Length; i++)
            {
                writer.Write(array[i]);
            }
        }

        /// <summary>
        /// Read a 3D array
        /// </summary>
        private float[,,] ReadArray3D(BinaryReader reader)
        {
            int dimensions = reader.ReadInt32();
            if (dimensions == -1)
                return null; // Null array
            if (dimensions != 3)
                throw new FormatException("Expected 3D array");

            int dim0 = reader.ReadInt32();
            int dim1 = reader.ReadInt32();
            int dim2 = reader.ReadInt32();

            float[,,] array = new float[dim0, dim1, dim2];
            for (int z = 0; z < dim2; z++)
            {
                for (int y = 0; y < dim1; y++)
                {
                    for (int x = 0; x < dim0; x++)
                    {
                        array[x, y, z] = reader.ReadSingle();
                    }
                }
            }

            return array;
        }

        /// <summary>
        /// Read a 2D array
        /// </summary>
        private float[,] ReadArray2D(BinaryReader reader)
        {
            int dimensions = reader.ReadInt32();
            if (dimensions == -1)
                return null; // Null array
            if (dimensions != 2)
                throw new FormatException("Expected 2D array");

            int dim0 = reader.ReadInt32();
            int dim1 = reader.ReadInt32();

            float[,] array = new float[dim0, dim1];
            for (int y = 0; y < dim1; y++)
            {
                for (int x = 0; x < dim0; x++)
                {
                    array[x, y] = reader.ReadSingle();
                }
            }

            return array;
        }

        /// <summary>
        /// Read a 1D array
        /// </summary>
        private float[] ReadFloatArray(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length <= 0)
                return null;

            float[] array = new float[length];
            for (int i = 0; i < length; i++)
            {
                array[i] = reader.ReadSingle();
            }

            return array;
        }

        /// <summary>
        /// Skip over an array without loading it
        /// </summary>
        private void SkipArray(BinaryReader reader)
        {
            int dimensions = reader.ReadInt32();
            if (dimensions == -1)
                return; // Null array

            if (dimensions == 2)
            {
                int dim0 = reader.ReadInt32();
                int dim1 = reader.ReadInt32();
                reader.BaseStream.Seek(dim0 * dim1 * sizeof(float), SeekOrigin.Current);
            }
            else if (dimensions == 3)
            {
                int dim0 = reader.ReadInt32();
                int dim1 = reader.ReadInt32();
                int dim2 = reader.ReadInt32();
                reader.BaseStream.Seek(dim0 * dim1 * dim2 * sizeof(float), SeekOrigin.Current);
            }
        }

        /// <summary>
        /// Skip over a float array without loading it
        /// </summary>
        private void SkipFloatArray(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length > 0)
            {
                reader.BaseStream.Seek(length * sizeof(float), SeekOrigin.Current);
            }
        }
        #endregion

        #region Verification and Status
        /// <summary>
        /// Wait for all frames to be saved with optional progress callback
        /// </summary>
        public bool WaitForAllFramesToSave(int timeoutSeconds = 60, Action<int, int> progressCallback = null)
        {
            if (readOnly) return true;

            DateTime startTime = DateTime.Now;
            int lastReported = -1;

            while (queuedFrames > 0 && (DateTime.Now - startTime).TotalSeconds < timeoutSeconds)
            {
                int current = savedFrames;
                int total = savedFrames + queuedFrames;

                if (progressCallback != null && current != lastReported)
                {
                    progressCallback(current, total);
                    lastReported = current;
                }

                Thread.Sleep(100);
            }

            return queuedFrames == 0;
        }

        /// <summary>
        /// Verify that all frames were saved properly
        /// </summary>
        public bool VerifyAllFramesSaved()
        {
            if (frameMetadata.Count == 0)
                return false;

            int missing = 0;
            for (int i = 0; i < frameMetadata.Count; i++)
            {
                string framePath = Path.Combine(cacheDirectory, frameMetadata[i].FileName);
                if (!File.Exists(framePath))
                {
                    missing++;
                    Logger.Log($"[FrameCacheManager] Missing frame file: {framePath}");
                }
            }

            if (missing > 0)
            {
                Logger.Log($"[FrameCacheManager] Missing {missing} of {frameMetadata.Count} frame files");
                return false;
            }

            return true;
        }
        #endregion

        #region Disposal
        /// <summary>
        /// Clean up resources
        /// </summary>
        public void Dispose()
        {
            if (isDisposed)
                return;

            isDisposed = true;

            if (!readOnly)
            {
                try
                {
                    // Signal cancellation to stop writer
                    cancellationTokenSource?.Cancel();

                    // Wait for writer to complete with timeout
                    if (writerTask != null && !writerTask.Wait(5000))
                    {
                        Logger.Log("[FrameCacheManager] Warning: Writer task did not complete gracefully");
                    }

                    // Dispose collections
                    frameQueue?.Dispose();
                    cancellationTokenSource?.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.Log($"[FrameCacheManager] Error during disposal: {ex.Message}");
                }
            }

            // Clear frame cache
            frameCache?.Clear();

            // Force garbage collection
            GC.Collect();
            GC.WaitForPendingFinalizers();

            Logger.Log("[FrameCacheManager] Disposed");
        }
        #endregion

        #region Data Structures
        /// <summary>
        /// Structure for frame data queued for saving
        /// </summary>
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

        /// <summary>
        /// Public class for frame metadata
        /// </summary>
        public class FrameMetadata
        {
            public int TimeStep { get; set; }
            public string FileName { get; set; }
            public float PWaveValue { get; set; }
            public float SWaveValue { get; set; }
            public float PWavePathProgress { get; set; }
            public float SWavePathProgress { get; set; }
        }

        /// <summary>
        /// Public class for cached frame data
        /// </summary>
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
        #endregion
    }
}
