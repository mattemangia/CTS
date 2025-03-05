using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Threading.Tasks;

namespace CTSegmenter
{
    public class ChunkedVolume : IDisposable
    {
        #region Fields and Properties
        public int Width { get; }
        public int Height { get; }
        public int Depth { get; }
        private readonly int _chunkDim;
        private readonly int _chunkCountX;
        private readonly int _chunkCountY;
        private readonly int _chunkCountZ;
        private readonly byte[][] _chunks;
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor[] _accessors;
        private readonly bool _useMemoryMapping;
        public int TotalChunks => _chunks.Length;
        public byte[][] Chunks => _chunks;
        #endregion

        #region Constructors
        // Constructor for an in-memory volume.
        public ChunkedVolume(int width, int height, int depth, int chunkDim = 256)
        {
            try
            {
                ValidateDimensions(width, height, depth, chunkDim);
                Width = width;
                Height = height;
                Depth = depth;
                _chunkDim = chunkDim;
                _chunkCountX = (width + chunkDim - 1) / chunkDim;
                _chunkCountY = (height + chunkDim - 1) / chunkDim;
                _chunkCountZ = (depth + chunkDim - 1) / chunkDim;
                _chunks = new byte[_chunkCountX * _chunkCountY * _chunkCountZ][];
                Logger.Log($"[Init] In-memory volume: {Width}x{Height}x{Depth}, chunkDim={_chunkDim}");
                InitializeChunks();
            }
            catch (Exception ex)
            {
                Logger.Log("[ChunkedVolume] Error in ChunkedVolume constructor (in-memory): " + ex);
                throw;
            }
        }

        // Constructor for a memory-mapped volume.
        public ChunkedVolume(int width, int height, int depth, int chunkDim,
                             MemoryMappedFile mmf, MemoryMappedViewAccessor[] accessors)
        {
            Width = width;
            Height = height;
            Depth = depth;
            _chunkDim = chunkDim;
            _mmf = mmf;
            _accessors = accessors;
            _useMemoryMapping = true;
            _chunkCountX = (Width + _chunkDim - 1) / _chunkDim;
            _chunkCountY = (Height + _chunkDim - 1) / _chunkDim;
            _chunkCountZ = (Depth + _chunkDim - 1) / _chunkDim;
        }
        #endregion

        #region Public Interface
        public byte this[int x, int y, int z]
        {
            get
            {
                ValidateCoordinates(x, y, z);
                var (chunkIndex, offset) = CalculateChunkIndexAndOffset(x, y, z);
                try
                {
                    return _useMemoryMapping
                        ? _accessors[chunkIndex].ReadByte(offset)
                        : _chunks[chunkIndex][offset];
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error reading voxel ({x},{y},{z}): {ex}");
                    throw;
                }
            }
            set
            {
                ValidateCoordinates(x, y, z);
                var (chunkIndex, offset) = CalculateChunkIndexAndOffset(x, y, z);
                try
                {
                    if (_useMemoryMapping)
                        _accessors[chunkIndex].Write(offset, value);
                    else
                        _chunks[chunkIndex][offset] = value;
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error writing voxel ({x},{y},{z}): {ex}");
                    throw;
                }
            }
        }

