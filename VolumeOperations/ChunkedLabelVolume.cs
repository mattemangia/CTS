using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Threading.Tasks;

namespace CTSegmenter
{
    /// <summary>
    /// Provides chunked storage for volumetric label data with support for both
    /// in-memory and memory-mapped file storage to handle 30GB+ datasets.
    /// </summary>
    public class ChunkedLabelVolume : IDisposable, ILabelVolumeData
    {
        #region Fields and Properties
        // Volume dimensions
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Depth { get; private set; }
        public int ChunkDim { get; private set; }

        // Chunking properties
        public int ChunkCountX { get; private set; }
        public int ChunkCountY { get; private set; }
        public int ChunkCountZ { get; private set; }

        // Storage mode fields
        private  byte[][] _chunks; // For in-memory mode
        private MemoryMappedFile _mmf; // For memory-mapped mode
        private MemoryMappedViewAccessor _viewAccessor; // Single view accessor for the whole file
        private readonly bool _useMemoryMapping;
        private readonly string _filePath; // Path to the backing file (if any)

        // Header size constant
        private const int HEADER_SIZE = 16; // 4 integers (ChunkDim, ChunkCountX, ChunkCountY, ChunkCountZ)

        // Thread synchronization
        private readonly object _lockObject = new object();
        private bool _disposed = false;
        #endregion

