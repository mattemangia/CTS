using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace CTSegmenter
{
    public class ChunkedLabelVolume : IDisposable, ILabelVolumeData
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Depth { get; private set; }
        public int ChunkDim { get; private set; }

        private int chunkCountX;
        private int chunkCountY;
        private int chunkCountZ;

        // For RAM mode.
        private byte[][] _chunks;
        // For memory-mapped mode.
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor[] _accessors;
        private bool _useMemoryMapping;

        /// <summary>
        /// Constructor for memory-mapped mode when an MMF is already available.
        /// </summary>
        public ChunkedLabelVolume(int width, int height, int depth, int chunkDim, MemoryMappedFile mmf)
        {
            if (width <= 0 || height <= 0 || depth <= 0)
                throw new ArgumentException("Invalid volume dimensions.");
            if (chunkDim <= 0)
                throw new ArgumentException("Chunk dimension must be positive.");

            Width = width;
            Height = height;
            Depth = depth;
            ChunkDim = chunkDim;
            _useMemoryMapping = true;

            chunkCountX = (width + chunkDim - 1) / chunkDim;
            chunkCountY = (height + chunkDim - 1) / chunkDim;
            chunkCountZ = (depth + chunkDim - 1) / chunkDim;

            Logger.Log($"[ChunkedLabelVolume] Initialized MM volume: {Width}x{Height}x{Depth}, ChunkDim={ChunkDim}");
            _mmf = mmf;
        }

        /// <summary>
        /// Constructor for creating a volume from scratch.
        /// </summary>
        public ChunkedLabelVolume(int width, int height, int depth, int chunkDim, bool useMemoryMapping, string filePath = null)
        {
            if (width <= 0 || height <= 0 || depth <= 0)
                throw new ArgumentException("Invalid volume dimensions.");
            if (chunkDim <= 0)
                throw new ArgumentException("Chunk dimension must be positive.");

            Width = width;
            Height = height;
            Depth = depth;
            ChunkDim = chunkDim;
            _useMemoryMapping = useMemoryMapping;

            chunkCountX = (width + chunkDim - 1) / chunkDim;
            chunkCountY = (height + chunkDim - 1) / chunkDim;
            chunkCountZ = (depth + chunkDim - 1) / chunkDim;

            Logger.Log($"[ChunkedLabelVolume] Initializing volume: {Width}x{Height}x{Depth}, ChunkDim={ChunkDim}, useMM={_useMemoryMapping}");

            if (_useMemoryMapping)
            {
                if (string.IsNullOrEmpty(filePath))
                    throw new ArgumentException("File path is required for memory mapping.");

                int totalChunks = chunkCountX * chunkCountY * chunkCountZ;
                long chunkSize = (long)ChunkDim * ChunkDim * ChunkDim;
                long totalSize = totalChunks * chunkSize;

                // Create or overwrite the backing file.
                using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    fs.SetLength(totalSize);
                }
                Logger.Log($"[ChunkedLabelVolume] Created file '{filePath}' with size {totalSize} bytes.");

                _mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, totalSize, MemoryMappedFileAccess.ReadWrite);
                _accessors = new MemoryMappedViewAccessor[totalChunks];

                for (int i = 0; i < totalChunks; i++)
                {
                    long offset = i * chunkSize;
                    _accessors[i] = _mmf.CreateViewAccessor(offset, chunkSize, MemoryMappedFileAccess.ReadWrite);
                    Logger.Log($"[ChunkedLabelVolume] Created accessor for chunk {i} at offset {offset}.");
                }
            }
            else
            {
                int totalChunks = chunkCountX * chunkCountY * chunkCountZ;
                _chunks = new byte[totalChunks][];
                int chunkSize = ChunkDim * ChunkDim * ChunkDim;
                for (int i = 0; i < totalChunks; i++)
                {
                    _chunks[i] = new byte[chunkSize];
                }
                Logger.Log($"[ChunkedLabelVolume] Initialized {totalChunks} RAM chunks, each of {chunkSize} bytes.");
            }
        }

        /// <summary>
        /// Indexer for voxel data access.
        /// </summary>
        public byte this[int x, int y, int z]
        {
            get
            {
                var (chunkIndex, offset) = GetChunkIndexAndOffset(x, y, z);
                return _useMemoryMapping ? _accessors[chunkIndex].ReadByte(offset) : _chunks[chunkIndex][offset];
            }
            set
            {
                var (chunkIndex, offset) = GetChunkIndexAndOffset(x, y, z);
                if (_useMemoryMapping)
                    _accessors[chunkIndex].Write(offset, value);
                else
                    _chunks[chunkIndex][offset] = value;
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
            int chunkIndex = (cz * chunkCountY + cy) * chunkCountX + cx;
            int lx = x % ChunkDim, ly = y % ChunkDim, lz = z % ChunkDim;
            int offset = (lz * ChunkDim + ly) * ChunkDim + lx;
            return (chunkIndex, offset);
        }

        /// <summary>
        /// Writes the header and chunk data to a binary writer.
        /// </summary>
        public void WriteChunks(BinaryWriter w)
        {
            Logger.Log("[WriteChunks] Writing header.");
            w.Write(ChunkDim);
            w.Write(chunkCountX);
            w.Write(chunkCountY);
            w.Write(chunkCountZ);

            int chunkSize = ChunkDim * ChunkDim * ChunkDim;
            if (!_useMemoryMapping && _chunks != null)
            {
                Logger.Log("[WriteChunks] Writing chunks in RAM mode.");
                for (int i = 0; i < _chunks.Length; i++)
                {
                    w.Write(_chunks[i], 0, chunkSize);
                    Logger.Log($"[WriteChunks] Written RAM chunk {i}.");
                }
            }
            else
            {
                Logger.Log("[WriteChunks] Writing chunks in memory-mapped mode.");
                for (int i = 0; i < _accessors.Length; i++)
                {
                    byte[] buffer = new byte[chunkSize];
                    _accessors[i].ReadArray(0, buffer, 0, chunkSize);
                    w.Write(buffer, 0, chunkSize);
                    Logger.Log($"[WriteChunks] Written MM chunk {i}.");
                }
            }
        }

        /// <summary>
        /// Reads the chunk data from a binary reader.
        /// This method assumes the header has already been read.
        /// </summary>
        public void ReadChunksHeaderAndData(BinaryReader br, MemoryMappedFile mmfIfAny = null, long offsetAfterHeader = 0)
        {
            Logger.Log("[ReadChunksHeaderAndData] Reading chunk data (header already processed).");
            int totalChunks = chunkCountX * chunkCountY * chunkCountZ;
            int chunkSize = ChunkDim * ChunkDim * ChunkDim;

            if (!_useMemoryMapping)
            {
                _chunks = new byte[totalChunks][];
                for (int i = 0; i < totalChunks; i++)
                {
                    _chunks[i] = br.ReadBytes(chunkSize);
                    Logger.Log($"[ReadChunksHeaderAndData] Loaded RAM chunk {i}.");
                }
            }
            else
            {
                _mmf = mmfIfAny ?? throw new Exception("A valid MMF is required for memory-mapped mode.");
                _accessors = new MemoryMappedViewAccessor[totalChunks];
                for (int i = 0; i < totalChunks; i++)
                {
                    long thisChunkOffset = offsetAfterHeader + (long)i * chunkSize;
                    _accessors[i] = _mmf.CreateViewAccessor(thisChunkOffset, chunkSize, MemoryMappedFileAccess.ReadWrite);
                    Logger.Log($"[ReadChunksHeaderAndData] Created MM accessor for chunk {i} at offset {thisChunkOffset}.");
                }
            }
        }

        /// <summary>
        /// Releases the memory-mapped file and its view accessors so that the file is no longer locked.
        /// </summary>
        public void ReleaseFileLock()
        {
            if (_accessors != null)
            {
                foreach (var acc in _accessors)
                {
                    acc.Dispose();
                }
                _accessors = null;
            }
            if (_mmf != null)
            {
                _mmf.Dispose();
                _mmf = null;
            }
            Logger.Log("[ReleaseFileLock] Released memory-mapped file resources.");
        }

        /// <summary>
        /// Disposes all resources.
        /// </summary>
        public void Dispose()
        {
            Logger.Log("[Dispose] Disposing ChunkedLabelVolume resources.");
            if (_accessors != null)
            {
                foreach (var acc in _accessors)
                    acc.Dispose();
            }
            _mmf?.Dispose();
            ReleaseFileLock();
        }

        public int ChunkCountX => chunkCountX;
        public int ChunkCountY => chunkCountY;
        public int ChunkCountZ => chunkCountZ;

        /// <summary>
        /// Returns the raw bytes of a single chunk (ChunkDim^3 bytes).
        /// </summary>
        public byte[] GetChunkBytes(int chunkIndex)
        {
            if (!_useMemoryMapping)
            {
                // Return the in-RAM chunk array
                return _chunks[chunkIndex];
            }
            else
            {
                // Read from the memory-mapped accessor
                byte[] data = new byte[ChunkDim * ChunkDim * ChunkDim];
                _accessors[chunkIndex].ReadArray(0, data, 0, data.Length);
                return data;
            }
        }

        /// <summary>
        /// Converts (cx, cy, cz) chunk coordinates into a linear chunk index.
        /// </summary>
        public int GetChunkIndex(int cx, int cy, int cz)
        {
            return (cz * chunkCountY + cy) * chunkCountX + cx;
        }

    }
}
