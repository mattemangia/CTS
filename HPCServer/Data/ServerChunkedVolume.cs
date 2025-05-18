using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Threading.Tasks;

namespace ParallelComputingServer.Data
{
    /// <summary>
    /// Simplified version of ChunkedVolume for the server environment without WinForms dependencies.
    /// Provides efficient storage for large 3D grayscale volumes using memory-mapped files.
    /// </summary>
    public class ServerChunkedVolume : IDisposable
    {
        #region Fields and Properties

        // Volume dimensions
        public int Width { get; }
        public int Height { get; }
        public int Depth { get; }

        // Chunking parameters
        private readonly int _chunkDim;
        private readonly int _chunkCountX;
        private readonly int _chunkCountY;
        private readonly int _chunkCountZ;

        // Data storage
        private MemoryMappedFile _mmf; // Memory-mapped file
        private MemoryMappedViewAccessor _viewAccessor; // Main view accessor
        private readonly long _headerSize;
        private readonly string _filePath;

        // Metadata
        private readonly int _bitsPerPixel; // 8 or 16 bit
        private double _pixelSize; // Size in meters per pixel

        // Interface properties
        public int ChunkDim => _chunkDim;
        public int ChunkCountX => _chunkCountX;
        public int ChunkCountY => _chunkCountY;
        public int ChunkCountZ => _chunkCountZ;
        public int TotalChunks => _chunkCountX * _chunkCountY * _chunkCountZ;

        // File header size (for memory-mapped files)
        private const int HEADER_SIZE = 36; // 8 ints (32 bytes) + 1 double (8 bytes)

        #endregion

        #region Constructors

