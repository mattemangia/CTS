using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Threading.Tasks;

namespace ParallelComputingServer.Data
{
    /// <summary>
    /// Simplified version of ChunkedLabelVolume for the server environment without WinForms dependencies.
    /// Provides efficient storage for volumetric label data using memory-mapped files.
    /// </summary>
    public class ServerChunkedLabelVolume : IDisposable
    {
        #region Fields and Properties

        // Volume dimensions
        public int Width { get; }
        public int Height { get; }
        public int Depth { get; }
        public int ChunkDim { get; }

        // Chunking properties
        public int ChunkCountX { get; }
        public int ChunkCountY { get; }
        public int ChunkCountZ { get; }

        // Memory-mapped file storage
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _viewAccessor;
        private readonly string _filePath;

        // Header size constant
        private const int HEADER_SIZE = 16; // 4 integers (ChunkDim, ChunkCountX, ChunkCountY, ChunkCountZ)

        // Thread synchronization
        private readonly object _lockObject = new object();
        private bool _disposed = false;

        public int TotalChunks => ChunkCountX * ChunkCountY * ChunkCountZ;

        #endregion

        #region Constructors

        /// <summary>
        /// Constructor for creating a volume from scratch with a new memory-mapped file.
        /// </summary>
        public ServerChunkedLabelVolume(int width, int height, int depth, int chunkDim, string filePath)
        {
            try
            {
                ValidateDimensions(width, height, depth, chunkDim);

                Width = width;
                Height = height;
                Depth = depth;
                ChunkDim = chunkDim;
                _filePath = filePath;

                ChunkCountX = (width + chunkDim - 1) / chunkDim;
                ChunkCountY = (height + chunkDim - 1) / chunkDim;
                ChunkCountZ = (depth + chunkDim - 1) / chunkDim;

                Console.WriteLine($"Initializing labels volume: {Width}x{Height}x{Depth}, ChunkDim={ChunkDim}");

                int totalChunks = ChunkCountX * ChunkCountY * ChunkCountZ;
                long chunkSize = (long)ChunkDim * ChunkDim * ChunkDim;
                long totalSize = HEADER_SIZE + totalChunks * chunkSize;

                // Create the memory-mapped file
                CreateOrOpenMemoryMappedFile(totalSize);

                // Write header
                WriteHeader();
            }
            catch (Exception ex)
            {
                // Clean up resources to avoid leaks
                if (_viewAccessor != null)
                {
                    try { _viewAccessor.Dispose(); } catch { }
                }

                if (_mmf != null)
                {
                    try { _mmf.Dispose(); } catch { }
                }

                Console.WriteLine($"ServerChunkedLabelVolume construction error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Constructor for loading a volume from an existing file.
        /// </summary>
        public ServerChunkedLabelVolume(string filePath)
        {
            try
            {
                _filePath = filePath;

                // Read header from file
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    ChunkDim = br.ReadInt32();
                    ChunkCountX = br.ReadInt32();
                    ChunkCountY = br.ReadInt32();
                    ChunkCountZ = br.ReadInt32();

                    // Calculate dimensions
                    Width = ChunkCountX * ChunkDim;
                    Height = ChunkCountY * ChunkDim;
                    Depth = ChunkCountZ * ChunkDim;
                }

                // Validate dimensions
                ValidateDimensions(Width, Height, Depth, ChunkDim);

                Console.WriteLine($"Opening labels volume from {filePath}: {Width}x{Height}x{Depth}, ChunkDim={ChunkDim}");

                // Open memory-mapped file
                _mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite);
                _viewAccessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            }
            catch (Exception ex)
            {
                // Clean up resources to avoid leaks
                if (_viewAccessor != null)
                {
                    try { _viewAccessor.Dispose(); } catch { }
                }

                if (_mmf != null)
                {
                    try { _mmf.Dispose(); } catch { }
                }

                Console.WriteLine($"ServerChunkedLabelVolume open error: {ex.Message}");
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
                    long globalOffset = CalculateGlobalOffset(chunkIndex, offset);
                    return _viewAccessor.ReadByte(globalOffset);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Get voxel error at ({x},{y},{z}): {ex.Message}");
                    return 0; // Return 0 (background) on error
                }
            }
            set
            {
                try
                {
                    ValidateCoordinates(x, y, z);
                    var (chunkIndex, offset) = GetChunkIndexAndOffset(x, y, z);
                    long globalOffset = CalculateGlobalOffset(chunkIndex, offset);
                    _viewAccessor.Write(globalOffset, value);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Set voxel error at ({x},{y},{z}): {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Sets the data for a specific chunk
        /// </summary>
        public void SetChunkData(int chunkIndex, byte[] chunkData)
        {
            try
            {
                if (chunkIndex < 0 || chunkIndex >= TotalChunks)
                    throw new ArgumentOutOfRangeException(nameof(chunkIndex), "Chunk index out of range");

                int chunkSize = ChunkDim * ChunkDim * ChunkDim;
                if (chunkData.Length != chunkSize)
                    throw new ArgumentException($"Chunk data size mismatch. Expected {chunkSize} bytes, got {chunkData.Length}.");

                long offset = CalculateGlobalOffset(chunkIndex, 0);
                _viewAccessor.WriteArray(offset, chunkData, 0, chunkSize);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SetChunkData error for chunk {chunkIndex}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Gets the bytes for a specific chunk
        /// </summary>
        public byte[] GetChunkBytes(int chunkIndex)
        {
            try
            {
                if (chunkIndex < 0 || chunkIndex >= TotalChunks)
                    throw new ArgumentOutOfRangeException(nameof(chunkIndex), "Chunk index out of range");

                int chunkSize = ChunkDim * ChunkDim * ChunkDim;
                byte[] result = new byte[chunkSize];

                long offset = CalculateGlobalOffset(chunkIndex, 0);
                _viewAccessor.ReadArray(offset, result, 0, chunkSize);

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GetChunkBytes error for chunk {chunkIndex}: {ex.Message}");
                int chunkSize = ChunkDim * ChunkDim * ChunkDim;
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

        /// <summary>
        /// Flushes the memory-mapped file to ensure all changes are written to disk
        /// </summary>
        public void Flush()
        {
            _viewAccessor?.Flush();
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

                Console.WriteLine("Released memory-mapped file resources");
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Creates or opens the memory-mapped file for the volume
        /// </summary>
        private void CreateOrOpenMemoryMappedFile(long totalSize)
        {
            // If the file already exists, delete it to start fresh
            if (File.Exists(_filePath))
            {
                try
                {
                    File.Delete(_filePath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Could not delete existing file: {ex.Message}");
                }
            }

            // Create the directory if it doesn't exist
            string directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Create a new file with the required size
            using (var fs = new FileStream(_filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                fs.SetLength(totalSize);
                fs.Flush(true);
            }

            // Create the memory-mapped file
            string mapName = $"ServerChunkedLabelVolume_{Guid.NewGuid()}";
            _mmf = MemoryMappedFile.CreateFromFile(
                _filePath,
                FileMode.Open,
                mapName,
                0,
                MemoryMappedFileAccess.ReadWrite);

            _viewAccessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
        }

        /// <summary>
        /// Writes header information to the memory-mapped file
        /// </summary>
        private void WriteHeader()
        {
            try
            {
                _viewAccessor.Write(0, ChunkDim);
                _viewAccessor.Write(4, ChunkCountX);
                _viewAccessor.Write(8, ChunkCountY);
                _viewAccessor.Write(12, ChunkCountZ);
                _viewAccessor.Flush();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error writing header: {ex.Message}");
                throw;
            }
        }

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
                    Console.WriteLine("Disposing ServerChunkedLabelVolume resources");
                    ReleaseFileLock();
                }

                _disposed = true;
            }
        }

        /// <summary>
        /// Finalizer to ensure resources are released.
        /// </summary>
        ~ServerChunkedLabelVolume()
        {
            Dispose(false);
        }

        #endregion
    }
}