        // Modified FromFolder method that accepts a flag for memory mapping.
        public static ChunkedVolume FromFolder(string folder, int chunkDim, ProgressForm progress, bool useMemoryMapping = false)
        {
            try
            {
                Logger.Log(useMemoryMapping
                    ? "[FromFolder] Loading volume with memory mapping."
                    : "[FromFolder] Loading full volume into RAM.");
                var slicePaths = GetValidImagePaths(folder);
                ValidateImageSet(slicePaths);
                var dimensions = GetVolumeDimensions(slicePaths);
                Logger.Log($"[FromFolder] Volume dimensions: {dimensions.Width}x{dimensions.Height}x{dimensions.Depth}");

                if (!useMemoryMapping)
                {
                    // Load the full volume into RAM
                    var volume = new ChunkedVolume(dimensions.Width, dimensions.Height, dimensions.Depth, chunkDim);
                    ProcessSlices(volume, slicePaths, progress);
                    return volume;
                }
                else
                {
                    // Determine the total number of chunks and total size (in bytes)
                    int chunkCountX = (dimensions.Width + chunkDim - 1) / chunkDim;
                    int chunkCountY = (dimensions.Height + chunkDim - 1) / chunkDim;
                    int chunkCountZ = (dimensions.Depth + chunkDim - 1) / chunkDim;
                    int totalChunks = chunkCountX * chunkCountY * chunkCountZ;
                    long chunkSize = (long)chunkDim * chunkDim * chunkDim;
                    long totalSize = chunkSize * totalChunks;

                    // Create a bin file named "volume.bin" in the dataset folder and set its size.
                    string binPath = Path.Combine(folder, "volume.bin");
                    Logger.Log($"[FromFolder] Creating memory-mapped bin file at {binPath}");
                    using (var fs = new FileStream(binPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                    {
                        fs.SetLength(totalSize);
                    }

                    // Open the bin file as a memory-mapped file.
                    MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(binPath, FileMode.Open, null, totalSize, MemoryMappedFileAccess.ReadWrite);

                    // Create an accessor for each chunk.
                    MemoryMappedViewAccessor[] accessors = new MemoryMappedViewAccessor[totalChunks];
                    for (int i = 0; i < totalChunks; i++)
                    {
                        long offset = i * chunkSize;
                        accessors[i] = mmf.CreateViewAccessor(offset, chunkSize, MemoryMappedFileAccess.ReadWrite);
                    }

                    // Create a volume instance that writes directly into the mmf.
                    var volume = new ChunkedVolume(dimensions.Width, dimensions.Height, dimensions.Depth, chunkDim, mmf, accessors);

                    // Process each slice and write directly into the memory-mapped file via the volume indexer.
                    ProcessSlices(volume, slicePaths, progress);
                    Logger.Log("[FromFolder] Memory-mapped volume created and loaded successfully.");
                    return volume;
                }
            }
            catch (Exception ex)
            {
                Logger.Log("[FromFolder] Error: " + ex);
                throw;
            }
        }

        public void SaveAsBin(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Create))
                using (var bw = new BinaryWriter(fs))
                {
                    WriteHeader(bw);
                    WriteChunks(bw);
                }
                Logger.Log("[SaveAsBin] Volume saved successfully.");
            }
            catch (Exception ex)
            {
                Logger.Log("[SaveAsBin] Error: " + ex);
                throw;
            }
        }

        public void ReadChunks(BinaryReader br)
        {
            int totalChunks = !_useMemoryMapping ? _chunks.Length : (_accessors != null ? _accessors.Length : 0);
            int chunkSize = _chunkDim * _chunkDim * _chunkDim;
            try
            {
                if (!_useMemoryMapping)
                {
                    for (int i = 0; i < totalChunks; i++)
                    {
                        _chunks[i] = br.ReadBytes(chunkSize);
                    }
                }
                else
                {
                    _accessors = new MemoryMappedViewAccessor[totalChunks];
                    for (int i = 0; i < totalChunks; i++)
                    {
                        long thisChunkOffset = br.BaseStream.Position;
                        _accessors[i] = _mmf.CreateViewAccessor(thisChunkOffset, chunkSize, MemoryMappedFileAccess.ReadWrite);
                        br.BaseStream.Seek(chunkSize, SeekOrigin.Current);
                    }
                }
                Logger.Log("[ReadChunks] Volume chunks read successfully.");
            }
            catch (Exception ex)
            {
                Logger.Log("[ReadChunks] Error: " + ex);
                throw;
            }
        }
        #endregion

        #region Core Implementation
        private void InitializeChunks()
        {
            int chunkSize = _chunkDim * _chunkDim * _chunkDim;
            for (int i = 0; i < _chunks.Length; i++)
            {
                _chunks[i] = new byte[chunkSize];
            }
            Logger.Log($"[InitializeChunks] Initialized {_chunks.Length} chunks of size {chunkSize} bytes each.");
        }