        /// <summary>
        /// Creates a volume that uses memory-mapped file storage
        /// </summary>
        public ServerChunkedVolume(int width, int height, int depth, int chunkDim, string filePath)
        {
            try
            {
                // Make sure dimensions are valid
                if (width <= 0 || width > 65536 || height <= 0 || height > 65536 ||
                    depth <= 0 || depth > 65536 || chunkDim <= 0 || chunkDim > 1024)
                {
                    throw new ArgumentException($"Invalid dimensions: {width}x{height}x{depth}, chunkDim={chunkDim}");
                }

                Width = width;
                Height = height;
                Depth = depth;
                _chunkDim = chunkDim;
                _bitsPerPixel = 8; // Default to 8-bit
                _pixelSize = 1e-6; // Default to 1 micron
                _filePath = filePath;

                _chunkCountX = (width + chunkDim - 1) / chunkDim;
                _chunkCountY = (height + chunkDim - 1) / chunkDim;
                _chunkCountZ = (depth + chunkDim - 1) / chunkDim;

                _headerSize = HEADER_SIZE;

                Console.WriteLine($"Creating memory-mapped volume: {Width}x{Height}x{Depth}, " +
                                 $"chunkDim={_chunkDim}, chunks={_chunkCountX}x{_chunkCountY}x{_chunkCountZ}");

                // Calculate file size
                long chunkSize = (long)chunkDim * chunkDim * chunkDim;
                long totalSize = _headerSize + (TotalChunks * chunkSize);

                // Create or open the memory-mapped file
                CreateOrOpenMemoryMappedFile(totalSize);

                // Write header information
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

                Console.WriteLine($"ServerChunkedVolume construction error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Creates a volume that uses memory-mapped file storage from an existing file
        /// </summary>
        public ServerChunkedVolume(string filePath)
        {
            try
            {
                _filePath = filePath;

                // Open the file and read header
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    Width = br.ReadInt32();
                    Height = br.ReadInt32();
                    Depth = br.ReadInt32();
                    _chunkDim = br.ReadInt32();
                    _bitsPerPixel = br.ReadInt32();
                    _pixelSize = br.ReadDouble();
                    _chunkCountX = br.ReadInt32();
                    _chunkCountY = br.ReadInt32();
                    _chunkCountZ = br.ReadInt32();
                }

                _headerSize = HEADER_SIZE;

                // Check if dimensions are valid
                if (Width <= 0 || Width > 65536 || Height <= 0 || Height > 65536 ||
                    Depth <= 0 || Depth > 65536 || _chunkDim <= 0 || _chunkDim > 1024)
                {
                    throw new ArgumentException($"Invalid dimensions in file: {Width}x{Height}x{Depth}, chunkDim={_chunkDim}");
                }

                Console.WriteLine($"Opening memory-mapped volume from {filePath}: {Width}x{Height}x{Depth}, " +
                                 $"chunkDim={_chunkDim}, chunks={_chunkCountX}x{_chunkCountY}x{_chunkCountZ}");

                // Open the memory-mapped file
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

                Console.WriteLine($"ServerChunkedVolume open error: {ex.Message}");
                throw;
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Indexer for accessing voxel data
        /// </summary>
        public byte this[int x, int y, int z]
        {
            get
            {
                try
                {
                    ValidateCoordinates(x, y, z);
                    var (chunkIndex, offset) = CalculateChunkIndexAndOffset(x, y, z);
                    long globalOffset = CalculateGlobalOffset(chunkIndex, offset);
                    return _viewAccessor.ReadByte(globalOffset);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Get voxel error at ({x},{y},{z}): {ex.Message}");
                    return 0; // Return black on error
                }
            }

            set
            {
                try
                {
                    ValidateCoordinates(x, y, z);
                    var (chunkIndex, offset) = CalculateChunkIndexAndOffset(x, y, z);
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

                int chunkSize = _chunkDim * _chunkDim * _chunkDim;
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
            int chunkSize = _chunkDim * _chunkDim * _chunkDim;

            try
            {
                byte[] buffer = new byte[chunkSize];
                long offset = CalculateGlobalOffset(chunkIndex, 0);
                _viewAccessor.ReadArray(offset, buffer, 0, chunkSize);
                return buffer;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GetChunkBytes error for chunk {chunkIndex}: {ex.Message}");
                return new byte[chunkSize]; // Return empty chunk on error
            }
        }

        /// <summary>
        /// Gets the index of a chunk from its coordinates
        /// </summary>
        public int GetChunkIndex(int cx, int cy, int cz)
        {
            if (cx < 0 || cx >= _chunkCountX || cy < 0 || cy >= _chunkCountY || cz < 0 || cz >= _chunkCountZ)
                throw new ArgumentOutOfRangeException($"Chunk coordinates ({cx},{cy},{cz}) out of range");

            return (cz * _chunkCountY + cy) * _chunkCountX + cx;
        }

        /// <summary>
        /// Flushes the memory-mapped file to ensure all changes are written to disk
        /// </summary>
        public void Flush()
        {
            _viewAccessor?.Flush();
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
            string mapName = $"ServerChunkedVolume_{Guid.NewGuid()}";
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
                _viewAccessor.Write(0, Width);
                _viewAccessor.Write(4, Height);
                _viewAccessor.Write(8, Depth);
                _viewAccessor.Write(12, _chunkDim);
                _viewAccessor.Write(16, _bitsPerPixel);
                _viewAccessor.Write(20, _pixelSize);
                _viewAccessor.Write(28, _chunkCountX);
                _viewAccessor.Write(32, _chunkCountY);
                _viewAccessor.Write(36, _chunkCountZ);
                _viewAccessor.Flush();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error writing header: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Calculate chunk index and offset for a voxel
        /// </summary>
        private (int chunkIndex, int offset) CalculateChunkIndexAndOffset(int x, int y, int z)
        {
            int cx = x / _chunkDim;
            int cy = y / _chunkDim;
            int cz = z / _chunkDim;

            int chunkIndex = GetChunkIndex(cx, cy, cz);

            int lx = x % _chunkDim;
            int ly = y % _chunkDim;
            int lz = z % _chunkDim;

            int offset = (lz * _chunkDim * _chunkDim) + (ly * _chunkDim) + lx;

            return (chunkIndex, offset);
        }

        /// <summary>
        /// Calculate global offset in memory-mapped file
        /// </summary>
        private long CalculateGlobalOffset(int chunkIndex, int localOffset)
        {
            long chunkSize = (long)_chunkDim * _chunkDim * _chunkDim;
            return _headerSize + (chunkIndex * chunkSize) + localOffset;
        }

        /// <summary>
        /// Validate coordinates
        /// </summary>
        private void ValidateCoordinates(int x, int y, int z)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height || z < 0 || z >= Depth)
                throw new IndexOutOfRangeException($"Coordinates ({x},{y},{z}) out of range.");
        }

        #endregion

        #region IDisposable Implementation

        private bool _disposed = false;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    try
                    {
                        // Clean up managed resources
                        if (_viewAccessor != null)
                        {
                            _viewAccessor.Dispose();
                        }

                        if (_mmf != null)
                        {
                            _mmf.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error during disposal: {ex.Message}");
                    }
                }

                _disposed = true;
            }
        }

        ~ServerChunkedVolume()
        {
            Dispose(false);
        }

        #endregion
    }
}