        #region Constructors
        /// <summary>
        /// Constructor for memory-mapped mode when an MMF is already available.
        /// </summary>
        public ChunkedLabelVolume(int width, int height, int depth, int chunkDim, MemoryMappedFile mmf)
        {
            try
            {
                ValidateDimensions(width, height, depth, chunkDim);

                Width = width;
                Height = height;
                Depth = depth;
                ChunkDim = chunkDim;
                _useMemoryMapping = true;

                ChunkCountX = (width + chunkDim - 1) / chunkDim;
                ChunkCountY = (height + chunkDim - 1) / chunkDim;
                ChunkCountZ = (depth + chunkDim - 1) / chunkDim;

                // Create a single view accessor for the entire file
                _mmf = mmf;
                _viewAccessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

                Logger.Log($"[ChunkedLabelVolume] Initialized MM volume: {Width}x{Height}x{Depth}, ChunkDim={ChunkDim}");

                // We do not initialize _chunks for memory-mapped mode
                _chunks = null;
            }
            catch (Exception ex)
            {
                Logger.Log($"[ChunkedLabelVolume] Construction error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Constructor for creating a volume from scratch.
        /// </summary>
        public ChunkedLabelVolume(int width, int height, int depth, int chunkDim, bool useMemoryMapping, string filePath = null)
        {
            try
            {
                ValidateDimensions(width, height, depth, chunkDim);

                Width = width;
                Height = height;
                Depth = depth;
                ChunkDim = chunkDim;
                _useMemoryMapping = useMemoryMapping;
                _filePath = filePath;

                ChunkCountX = (width + chunkDim - 1) / chunkDim;
                ChunkCountY = (height + chunkDim - 1) / chunkDim;
                ChunkCountZ = (depth + chunkDim - 1) / chunkDim;

                Logger.Log($"[ChunkedLabelVolume] Initializing volume: {Width}x{Height}x{Depth}, ChunkDim={ChunkDim}, useMM={_useMemoryMapping}");

                if (_useMemoryMapping)
                {
                    if (string.IsNullOrEmpty(filePath))
                        throw new ArgumentException("File path is required for memory mapping.");

                    int totalChunks = ChunkCountX * ChunkCountY * ChunkCountZ;
                    long chunkSize = (long)ChunkDim * ChunkDim * ChunkDim;
                    long totalSize = HEADER_SIZE + totalChunks * chunkSize;

                    try
                    {
                        // Create or overwrite the backing file
                        using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                        {
                            // Write the header information
                            using (var bw = new BinaryWriter(fs))
                            {
                                bw.Write(ChunkDim);
                                bw.Write(ChunkCountX);
                                bw.Write(ChunkCountY);
                                bw.Write(ChunkCountZ);
                            }

                            // Pre-allocate the file
                            fs.SetLength(totalSize);
                            fs.Flush(true);
                        }

                        Logger.Log($"[ChunkedLabelVolume] Created file '{filePath}' with size {totalSize:N0} bytes.");

                        // Open memory-mapped file
                        _mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite);
                        _viewAccessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ChunkedLabelVolume] Error creating memory-mapped file: {ex.Message}");
                        throw;
                    }
                }
                else
                {
                    // Initialize in-memory storage
                    int totalChunks = ChunkCountX * ChunkCountY * ChunkCountZ;
                    _chunks = new byte[totalChunks][];
                    int chunkSize = ChunkDim * ChunkDim * ChunkDim;

                    // Initialize all chunks with zeros
                    Parallel.For(0, totalChunks, i =>
                    {
                        _chunks[i] = new byte[chunkSize];
                    });

                    Logger.Log($"[ChunkedLabelVolume] Initialized {totalChunks} RAM chunks, each of {chunkSize} bytes.");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ChunkedLabelVolume] Construction error: {ex.Message}");
                throw;
            }
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Indexer for voxel data access.
        /// </summary>
        public byte this[int x, int y, int z]
        {
            get
            {
                try
                {
                    ValidateCoordinates(x, y, z);

                    var (chunkIndex, offset) = GetChunkIndexAndOffset(x, y, z);

                    if (_useMemoryMapping)
                    {
                        long globalOffset = CalculateGlobalOffset(chunkIndex, offset);
                        return _viewAccessor.ReadByte(globalOffset);
                    }
                    else
                    {
                        return _chunks[chunkIndex][offset];
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ChunkedLabelVolume] Get voxel error at ({x},{y},{z}): {ex.Message}");
                    return 0; // Return 0 (background) on error
                }
            }
            set
            {
                try
                {
                    ValidateCoordinates(x, y, z);

                    var (chunkIndex, offset) = GetChunkIndexAndOffset(x, y, z);

                    if (_useMemoryMapping)
                    {
                        long globalOffset = CalculateGlobalOffset(chunkIndex, offset);
                        _viewAccessor.Write(globalOffset, value);
                    }
                    else
                    {
                        _chunks[chunkIndex][offset] = value;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ChunkedLabelVolume] Set voxel error at ({x},{y},{z}): {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Writes the header and chunk data to a binary writer.
        /// </summary>
        public void WriteChunks(BinaryWriter writer)
        {
            try
            {
                // Write header
                writer.Write(ChunkDim);
                writer.Write(ChunkCountX);
                writer.Write(ChunkCountY);
                writer.Write(ChunkCountZ);

                int chunkSize = ChunkDim * ChunkDim * ChunkDim;
                int totalChunks = ChunkCountX * ChunkCountY * ChunkCountZ;

                Logger.Log($"[WriteChunks] Writing {totalChunks} chunks, each {chunkSize} bytes");

                if (!_useMemoryMapping)
                {
                    // In-memory mode: write chunks directly
                    for (int i = 0; i < totalChunks; i++)
                    {
                        writer.Write(_chunks[i], 0, chunkSize);
                    }
                }
                else
                {
                    // Memory-mapped mode: use a buffer to read and write chunks
                    byte[] buffer = new byte[chunkSize];

                    for (int i = 0; i < totalChunks; i++)
                    {
                        long offset = CalculateGlobalOffset(i, 0);
                        _viewAccessor.ReadArray(offset, buffer, 0, chunkSize);
                        writer.Write(buffer, 0, chunkSize);
                    }
                }

                Logger.Log("[WriteChunks] All chunks written successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"[WriteChunks] Error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Reads the chunk data from a binary reader.
        /// This method assumes the header has already been read.
        /// </summary>
        public void ReadChunksHeaderAndData(BinaryReader reader, MemoryMappedFile mmfIfAny = null, long offsetAfterHeader = 0)
        {
            try
            {
                Logger.Log("[ReadChunksHeaderAndData] Starting to read chunk data");

                int totalChunks = ChunkCountX * ChunkCountY * ChunkCountZ;
                int chunkSize = ChunkDim * ChunkDim * ChunkDim;

                if (!_useMemoryMapping)
                {
                    // In-memory mode: read chunks into arrays
                    _chunks = new byte[totalChunks][];

                    for (int i = 0; i < totalChunks; i++)
                    {
                        _chunks[i] = reader.ReadBytes(chunkSize);

                        // Ensure we read a complete chunk
                        if (_chunks[i].Length < chunkSize)
                        {
                            // Pad with zeros if we didn't read enough data
                            byte[] completeChunk = new byte[chunkSize];
                            Array.Copy(_chunks[i], completeChunk, _chunks[i].Length);
                            _chunks[i] = completeChunk;

                            Logger.Log($"[ReadChunksHeaderAndData] Warning: Chunk {i} was incomplete ({_chunks[i].Length}/{chunkSize} bytes)");
                        }
                    }
                }
                else
                {
                    // Memory-mapped mode: use the provided MMF or existing one
                    _mmf = mmfIfAny ?? _mmf;

                    if (_mmf == null)
                        throw new InvalidOperationException("Memory-mapped file is required for memory-mapped mode");

                    if (_viewAccessor == null)
                        _viewAccessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

                    // Read chunks from the reader and write to memory-mapped file
                    byte[] buffer = new byte[chunkSize];

                    for (int i = 0; i < totalChunks; i++)
                    {
                        // Read from binary reader
                        int bytesRead = reader.Read(buffer, 0, chunkSize);

                        // If we didn't read a full chunk, fill the rest with zeros
                        if (bytesRead < chunkSize)
                        {
                            for (int j = bytesRead; j < chunkSize; j++)
                                buffer[j] = 0;

                            Logger.Log($"[ReadChunksHeaderAndData] Warning: Chunk {i} was incomplete ({bytesRead}/{chunkSize} bytes)");
                        }

                        // Write to memory-mapped file
                        long offset = offsetAfterHeader + (long)i * chunkSize;
                        _viewAccessor.WriteArray(offset, buffer, 0, chunkSize);
                    }
                }

                Logger.Log("[ReadChunksHeaderAndData] Completed reading chunks");
            }
            catch (Exception ex)
            {
                Logger.Log($"[ReadChunksHeaderAndData] Error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Releases the memory-mapped file and its view accessors so that the file is no longer locked.
        /// </summary>
        public void ReleaseFileLock()
        {
            lock (_lockObject)
            {
                if (_viewAccessor != null)
                {
                    _viewAccessor.Dispose();
                    _viewAccessor = null;
                }

                if (_mmf != null)
                {
                    _mmf.Dispose();
                    _mmf = null;
                }

                Logger.Log("[ReleaseFileLock] Released memory-mapped file resources");
            }
        }

        /// <summary>
        /// Returns the raw bytes of a single chunk (ChunkDim^3 bytes).
        /// </summary>
        public byte[] GetChunkBytes(int chunkIndex)
        {
            if (chunkIndex < 0 || chunkIndex >= ChunkCountX * ChunkCountY * ChunkCountZ)
                throw new ArgumentOutOfRangeException(nameof(chunkIndex), "Chunk index out of range");

            int chunkSize = ChunkDim * ChunkDim * ChunkDim;
            byte[] result = new byte[chunkSize];

            try
            {
                if (!_useMemoryMapping)
                {
                    // In-memory mode: return a copy of the chunk
                    if (_chunks != null && chunkIndex < _chunks.Length)
                    {
                        Array.Copy(_chunks[chunkIndex], result, chunkSize);
                    }
                }
                else
                {
                    // Memory-mapped mode: read from the file
                    if (_viewAccessor != null)
                    {
                        long offset = CalculateGlobalOffset(chunkIndex, 0);
                        _viewAccessor.ReadArray(offset, result, 0, chunkSize);
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                Logger.Log($"[GetChunkBytes] Error reading chunk {chunkIndex}: {ex.Message}");
                return new byte[chunkSize]; // Return empty array on error
            }
        }

        /// <summary>
        /// Converts (cx, cy, cz) chunk coordinates into a linear chunk index.
        /// </summary>
        public int GetChunkIndex(int cx, int cy, int cz)
        {
            if (cx < 0 || cx >= ChunkCountX || cy < 0 || cy >= ChunkCountY || cz < 0 || cz >= ChunkCountZ)
                throw new ArgumentOutOfRangeException($"Chunk coordinates ({cx},{cy},{cz}) out of range");

            return (cz * ChunkCountY + cy) * ChunkCountX + cx;
        }
        #endregion

        #region Private Methods
        /// <summary>
        /// Computes the chunk index and the voxel offset within that chunk.
        /// </summary>
        private (int chunkIndex, int offset) GetChunkIndexAndOffset(int x, int y, int z)
        {
            int cx = x / ChunkDim;
            int cy = y / ChunkDim;
            int cz = z / ChunkDim;

            int chunkIndex = GetChunkIndex(cx, cy, cz);

            int lx = x % ChunkDim;
            int ly = y % ChunkDim;
            int lz = z % ChunkDim;

            int offset = (lz * ChunkDim * ChunkDim) + (ly * ChunkDim) + lx;

            return (chunkIndex, offset);
        }

        /// <summary>
        /// Calculates the global offset in the memory-mapped file for a given chunk and local offset.
        /// </summary>
        private long CalculateGlobalOffset(int chunkIndex, int localOffset)
        {
            long chunkSize = (long)ChunkDim * ChunkDim * ChunkDim;
            return HEADER_SIZE + (chunkIndex * chunkSize) + localOffset;
        }

        /// <summary>
        /// Validates the volume dimensions.
        /// </summary>
        private static void ValidateDimensions(int width, int height, int depth, int chunkDim)
        {
            if (width <= 0)
                throw new ArgumentException("Width must be positive", nameof(width));
            if (height <= 0)
                throw new ArgumentException("Height must be positive", nameof(height));
            if (depth <= 0)
                throw new ArgumentException("Depth must be positive", nameof(depth));
            if (chunkDim <= 0)
                throw new ArgumentException("Chunk dimension must be positive", nameof(chunkDim));
        }

        /// <summary>
        /// Validates the voxel coordinates.
        /// </summary>
        private void ValidateCoordinates(int x, int y, int z)
        {
            if (x < 0 || x >= Width)
                throw new ArgumentOutOfRangeException(nameof(x), $"X coordinate {x} is outside valid range [0,{Width - 1}]");
            if (y < 0 || y >= Height)
                throw new ArgumentOutOfRangeException(nameof(y), $"Y coordinate {y} is outside valid range [0,{Height - 1}]");
            if (z < 0 || z >= Depth)
                throw new ArgumentOutOfRangeException(nameof(z), $"Z coordinate {z} is outside valid range [0,{Depth - 1}]");
        }
        #endregion

        #region IDisposable Implementation
        /// <summary>
        /// Disposes all resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes managed and unmanaged resources.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    Logger.Log("[Dispose] Disposing ChunkedLabelVolume resources");
                    ReleaseFileLock();
                }

                _disposed = true;
            }
        }

        /// <summary>
        /// Finalizer to ensure resources are released.
        /// </summary>
        ~ChunkedLabelVolume()
        {
            Dispose(false);
        }
        #endregion
    }
}