        private (int chunkIndex, int offset) CalculateChunkIndexAndOffset(int x, int y, int z)
        {
            int cx = x / _chunkDim;
            int cy = y / _chunkDim;
            int cz = z / _chunkDim;
            int chunkIndex = (cz * _chunkCountY + cy) * _chunkCountX + cx;
            int lx = x % _chunkDim, ly = y % _chunkDim, lz = z % _chunkDim;
            int offset = (lz * _chunkDim + ly) * _chunkDim + lx;
            return (chunkIndex, offset);
        }
        #endregion

        #region Image Processing
        private static Bitmap ConvertTo24bpp(Bitmap source)
        {
            try
            {
                Logger.Log($"[ConvertTo24bpp] Converting image of size {source.Width}x{source.Height} from {source.PixelFormat}.");
                Bitmap clone = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(clone))
                {
                    g.DrawImage(source, new Rectangle(0, 0, clone.Width, clone.Height));
                }
                return clone;
            }
            catch (Exception ex)
            {
                Logger.Log("[ConvertTo24bpp] Conversion error: " + ex);
                throw;
            }
        }

        private static Bitmap LoadBitmapFromFile(string filePath)
        {
            try
            {
                byte[] imageBytes = File.ReadAllBytes(filePath);
                using (MemoryStream ms = new MemoryStream(imageBytes))
                {
                    return new Bitmap(ms);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[LoadBitmapFromFile] Error loading {filePath}: {ex}");
                throw;
            }
        }

        private static void ProcessSlices(ChunkedVolume volume, IReadOnlyList<string> slicePaths, ProgressForm progress)
        {
            var exceptions = new ConcurrentQueue<Exception>();
            int maxCores = Math.Max(1, Environment.ProcessorCount / 2);
            Parallel.For(0, slicePaths.Count, new ParallelOptions { MaxDegreeOfParallelism = maxCores }, z =>
            {
                try
                {
                    Logger.Log($"[ProcessSlices] Processing slice {z}: {slicePaths[z]}");
                    using (var orig = LoadBitmapFromFile(slicePaths[z]))
                    {
                        using (Bitmap slice = ConvertTo24bpp(orig))
                        {
                            using (var fastBmp = new FastBitmap(slice))
                            {
                                fastBmp.LockBits();
                                ProcessSlice(volume, z, fastBmp);
                            }
                        }
                    }
                    progress?.SafeUpdateProgress(z + 1, slicePaths.Count);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ProcessSlices] Exception on slice {z}: {ex}");
                    exceptions.Enqueue(new Exception($"Slice {z} ({slicePaths[z]}): {ex.Message}"));
                }
            });
            if (!exceptions.IsEmpty)
            {
                foreach (var e in exceptions)
                    Logger.Log("[ProcessSlices] Logged exception: " + e.Message);
                throw new AggregateException("Volume loading errors:", exceptions);
            }
        }

        private static void ProcessSlice(ChunkedVolume volume, int z, FastBitmap fastBmp)
        {
            int maxCores = Math.Max(1, Environment.ProcessorCount / 2);
            int bmpWidth = fastBmp.Width;
            int bmpHeight = fastBmp.Height;
            Parallel.For(0, bmpHeight, new ParallelOptions { MaxDegreeOfParallelism = maxCores }, y =>
            {
                for (int x = 0; x < bmpWidth; x++)
                {
                    try
                    {
                        byte gray = fastBmp.GetGrayValue(x, y);
                        volume[x, y, z] = gray;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ProcessSlice] Error at slice {z}, pixel ({x},{y}): {ex}");
                        throw;
                    }
                }
            });
        }
        #endregion

        #region Validation Methods
        private static void ValidateDimensions(int width, int height, int depth, int chunkDim)
        {
            if (width <= 0 || height <= 0 || depth <= 0 || chunkDim <= 0)
                throw new ArgumentException("[ValidateDimensions] Invalid volume dimensions or chunk dimension.");
        }

        private void ValidateCoordinates(int x, int y, int z)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height || z < 0 || z >= Depth)
                throw new IndexOutOfRangeException("[ValidateCoordinates] Voxel coordinates out of range.");
        }

        private static void ValidateImageSet(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0)
                throw new FileNotFoundException("[ValidateImageSet] No valid images found.");
            using (var first = new Bitmap(paths[0]))
            {
                if (first.Width <= 0 || first.Height <= 0)
                    throw new InvalidDataException("[ValidateImageSet] Invalid dimensions in the first image.");
            }
        }

        private static List<string> GetValidImagePaths(string folder)
        {
            return Directory.GetFiles(folder)
                .Where(f => new[] { ".bmp", ".tif", ".tiff", ".png", ".jpg" }
                    .Contains(Path.GetExtension(f).ToLower()))
                .OrderBy(f => f)
                .ToList();
        }

        private static (int Width, int Height, int Depth) GetVolumeDimensions(IReadOnlyList<string> paths)
        {
            using (var bmp = new Bitmap(paths[0]))
            {
                Logger.Log($"[GetVolumeDimensions] First image: {bmp.Width}x{bmp.Height}");
                return (bmp.Width, bmp.Height, paths.Count);
            }
        }
        #endregion

        #region File Operations
        private void WriteHeader(BinaryWriter bw)
        {
            bw.Write(Width);
            bw.Write(Height);
            bw.Write(Depth);
            bw.Write(_chunkDim);
            bw.Write(_chunkCountX);
            bw.Write(_chunkCountY);
            bw.Write(_chunkCountZ);
        }

        public void WriteChunks(BinaryWriter bw)
        {
            int chunkSize = _chunkDim * _chunkDim * _chunkDim;
            try
            {
                if (!_useMemoryMapping)
                {
                    foreach (var chunk in _chunks)
                    {
                        bw.Write(chunk, 0, chunkSize);
                    }
                }
                else
                {
                    foreach (var acc in _accessors)
                    {
                        byte[] buffer = new byte[chunkSize];
                        acc.ReadArray(0, buffer, 0, chunkSize);
                        bw.Write(buffer, 0, chunkSize);
                    }
                }
                Logger.Log("[WriteChunks] Volume chunks written successfully.");
            }
            catch (Exception ex)
            {
                Logger.Log("[WriteChunks] Error: " + ex);
                throw;
            }
        }
        #endregion

        #region Memory Management
        public void Dispose()
        {
            if (_useMemoryMapping && _accessors != null)
            {
                foreach (var acc in _accessors)
                    acc.Dispose();
                _mmf?.Dispose();
            }
        }
        #endregion

        #region FastBitmap Implementation
        public sealed class FastBitmap : IDisposable
        {
            private readonly Bitmap _bitmap;
            private BitmapData _data;
            private byte[] _bytes;
            private bool _disposed;
            private int _cachedWidth;
            private int _cachedHeight;

            public int Width => _cachedWidth;
            public int Height => _cachedHeight;

            public FastBitmap(Bitmap bitmap)
            {
                _bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
                if (_bitmap.PixelFormat != PixelFormat.Format24bppRgb)
                    throw new NotSupportedException("[FastBitmap] FastBitmap requires a 24bppRgb bitmap.");
            }

            public void LockBits()
            {
                _data = _bitmap.LockBits(new Rectangle(0, 0, _bitmap.Width, _bitmap.Height),
                    ImageLockMode.ReadOnly, _bitmap.PixelFormat);
                _bytes = new byte[_data.Stride * _data.Height];
                System.Runtime.InteropServices.Marshal.Copy(_data.Scan0, _bytes, 0, _bytes.Length);
                _cachedWidth = _bitmap.Width;
                _cachedHeight = _bitmap.Height;
            }

            public byte GetGrayValue(int x, int y)
            {
                if (x < 0 || x >= _cachedWidth || y < 0 || y >= _cachedHeight)
                    throw new ArgumentOutOfRangeException($"Coordinates ({x},{y}) out of bounds.");
                int offset = y * _data.Stride + x * 3;
                int sum = _bytes[offset] + _bytes[offset + 1] + _bytes[offset + 2];
                return (byte)(sum / 3);
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _bitmap.UnlockBits(_data);
                _disposed = true;
            }
        }
        #endregion
    }
}
